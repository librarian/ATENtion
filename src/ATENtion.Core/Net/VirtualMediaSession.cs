using System;
using System.Net.Sockets;
using System.Threading;
using ATENtion.Core.Diagnostics;
using ATENtion.Core.Storage;

namespace ATENtion.Core.Net
{
    /// <summary>The parameters for opening a <see cref="VirtualMediaSession"/>.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the host, the virtual-media data port, the ISO image path, the connect
    /// timeout, and the logical unit number for a virtual-media session.
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
        /// <summary>The local path to the ISO to serve as a read-only CD-ROM.</summary>
        public string ImagePath { get; set; }
        /// <summary>The TCP connect timeout, in milliseconds.</summary>
        public int ConnectTimeoutMs { get; set; } = 10000;
        /// <summary>The logical unit number echoed in response headers (the BMC sets it per command anyway).</summary>
        public byte Lun { get; set; } = 0;
    }

    /// <summary>
    /// Runs a read-only virtual-media session that presents a local ISO to the host as a USB CD-ROM,
    /// serving the SCSI commands the BMC forwards over a plaintext channel.
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
    /// frame, identified by its type byte, ends the session. Unlike the KVM channel, virtual media
    /// is plaintext: there is no TLS and no integrity digest to reproduce.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads sectors through an <see cref="IsoBlockSource"/>, runs commands through a
    /// <see cref="ScsiCdRomTarget"/>, and frames bytes with <see cref="VmediaFraming"/>. Raises
    /// <see cref="Faulted"/> on an unexpected loop error and <see cref="Closed"/> on a clean end.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Read-only. <see cref="Open"/> must be called before <see cref="StartServing"/>.
    /// Writes to the socket serialise on an internal lock. <see cref="Dispose"/> closes the socket
    /// before joining the serve thread so the thread has exited before the ISO it reads is disposed.
    /// </para>
    /// <para>
    /// PROVENANCE - Mirrors the native serve loop iKVM64.dll FUN_180008840 (read an eight-byte frame,
    /// process it, respond); the transport is plaintext TCP with no integrity digest. PORTED FAITHFULLY: the end-to-end attach and ISO boot are not yet
    /// confirmed against hardware.
    /// </para>
    /// </remarks>
    public sealed class VirtualMediaSession : IDisposable
    {
        private readonly VirtualMediaOptions _options;
        private TcpClient _tcp;
        private BufferedRfbStream _stream;
        private IsoBlockSource _iso;
        private ScsiCdRomTarget _target;
        private Thread _serveThread;
        private readonly object _sendLock = new object();
        private volatile bool _running;

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

            KvmLog.Write($"vmedia: TCP connecting to {_options.Host}:{_options.Port} (plaintext)...");
            _tcp = new TcpClient { NoDelay = true }; // native sets TCP_NODELAY and 0x88b80 send/recv buffers
            _tcp.SendBufferSize = 0x88b80;
            _tcp.ReceiveBufferSize = 0x88b80;
            if (!_tcp.ConnectAsync(_options.Host, _options.Port).Wait(_options.ConnectTimeoutMs))
                throw new TimeoutException($"Timed out connecting to {_options.Host}:{_options.Port}.");
            _stream = new BufferedRfbStream(_tcp.GetStream());
            KvmLog.Write("vmedia: connected.");
        }

        /// <summary>Starts the background serve loop.</summary>
        /// <exception cref="InvalidOperationException"><see cref="Open"/> has not been called.</exception>
        public void StartServing()
        {
            if (_stream == null) throw new InvalidOperationException("Call Open() first.");
            _running = true;
            _serveThread = new Thread(ServeLoop) { IsBackground = true, Name = "vmedia-serve" };
            _serveThread.Start();
        }

        private void ServeLoop()
        {
            KvmLog.Write("vmedia: serve loop started (USB-MSC target).");
            try
            {
                while (_running)
                {
                    byte[] header = _stream.ReadExact(VmediaFraming.HeaderSize);
                    int payloadLen = VmediaFraming.ReadPayloadLength(header);
                    byte lun = VmediaFraming.ReadLun(header);
                    // The header's type word distinguishes a command/CBW carrier from a control frame;
                    // a teardown is signalled by the type byte (FUN_1800085a0: type 4 disables both
                    // directions). A 31-byte payload is a CBW. Anything else is a control frame to read past.
                    int type = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];

                    if (payloadLen == VmediaFraming.CbwSize)
                    {
                        byte[] cbwBytes = _stream.ReadExact(VmediaFraming.CbwSize);
                        ServeCommand(lun, CommandBlockWrapper.Parse(cbwBytes));
                        continue;
                    }

                    if (payloadLen > 0) _stream.Skip(payloadLen); // control payload: consume and ignore it
                    if ((type & 0xFF000000) != 0 && header[3] == 0x04)
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

        /// <summary>Stops the session, closing the socket before joining the serve thread.</summary>
        public void Dispose()
        {
            _running = false;
            // Close the socket first to unblock the serve loop's blocking read, then join it so the
            // thread has exited before the ISO it may still be reading from is disposed.
            try { _tcp?.Close(); } catch { }
            try { _serveThread?.Join(2000); } catch { }
            try { _iso?.Dispose(); } catch { }
        }
    }
}
