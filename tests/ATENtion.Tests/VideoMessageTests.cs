using System.IO;
using ATENtion.Core.Net;
using ATENtion.Core.Protocol;
using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the FramebufferUpdate envelope parse, including the empty-update case.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="FramebufferUpdate"/> reads a single-rectangle update and
    /// hands back a decodable payload, and that a zero-rectangle update yields no rectangles rather than
    /// desynchronising the stream.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the FramebufferUpdate framing.
    /// </para>
    /// </remarks>
    public class VideoMessageTests
    {
        [Fact]
        public void FramebufferUpdate_With_One_Rect_Wraps_Decodable_Payload()
        {
            const int w = 8, h = 4;

            var palette = new byte[AtenPalette.ByteSize];
            for (int i = 0; i < AtenPalette.EntryCount; i++) { palette[i * 4 + 2] = (byte)i; palette[i * 4 + 3] = 0xFF; }
            var indices = new byte[w * h];
            for (int p = 0; p < indices.Length; p++) indices[p] = (byte)p;
            byte[] payload = AtenPacketBuilder.BuildPalette8Keyframe(palette, indices);

            var ms = new MemoryStream();
            var wr = new BufferedRfbStream(ms);
            // FramebufferUpdate body (type byte consumed separately): [pad][numRects=1][rect]
            wr.WriteU8(0);                  // pad
            wr.WriteU16BE(1);               // numRects
            wr.WriteU16BE(0); wr.WriteU16BE(0); wr.WriteU16BE(w); wr.WriteU16BE(h); // x,y,w,h
            wr.WriteU32BE(0);               // encoding
            wr.WriteU32BE(0);               // mode
            wr.WriteU32BE((uint)payload.Length); // dataLen
            wr.WriteBytes(payload);
            ms.Position = 0;

            var fbu = FramebufferUpdate.Read(new BufferedRfbStream(ms));

            Assert.Equal(1, fbu.RectCount);
            Assert.Equal(payload.Length, fbu.Rects[0].Payload.Length);

            var decoder = new AtenTileDecoder(w, h);
            decoder.DecodePacket(fbu.Rects[0].Payload);
            for (int p = 0; p < indices.Length; p++)
                Assert.Equal(indices[p], decoder.Frame.Pixels[p * 4 + 2]); // R channel == index
        }

        [Fact]
        public void Empty_FramebufferUpdate_Yields_No_Rects()
        {
            var ms = new MemoryStream();
            var wr = new BufferedRfbStream(ms);
            wr.WriteU8(0);      // pad
            wr.WriteU16BE(0);   // numRects = 0  (no changes)
            ms.Position = 0;

            var fbu = FramebufferUpdate.Read(new BufferedRfbStream(ms));
            Assert.Equal(0, fbu.RectCount);
            Assert.Empty(fbu.Rects);
        }
    }
}
