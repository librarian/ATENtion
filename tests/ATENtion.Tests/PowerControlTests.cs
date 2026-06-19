using ATENtion.Core.Protocol;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the two-byte OEM power record for each power command.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="PowerControl"/> builds <c>[0x1a][code]</c> with the code
    /// byte matching each <see cref="PowerCommand"/> (Off 0, On 1, Reset 2, SoftOff 3).
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the power record.
    /// </para>
    /// </remarks>
    public class PowerControlTests
    {
        [Theory]
        [InlineData(PowerCommand.Off, 0)]
        [InlineData(PowerCommand.On, 1)]
        [InlineData(PowerCommand.Reset, 2)]
        [InlineData(PowerCommand.SoftOff, 3)]
        public void Builds_OEM_Power_Record(PowerCommand cmd, byte expectedCode)
        {
            byte[] frame = PowerControl.Build(cmd);
            Assert.Equal(2, frame.Length);
            Assert.Equal(0x1a, frame[0]);
            Assert.Equal(expectedCode, frame[1]);
        }
    }
}
