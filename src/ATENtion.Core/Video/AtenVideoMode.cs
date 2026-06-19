namespace ATENtion.Core.Video
{
    /// <summary>The pixel-conversion modes the native pixel writer accepts.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Names the source pixel format passed to <see cref="PixelConverter"/>, with each
    /// value being the exact mode byte the native engine uses.
    /// </para>
    /// <para>
    /// PROVENANCE - The mode argument of the native pixel writer iKVM64.dll FUN_18000d180(state,
    /// mode, src, dst, count).
    /// </para>
    /// </remarks>
    public enum AtenPixelMode : byte
    {
        /// <summary>4-bit palette index, two pixels per source byte, low nibble first (0x04).</summary>
        Palette4 = 0x04,

        /// <summary>8-bit palette index, one pixel per source byte (0x08).</summary>
        Palette8 = 0x08,

        /// <summary>16-bit RGB555 little-endian, expanded to 32-bit BGRA (0x10).</summary>
        Rgb555 = 0x10,

        /// <summary>32-bit source pixels, copied with the engine's byte swizzle (0x20).</summary>
        Rgb32 = 0x20,
    }

    /// <summary>The encoding type class carried in byte 1 of the video header.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Names the encoding type that, together with the subtype, selects the decode path
    /// and determines whether a palette block is present and the bit-plane element width on the
    /// tile-update path.
    /// </para>
    /// <para>
    /// PROVENANCE - Byte 1 of the ten-byte video header parsed by iKVM64.dll FUN_18000b630.
    /// </para>
    /// </remarks>
    public enum AtenEncodingType : byte
    {
        /// <summary>Type 1: with subtype 0, carries a 1024-byte palette and is nibble-indexed.</summary>
        Type1 = 1,

        /// <summary>Type 2: 16-bit-per-pixel tiles via the bit-plane path.</summary>
        Type2 = 2,

        /// <summary>Type 4: 32-bit-per-pixel tiles via the bit-plane path.</summary>
        Type4 = 4,

        /// <summary>Type 5: carries a 1024-byte palette, as type 1 with subtype 0 does.</summary>
        Type5 = 5,
    }
}
