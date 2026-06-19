using ATENtion.Core.Storage;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the virtual-media framing: the ATEN header, the CSW, and the CBW parse.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="VmediaFraming"/> builds the eight-byte data and status
    /// headers with the marker, LUN, flag, and little-endian length, reads the payload length and LUN
    /// back, builds a USB-BOT CSW with the echoed tag and residue, and parses a 31-byte CBW into its
    /// standard fields.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the USB-MSC framing.
    /// </para>
    /// </remarks>
    public class VmediaFramingTests
    {
        [Fact]
        public void BuildHeader_DataFrame_HasMarkerLunFlagAndLeLength()
        {
            byte[] h = VmediaFraming.BuildHeader(lun: 3, flag: VmediaFraming.FlagData, payloadLen: 0x1234);
            Assert.Equal(new byte[] { 0x22, 0x00, 0x03, 0x00, 0x34, 0x12, 0x00, 0x00 }, h);
        }

        [Fact]
        public void BuildHeader_StatusFrame_SetsFlagFf()
        {
            byte[] h = VmediaFraming.BuildHeader(lun: 0, flag: VmediaFraming.FlagStatus, payloadLen: 13);
            Assert.Equal(new byte[] { 0x22, 0x00, 0x00, 0xFF, 0x0D, 0x00, 0x00, 0x00 }, h);
        }

        [Fact]
        public void ReadPayloadLength_IsLittleEndian()
        {
            byte[] h = { 0x22, 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00 };
            Assert.Equal(31, VmediaFraming.ReadPayloadLength(h));
            Assert.Equal((byte)0, VmediaFraming.ReadLun(h));
        }

        [Fact]
        public void BuildCsw_MatchesUsbBotLayout()
        {
            byte[] csw = VmediaFraming.BuildCsw(tag: 0xAABBCCDD, residue: 0x10, status: 0);
            Assert.Equal(13, csw.Length);
            Assert.Equal((byte)'U', csw[0]);
            Assert.Equal((byte)'S', csw[1]);
            Assert.Equal((byte)'B', csw[2]);
            Assert.Equal((byte)'S', csw[3]);
            // tag echoed little-endian
            Assert.Equal(new byte[] { 0xDD, 0xCC, 0xBB, 0xAA }, new[] { csw[4], csw[5], csw[6], csw[7] });
            // residue little-endian
            Assert.Equal(new byte[] { 0x10, 0x00, 0x00, 0x00 }, new[] { csw[8], csw[9], csw[10], csw[11] });
            Assert.Equal((byte)0, csw[12]);
        }

        [Fact]
        public void Cbw_Parse_ReadsStandardFields()
        {
            // Build a READ(10) CBW: sig "USBC", tag, xferlen=2048, flags=0x80 (data-in), lun 0, cdb len 10.
            var p = new byte[31];
            p[0] = 0x55; p[1] = 0x53; p[2] = 0x42; p[3] = 0x43;          // USBC
            p[4] = 0x01; p[5] = 0x02; p[6] = 0x03; p[7] = 0x04;          // tag = 0x04030201
            p[8] = 0x00; p[9] = 0x08; p[10] = 0x00; p[11] = 0x00;        // xferlen = 0x800 (2048)
            p[12] = 0x80;                                                 // data-in
            p[13] = 0x00;                                                 // lun
            p[14] = 0x0A;                                                 // cdb length 10
            p[15] = 0x28; p[17] = 0x00; p[18] = 0x00; p[19] = 0x00; p[20] = 0x05; // READ(10) LBA 5
            p[22] = 0x00; p[23] = 0x01;                                   // transfer length 1 block

            var cbw = CommandBlockWrapper.Parse(p);
            Assert.Equal(VmediaFraming.CbwSignature, cbw.Signature);
            Assert.Equal(0x04030201u, cbw.Tag);
            Assert.Equal(2048u, cbw.DataTransferLength);
            Assert.True(cbw.IsDataIn);
            Assert.Equal((byte)0x28, cbw.Opcode);
            Assert.Equal((byte)10, cbw.CdbLength);
        }
    }
}
