using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ATENtion.Core.Video;

namespace ATENtion.App.Video
{
    /// <summary>
    /// Bridges a Core <see cref="FrameBuffer"/> to a WPF <see cref="WriteableBitmap"/>, uploading
    /// either the whole surface or only the regions that changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Maintains the bitmap the UI binds its image source to and copies decoded BGRA pixels
    /// into it. The decoder fills the framebuffer directly. This type transfers those pixels to the
    /// bitmap.
    /// </para>
    /// <para>
    /// OPERATION - The bitmap is recreated whenever the frame dimensions change. A full update writes
    /// the entire surface. An incremental update writes only the changed rectangles, which is far
    /// cheaper for the common case of a few 16x16 tiles. When several regions change at once, they are
    /// copied in a single back-buffer lock: the buffer is locked once, each region's rows are copied
    /// straight into the back buffer, each region is marked dirty, and the buffer is unlocked once,
    /// rather than locking and unlocking per region. A full-width region with matching strides is
    /// copied as one contiguous block. The decoder marks a whole-screen change with a sentinel
    /// rectangle, which falls back to a full upload, as does the first frame after a resize.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads pixels from a Core <see cref="FrameBuffer"/> and its
    /// <see cref="DirtyRect"/> regions. It produces a <see cref="WriteableBitmap"/> for the UI.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Must be used on the UI thread, as it touches WPF imaging objects. The session's
    /// present logic snapshots the framebuffer so the pump thread is not blocked.
    /// </para>
    /// </remarks>
    public sealed class WpfFrameRenderer
    {
        /// <summary>The bitmap the UI displays; recreated when the frame size changes.</summary>
        public WriteableBitmap Bitmap { get; private set; }

        /// <summary>Ensures the bitmap matches the frame's dimensions, recreating it on a change.</summary>
        /// <param name="frame">The framebuffer whose size the bitmap must match.</param>
        public void EnsureSize(FrameBuffer frame)
        {
            if (Bitmap == null || Bitmap.PixelWidth != frame.Width || Bitmap.PixelHeight != frame.Height)
            {
                Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            }
        }

        /// <summary>Ensures the bitmap matches the given dimensions, recreating it on a change.</summary>
        /// <param name="width">The required width, in pixels.</param>
        /// <param name="height">The required height, in pixels.</param>
        public void EnsureSize(int width, int height)
        {
            if (Bitmap == null || Bitmap.PixelWidth != width || Bitmap.PixelHeight != height)
                Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        }

        /// <summary>Uploads a full surface from a raw BGRA snapshot.</summary>
        /// <param name="pixels">The BGRA pixel bytes.</param>
        /// <param name="width">The width, in pixels.</param>
        /// <param name="height">The height, in pixels.</param>
        /// <param name="stride">The row stride, in bytes.</param>
        public void WriteFull(byte[] pixels, int width, int height, int stride)
        {
            EnsureSize(width, height);
            Bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        }

        /// <summary>
        /// Uploads a single changed region. Only the changed area is transferred, so WPF re-composites
        /// just that region rather than the whole scaled image.
        /// </summary>
        /// <param name="pixels">The source BGRA bytes.</param>
        /// <param name="stride">The source row stride, in bytes.</param>
        /// <param name="r">The region to upload.</param>
        public void WriteRegion(byte[] pixels, int stride, Int32Rect r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            Bitmap.WritePixels(r, pixels, stride, r.Y * stride + r.X * 4);
        }

