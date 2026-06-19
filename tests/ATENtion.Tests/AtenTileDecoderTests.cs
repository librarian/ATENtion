using System.Collections.Generic;
using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the tile decoder's palette-keyframe path and its handling of unsupported encodings.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="AtenTileDecoder"/> decodes an 8-bit palette keyframe to the
    /// expected BGRA pixels and reports a full-screen dirty region, and that an encoding with no decode
    /// path raises <see cref="UnsupportedEncodingException"/>.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the decode dispatch.
    /// </para>
    /// </remarks>
    public class AtenTileDecoderTests
    {
        [Fact]
        public void Decodes_Palette8_Keyframe_Into_Bgra_Framebuffer()
        {
            const int w = 8, h = 4;

            // Palette: index i -> (B=i, G=2i, R=3i, A=0xFF)
            var palette = new byte[AtenPalette.ByteSize];
            for (int i = 0; i < AtenPalette.EntryCount; i++)
            {
                palette[i * 4 + 0] = (byte)i;
                palette[i * 4 + 1] = (byte)(i * 2);
                palette[i * 4 + 2] = (byte)(i * 3);
                palette[i * 4 + 3] = 0xFF;
            }

            var indices = new byte[w * h];
            for (int p = 0; p < indices.Length; p++)
                indices[p] = (byte)p;

            byte[] packet = AtenPacketBuilder.BuildPalette8Keyframe(palette, indices);

            var decoder = new AtenTileDecoder(w, h);
            IReadOnlyList<DirtyRect> dirty = decoder.DecodePacket(packet);

            Assert.Single(dirty);
            Assert.True(dirty[0].IsFullScreen);

            byte[] px = decoder.Frame.Pixels;
            Assert.Equal(w * h * 4, px.Length);
            for (int p = 0; p < indices.Length; p++)
            {
                byte idx = indices[p];
                Assert.Equal((byte)idx, px[p * 4 + 0]);       // B
                Assert.Equal((byte)(idx * 2), px[p * 4 + 1]); // G
                Assert.Equal((byte)(idx * 3), px[p * 4 + 2]); // R
                Assert.Equal(0xFF, px[p * 4 + 3]);            // A
            }
        }

        [Fact]
        public void Incremental_Packet_Reports_Unsupported()
        {
            var packet = new byte[VideoPacketHeader.HeaderSize];
            packet[0] = 0; // not a full frame
            packet[1] = 3; // type 3 (no bit-plane path ported)
            var decoder = new AtenTileDecoder(16, 16);
            Assert.Throws<UnsupportedEncodingException>(() => decoder.DecodePacket(packet));
        }
    }
}
