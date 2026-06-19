using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the bit-plane transpose: the plane-to-channel-to-bit mapping and pixel selection.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down the mechanism of <see cref="BitPlaneDeinterleave"/> with one byte per plane:
    /// that specific planes set specific channel bits of a chosen pixel (channel bit k from plane
    /// groupBase + 7 - k, with Green 0..7, Blue 8..15, Red 24..31), that the 16-bit path produces the
    /// RGB555 word bytes, and that a set bit selects the correct pixel.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the transpose. The mechanism is tested. The
    /// channel-to-group assignment is RE-derived and not yet hardware-confirmed.
    /// </para>
    /// </remarks>
    public class BitPlaneTests
    {
        // Mechanism tests: with bytesPerPlane = 1 (8 pixels), each plane's byte 0 holds
        // one bit per pixel. Verify the plane->channel->bit transpose for pixel 0.
        // Mapping: channel bit k <- plane (groupBase + 7 - k); Green=0..7, Blue=8..15, Red=24..31.

        [Fact]
        public void Plane15_Sets_Blue_Bit0_Of_Pixel0()
        {
            var planes = new byte[32];     // 32 planes x 1 byte
            planes[15] = 0x01;             // plane 15, byte 0, bit 0 (pixel 0)
            var dst = new byte[8 * 4];

            BitPlaneDeinterleave.Decode24(planes, 8, dst, 0);

            Assert.Equal(0x01, dst[0]); // B bit0
            Assert.Equal(0x00, dst[1]); // G
            Assert.Equal(0x00, dst[2]); // R
            Assert.Equal(0xFF, dst[3]); // A
        }

        [Fact]
        public void Plane0_Sets_Green_Bit7_And_Plane24_Sets_Red_Bit7()
        {
            var planes = new byte[32];
            planes[0] = 0x01;   // plane 0  -> Green bit7 of pixel 0
            planes[24] = 0x01;  // plane 24 -> Red   bit7 of pixel 0
            var dst = new byte[8 * 4];

            BitPlaneDeinterleave.Decode24(planes, 8, dst, 0);

            Assert.Equal(0x00, dst[0]); // B
            Assert.Equal(0x80, dst[1]); // G bit7
            Assert.Equal(0x80, dst[2]); // R bit7
        }

        [Fact]
        public void Decode16_Maps_Planes_To_Rgb555_Words()
        {
            var planes = new byte[16];
            planes[15] = 0x01; // -> v0 bit0
            planes[0] = 0x01;  // -> v1 bit7
            var dst16 = new byte[8 * 2];

            BitPlaneDeinterleave.Decode16(planes, 8, dst16, 0);

            Assert.Equal(0x01, dst16[0]); // v0 (pixel 0)
            Assert.Equal(0x80, dst16[1]); // v1 (pixel 0)
        }

        [Fact]
        public void Bit_Selects_Correct_Pixel()
        {
            var planes = new byte[32];
            planes[15] = 0x04;  // plane 15, byte 0, bit 2 -> pixel 2, Blue bit0
            var dst = new byte[8 * 4];

            BitPlaneDeinterleave.Decode24(planes, 8, dst, 0);

            Assert.Equal(0x00, dst[0 * 4 + 0]); // pixel 0 B
            Assert.Equal(0x01, dst[2 * 4 + 0]); // pixel 2 B bit0
        }
    }
}
