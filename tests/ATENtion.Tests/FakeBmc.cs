using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ATENtion.Core.Net;

namespace ATENtion.Tests
{
    /// <summary>A loopback BMC for session tests: handshakes, then answers every frame request.</summary>
    /// <remarks>
    /// Each FramebufferUpdateRequest is answered with the no-signal marker, as a powered-down host
    /// is, or with a small raw keyframe once <see cref="HostOn"/> is set. Other server messages can
    /// be injected with <see cref="Push"/>.
    /// </remarks>
    internal sealed class FakeBmc : IDisposable
    {
        public const int Width = 64, Height = 48;
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Thread _thread;
        private readonly bool _silent;
        private Socket _client;
        private int _fullRequests, _requests;

        /// <param name="silent">Accept the connection but never send anything, not even the banner.</param>
        public FakeBmc(bool silent = false)
        {
            _silent = silent;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public int Port { get; }
        public int FullRequests => Volatile.Read(ref _fullRequests);
        public int Requests => Volatile.Read(ref _requests);
        public volatile bool Answer = true;
        public volatile bool HostOn;

        /// <summary>The power byte for host-status replies (0x37), or -1 to ignore the query as some firmware may.</summary>
        public volatile int HostStatusPower = 1;
        public int StatusQueries => Volatile.Read(ref _statusQueries);
        private int _statusQueries;

        /// <summary>Key events received, in order: the USB-HID usage and whether it was a press.</summary>
        public List<(uint Usage, bool Down)> Keys
        {
            get { lock (_keys) return new List<(uint, bool)>(_keys); }
        }
        private readonly List<(uint, bool)> _keys = new List<(uint, bool)>();

        public KvmConnectionOptions Options() => new KvmConnectionOptions
        {
            Host = "127.0.0.1", Port = Port, KvmUsername = "user", KvmPassword = "pass",
        };

        /// <summary>Sends raw server messages to the client.</summary>
        public void Push(params byte[][] messages)
        {
            foreach (var m in messages) _client.Send(m);
        }

        private void Serve()
        {
            try
            {
                _client = _listener.AcceptSocket();
                if (_silent) { Thread.Sleep(Timeout.Infinite); return; }
                _client.Send(HandshakeScript());

                // The client stream is parsed message by message, so a status query (a single 0x37
                // byte) cannot be confused with a 0x37 inside another message.
                var received = new List<byte>();
                int at = 12 + 1 + 48 + 1;   // version echo, security type, credentials, ClientInit
                var chunk = new byte[4096];
                int n;
                while ((n = _client.Receive(chunk)) > 0)
                {
                    for (int i = 0; i < n; i++) received.Add(chunk[i]);
                    while (at < received.Count)
                    {
                        int length = MessageLength(received[at]);
                        if (length < 0) throw new InvalidOperationException($"fake BMC: unknown client message 0x{received[at]:x2}");
                        if (at + length > received.Count) break;
                        Handle(received, at);
                        at += length;
                    }
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (ThreadInterruptedException) { }
        }

        // Lengths of the messages this client sends, by type byte.
        private static int MessageLength(byte type)
        {
            switch (type)
            {
                case 0x03: return 10;   // FramebufferUpdateRequest
                case 0x04: return 18;   // key
                case 0x05: return 18;   // pointer
                case 0x07: return 3;    // runImage
                case 0x15: return 9;    // keepalive
                case 0x1a: return 2;    // power
                case 0x32: return 5;    // screen info
                case 0x36: return 3;    // mouse mode
                case 0x37: return 1;    // host-status query
                case 0x3a: return 1;    // hot-plug
                default: return -1;
            }
        }

        private void Handle(List<byte> d, int at)
        {
            switch (d[at])
            {
                case 0x03:
                    Interlocked.Increment(ref _requests);
                    if (d[at + 1] == 0) Interlocked.Increment(ref _fullRequests);
                    if (Answer) _client.Send(HostOn ? FrameUpdate() : NoSignalUpdate());
                    break;
                case 0x04:   // [4][0][down][0][0][usage : u32 BE][padding]
                    uint usage = (uint)(d[at + 5] << 24 | d[at + 6] << 16 | d[at + 7] << 8 | d[at + 8]);
                    lock (_keys) _keys.Add((usage, d[at + 2] == 1));
                    break;
                case 0x37:
                    Interlocked.Increment(ref _statusQueries);
                    int power = HostStatusPower;
                    if (Answer && power >= 0) _client.Send(new byte[] { 0x37, 0x00, 0x01, (byte)power });
                    break;
            }
        }

        private static byte[] HandshakeScript()
        {
            var b = new List<byte>();
            b.AddRange(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            b.Add(1); b.Add(16);                   // one security type: 16
            b.AddRange(new byte[24]);              // challenge
            U32(b, 0);                             // SecurityResult OK
            U16(b, Width); U16(b, Height);
            b.AddRange(new byte[16]);              // pixel format
            U32(b, 4); b.AddRange(Encoding.ASCII.GetBytes("FAKE"));
            b.AddRange(new byte[12]);              // ATEN ServerInit tail
            return b.ToArray();
        }

        /// <summary>One rectangle of -640 x -480 with no data: the BMC's "no video signal" marker.</summary>
        public static byte[] NoSignalUpdate()
        {
            var b = new List<byte> { 0, 0 };       // FramebufferUpdate, padding
            U16(b, 1);
            U16(b, 0); U16(b, 0); U16(b, 0xFD80); U16(b, 0xFE20);
            U32(b, 0); U32(b, 0); U32(b, 0);       // encoding, mode, dataLen
            return b.ToArray();
        }

        /// <summary>A raw Hermon keyframe: ten-byte header (frameMode 1, type 0, length), RGB555 pixels.</summary>
        public static byte[] FrameUpdate() => Update(0, RawKeyframe());

        /// <summary>A FramebufferUpdate carrying one full-screen rectangle with the given encoding and payload.</summary>
        public static byte[] Update(uint encoding, byte[] payload)
        {
            var b = new List<byte> { 0, 0 };
            U16(b, 1);
            U16(b, 0); U16(b, 0); U16(b, Width); U16(b, Height);
            U32(b, encoding); U32(b, 0); U32(b, (uint)payload.Length);
            b.AddRange(payload);
            return b.ToArray();
        }

        private static byte[] RawKeyframe()
        {
            int length = 10 + Width * Height * 2;
            var p = new List<byte> { 1, 0, 0, 0, 0, 0 };
            U32(p, (uint)length);
            p.AddRange(new byte[Width * Height * 2]);
            return p.ToArray();
        }

        /// <summary>A privilege record (0x39): two words and a 256-byte "&lt;sid&gt; ROLE &lt;ip&gt;" string.</summary>
        public static byte[] Privilege(uint index, uint b, string info)
        {
            var r = new List<byte> { 0x39 };
            U32(r, index); U32(r, b);
            var text = new byte[256];
            Encoding.ASCII.GetBytes(info).CopyTo(text, 0);
            r.AddRange(text);
            return r.ToArray();
        }

        private static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }

        private static void U32(List<byte> b, uint v)
        {
            b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
        }

        public void Dispose()
        {
            try { _client?.Close(); } catch { }
            try { _listener.Stop(); } catch { }
            _thread.Interrupt();
            _thread.Join(2000);
        }
    }
}
