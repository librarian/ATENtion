using System.Windows.Media;

namespace ATENtion.App
{
    /// <summary>The connection-state indicator colours, defined once for the whole UI.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Holds the four brushes that show connection state, so the main window's status line
    /// and the Session Info dialog draw from one definition rather than each repeating the RGB values.
    /// </para>
    /// <para>
    /// OPERATION - Each brush is frozen at creation, which makes it immutable and safe to share across
    /// the UI without per-use allocation.
    /// </para>
    /// </remarks>
    internal static class StatusColors
    {
        /// <summary>Green: this session holds input control.</summary>
        public static readonly Brush Controlling = Frozen(0x66, 0xCC, 0x66);
        /// <summary>Amber: connected but view-only.</summary>
        public static readonly Brush ViewOnly = Frozen(0xDD, 0xBB, 0x44);
        /// <summary>Red: disconnected.</summary>
        public static readonly Brush Disconnected = Frozen(0xE0, 0x56, 0x56);
        /// <summary>Grey: connecting or idle.</summary>
        public static readonly Brush Neutral = Frozen(0xAA, 0xAA, 0xAA);

        // Builds a frozen solid-colour brush from its RGB components.
        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
