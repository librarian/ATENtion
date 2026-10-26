using System;
using System.IO;
using System.Reflection;
using ATENtion.Core.Net;
using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    public class RequestAccountingTests
    {
        [Fact]
        public void A_Reply_Before_Send_Returns_Does_Not_Leave_A_Phantom_Request()
        {
            using (var session = new KvmVideoSession(new KvmConnectionOptions()))
            using (var transport = new CallbackStream())
            {
                Attach(session, transport);
                // Force the receive-side accounting to run while the send is still in Flush.
                // No sleeps or scheduler-dependent loopback timing are needed.
                transport.OnFlush = () => Call(session, "ReleaseOneRequest");
                Call(session, "SendUpdate", true);
                Assert.Equal(0, Outstanding(session));
                Call(session, "SendUpdate", true);
                Assert.Equal(0, Outstanding(session));
                Assert.Equal(20, transport.Length); // two framebuffer requests
            }
        }

        [Fact]
        public void A_Failed_Send_Does_Not_Leave_A_Phantom_Request()
        {
            using (var session = new KvmVideoSession(new KvmConnectionOptions()))
            using (var transport = new CallbackStream())
            {
                Attach(session, transport);
                transport.OnFlush = () => throw new IOException("send failed");
                var error = Assert.Throws<TargetInvocationException>(() => Call(session, "SendUpdate", true));
                Assert.IsType<IOException>(error.InnerException);
                Assert.Equal(0, Outstanding(session));
            }
        }

        // Exercise the actual send and receive accounting without adding production-only test hooks.
        private static void Attach(KvmVideoSession session, Stream transport)
        {
            var connection = (KvmConnection)typeof(KvmVideoSession)
                .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
            typeof(KvmConnection).GetProperty("Stream").SetValue(connection, new BufferedRfbStream(transport));
            typeof(KvmVideoSession).GetProperty("Decoder").SetValue(session, new AtenTileDecoder(64, 48));
        }

        private static object Call(KvmVideoSession session, string method, params object[] args) =>
            typeof(KvmVideoSession).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(session, args);

        private static int Outstanding(KvmVideoSession session) =>
            (int)typeof(KvmVideoSession).GetField("_outstanding", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(session);

        private sealed class CallbackStream : MemoryStream
        {
            public Action OnFlush;
            public override void Flush() => OnFlush?.Invoke();
        }
    }
}
