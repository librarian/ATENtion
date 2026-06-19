using System;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// A 32-bit BGRA framebuffer: the buffer the decoder writes pixels into and the UI reads to
    /// present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Holds the decoded console image as a flat BGRA byte array, with its current
    /// dimensions and row stride. The byte order is blue, green, red, alpha, so the buffer can be
    /// handed to a WPF WriteableBitmap in PixelFormats.Bgra32 without a copy or swizzle.
    /// </para>
    /// <para>
    /// OPERATION - A resize reallocates the pixel array to width * height * 4 bytes. The decoder
    /// writes into <see cref="Pixels"/> directly. The renderer reads it.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Not thread-safe. The receive pump writes it and the UI reads a snapshot of
    /// it. A resize replaces the array, so any retained reference to a previous <see cref="Pixels"/>
    /// becomes stale.
    /// </para>
    /// <para>
    /// PROVENANCE - Corresponds to the native output buffer at state+0x28 (the Java direct
    /// ByteBuffer), sized width * height * 4 bytes.
    /// </para>
    /// </remarks>
    public sealed class FrameBuffer
    {
        /// <summary>Bytes per pixel in the BGRA buffer.</summary>
        public const int BytesPerPixel = 4;

        /// <summary>Creates a framebuffer of the given dimensions.</summary>
        /// <param name="width">The width, in pixels.</param>
        /// <param name="height">The height, in pixels.</param>
        public FrameBuffer(int width, int height)
        {
            Resize(width, height);
        }

        /// <summary>The current width, in pixels.</summary>
        public int Width { get; private set; }
        /// <summary>The current height, in pixels.</summary>
        public int Height { get; private set; }
        /// <summary>The row stride, in bytes (<see cref="Width"/> * <see cref="BytesPerPixel"/>).</summary>
        public int Stride => Width * BytesPerPixel;

        /// <summary>The raw BGRA bytes; length is <see cref="Width"/> * <see cref="Height"/> * 4.</summary>
        public byte[] Pixels { get; private set; }

        /// <summary>Resizes the framebuffer, reallocating the pixel array to the new dimensions.</summary>
        /// <param name="width">The new width, in pixels; must be positive.</param>
        /// <param name="height">The new height, in pixels; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Framebuffer dimensions must be positive.");
            Width = width;
            Height = height;
            Pixels = new byte[width * height * BytesPerPixel];
        }
    }
}
