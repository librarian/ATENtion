using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using ATENtion.Core.Diagnostics;
using ATENtion.Core.Storage;

namespace ATENtion.Core.Net
{
    /// <summary>The parameters for opening a <see cref="VirtualMediaSession"/>.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the host, the virtual-media data port, the ISO image path, the connect
    /// timeout, transport, client certificate, and temporary credentials for a virtual-media session.
    /// </para>
    /// <para>
    /// PROVENANCE - The default port 623 is the vmedia main_port from launch.jnlp (devStruct+0x780).
    /// </para>
    /// </remarks>
    public sealed class VirtualMediaOptions
    {
        /// <summary>The BMC host name or address.</summary>
        public string Host { get; set; }
        /// <summary>The virtual-media data port; defaults to 623.</summary>
        public int Port { get; set; } = 623;
        /// <summary>True to use direct mutual TLS instead of the vendor's local stunnel endpoint.</summary>
        public bool UseTls { get; set; } = true;
        /// <summary>The client certificate presented to the BMC's mutual-TLS listener.</summary>
        public X509Certificate2 ClientCertificate { get; set; }
        /// <summary>JNLP argument 1: the temporary ATEN username.</summary>
        public string Username { get; set; }
        /// <summary>JNLP argument 2: the temporary ATEN password.</summary>
        public string Password { get; set; }
        /// <summary>The local path to the ISO to serve as a read-only CD-ROM.</summary>
        public string ImagePath { get; set; }
        /// <summary>The TCP connect timeout, in milliseconds.</summary>
        public int ConnectTimeoutMs { get; set; } = 10000;
        /// <summary>The logical unit number echoed in response headers (the BMC sets it per command anyway).</summary>
        public byte Lun { get; set; } = 1;
    }

    /// <summary>
    /// Runs a read-only virtual-media session that presents a local ISO to the host as a USB CD-ROM,
    /// serving the SCSI commands the BMC forwards over its virtual-media channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Connects to the BMC's virtual-media port, then acts as a USB mass-storage target:
    /// it reads the command frames the BMC forwards, runs each SCSI command against the ISO, and
    /// returns the data and status the host's mass-storage driver expects.
    /// </para>
    /// <para>
    /// OPERATION - A background serve loop reads an eight-byte ATEN frame header, then its payload.
    /// A 31-byte payload is a USB Command Block Wrapper, which is parsed and executed against the
    /// <see cref="ScsiCdRomTarget"/>. The result is framed back as an optional data frame followed
    /// by a status (CSW) frame. Any other payload is a control frame and is read past. A teardown
    /// frame, identified by its type byte, ends the session. Direct connections to the public BMC
    /// endpoint use mutual TLS; a diagnostic local-stunnel connection may be plaintext.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads sectors through an <see cref="IsoBlockSource"/>, runs commands through a
    /// <see cref="ScsiCdRomTarget"/>, and frames bytes with <see cref="VmediaFraming"/>. Raises
    /// <see cref="Faulted"/> on an unexpected loop error and <see cref="Closed"/> on a clean end.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Read-only. <see cref="Open"/> must be called before <see cref="StartServing"/>.
    /// Writes to the socket serialise on an internal lock. <see cref="Dispose"/> requests a clean
    /// detach, then closes the socket and joins the serve thread before disposing the ISO.
    /// </para>
    /// <para>
    /// PROVENANCE - Mirrors the native serve loop iKVM64.dll FUN_180008840 (read an eight-byte frame,
    /// process it, respond), the firmware's multi-connection attach flow, and the native USB
    /// descriptor/authentication record. VERIFIED LIVE with an AST2400 BMC and a Linux host.
    /// </para>
    /// </remarks>
    public sealed class VirtualMediaSession : IDisposable
    {
        private readonly VirtualMediaOptions _options;
        private TcpClient _tcp;
        private Stream _transport;
        private BufferedRfbStream _stream;
        private IsoBlockSource _iso;
        private ScsiCdRomTarget _target;
        private Thread _serveThread;
        private readonly object _sendLock = new object();
        private volatile bool _running;
        private Timer _keepAliveTimer;

        /// <summary>Control frame type of the health request the BMC accepts as session liveness.</summary>
        private const uint HealthControlType = 8;

        /// <summary>
        /// The BMC drops the session after roughly thirty seconds of silence on the data channel,
        /// so stay comfortably below that.
        /// </summary>
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

        // A write blocked this long means the link is gone; it faults rather than hold the lock.
        private const int SendTimeoutMs = 30000;

