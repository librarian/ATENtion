using System;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// Copies a decoded BGRA tile into the framebuffer at a pixel position, clipping to the
    /// framebuffer bounds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Places one decoded tile into the framebuffer at (x, y), writing only the pixels
    /// that fall inside the surface.
    /// </para>
    /// <para>
    /// OPERATION - When the whole tile is in bounds, which is the common case, each tile row is
    /// contiguous in both the source and the destination, so the row is copied in a single block
    /// copy. Only the tiles that straddle an edge of a non-16-aligned resolution take the per-pixel
    /// path, which tests each pixel against the bounds and copies the four BGRA bytes individually.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Writes into a <see cref="FrameBuffer"/>. The tile bytes are produced by the
    /// decode paths in <see cref="AtenTileDecoder"/>.
    /// </para>
    /// <para>
    /// PROVENANCE - Port of the native tile blit iKVM64.dll FUN_18000d340, expressed in plain pixel
    /// coordinates.
    /// </para>
    /// </remarks>
    public static class TileBlitter
    {
        /// <summary>Bytes per pixel in the BGRA framebuffer and tile buffers.</summary>
        public const int BytesPerPixel = 4;

        /// <summary>Blits a tile into the framebuffer at a pixel position, clipping to bounds.</summary>
        /// <param name="frame">The destination framebuffer.</param>
        /// <param name="tileBgra">The tile's BGRA pixels, row-major.</param>
        /// <param name="tileWidth">The tile width, in pixels.</param>
        /// <param name="tileHeight">The tile height, in pixels.</param>
        /// <param name="x">The destination left edge, in pixels (may place the tile partly off-surface).</param>
        /// <param name="y">The destination top edge, in pixels (may place the tile partly off-surface).</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="tileBgra"/> is null.</exception>
        public static void Blit(FrameBuffer frame, byte[] tileBgra, int tileWidth, int tileHeight, int x, int y)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (tileBgra == null) throw new ArgumentNullException(nameof(tileBgra));

            byte[] dst = frame.Pixels;
            int fbStride = frame.Stride;
            int tileStride = tileWidth * BytesPerPixel;

            // Fast path: the whole tile is in bounds (the common case; only non-16-aligned edge tiles
            // need clipping). A tile row is contiguous in both buffers, so copy it in one block copy
            // rather than as per-pixel, bounds-checked byte assignments.
            if (x >= 0 && y >= 0 && x + tileWidth <= frame.Width && y + tileHeight <= frame.Height)
            {
                for (int row = 0; row < tileHeight; row++)
                {
                    int s = row * tileStride;
                    int d = (y + row) * fbStride + x * BytesPerPixel;
                    Buffer.BlockCopy(tileBgra, s, dst, d, tileStride);
                }
                return;
            }

            // Slow path: the tile straddles an edge, so clip it pixel by pixel.
            for (int row = 0; row < tileHeight; row++)
            {
                int dy = y + row;
                if (dy < 0 || dy >= frame.Height) continue;

                for (int col = 0; col < tileWidth; col++)
                {
                    int dx = x + col;
                    if (dx < 0 || dx >= frame.Width) continue;

                    int s = row * tileStride + col * BytesPerPixel;
                    int d = dy * fbStride + dx * BytesPerPixel;
                    dst[d + 0] = tileBgra[s + 0];
                    dst[d + 1] = tileBgra[s + 1];
                    dst[d + 2] = tileBgra[s + 2];
                    dst[d + 3] = tileBgra[s + 3];
                }
            }
        }
    }
}
