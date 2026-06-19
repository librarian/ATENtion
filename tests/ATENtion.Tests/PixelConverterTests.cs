using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies each pixel-conversion mode produces the expected BGRA output.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="PixelConverter"/> resolves 8-bit and 4-bit palette indices
    /// to BGRA (low nibble first for 4-bit), expands RGB555 channels correctly, passes 32-bit pixels
    /// through, and forces the alpha opaque in every mode.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the pixel writer.
    /// </para>
    /// </remarks>
    public class PixelConverterTests
    {
        private static AtenPalette PaletteWith(int index, byte b, byte g, byte r, byte a)
        {
            var raw = new byte[AtenPalette.ByteSize];
            raw[index * 4 + 0] = b;
            raw[index * 4 + 1] = g;
            raw[index * 4 + 2] = r;
            raw[index * 4 + 3] = a;
            var pal = new AtenPalette();
            pal.Load(raw, 0);
            return pal;
        }

        [Fact]
        public void Palette8_Emits_Bgra_From_Palette()
        {
            var pal = PaletteWith(7, b: 0x11, g: 0x22, r: 0x33, a: 0x44);
            var dst = new byte[4];

            int written = PixelConverter.Palette8(new byte[] { 7 }, 0, 1, pal, dst, 0);

            Assert.Equal(4, written);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0xFF }, dst); // A forced opaque
        }

        [Fact]
        public void Palette4_Emits_Two_Pixels_LowNibbleFirst()
        {
            var raw = new byte[AtenPalette.ByteSize];
            // index 1 -> blue, index 2 -> red
            raw[1 * 4 + 0] = 0xFF; // B
            raw[2 * 4 + 2] = 0xFF; // R
            var pal = new AtenPalette();
            pal.Load(raw, 0);

            var dst = new byte[8];
            // byte 0x21 -> low nibble 1, high nibble 2
            int written = PixelConverter.Palette4(new byte[] { 0x21 }, 0, 1, pal, dst, 0);

            Assert.Equal(8, written);
            Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0xFF }, new[] { dst[0], dst[1], dst[2], dst[3] }); // blue
            Assert.Equal(new byte[] { 0x00, 0x00, 0xFF, 0xFF }, new[] { dst[4], dst[5], dst[6], dst[7] }); // red
        }

        [Fact]
        public void Rgb555_Pure_Blue()
        {
            // v0 = 0x1F (low 5 bits set), v1 = 0x00
            var dst = new byte[4];
            int written = PixelConverter.Rgb555(new byte[] { 0x1F, 0x00 }, 0, 2, dst, 0);

            Assert.Equal(4, written);
            Assert.Equal(0xF8, dst[0]); // B = 0x1F << 3
            Assert.Equal(0x00, dst[1]); // G
            Assert.Equal(0x00, dst[2]); // R
            Assert.Equal(0xFF, dst[3]); // A
        }

        [Fact]
        public void Rgb32_Passthrough_Bgr()
        {
            var dst = new byte[4];
            int written = PixelConverter.Rgb32(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, 0, 4, dst, 0);

            Assert.Equal(4, written);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xFF }, dst);
        }
    }
}
