using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the ATEN run-length decoder against its token grammar.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="AtenRle"/> expands a chunk of mixed tokens (literals, the
    /// 0xAA fixed run, the 0x55 escapes, and a counted run) to the exact bytes, and that the
    /// final-byte-of-a-chunk boundary rule emits a lone lead byte as a literal.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the RLE grammar.
    /// </para>
    /// </remarks>
    public class AtenRleTests
    {
        [Fact]
        public void Decodes_Mixed_Tokens_In_One_Chunk()
        {
            // Tokens (10 source bytes):
            //   0x10              -> literal 0x10
            //   0xAA 0x20         -> 0x20 0x20 0x20
            //   0x55 0x00         -> literal 0x55
            //   0x55 0x01         -> literal 0xAA
            //   0x55 0x03 0x77    -> run of 4 x 0x77
            byte[] tokens = { 0x10, 0xAA, 0x20, 0x55, 0x00, 0x55, 0x01, 0x55, 0x03, 0x77 };
            var src = new byte[4 + tokens.Length];
            src[0] = (byte)tokens.Length; // chunk byte count = 10 (LE u32)
            System.Buffer.BlockCopy(tokens, 0, src, 4, tokens.Length);

            var dst = new byte[64];
            int produced = AtenRle.Decode(src, 0, src.Length, dst);

            byte[] expected = { 0x10, 0x20, 0x20, 0x20, 0x55, 0xAA, 0x77, 0x77, 0x77, 0x77 };
            Assert.Equal(expected.Length, produced);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], dst[i]);
        }

        [Fact]
        public void Final_Chunk_Byte_Is_Literal()
        {
            // Single chunk of 1 byte: a lone 0x55 must be emitted literally (boundary rule).
            byte[] tokens = { 0x55 };
            var src = new byte[4 + tokens.Length];
            src[0] = (byte)tokens.Length;
            System.Buffer.BlockCopy(tokens, 0, src, 4, tokens.Length);

            var dst = new byte[8];
            int produced = AtenRle.Decode(src, 0, src.Length, dst);

            Assert.Equal(1, produced);
            Assert.Equal(0x55, dst[0]);
        }
    }
}
