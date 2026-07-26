using System;
using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    public sealed class AspeedPacketHeaderTests
    {
        [Fact]
        public void ParsesCapturedAst420Wrapper()
        {
            byte[] packet = { 4, 7, 0x01, 0xa6, 0, 0, 0, 0, 0, 0, 0, 0 };

            AspeedPacketHeader header = AspeedPacketHeader.Parse(packet);

            Assert.Equal((byte)4, header.Selector);
            Assert.Equal((byte)7, header.AdvanceSelector);
            Assert.Equal(1, header.Mode420);
        }

        [Fact]
        public void ParsesAst444Wrapper()
        {
            byte[] packet = { 7, 7, 0x01, 0xbc, 0, 0, 0, 0, 0, 0, 0, 0 };

            Assert.Equal(0, AspeedPacketHeader.Parse(packet).Mode420);
        }

        [Fact]
        public void RejectsUnknownMarker()
        {
            byte[] packet = { 4, 7, 0x12, 0x34, 0, 0, 0, 0, 0, 0, 0, 0 };

            Assert.Throws<UnsupportedEncodingException>(() => AspeedPacketHeader.Parse(packet));
        }

        [Fact]
        public void RejectsShortPacket()
        {
            Assert.Throws<ArgumentException>(() => AspeedPacketHeader.Parse(new byte[11]));
        }
    }
}
