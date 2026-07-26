using System;
using ATENtion.Core.Protocol;
using Xunit;

namespace ATENtion.Tests
{
    public class ScreenInfoRequestTests
    {
        [Fact]
        public void Builds_Enhanced_Text_High_Quality_Request()
        {
            Assert.Equal(
                new byte[] { 0x32, 0x00, 0x0B, 0x01, 0xBC },
                ScreenInfoRequest.Build(11, ScreenInfoRequest.EnhancedTextMode));
        }

        [Fact]
        public void Rejects_Unknown_Quality_And_Mode()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ScreenInfoRequest.Build(12, ScreenInfoRequest.EnhancedTextMode));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ScreenInfoRequest.Build(11, 420));
        }
    }
}
