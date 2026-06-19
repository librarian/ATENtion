using System;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// Decodes the ATEN run-length compression used inside the bit-plane video path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Expands an RLE payload into its decompressed bytes, which for the bit-plane paths
    /// are the colour planes the transpose stage then reads.
    /// </para>
    /// <para>
    /// OPERATION - The payload is a series of four-byte-aligned chunks. Each chunk begins with a
    /// little-endian u32 giving the number of source token bytes that follow, and the decoder reads
    /// tokens until that many source bytes are consumed before advancing to the next aligned chunk.
    /// Two lead bytes, 0x55 and 0xAA, introduce escapes and runs. Any other byte is a literal. A
    /// chunk whose final source byte lands on a lead byte emits it as a literal, the native
    /// "remaining == 1" boundary rule, because a multi-byte token cannot be completed there.
    /// </para>
    /// <para>
    /// WIRE FORMAT - Token grammar:
    /// <code>
    ///   0x55 0x00       literal byte 0x55
    ///   0x55 0x01       literal byte 0xAA
    ///   0x55 n   v      run of (n + 1) copies of v        (n &gt;= 2)
    ///   0xAA v          three copies of v
    ///   b               literal byte b                    (b not 0x55, 0xAA)
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Output feeds <see cref="BitPlaneDeinterleave"/>. The caller sizes the
    /// destination to hold the full decompressed image or tile.
    /// </para>
    /// <para>
    /// RESTRICTIONS - PORTED FAITHFULLY from the decompiler but not yet validated against a live
    /// capture; the chunk-count semantics are the part most in need of confirmation once a real
    /// bit-plane frame is available. The target BMC uses the type-0 path, which does not exercise
    /// this codec.
    /// </para>
    /// <para>
    /// PROVENANCE - RLE stage of the native tile decoder iKVM64.dll FUN_18000b630.
    /// </para>
    /// </remarks>
    public static class AtenRle
    {
        /// <summary>Decodes the whole RLE payload into the destination buffer.</summary>
        /// <param name="src">The buffer containing the RLE payload.</param>
        /// <param name="srcOffset">The offset in <paramref name="src"/> where the payload starts.</param>
        /// <param name="srcLength">The length of the payload, in bytes.</param>
        /// <param name="dst">The destination buffer, sized for the decompressed output.</param>
        /// <returns>The number of bytes written to <paramref name="dst"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="src"/> or <paramref name="dst"/> is null.</exception>
        public static int Decode(byte[] src, int srcOffset, int srcLength, byte[] dst)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (dst == null) throw new ArgumentNullException(nameof(dst));

            int inPos = 0;   // position within the payload, relative to srcOffset
            int outPos = 0;

            while (true)
            {
                inPos = Align4(inPos);
                if (inPos >= srcLength || inPos + 4 > srcLength)
                    break;

                int chunkBytes = src[srcOffset + inPos]
                               | src[srcOffset + inPos + 1] << 8
                               | src[srcOffset + inPos + 2] << 16
                               | src[srcOffset + inPos + 3] << 24;
                inPos += 4;
                if (chunkBytes <= 0)
                    continue;

                int consumed = 0;
                while (consumed < chunkBytes)
                {
                    int remaining = chunkBytes - consumed;
                    byte lead = src[srcOffset + inPos];

                    // The final byte of a chunk is always a literal: a multi-byte token cannot be
                    // completed within the chunk's remaining single byte.
                    if (remaining == 1)
                    {
                        dst[outPos++] = lead;
                        inPos += 1; consumed += 1;
                        continue;
                    }

                    if (lead == 0x55)
                    {
                        byte b = src[srcOffset + inPos + 1];
                        if (b == 0)
                        {
                            dst[outPos++] = 0x55; // escaped literal 0x55
                            inPos += 2; consumed += 2;
                        }
                        else if (b == 1)
                        {
                            dst[outPos++] = 0xAA; // escaped literal 0xAA
                            inPos += 2; consumed += 2;
                        }
                        else
                        {
                            byte v = src[srcOffset + inPos + 2];
                            int runLen = b + 1; // run of (n + 1) copies
                            for (int k = 0; k < runLen; k++)
                                dst[outPos + k] = v;
                            outPos += runLen;
                            inPos += 3; consumed += 3;
                        }
                    }
                    else if (lead == 0xAA)
                    {
                        byte v = src[srcOffset + inPos + 1];
                        dst[outPos + 0] = v; // fixed run of three
                        dst[outPos + 1] = v;
                        dst[outPos + 2] = v;
                        outPos += 3;
                        inPos += 2; consumed += 2;
                    }
                    else
                    {
                        dst[outPos++] = lead; // plain literal
                        inPos += 1; consumed += 1;
                    }
                }
            }

            return outPos;
        }

        // Rounds a position up to the next four-byte boundary.
        private static int Align4(int pos) => (pos & 3) == 0 ? pos : pos + (4 - (pos & 3));
    }
}
