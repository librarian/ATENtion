using System;
using System.Diagnostics;
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
            // A BMC that ignores the host-status query, so the check is a full frame.
            using (var bmc = new FakeBmc { HostStatusPower = -1 })
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
            var session = new KvmVideoSession(bmc.Options())
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
    }
}
