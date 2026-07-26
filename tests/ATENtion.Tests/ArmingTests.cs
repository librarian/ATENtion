using ATENtion.Core.Net;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>
    /// Verifies the BMC web-arming parsers: the JNLP argument extraction and the CSRF token scrape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="BmcArmingClient"/> reads the token, port, and certificate
    /// from a launch JNLP by argument position, handles an empty JNLP, and extracts the CSRF token from
    /// the BMC top menu, returning null when none is present.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the arming flow reversed.
    /// </para>
    /// </remarks>
    public class ArmingTests
    {
        [Fact]
        public void ParseJnlp_Extracts_Token_Port_And_Certificate()
        {
            string jnlp = @"<jnlp><application-desc main-class='tw.com.aten.ikvm.KVMMain'>
                <argument>172.16.16.139</argument>
                <argument>wulhxykewtfeaeyy</argument>
                <argument>different-password==</argument>
                <argument>PVE2-IPMI</argument>
                <argument>63630</argument>
                <argument>63631</argument>
                <argument>0</argument>
                <argument>0</argument>
                <argument>1</argument>
                <argument>5900</argument>
                <argument>623</argument>
                <argument>1</argument>
                <argument>-----BEGIN CERTIFICATE-----
MIIDExamplecert==
-----END CERTIFICATE-----</argument>
                </application-desc></jnlp>";

            var r = BmcArmingClient.ParseJnlp(jnlp);

            Assert.Equal("wulhxykewtfeaeyy", r.KvmUsername);
            Assert.Equal("different-password==", r.KvmPassword);
            Assert.Equal(63630, r.KvmPort);
            Assert.Equal(63631, r.VirtualMediaLocalPort);
            Assert.Equal(623, r.VirtualMediaPort);
            Assert.Equal(1, r.VirtualMediaEnabled);
            Assert.NotNull(r.ServerCertificatePem);
            Assert.Contains("BEGIN CERTIFICATE", r.ServerCertificatePem);
            Assert.Contains("END CERTIFICATE", r.ServerCertificatePem);
        }

        [Fact]
        public void ParseJnlp_Handles_Empty()
        {
            var r = BmcArmingClient.ParseJnlp("");
            Assert.Null(r.KvmUsername);
            Assert.Null(r.KvmPassword);
        }

        [Theory]
        [InlineData("<script>SmcCsrfInsert('CSRF_TOKEN', \"abc123def\");</script>", "abc123def")]
        [InlineData("var x; SmcCsrfInsert(\"CSRF_TOKEN\",\"TOKVAL\")", "TOKVAL")]
        [InlineData("SmcCsrfInsert (\"CSRF_TOKEN\", \"abc/DEF+ghi==\");", "abc/DEF+ghi==")]
        public void ExtractCsrfToken_Reads_Quoted_Value(string page, string expected)
        {
            Assert.Equal(expected, BmcArmingClient.ExtractCsrfToken(page));
        }

        [Fact]
        public void ExtractCsrfToken_Returns_Null_When_Absent()
        {
            Assert.Null(BmcArmingClient.ExtractCsrfToken("<html>no token here</html>"));
        }
    }
}
