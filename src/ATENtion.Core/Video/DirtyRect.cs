namespace ATENtion.Core.Video
{
    /// <summary>
    /// A changed region reported by the decoder, in pixels, with a sentinel form that means the
    /// whole surface changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Names a rectangle the decoder touched, so the renderer can repaint only that
    /// region. A full keyframe is reported as a single rectangle carrying the sentinel coordinates,
    /// which <see cref="IsFullScreen"/> recognises.
    /// </para>
    /// <para>
    /// PROVENANCE - Mirrors the eight-byte rectangle entries the native engine writes into its
    /// dirty-rect list, {u16 x, u16 y, u16 w, u16 h}. The full-screen sentinel is
    /// {0xffff, 0xffff, 16, 16}.
    /// </para>
    /// </remarks>
    public readonly struct DirtyRect
    {
        /// <summary>Creates a rectangle from its pixel coordinates and size.</summary>
        /// <param name="x">The left edge, in pixels.</param>
        /// <param name="y">The top edge, in pixels.</param>
        /// <param name="width">The width, in pixels.</param>
        /// <param name="height">The height, in pixels.</param>
        public DirtyRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>The left edge, in pixels.</summary>
        public int X { get; }
        /// <summary>The top edge, in pixels.</summary>
        public int Y { get; }
        /// <summary>The width, in pixels.</summary>
        public int Width { get; }
        /// <summary>The height, in pixels.</summary>
        public int Height { get; }

        /// <summary>True when this is the full-screen sentinel (x and y both 0xffff), meaning repaint everything.</summary>
        public bool IsFullScreen => X == 0xffff && Y == 0xffff;

        /// <summary>Formats the rectangle, or "DirtyRect(FULL)" for the full-screen sentinel.</summary>
        /// <returns>A short description for diagnostics.</returns>
        public override string ToString() =>
            IsFullScreen ? "DirtyRect(FULL)" : $"DirtyRect({X},{Y} {Width}x{Height})";
    }
}
