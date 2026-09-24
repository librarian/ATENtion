using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ATENtion.Core.Net;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>
    /// A paused (hidden-tab) session keeps checking its host, and the watchdog judges its silence
    /// against those checks.
    /// </summary>
    /// <remarks>
    /// The session runs for real against a loopback server that completes the handshake and answers
    /// each full frame request with the BMC's no-signal marker, as a powered-down host does.
    /// </remarks>
    public class PausedSessionTests
    {
        [Fact]
        public void Paused_Session_Checks_Its_Host_And_Updates_The_Signal()
        {
            using (var bmc = new FakeBmc())
            using (var session = Connect(bmc, pausedCheckSeconds: 1))
            {
                int afterStart = WaitForRequests(bmc, 1);   // the startup keyframe
                Assert.True(WaitFor(() => session.VideoSignal == VideoSignalState.Absent));
                DateTime first = session.VideoSignalUtc;

                Assert.True(WaitFor(() => bmc.FullRequests >= afterStart + 2, 4000),
                    $"expected periodic checks while paused, saw {bmc.FullRequests - afterStart}");
                Assert.True(WaitFor(() => session.VideoSignalUtc > first));
            }
        }

        [Fact]
        public void Paused_Session_With_Checks_Off_Sends_No_Requests()
        {
            using (var bmc = new FakeBmc())
            using (var session = Connect(bmc, pausedCheckSeconds: 0))
            {
                int afterStart = WaitForRequests(bmc, 1);
                Thread.Sleep(2500);
                Assert.Equal(afterStart, bmc.FullRequests);
            }
        }

        [Fact]
        public void Paused_Session_Faults_When_Checks_Go_Unanswered()
        {
            using (var bmc = new FakeBmc())
            using (var faulted = new ManualResetEventSlim())
            using (var session = Connect(bmc, pausedCheckSeconds: 1, stallTimeout: 2,
                                         onFault: () => faulted.Set()))
            {
                WaitForRequests(bmc, 1);
                bmc.Answer = false;
                Assert.True(faulted.Wait(8000), "the watchdog should fault a paused session that stops answering");
            }
        }

        [Fact]
        public void Paused_Session_That_Answers_Is_Not_Faulted()
        {
            using (var bmc = new FakeBmc())
            using (var faulted = new ManualResetEventSlim())
            using (var session = Connect(bmc, pausedCheckSeconds: 1, stallTimeout: 2,
                                         onFault: () => faulted.Set()))
            {
                Assert.False(faulted.Wait(5000), "a paused session answering its checks is healthy");
            }
        }

        [Theory]
        // Streaming: probe at the soft limit, fault at the hard one.
        [InlineData(3.9, false, 10, KvmVideoSession.StallAction.None)]
        [InlineData(4.0, false, 10, KvmVideoSession.StallAction.Probe)]
        [InlineData(10.0, false, 10, KvmVideoSession.StallAction.Fault)]
        // Paused with checks: silence up to one check interval is expected, and there is no probe.
        [InlineData(10.0, true, 10, KvmVideoSession.StallAction.None)]
        [InlineData(19.9, true, 10, KvmVideoSession.StallAction.None)]
        [InlineData(20.0, true, 10, KvmVideoSession.StallAction.Fault)]
        // Paused without checks: nothing is expected, so silence proves nothing.
        [InlineData(3600, true, 0, KvmVideoSession.StallAction.None)]
        public void Watchdog_Judges_Silence_Against_What_Was_Asked_For(
            double idle, bool paused, int checkSeconds, object expected)   // object: the enum is internal
        {
            Assert.Equal(expected, KvmVideoSession.EvaluateStall(idle, paused, checkSeconds,
                probeSeconds: 4, timeoutSeconds: 10));
        }

        private static KvmVideoSession Connect(FakeBmc bmc, int pausedCheckSeconds, int stallTimeout = 10,
                                               Action onFault = null)
        {
            var session = new KvmVideoSession(new KvmConnectionOptions
            {
                Host = "127.0.0.1",
                Port = bmc.Port,
                KvmUsername = "user",
                KvmPassword = "pass",
            })
            {
                VideoPaused = true,
                PausedCheckSeconds = pausedCheckSeconds,
                StallProbeSeconds = 1,
                StallTimeoutSeconds = stallTimeout,
                LogInput = false,
            };
            if (onFault != null) session.Faulted += (s, e) => onFault();
            session.Open();
            session.StartPump();
            return session;
        }

        private static int WaitForRequests(FakeBmc bmc, int count)
        {
            Assert.True(WaitFor(() => bmc.FullRequests >= count), "no full frame request arrived");
            Thread.Sleep(200);   // let the reply land before the caller takes its baseline
            return bmc.FullRequests;
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        /// <summary>A loopback BMC: handshakes, then answers full requests with the no-signal marker.</summary>
        private sealed class FakeBmc : IDisposable
        {
            private const int Width = 1024, Height = 768;
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread _thread;
            private Socket _client;
            private int _fullRequests;

            public FakeBmc()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            public int Port { get; }
            public int FullRequests => Volatile.Read(ref _fullRequests);
            public volatile bool Answer = true;

            private void Serve()
            {
                try
                {
                    _client = _listener.AcceptSocket();
                    _client.Send(HandshakeScript());

                    // A full request is [3][incremental=0][x=0][y=0][w][h], all big-endian.
                    byte[] full = { 3, 0, 0, 0, 0, 0, Width >> 8, Width & 0xff, Height >> 8, Height & 0xff };
                    var received = new List<byte>();
                    int scanned = 0;
                    var chunk = new byte[4096];
                    int n;
                    while ((n = _client.Receive(chunk)) > 0)
                    {
                        for (int i = 0; i < n; i++) received.Add(chunk[i]);
                        for (; scanned + full.Length <= received.Count; scanned++)
                        {
                            if (!Matches(received, scanned, full)) continue;
                            Interlocked.Increment(ref _fullRequests);
                            if (Answer) _client.Send(NoSignalUpdate());
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }

            private static bool Matches(List<byte> data, int at, byte[] pattern)
            {
                for (int i = 0; i < pattern.Length; i++)
                    if (data[at + i] != pattern[i]) return false;
                return true;
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

            // One rectangle of -640 x -480 with no data: the BMC's "no video signal" marker.
            private static byte[] NoSignalUpdate()
            {
                var b = new List<byte> { 0, 0 };       // FramebufferUpdate, padding
                U16(b, 1);
                U16(b, 0); U16(b, 0); U16(b, 0xFD80); U16(b, 0xFE20);
                U32(b, 0); U32(b, 0); U32(b, 0);       // encoding, mode, dataLen
                return b.ToArray();
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
                _thread.Join(2000);
            }
        }
    }
}
