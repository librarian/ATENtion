namespace ATENtion.Core.Video
{
    /// <summary>
    /// Performs the Pilot-III planar-to-chunky transpose: the final stage of the bit-plane video
    /// decode path, which turns separate one-bit colour planes back into packed pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Reassembles per-pixel colour values from a buffer in which the image is stored
    /// as separate one-bit planes. Two layouts are handled: a 24-bit form producing 32-bit BGRA,
    /// and a 16-bit form producing RGB555 words.
    /// </para>
    /// <para>
    /// OPERATION - In the planar buffer, pixel N's contribution to plane p lives at byte N &gt;&gt; 3,
    /// bit N &amp; 7. Each eight-bit channel value is rebuilt by reading the eight planes that make up
    /// its group: output bit k of a channel reads plane (groupBase + 7 - k). The plane groups are
    /// Green at 0..7, Blue at 8..15, and Red at 24..31, with a gap across planes 16..23 in the
    /// 24-bit layout. The 16-bit layout uses only the first two groups.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Operates on the RLE-decompressed plane buffer produced by
    /// <see cref="AtenRle"/>; its 16-bit output feeds <see cref="PixelConverter.Rgb555"/>.
    /// </para>
    /// <para>
    /// RESTRICTIONS - The transpose mechanism is unit-tested. The channel-to-group assignment and
    /// the 16..23 plane gap are RE-derived and not yet confirmed against a real BMC frame. They are
    /// isolated as the constants below, so a correction is a one-line change. The BMC in hand
    /// uses the type-0 path, not this one.
    /// </para>
    /// <para>
    /// PROVENANCE - Final stage of the native bit-plane decoder iKVM64.dll FUN_18000b630, derived
    /// from its plane-stride offset arithmetic.
    /// </para>
    /// </remarks>
    public static class BitPlaneDeinterleave
    {
        // Plane group base indices. Each colour channel spans eight consecutive planes from its base.
        private const int GreenBase = 0;
        private const int BlueBase = 8;
        private const int RedBase = 24;

        /// <summary>Total plane slots spanned by the 24-bit layout (the Red group ends at plane 31).</summary>
        public const int PlaneSlots24 = 32;

        /// <summary>Plane slots for the 16-bit (RGB555) layout: two groups across planes 0..15.</summary>
        public const int PlaneSlots16 = 16;

        /// <summary>Decodes a 24-bit (type 4) planar buffer into 32-bit BGRA pixels.</summary>
        /// <param name="planes">The RLE-decompressed plane buffer, at least
        /// <see cref="PlaneSlots24"/> times the per-plane byte count.</param>
        /// <param name="numPixels">The number of pixels to produce.</param>
        /// <param name="dst">The BGRA output buffer, at least <paramref name="numPixels"/> * 4 bytes.</param>
        /// <param name="dstOffset">The byte offset into <paramref name="dst"/> to write from.</param>
        public static void Decode24(byte[] planes, int numPixels, byte[] dst, int dstOffset)
        {
            int bytesPerPlane = (numPixels + 7) / 8;
            for (int n = 0; n < numPixels; n++)
            {
                int byteIdx = n >> 3;
                int bit = n & 7;
                int d = dstOffset + n * 4;
                dst[d + 0] = AssembleByte(planes, BlueBase, bytesPerPlane, byteIdx, bit);  // blue
                dst[d + 1] = AssembleByte(planes, GreenBase, bytesPerPlane, byteIdx, bit); // green
                dst[d + 2] = AssembleByte(planes, RedBase, bytesPerPlane, byteIdx, bit);   // red
                dst[d + 3] = 0xFF;                                                          // alpha (opaque)
            }
        }

        /// <summary>
        /// Decodes a 16-bit (type 2) planar buffer into RGB555 words, two bytes per pixel, in the
        /// form <see cref="PixelConverter.Rgb555"/> expects.
        /// </summary>
        /// <remarks>
        /// The two output bytes use the same plane groups as <see cref="Decode24"/>'s Blue (8..15)
        /// and Green (0..7): the low byte comes from planes 8..15, the high byte from planes 0..7.
        /// </remarks>
        /// <param name="planes">The RLE-decompressed plane buffer.</param>
        /// <param name="numPixels">The number of pixels to produce.</param>
        /// <param name="dst16">The RGB555 output buffer, at least <paramref name="numPixels"/> * 2 bytes.</param>
        /// <param name="dstOffset">The byte offset into <paramref name="dst16"/> to write from.</param>
        public static void Decode16(byte[] planes, int numPixels, byte[] dst16, int dstOffset)
        {
            int bytesPerPlane = (numPixels + 7) / 8;
            for (int n = 0; n < numPixels; n++)
            {
                int byteIdx = n >> 3;
                int bit = n & 7;
                int d = dstOffset + n * 2;
                dst16[d + 0] = AssembleByte(planes, 8, bytesPerPlane, byteIdx, bit); // low byte
                dst16[d + 1] = AssembleByte(planes, 0, bytesPerPlane, byteIdx, bit); // high byte
            }
        }

        // Reassembles one eight-bit channel value for one pixel from its eight planes. Output bit k
        // reads plane (groupBase + 7 - k), so the plane order is reversed relative to the bit order.
        private static byte AssembleByte(byte[] planes, int groupBase, int bytesPerPlane, int byteIdx, int bit)
        {
            int v = 0;
            for (int k = 0; k < 8; k++)
            {
                int plane = groupBase + (7 - k);
                int srcIndex = plane * bytesPerPlane + byteIdx;
                v |= ((planes[srcIndex] >> bit) & 1) << k;
            }
            return (byte)v;
        }
    }
}
