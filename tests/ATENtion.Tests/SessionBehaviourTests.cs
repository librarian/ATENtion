using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ATENtion.Core.Net;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Session behaviour against a loopback BMC: request accounting, the user list, and failures.</summary>
    public class SessionBehaviourTests
    {
        [Fact]
        public void Video_Runs_Freely_Once_A_Host_Comes_On()
        {
            // While the host is off, every request is answered with the no-signal marker. Those
            // replies used to stay counted as outstanding, so once pictures came back the session
            // only sent the periodic full request: about one frame a second here instead of a stream.
            using (var bmc = new FakeBmc())
            using (var session = Open(bmc, s => s.FullRefreshIntervalTicks = 1))
            {
                Thread.Sleep(4000);
                bmc.HostOn = true;
                Thread.Sleep(300);
                long before = session.FramesDecoded;
                Thread.Sleep(2000);
                long frames = session.FramesDecoded - before;
                // Hundreds on loopback; the bug gave about two. Low enough for a slow CI runner.
                Assert.True(frames > 20, $"only {frames} frames in 2s after the host came on");
            }
        }

        [Fact]
        public void No_Signal_Replies_Do_Not_Loop_Requests()
        {
            // Topping the pipeline up after a no-signal reply would request and reply as fast as the
            // link allows while the host is off. Requests stay at the timer's pace instead.
            using (var bmc = new FakeBmc())
            using (var session = Open(bmc, s => s.FullRefreshIntervalTicks = 1))
            {
                Thread.Sleep(3000);
                Assert.True(bmc.Requests < 20, $"{bmc.Requests} requests in 3s with the host off");
            }
        }

        [Fact]
        public void User_List_Follows_Joins_And_Leaves()
        {
            // The record sequence an X10 board sent (addresses changed): the list on connect, a
            // web-logout notice, a full resend when someone joined, and one record when they left.
            using (var bmc = new FakeBmc())
            using (var session = Open(bmc))
            {
                WaitFor(() => bmc.Requests > 0);
                bmc.Push(FakeBmc.Privilege(3, 1, "562 ADMIN 10.0.0.46"), FakeBmc.Privilege(2, 1, "328 ADMIN 10.0.0.46"),
                         FakeBmc.Privilege(1, 1, "252 ADMIN 10.0.0.92"));
                Assert.True(WaitFor(() => session.Sessions.Count == 3));

                bmc.Push(FakeBmc.Privilege(1, 3, "Kill Sesssion Due to Web Logout"));
                Assert.True(WaitFor(() => session.LastNotice == PrivilegeRecordType.WebLogout));
                Assert.Equal(3, session.Sessions.Count);   // a notice, not a session

                bmc.Push(FakeBmc.Privilege(4, 1, "562 ADMIN 10.0.0.46"), FakeBmc.Privilege(3, 1, "328 ADMIN 10.0.0.46"),
                         FakeBmc.Privilege(2, 1, "252 ADMIN 10.0.0.92"), FakeBmc.Privilege(1, 1, "709 ADMIN 10.0.0.92"),
                         // then the newcomer again, twice, on its own
                         FakeBmc.Privilege(1, 1, "709 ADMIN 10.0.0.92"), FakeBmc.Privilege(1, 1, "709 ADMIN 10.0.0.92"));
                Thread.Sleep(300);
                Assert.True(WaitFor(() => session.Sessions.Count == 4),
                    "list after a join: " + string.Join(" | ", session.Sessions));

                bmc.Push(FakeBmc.Privilege(1, 2, "709 ADMIN 10.0.0.92"));
                Assert.True(WaitFor(() => session.Sessions.Count == 3),
                    "list after a leave: " + string.Join(" | ", session.Sessions));
                Assert.DoesNotContain(session.Sessions, e => e.StartsWith("709 "));
            }
        }

        [Fact]
        public void A_Silent_BMC_Fails_The_Handshake_Instead_Of_Hanging()
        {
            using (var bmc = new FakeBmc(silent: true))
            {
                var options = bmc.Options();
                options.HandshakeTimeoutMs = 700;
                var session = new KvmVideoSession(options);
                var open = System.Threading.Tasks.Task.Run(() => session.Open());
                bool finished = ((System.IAsyncResult)open).AsyncWaitHandle.WaitOne(5000);
                session.Dispose();   // ends a hung attempt, so the test cannot hang
                Assert.True(finished, "the handshake was still waiting after 5s");
                Assert.True(open.IsFaulted, "the handshake should have failed");
            }
        }

        [Fact]
        public void A_Refused_Connection_Reports_The_Real_Cause()
        {
            // A port that was just free: nothing listens, so the connection is refused.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var session = new KvmVideoSession(new KvmConnectionOptions { Host = "127.0.0.1", Port = port });
            var ex = Assert.ThrowsAny<Exception>(() => session.Open());
            Assert.IsType<SocketException>(ex);
            Assert.DoesNotContain("One or more errors", ex.Message);
            session.Dispose();
        }

        [Fact]
        public void Manual_Refresh_Is_Sent_By_The_Input_Thread()
        {
            using (var bmc = new FakeBmc())
            using (var session = Open(bmc, s => { s.VideoPaused = true; s.PausedCheckSeconds = 0; }))
            {
                WaitFor(() => bmc.FullRequests >= 1);   // the startup keyframe
                Thread.Sleep(300);
                int before = bmc.FullRequests;
                session.RequestFullRefresh();
                Assert.True(WaitFor(() => bmc.FullRequests == before + 1));
            }
        }

        [Fact]
        public void A_Rectangle_That_Cannot_Be_Decoded_Is_Skipped_And_Reported()
        {
            using (var bmc = new FakeBmc())
            using (var faulted = new ManualResetEventSlim())
            using (var session = Open(bmc))
            {
                session.Faulted += (s, e) => faulted.Set();
                WaitFor(() => bmc.Requests > 0);
                // An ASPEED rectangle with an unknown stream marker.
                bmc.Push(FakeBmc.Update(0x57, new byte[] { 0, 0, 0x12, 0x34, 0, 0, 0, 0, 0, 0, 0, 0 }));
                Assert.True(WaitFor(() => session.LastDecodeError != null), "no decode error recorded");
                Assert.False(faulted.IsSet, "a bad rectangle ended the session");

                bmc.HostOn = true;          // a good frame clears it
                session.RequestFullRefresh();
                Assert.True(WaitFor(() => session.LastDecodeError == null));
            }
        }

        private static KvmVideoSession Open(FakeBmc bmc, Action<KvmVideoSession> configure = null)
        {
            var session = new KvmVideoSession(bmc.Options()) { LogInput = false };
            configure?.Invoke(session);
            session.Open();
            session.StartPump();
            return session;
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

    /// <summary>The privilege-record list logic on its own.</summary>
    public class SessionListBuilderTests
    {
        private const uint Present = 1, Ended = 2, Logout = 3;

        [Fact]
        public void A_Burst_Completes_At_Index_One()
        {
            var b = new SessionListBuilder();
            Assert.False(b.Add(2, Present, "1 a x", out _));
            Assert.True(b.Add(1, Present, "2 b y", out var list));
            Assert.Equal(new[] { "1 a x", "2 b y" }, list);
        }

        [Fact]
        public void An_Ended_Record_Removes_That_Session()
        {
            var b = new SessionListBuilder();
            b.Add(2, Present, "1 a x", out _);
            b.Add(1, Present, "2 b y", out _);
            Assert.True(b.Add(1, Ended, "2 b y", out var list));
            Assert.Equal(new[] { "1 a x" }, list);
        }

        [Fact]
        public void Notices_Leave_The_List_Alone()
        {
            var b = new SessionListBuilder();
            b.Add(1, Present, "1 a x", out _);
            Assert.False(b.Add(1, Logout, "Kill Sesssion Due to Web Logout", out _));
            Assert.False(b.Add(1, 7, "", out _));
        }

        [Fact]
        public void A_Lone_Record_Updates_One_Entry_Rather_Than_Replacing_The_List()
        {
            var b = new SessionListBuilder();
            b.Add(2, Present, "1 a x", out _);
            b.Add(1, Present, "2 b y", out _);
            Assert.False(b.Add(1, Present, "2 b y", out _));          // the repeat of a newcomer
            Assert.True(b.Add(1, Present, "3 c z", out var list));    // a session not yet listed
            Assert.Equal(new[] { "1 a x", "2 b y", "3 c z" }, list);
        }

        [Fact]
        public void An_Unchanged_Resend_Is_Not_A_Change()
        {
            var b = new SessionListBuilder();
            b.Add(1, Present, "1 a x", out _);
            Assert.False(b.Add(1, Present, "1 a x", out _));
        }

        [Fact]
        public void Ending_An_Unknown_Session_Is_Not_A_Change()
        {
            var b = new SessionListBuilder();
            b.Add(1, Present, "1 a x", out _);
            Assert.False(b.Add(1, Ended, "9 z z", out _));
        }
    }

    public class ArmingUrlTests
    {
        [Theory]
        [InlineData("10.0.0.5", true, 0, "https://10.0.0.5:443")]
        [InlineData("bmc.example", false, 0, "http://bmc.example:80")]
        [InlineData("fd00::5", true, 0, "https://[fd00::5]:443")]
        [InlineData("[fd00::5]", true, 8443, "https://[fd00::5]:8443")]
        public void The_Web_Root_Is_A_Valid_Url(string host, bool https, int port, string expected)
        {
            Assert.Equal(expected, BmcArmingClient.WebBaseUrl(host, https, port));
        }
    }
}

namespace ATENtion.Tests
{
    /// <summary>Host power from the BMC's status reply, and the checks that use it.</summary>
    public class HostStatusTests
    {
        [Fact]
        public void Power_Comes_From_The_Status_Reply_Even_Without_A_Picture()
        {
            // On, but no video: the case the video signal alone cannot tell from off.
            using (var bmc = new FakeBmc { HostStatusPower = 1 })
            using (var session = Open(bmc))
            {
                Assert.True(Wait(() => session.HostPower == HostPowerState.On));
                Assert.True(Wait(() => session.VideoSignal == VideoSignalState.Absent));
                Assert.True(session.HostStatusSupported);
            }
        }

        [Fact]
        public void A_Powered_Off_Host_Reads_As_Off()
        {
            using (var bmc = new FakeBmc { HostStatusPower = 0 })
            using (var session = Open(bmc))
                Assert.True(Wait(() => session.HostPower == HostPowerState.Off));
        }

        [Fact]
        public void A_Hidden_Tab_Checks_With_The_Status_Query_When_The_BMC_Answers_It()
        {
            using (var bmc = new FakeBmc())
            using (var session = Open(bmc, s => { s.VideoPaused = true; s.PausedCheckSeconds = 1; }))
            {
                Assert.True(Wait(() => session.HostStatusSupported));
                int fullAfterStart = bmc.FullRequests, statusAfterStart = bmc.StatusQueries;
                Thread.Sleep(3500);
                Assert.Equal(fullAfterStart, bmc.FullRequests);
                Assert.True(bmc.StatusQueries >= statusAfterStart + 2, $"{bmc.StatusQueries - statusAfterStart} status checks");
            }
        }

        [Fact]
        public void A_Hidden_Tab_Falls_Back_To_Full_Frames_When_Status_Goes_Unanswered()
        {
            using (var bmc = new FakeBmc { HostStatusPower = -1 })
            using (var session = Open(bmc, s => { s.VideoPaused = true; s.PausedCheckSeconds = 1; }))
            {
                Assert.True(Wait(() => bmc.FullRequests >= 1));
                Thread.Sleep(300);
                int fullAfterStart = bmc.FullRequests;
                Thread.Sleep(3500);
                Assert.False(session.HostStatusSupported);
                Assert.True(bmc.FullRequests >= fullAfterStart + 2, $"{bmc.FullRequests - fullAfterStart} full checks");
            }
        }

        [Fact]
        public void A_Visible_Tab_Keeps_Asking_For_Status()
        {
            using (var bmc = new FakeBmc())
            using (var session = Open(bmc, s => s.HostStatusSeconds = 1))
            {
                Assert.True(Wait(() => bmc.StatusQueries >= 3, 5000), $"{bmc.StatusQueries} queries");
            }
        }

        [Fact]
        public void The_Frame_Rate_Limit_Is_Honoured()
        {
            using (var bmc = new FakeBmc { HostOn = true })
            using (var session = Open(bmc, s => s.MinFrameIntervalMs = 100))
            {
                Thread.Sleep(500);
                long before = session.FramesDecoded;
                Thread.Sleep(2000);
                long frames = session.FramesDecoded - before;
                // About 20 at 10 per second; unlimited is hundreds. The floor only proves frames flow.
                Assert.InRange(frames, 4, 30);
            }
        }

        private static KvmVideoSession Open(FakeBmc bmc, Action<KvmVideoSession> configure = null)
        {
            var session = new KvmVideoSession(bmc.Options()) { LogInput = false };
            configure?.Invoke(session);
            session.Open();
            session.StartPump();
            return session;
        }

        private static bool Wait(Func<bool> condition, int timeoutMs = 3000)
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

    /// <summary>ASPEED block maps become the rectangles that changed.</summary>

}

namespace ATENtion.Tests
{
    /// <summary>What switching tabs relies on: MainWindow.SwitchTo releases held input on the tab being left.</summary>
    public class TabSwitchRegressionTests
    {
        [Fact]
        public void Releasing_Held_Input_Sends_An_Up_For_Every_Key_Still_Down()
        {
            // Hold Shift and A on one tab, switch away: both must come up on that host, not later on
            // whichever tab is visible when the keys are physically released.
            using (var bmc = new FakeBmc())
            using (var session = Open(bmc))
            {
                session.SendKey(0xE1, true);     // left Shift
                session.SendKey(0x04, true);     // A
                session.SendKey(0x05, true);     // B, released normally
                session.SendKey(0x05, false);
                session.ReleaseHeldKeys();
                Assert.True(Wait(() => bmc.Keys.Count == 6), $"{bmc.Keys.Count} key events");
                var ups = bmc.Keys.Skip(4).ToList();
                Assert.All(ups, k => Assert.False(k.Down));
                Assert.Equal(new uint[] { 0x04, 0xE1 }, ups.Select(k => k.Usage).OrderBy(u => u).ToArray());

                session.ReleaseHeldKeys();       // nothing left held: nothing more sent
                Thread.Sleep(300);
                Assert.Equal(6, bmc.Keys.Count);
            }
        }

        private static KvmVideoSession Open(FakeBmc bmc, Action<KvmVideoSession> configure = null)
        {
            var session = new KvmVideoSession(bmc.Options()) { LogInput = false };
            configure?.Invoke(session);
            session.Open();
            session.StartPump();
            return session;
        }

        private static bool Wait(Func<bool> condition, int timeoutMs = 3000)
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
