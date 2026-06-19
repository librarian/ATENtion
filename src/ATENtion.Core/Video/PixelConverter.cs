using System;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// Converts decoded source pixels of each ATEN pixel mode into 32-bit BGRA, the byte order WPF
    /// renders directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Provides one conversion method per pixel mode (4-bit and 8-bit palette, RGB555,
    /// and 32-bit RGB), plus a dispatcher. Each writes BGRA pixels into a destination buffer and
    /// returns the number of destination bytes written, four per pixel.
    /// </para>
    /// <para>
    /// OPERATION - The output is BGRA, the memory order WPF's PixelFormats.Bgra32 expects. The
    /// native engine emits pixels in A, B, G, R byte order with the alpha set to zero for the RGB
    /// modes. The blue, green, and red derivations are kept identical to the native code, and the
    /// alpha is forced opaque (0xFF) so the surface renders solid. The 5-bit colour channels of the
    /// RGB555 modes are expanded to eight bits by a left shift.
    /// </para>
    /// <para>
    /// DEPENDENCIES - The palette modes resolve indices through an <see cref="AtenPalette"/>. The
    /// output buffers are framebuffer or tile buffers owned by <see cref="AtenTileDecoder"/>.
    /// </para>
    /// <para>
    /// PROVENANCE - Port of the native pixel writer iKVM64.dll FUN_18000d180(state, mode, src, dst,
    /// count). The raw RGB555 keyframe format is VERIFIED LIVE. The bit-plane-fed modes are
    /// PORTED FAITHFULLY.
    /// </para>
    /// </remarks>
    public static class PixelConverter
    {
        // Alpha value written for every pixel so the BGRA surface renders opaque.
        private const byte OpaqueAlpha = 0xFF;

        /// <summary>Dispatches to the converter for the given pixel mode.</summary>
        /// <param name="mode">The source pixel mode.</param>
        /// <param name="src">The source bytes.</param>
        /// <param name="srcOffset">The offset into <paramref name="src"/> to read from.</param>
        /// <param name="srcByteCount">The number of source bytes to consume (the native count).</param>
        /// <param name="palette">The palette for the palette modes; may be null for the RGB modes.</param>
        /// <param name="dst">The BGRA destination buffer.</param>
        /// <param name="dstOffset">The offset into <paramref name="dst"/> to write from.</param>
        /// <returns>The number of destination bytes written.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The pixel mode is not recognised.</exception>
        public static int Convert(AtenPixelMode mode, byte[] src, int srcOffset, int srcByteCount,
                                  AtenPalette palette, byte[] dst, int dstOffset)
        {
            return mode switch
            {
                AtenPixelMode.Palette4 => Palette4(src, srcOffset, srcByteCount, palette, dst, dstOffset),
                AtenPixelMode.Palette8 => Palette8(src, srcOffset, srcByteCount, palette, dst, dstOffset),
                AtenPixelMode.Rgb555 => Rgb555(src, srcOffset, srcByteCount, dst, dstOffset),
                AtenPixelMode.Rgb32 => Rgb32(src, srcOffset, srcByteCount, dst, dstOffset),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown ATEN pixel mode."),
            };
        }

        /// <summary>Mode 0x04: each source byte holds two 4-bit palette indices, low nibble first.</summary>
        /// <param name="src">The source bytes.</param>
        /// <param name="srcOffset">The offset into <paramref name="src"/> to read from.</param>
        /// <param name="srcByteCount">The number of source bytes to consume.</param>
        /// <param name="palette">The palette to resolve indices through.</param>
        /// <param name="dst">The BGRA destination buffer.</param>
        /// <param name="dstOffset">The offset into <paramref name="dst"/> to write from.</param>
        /// <returns>The number of destination bytes written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="palette"/> is null.</exception>
        public static int Palette4(byte[] src, int srcOffset, int srcByteCount,
                                   AtenPalette palette, byte[] dst, int dstOffset)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            int d = dstOffset;
            for (int i = 0; i < srcByteCount; i++)
            {
                byte v = src[srcOffset + i];
                d += WritePalettePixel(palette, v & 0x0f, dst, d); // low nibble first
                d += WritePalettePixel(palette, v >> 4, dst, d);   // then high nibble
            }
            return d - dstOffset;
        }

        /// <summary>Mode 0x08: one palette index per source byte.</summary>
        /// <param name="src">The source bytes.</param>
        /// <param name="srcOffset">The offset into <paramref name="src"/> to read from.</param>
        /// <param name="srcByteCount">The number of source bytes to consume.</param>
        /// <param name="palette">The palette to resolve indices through.</param>
        /// <param name="dst">The BGRA destination buffer.</param>
        /// <param name="dstOffset">The offset into <paramref name="dst"/> to write from.</param>
        /// <returns>The number of destination bytes written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="palette"/> is null.</exception>
        public static int Palette8(byte[] src, int srcOffset, int srcByteCount,
                                   AtenPalette palette, byte[] dst, int dstOffset)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            int d = dstOffset;
            for (int i = 0; i < srcByteCount; i++)
                d += WritePalettePixel(palette, src[srcOffset + i], dst, d);
            return d - dstOffset;
        }

        /// <summary>Mode 0x10: 16-bit RGB555, little-endian, two source bytes per pixel.</summary>
        /// <param name="src">The source bytes.</param>
        /// <param name="srcOffset">The offset into <paramref name="src"/> to read from.</param>
        /// <param name="srcByteCount">The number of source bytes to consume.</param>
        /// <param name="dst">The BGRA destination buffer.</param>
        /// <param name="dstOffset">The offset into <paramref name="dst"/> to write from.</param>
        /// <returns>The number of destination bytes written.</returns>
        public static int Rgb555(byte[] src, int srcOffset, int srcByteCount, byte[] dst, int dstOffset)
        {
            int pixels = (srcByteCount + 1) / 2; // ceil, matching the native ((count-1)>>1)+1
            int s = srcOffset, d = dstOffset;
            for (int i = 0; i < pixels; i++)
            {
                byte v0 = src[s];
                byte v1 = src[s + 1];
                dst[d + 0] = (byte)(v0 << 3);                      // blue
                dst[d + 1] = (byte)((v0 >> 3 & 0x1c) | (v1 << 5)); // green (split across both bytes)
                dst[d + 2] = (byte)(v1 & 0xf8);                    // red
                dst[d + 3] = OpaqueAlpha;                          // alpha (opaque)
                s += 2;
                d += 4;
            }
            return d - dstOffset;
        }

        /// <summary>Mode 0x20: 32-bit source pixels in {B, G, R, A} order, four source bytes per pixel.</summary>
        /// <param name="src">The source bytes.</param>
        /// <param name="srcOffset">The offset into <paramref name="src"/> to read from.</param>
        /// <param name="srcByteCount">The number of source bytes to consume.</param>
        /// <param name="dst">The BGRA destination buffer.</param>
        /// <param name="dstOffset">The offset into <paramref name="dst"/> to write from.</param>
        /// <returns>The number of destination bytes written.</returns>
        public static int Rgb32(byte[] src, int srcOffset, int srcByteCount, byte[] dst, int dstOffset)
        {
            int pixels = (srcByteCount + 3) / 4; // ceil, matching the native ((count-1)>>2)+1
            int s = srcOffset, d = dstOffset;
            for (int i = 0; i < pixels; i++)
            {
                dst[d + 0] = src[s + 0];  // blue
                dst[d + 1] = src[s + 1];  // green
                dst[d + 2] = src[s + 2];  // red
                dst[d + 3] = OpaqueAlpha; // alpha forced opaque (the engine swizzles src[3])
                s += 4;
                d += 4;
            }
            return d - dstOffset;
        }

        /// <summary>
        /// Converts raw 16-bit RGB555 little-endian pixels into 32-bit BGRA. This is the
        /// encoding-type-0 full-frame format the target BMC actually sends.
        /// </summary>
        /// <remarks>
        /// The format is RGB555, not RGB565: a live grayscale frame only decodes correctly as
        /// 5-5-5. The 16-bit word is laid out 0RRRRRGG GGGBBBBB; each 5-bit
        /// channel is expanded to eight bits by a left shift of three.
        /// </remarks>
        /// <param name="src">The source RGB555 bytes.</param>
        /// <param name="srcOffset">The offset into <paramref name="src"/> to read from.</param>
        /// <param name="pixels">The number of pixels to convert.</param>
        /// <param name="dst">The BGRA destination buffer.</param>
        /// <param name="dstOffset">The offset into <paramref name="dst"/> to write from.</param>
        public static void RawRgb555(byte[] src, int srcOffset, int pixels, byte[] dst, int dstOffset)
        {
            int s = srcOffset, d = dstOffset;
            for (int i = 0; i < pixels; i++)
            {
                int v = src[s] | (src[s + 1] << 8);
                dst[d + 0] = (byte)((v & 0x1f) << 3);          // blue
                dst[d + 1] = (byte)(((v >> 5) & 0x1f) << 3);   // green
                dst[d + 2] = (byte)(((v >> 10) & 0x1f) << 3);  // red
                dst[d + 3] = OpaqueAlpha;                      // alpha (opaque)
                s += 2;
                d += 4;
            }
        }

        // Writes one palette-resolved pixel as BGRA and returns the four bytes written.
        private static int WritePalettePixel(AtenPalette palette, int index, byte[] dst, int d)
        {
            dst[d + 0] = palette.B(index);
            dst[d + 1] = palette.G(index);
            dst[d + 2] = palette.R(index);
            dst[d + 3] = OpaqueAlpha;
            return 4;
        }
    }
}
