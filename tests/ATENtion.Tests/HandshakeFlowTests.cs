using System.IO;
using System.Text;
using ATENtion.Core.Crypto;
using ATENtion.Core.Net;
using ATENtion.Core.Protocol;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the end-to-end RFB handshake against a scripted server.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="RfbHandshake"/> driven over a <see cref="FakeDuplexStream"/>
    /// parses the version, security type, and ServerInit, and writes the correct bytes back: the version
    /// echo, the chosen security type, the token in both 24-byte credential fields, and the ClientInit
    /// shared-flag. A failed SecurityResult raises <see cref="RfbAuthException"/> with the server's reason.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the handshake sequence.
    /// </para>
    /// </remarks>
    public class HandshakeFlowTests
    {
        private static byte[] BuildServerScript()
        {
            var ms = new MemoryStream();
            var w = new BufferedRfbStream(ms);
            w.WriteBytes(Encoding.ASCII.GetBytes("RFB 003.008\n")); // ProtocolVersion
            w.WriteU8(1);                                           // 1 security type
            w.WriteU8(16);                                          // type 16
            w.WriteBytes(new byte[24]);                             // 24-byte challenge
            w.WriteU32BE(0);                                        // SecurityResult = OK
            w.WriteU16BE(1024);                                     // ServerInit width
            w.WriteU16BE(768);                                      // ServerInit height
            w.WriteBytes(new byte[16]);                             // pixel format
            w.WriteU32BE(4);                                        // name length
            w.WriteBytes(Encoding.ASCII.GetBytes("PVE2"));          // name
            w.WriteBytes(new byte[12]);                             // ATEN ServerInit tail
            return ms.ToArray();
        }

        [Fact]
        public void Full_Handshake_Parses_Server_And_Sends_Credentials()
        {
            var transport = new FakeDuplexStream(BuildServerScript());
            var stream = new BufferedRfbStream(transport);

            var session = new RfbHandshake().Run(stream, token: "abcd", crypto: new RfbkmCrypto());

            Assert.Equal(3, session.Version.Major);
            Assert.Equal(8, session.Version.Minor);
            Assert.Equal(16, session.SecurityType);
            Assert.Equal(1024, session.ServerInit.Width);
            Assert.Equal(768, session.ServerInit.Height);
            Assert.Equal("PVE2", session.ServerInit.Name);

            // What the client sent: version echo, chosen type, 2x24 cred fields, ClientInit 0.
            byte[] sent = transport.Written;
            string versionEcho = Encoding.ASCII.GetString(sent, 0, 12);
            Assert.Equal("RFB 003.008\n", versionEcho);
            Assert.Equal(16, sent[12]);            // chosen security type

            // username field (24 bytes) = "abcd" + NUL padding
            Assert.Equal((byte)'a', sent[13]);
            Assert.Equal((byte)'b', sent[14]);
            Assert.Equal((byte)'c', sent[15]);
            Assert.Equal((byte)'d', sent[16]);
            for (int i = 17; i < 13 + 24; i++) Assert.Equal(0, sent[i]);
            // password field begins right after (also "abcd"...)
            Assert.Equal((byte)'a', sent[13 + 24]);
            // ClientInit shared-flag (0) is the final byte.
            Assert.Equal(0, sent[13 + 48]);
            Assert.Equal(13 + 48 + 1, sent.Length);
        }

        [Fact]
        public void Failed_Auth_Throws_With_Reason()
        {
            var ms = new MemoryStream();
            var w = new BufferedRfbStream(ms);
            w.WriteBytes(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            w.WriteU8(1);
            w.WriteU8(2);
            w.WriteBytes(new byte[24]);      // challenge
            w.WriteU32BE(1);                 // SecurityResult = FAIL
            w.WriteU32BE(13);                // reason length
            w.WriteBytes(Encoding.UTF8.GetBytes("bad password!"));

            var stream = new BufferedRfbStream(new FakeDuplexStream(ms.ToArray()));

            var ex = Assert.Throws<RfbAuthException>(
                () => new RfbHandshake().Run(stream, "wrongtoken", new RfbkmCrypto()));
            Assert.Equal(1u, ex.ResultCode);
            Assert.Contains("bad password", ex.Reason);
        }
    }
}