        /// <summary>Creates a virtual-media session bound to the given options.</summary>
        /// <param name="options">The host, port, and ISO image to serve.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        public VirtualMediaSession(VirtualMediaOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>Raised when the serve loop stops on an unexpected error.</summary>
        public event EventHandler<Exception> Faulted;
        /// <summary>Raised when the session ends cleanly (the BMC closed the channel or sent a teardown frame).</summary>
        public event EventHandler Closed;

        /// <summary>True while the serve loop is running.</summary>
        public bool IsRunning => _running;
        /// <summary>The number of SCSI commands served so far.</summary>
        public long CommandsServed { get; private set; }
        /// <summary>The number of data-in bytes served so far.</summary>
        public long BytesServed { get; private set; }
        /// <summary>The path of the ISO being served.</summary>
        public string ImagePath => _options.ImagePath;

        /// <summary>Opens the ISO and connects the socket.</summary>
        /// <exception cref="TimeoutException">The connection did not complete within the timeout.</exception>
        public void Open()
        {
            _iso = new IsoBlockSource(_options.ImagePath);
            _target = new ScsiCdRomTarget(_iso);
            KvmLog.Write($"vmedia: ISO '{_options.ImagePath}' = {_iso.TotalBlocks} sectors " +
                         $"({_iso.LengthBytes:n0} bytes), lastLBA {_iso.LastLba}.");

            PerformAttachHandshake();
            KvmLog.Write("vmedia: authenticated ISO attach accepted.");
        }

        /// <summary>Reads which of the BMC's virtual-media slots hold media, without attaching anything.</summary>
        /// <remarks>
        /// One health exchange on its own connection, the same the attach starts with. Needs no
        /// credentials. Returns null if the reply has an unexpected shape.
        /// </remarks>
        public static VirtualMediaSlots ProbeSlots(VirtualMediaOptions options)
        {
            var probe = new VirtualMediaSession(options);
            using (Channel health = probe.OpenChannel("status"))
            {
                SendControl(health.Stream, 8);
                return VirtualMediaSlots.Parse(ReadControl(health.Stream, 9));
            }
        }

