using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the ten-byte video header parse: flags, palette presence, and the big-endian length.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="VideoPacketHeader"/> reports a palette keyframe with the
    /// right payload offset and length, reads the total length big-endian, and recognises that types
    /// (1, 0) and 5 carry a palette block.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the video header.
    /// </para>
    /// </remarks>
    public class VideoPacketHeaderTests
    {
        [Fact]
        public void Parses_Palette8_Keyframe_Header()
        {
            var palette = new byte[AtenPalette.ByteSize];
            var indices = new byte[64 * 48];
            byte[] packet = AtenPacketBuilder.BuildPalette8Keyframe(palette, indices);

            var h = VideoPacketHeader.Parse(packet);

            Assert.True(h.IsFullFrame);
            Assert.Equal((byte)AtenEncodingType.Type1, h.EncodingType);
            Assert.Equal(0, h.Subtype);
            Assert.True(h.HasPalette);
            Assert.Equal(VideoPacketHeader.PayloadOffsetWithPalette, h.PayloadOffset);
            Assert.Equal(0x40a, h.PayloadOffset);
            Assert.Equal((uint)(0x40a + indices.Length), h.TotalLength);
            Assert.Equal(indices.Length, h.PayloadLength);
        }

        [Fact]
        public void Reads_BigEndian_Length()
        {
            var packet = new byte[VideoPacketHeader.HeaderSize];
            packet[0] = 0;                 // incremental
            packet[1] = 2;                 // type 2
            packet[2] = 0;                 // subtype 0
            packet[6] = 0x00;
            packet[7] = 0x01;              // 0x00010000
            packet[8] = 0x00;
            packet[9] = 0x0A;              // ... + 0x0A => 0x0001000A

            var h = VideoPacketHeader.Parse(packet);

            Assert.False(h.IsFullFrame);
            Assert.Equal((uint)0x0001000A, h.TotalLength);
            Assert.False(h.HasPalette);
            Assert.Equal(VideoPacketHeader.HeaderSize, h.PayloadOffset);
        }

        [Fact]
        public void Type5_HasPalette()
        {
            var packet = new byte[VideoPacketHeader.HeaderSize];
            packet[1] = 5;
            var h = VideoPacketHeader.Parse(packet);
            Assert.True(h.HasPalette);
        }
    }
}
