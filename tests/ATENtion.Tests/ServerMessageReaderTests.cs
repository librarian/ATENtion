using System.IO;
using ATENtion.Core.Net;
using ATENtion.Core.Protocol;
using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the receive reader stays byte-aligned across status messages and a video frame.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="ServerMessageReader"/> consumes the status messages (0x16,
    /// 0x37, 0x3c) by their exact byte counts and then decodes a following FramebufferUpdate to the
    /// expected pixels, proving the stream never drifts out of alignment.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the receive pump and message bodies.
    /// </para>
    /// </remarks>
    public class ServerMessageReaderTests
    {
        [Fact]
        public void Consumes_Status_Messages_Then_Decodes_Video_In_Alignment()
        {
            const int w = 8, h = 4;

            // A palette8 keyframe to wrap in a FramebufferUpdate.
            var palette = new byte[AtenPalette.ByteSize];
            for (int i = 0; i < AtenPalette.EntryCount; i++) { palette[i * 4 + 2] = (byte)i; palette[i * 4 + 3] = 0xFF; }
            var indices = new byte[w * h];
            for (int p = 0; p < indices.Length; p++) indices[p] = (byte)p;
            byte[] payload = AtenPacketBuilder.BuildPalette8Keyframe(palette, indices);

            var ms = new MemoryStream();
            var wr = new BufferedRfbStream(ms);

            // 1) type 0x16 (1 byte body)
            wr.WriteU8(0x16); wr.WriteU8(0xEE);
            // 2) type 0x37 (3 byte body)
            wr.WriteU8(0x37); wr.WriteBytes(new byte[] { 1, 2, 3 });
            // 3) type 0x3c (8 byte body)
            wr.WriteU8(0x3c); wr.WriteBytes(new byte[8]);
            // 4) type 0 FramebufferUpdate wrapping the keyframe: [type][pad][numRects=1][rect]
            wr.WriteU8(RfbMessageType.FramebufferUpdate);
            wr.WriteU8(0);                       // pad
            wr.WriteU16BE(1);                    // numRects
            wr.WriteU16BE(0); wr.WriteU16BE(0); wr.WriteU16BE(w); wr.WriteU16BE(h);
            wr.WriteU32BE(0); wr.WriteU32BE(0); wr.WriteU32BE((uint)payload.Length);
            wr.WriteBytes(payload);
            ms.Position = 0;

            var stream = new BufferedRfbStream(ms);
            var decoder = new AtenTileDecoder(w, h);

            var m1 = ServerMessageReader.ConsumeOne(stream, decoder);
            var m2 = ServerMessageReader.ConsumeOne(stream, decoder);
            var m3 = ServerMessageReader.ConsumeOne(stream, decoder);
            var m4 = ServerMessageReader.ConsumeOne(stream, decoder);

            Assert.False(m1.IsFrame); Assert.Equal(0x16, m1.Type);
            Assert.False(m2.IsFrame); Assert.Equal(0x37, m2.Type);
            Assert.False(m3.IsFrame); Assert.Equal(0x3c, m3.Type);
            Assert.True(m4.IsFrame);  Assert.Equal(0x00, m4.Type);

            // Alignment held: the video frame decoded to the expected pixels.
            for (int p = 0; p < indices.Length; p++)
                Assert.Equal(indices[p], decoder.Frame.Pixels[p * 4 + 2]); // R channel == index
        }
    }
}