        private Channel OpenChannel(string purpose)
        {
            KvmLog.Write($"vmedia: opening {purpose} channel to {_options.Host}:{_options.Port} " +
                         $"({(_options.UseTls ? "TLS" : "plaintext")})...");
            // Native sets TCP_NODELAY and 0x88b80 send/recv buffers.
            var tcp = TcpConnect.Open(_options.Host, _options.Port, _options.ConnectTimeoutMs, t =>
            {
                t.SendBufferSize = 0x88b80;
                t.ReceiveBufferSize = 0x88b80;
            });
            try
            {
                // Bounded for the attach exchange; the data channel lifts the read limit once serving.
                tcp.ReceiveTimeout = _options.ConnectTimeoutMs;
                tcp.SendTimeout = SendTimeoutMs;
                Stream transport = tcp.GetStream();
                if (_options.UseTls)
                {
                    var ssl = new SslStream(transport, false, AcceptServerCertificate);
                    var certificates = new X509CertificateCollection();
                    if (_options.ClientCertificate != null) certificates.Add(_options.ClientCertificate);
                    ssl.AuthenticateAsClient(_options.Host, certificates, SslProtocols.Tls12, false);
                    transport = ssl;
                    KvmLog.Write($"vmedia: TLS established: {ssl.SslProtocol}, cipher {ssl.CipherAlgorithm}.");
                }
                return new Channel(tcp, transport);
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        private void PerformAttachHandshake()
        {
            // The native ATEN client uses distinct sockets for health, initialization, and data.
            // AST2400 firmware accepts one pre-attach control exchange per connection.
            using (Channel health = OpenChannel("health"))
            {
                SendControl(health.Stream, 8);
                byte[] initialHealth = ReadControl(health.Stream, 9);
                KvmLog.Write("vmedia: health exchange accepted: " + KvmLog.Hex(initialHealth));
                // Refuse before attaching rather than let the BMC turn the attach down.
                if (VirtualMediaSlots.Parse(initialHealth)?.InUse(VirtualMediaSlots.OwnSlot) == true)
                    throw VirtualMediaRefusedException.SlotTaken();
            }

            using (Channel initialization = OpenChannel("initialization"))
            {
                SendControl(initialization.Stream, 10);
                ReadControl(initialization.Stream, 11);
                KvmLog.Write("vmedia: session initialization accepted.");
            }

            Channel data = OpenChannel("data");
            _tcp = data.Tcp;
            _transport = data.Transport;
            _stream = data.Stream;

            using (Channel health = OpenChannel("post-init health"))
            {
                SendControl(health.Stream, 8);
                ReadControl(health.Stream, 9);
                KvmLog.Write("vmedia: post-init health exchange accepted.");
            }

            _stream.WriteBytes(VmediaHandshake.BuildAttachRecord(_options.Username, _options.Password));
            _stream.Flush();
            byte[] mountStatus = ReadControl(_stream, 2);
            KvmLog.Write("vmedia: mount status: " + KvmLog.Hex(mountStatus));
            if (mountStatus.Length < 1 || mountStatus[0] != 0)
                throw VirtualMediaRefusedException.ForStatus(mountStatus.Length == 0 ? -1 : mountStatus[0]);
            KvmLog.Write("vmedia: authenticated device record accepted.");

            // Native device-worker acknowledgement for all three virtual USB slots.
            SendControl(7, new byte[] { 0x05, 0x03, 0x01, 0x10, 0x02, 0x20, 0x03, 0x30 });
        }

        private void SendControl(uint type, byte[] payload = null)
        {
            SendControl(_stream, type, payload);
        }

        private static void SendControl(BufferedRfbStream stream, uint type, byte[] payload = null)
        {
            stream.WriteBytes(VmediaHandshake.BuildControlFrame(type, payload));
            stream.Flush();
        }

        private static byte[] ReadControl(BufferedRfbStream stream, uint expectedType)
        {
            byte[] prefix = stream.ReadExact(4);
            uint type = VmediaFraming.ReadType(prefix);
            byte[] length = stream.ReadExact(4);
            int count = length[0] | (length[1] << 8) | (length[2] << 16) | (length[3] << 24);
            if (count < 0 || count > 1024 * 1024)
                throw new InvalidDataException($"Invalid vmedia control payload length {count}.");
            byte[] payload = count == 0 ? ScsiResult.NoData : stream.ReadExact(count);
            if (type != expectedType)
                throw new InvalidDataException($"Expected vmedia control type {expectedType}, received {type}.");
            return payload;
        }

        private sealed class Channel : IDisposable
        {
            public Channel(TcpClient tcp, Stream transport)
            {
                Tcp = tcp;
                Transport = transport;
                Stream = new BufferedRfbStream(transport);
            }

            public TcpClient Tcp { get; }
            public Stream Transport { get; }
            public BufferedRfbStream Stream { get; }

            public void Dispose()
            {
                try { Transport.Dispose(); } catch { }
                try { Tcp.Close(); } catch { }
            }
        }

        /// <summary>Starts the background serve loop.</summary>
        /// <exception cref="InvalidOperationException"><see cref="Open"/> has not been called.</exception>
        public void StartServing()
        {
            if (_stream == null) throw new InvalidOperationException("Call Open() first.");
            // The host only reads while it wants data, so silence on the data channel is normal.
            _tcp.ReceiveTimeout = 0;
            _running = true;
            _serveThread = new Thread(ServeLoop) { IsBackground = true, Name = "vmedia-serve" };
            _serveThread.Start();

            // A guest only issues SCSI commands while actually reading, so an idle disc would
            // otherwise let the channel fall silent and be dropped.
            _keepAliveTimer = new Timer(_ => SendKeepAlive(), null,
                                        KeepAliveInterval, KeepAliveInterval);
        }

        // Must be the data channel: an exchange on a separately opened health channel does not
        // reset the BMC's timer.
        private void SendKeepAlive()
        {
            if (!_running) return;
            try
            {
                lock (_sendLock) SendControl(HealthControlType);
            }
            catch (Exception ex)
            {
                if (!_running) return;
                KvmLog.Error("vmedia keepalive", ex);
                _running = false;
                Faulted?.Invoke(this, ex);
            }
        }

        private void ServeLoop()
        {
            KvmLog.Write("vmedia: serve loop started (USB-MSC target).");
            try
            {
                while (_running)
                {
                    byte[] prefix = _stream.ReadExact(4);
                    uint type = VmediaFraming.ReadType(prefix);
                    if (prefix[0] == VmediaFraming.CommandMarker)
                    {
                        byte[] rest = _stream.ReadExact(VmediaFraming.CommandHeaderSize - 4);
                        var header = new byte[VmediaFraming.CommandHeaderSize];
                        Array.Copy(prefix, header, 4);
                        Array.Copy(rest, 0, header, 4, rest.Length);
                        int payloadLen = VmediaFraming.ReadCommandPayloadLength(header);
                        byte lun = VmediaFraming.ReadCommandLun(header);
                        if (payloadLen != VmediaFraming.CbwSize)
                            throw new InvalidDataException($"Unexpected vmedia command payload length {payloadLen}.");
                        byte[] cbwBytes = _stream.ReadExact(VmediaFraming.CbwSize);
                        var cbw = CommandBlockWrapper.Parse(cbwBytes);
                        if (cbw.Signature != VmediaFraming.CbwSignature)
                            throw new InvalidDataException("Invalid USB command-block signature.");
                        ServeCommand(lun, cbw);
                        continue;
                    }

                    byte[] length = _stream.ReadExact(4);
                    int controlLength = length[0] | (length[1] << 8) | (length[2] << 16) | (length[3] << 24);
                    if (controlLength < 0 || controlLength > 1024 * 1024)
                        throw new InvalidDataException($"Invalid vmedia control payload length {controlLength}.");
                    if (controlLength > 0) _stream.Skip(controlLength); // control payload: consume and ignore it
                    if (type == 4 || type == 6)
                    {
                        KvmLog.Write("vmedia: teardown frame received; ending session.");
                        break;
                    }
                }
                _running = false;
                Closed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _running = false;
                    KvmLog.Error("vmedia serve loop", ex);
                    Faulted?.Invoke(this, ex);
                }
            }
        }

        // Runs one SCSI command and frames the response: an optional data-in frame, then the CSW.
        private void ServeCommand(byte lun, CommandBlockWrapper cbw)
        {
            ScsiResult result = _target.Execute(cbw);
            CommandsServed++;

            byte[] data = result.Data;
            // A target must never return more bytes than the host asked for.
            if (data.Length > cbw.DataTransferLength) data = TrimTo(data, (int)cbw.DataTransferLength);

            uint residue = cbw.DataTransferLength > (uint)data.Length
                ? cbw.DataTransferLength - (uint)data.Length
                : 0;

            lock (_sendLock)
            {
                if (cbw.IsDataIn && data.Length > 0)
                {
                    _stream.WriteBytes(VmediaFraming.BuildHeader(lun, VmediaFraming.FlagData, data.Length));
                    _stream.WriteBytes(data);
                    BytesServed += data.Length;
                }
                byte[] csw = VmediaFraming.BuildCsw(cbw.Tag, residue, result.Status);
                _stream.WriteBytes(VmediaFraming.BuildHeader(lun, VmediaFraming.FlagStatus, csw.Length));
                _stream.WriteBytes(csw);
                _stream.Flush();
            }

            if (KvmLog.Enabled)
                KvmLog.Write($"vmedia: op 0x{cbw.Opcode:x2} tag {cbw.Tag} -> {data.Length}B data, " +
                             $"status {result.Status}, residue {residue}.");
        }

        // Returns a copy of the data truncated to the given length, or the shared empty array.
        private static byte[] TrimTo(byte[] data, int len)
        {
            if (len <= 0) return ScsiResult.NoData;
            var trimmed = new byte[len];
            Array.Copy(data, trimmed, len);
            return trimmed;
        }

        /// <summary>Signals the serve loop to stop.</summary>
        public void Stop() => _running = false;

        /// <summary>Stops the session and waits briefly for the BMC's teardown acknowledgement.</summary>
        public void Dispose()
        {
            _running = false;
            try { _keepAliveTimer?.Dispose(); } catch { }
            _keepAliveTimer = null;
            // Under the send lock: the serve thread or a keepalive may be mid-write on the same stream.
            // A writer stuck on a dead link is not waited out; the socket close below ends it.
            if (_stream != null && Monitor.TryEnter(_sendLock, 1000))
            {
                try { SendControl(5); } catch { }
                finally { Monitor.Exit(_sendLock); }
            }
            // The BMC normally answers type 5 with type 6, which lets the serve loop finish cleanly.
            // If it does not, close the socket after a short grace period to unblock the read.
            try { _serveThread?.Join(1000); } catch { }
            try { _tcp?.Close(); } catch { }
            try { _serveThread?.Join(1000); } catch { }
            try { _transport?.Dispose(); } catch { }
            try { _iso?.Dispose(); } catch { }
        }

        private static bool AcceptServerCertificate(object sender, X509Certificate certificate,
                                                    X509Chain chain, SslPolicyErrors errors) => true;
    }
}