        /// <summary>
        /// Uploads several changed regions in a single back-buffer lock.
        /// </summary>
        /// <remarks>
        /// This is cheaper than one <see cref="WriteRegion"/> per region, which would lock, copy,
        /// dirty, and unlock the bitmap on each call. The buffer is locked once, each region's rows are
        /// copied straight into the back buffer (a full-width matching-stride region as one block,
        /// otherwise row by row), each region is marked dirty, and the buffer is unlocked once. This is
        /// the common path for a multi-tile incremental frame.
        /// </remarks>
        /// <param name="pixels">The source BGRA bytes.</param>
        /// <param name="srcStride">The source row stride, in bytes.</param>
        /// <param name="regions">The regions to upload.</param>
        public void WriteRegions(byte[] pixels, int srcStride, IReadOnlyList<Int32Rect> regions)
        {
            if (Bitmap == null || regions == null || regions.Count == 0) return;
            Bitmap.Lock();
            try
            {
                IntPtr back = Bitmap.BackBuffer;
                int dstStride = Bitmap.BackBufferStride;
                for (int i = 0; i < regions.Count; i++)
                {
                    Int32Rect r = regions[i];
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    int rowBytes = r.Width * 4;
                    if (srcStride == dstStride && r.X == 0 && rowBytes == srcStride)
                    {
                        // A full-width region with matching strides: its rows are contiguous in both
                        // buffers, so copy the whole block in one Marshal.Copy rather than row by row.
                        int srcOff = r.Y * srcStride;
                        int dstOff = r.Y * dstStride;
                        System.Runtime.InteropServices.Marshal.Copy(pixels, srcOff, IntPtr.Add(back, dstOff), rowBytes * r.Height);
                    }
                    else
                    {
                        for (int row = 0; row < r.Height; row++)
                        {
                            int srcOff = (r.Y + row) * srcStride + r.X * 4;
                            int dstOff = (r.Y + row) * dstStride + r.X * 4;
                            System.Runtime.InteropServices.Marshal.Copy(pixels, srcOff, IntPtr.Add(back, dstOff), rowBytes);
                        }
                    }
                    Bitmap.AddDirtyRect(r);
                }
            }
            finally { Bitmap.Unlock(); }
        }

        /// <summary>Uploads a full surface from a raw BGRA snapshot.</summary>
        /// <param name="pixels">The BGRA pixel bytes.</param>
        /// <param name="width">The width, in pixels.</param>
        /// <param name="height">The height, in pixels.</param>
        public void Update(byte[] pixels, int width, int height) => WriteFull(pixels, width, height, width * 4);

        /// <summary>Uploads the whole framebuffer.</summary>
        /// <param name="frame">The framebuffer to upload.</param>
        public void Update(FrameBuffer frame)
        {
            EnsureSize(frame);
            Bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height),
                               frame.Pixels, frame.Stride, 0);
        }

        /// <summary>
        /// Uploads only the changed regions of the framebuffer, falling back to a full upload when the
        /// whole surface changed or the surface was just resized.
        /// </summary>
        /// <param name="frame">The framebuffer holding the current image.</param>
        /// <param name="dirty">The changed regions, or the whole-screen sentinel.</param>
        public void Update(FrameBuffer frame, IReadOnlyList<DirtyRect> dirty)
        {
            bool resized = Bitmap == null || Bitmap.PixelWidth != frame.Width || Bitmap.PixelHeight != frame.Height;
            EnsureSize(frame);

            if (resized || dirty == null || dirty.Count == 0 || IsFullScreen(dirty))
            {
                Bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height),
                                   frame.Pixels, frame.Stride, 0);
                return;
            }

            foreach (var r in dirty)
            {
                int x = Clamp(r.X, 0, frame.Width);
                int y = Clamp(r.Y, 0, frame.Height);
                int w = Clamp(r.Width, 0, frame.Width - x);
                int h = Clamp(r.Height, 0, frame.Height - y);
                if (w <= 0 || h <= 0) continue;
                // The source offset of this sub-rectangle within the shared pixel buffer; WritePixels
                // walks it using the full frame stride, so only (x, y, w, h) is uploaded.
                int srcOffset = y * frame.Stride + x * 4;
                Bitmap.WritePixels(new Int32Rect(x, y, w, h), frame.Pixels, frame.Stride, srcOffset);
            }
        }

        // True when the dirty list contains the decoder's whole-screen sentinel (see AtenTileDecoder).
        private static bool IsFullScreen(IReadOnlyList<DirtyRect> dirty)
        {
            for (int i = 0; i < dirty.Count; i++)
                if (dirty[i].X == 0xffff && dirty[i].Y == 0xffff) return true;
            return false;
        }

        // Clamps a value to the inclusive lower and upper bounds.
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
