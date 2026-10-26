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

}

namespace ATENtion.Tests
{
    /// <summary>Host power from the BMC's status reply, and the checks that use it.</summary>

    /// <summary>ASPEED block maps become the rectangles that changed.</summary>

}

namespace ATENtion.Tests
{
    /// <summary>What switching tabs relies on: MainWindow.SwitchTo releases held input on the tab being left.</summary>

}
