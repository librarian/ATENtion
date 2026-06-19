using System.IO;
using System.Text;
using ATENtion.Core.Net;
using ATENtion.Core.Protocol;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the individual handshake steps: version, security negotiation, and ServerInit.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="ProtocolVersion"/> round-trips the banner, that
    /// <see cref="RfbSecurity"/> selects the last offered type and replies with it (and throws on an
    /// empty list), and that <see cref="ServerInit"/> reads the dimensions and name past the ATEN trailer.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the handshake messages.
    /// </para>
    /// </remarks>
    public class HandshakeTests
    {
        [Fact]
        public void ProtocolVersion_Parses_And_Formats()
        {
            var ms = new MemoryStream(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            var v = ProtocolVersion.Read(new BufferedRfbStream(ms));
            Assert.Equal(3, v.Major);
            Assert.Equal(8, v.Minor);
            Assert.Equal("RFB 003.008\n", v.ToString());
        }

        [Fact]
        public void Security_Negotiation_Picks_Last_And_Replies()
        {
            // Server offers 3 types: 1, 2, 16. Native client picks the last (16).
            var ms = new MemoryStream();
            ms.Write(new byte[] { 3, 1, 2, 16 }, 0, 4);
            ms.Position = 0;

            byte chosen = RfbSecurity.Negotiate(new BufferedRfbStream(ms));

            Assert.Equal(16, chosen);
            // The reply byte was written right after the 4 bytes consumed.
            Assert.Equal(16, ms.ToArray()[4]);
        }

        [Fact]
        public void Empty_Security_List_Throws()
        {
            var ms = new MemoryStream(new byte[] { 0 });
            Assert.Throws<RfbProtocolException>(() => RfbSecurity.Negotiate(new BufferedRfbStream(ms)));
        }

        [Fact]
        public void ServerInit_Reads_Dimensions_And_Name()
        {
            var ms = new MemoryStream();
            var w = new BufferedRfbStream(ms);
            w.WriteU16BE(1024);             // width
            w.WriteU16BE(768);              // height
            w.WriteBytes(new byte[16]);     // pixel format
            w.WriteU32BE(4);                // name length
            w.WriteBytes(Encoding.ASCII.GetBytes("PVE2"));
            w.WriteBytes(new byte[12]);     // ATEN ServerInit tail
            ms.Position = 0;

            var init = ServerInit.Read(new BufferedRfbStream(ms));

            Assert.Equal(1024, init.Width);
            Assert.Equal(768, init.Height);
            Assert.Equal("PVE2", init.Name);
        }
    }
}
