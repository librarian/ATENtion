using System.Windows.Input;

namespace ATENtion.App
{
    /// <summary>
    /// Maps a WPF <see cref="Key"/> to the ATEN iKVM keysym, which is the raw USB-HID keyboard usage
    /// code sent verbatim on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Translates a key the viewer captured into the value the BMC's virtual USB keyboard
    /// expects. The keysym is the raw USB-HID usage from the Keyboard/Keypad usage page. Case and
    /// shifted symbols are not encoded here.
    /// </para>
    /// <para>
    /// OPERATION - Letters, digits, function keys, and keypad digits map by arithmetic from their key
    /// ranges. The remaining keys map through a switch. Modifiers (Shift, Ctrl, Alt, the Windows keys)
    /// travel as their own key events, left and right distinct, and the BMC derives upper case and
    /// shifted symbols from them exactly as a physical USB keyboard does. The map returns the raw
    /// usage only.
    /// </para>
    /// <para>
    /// RESTRICTIONS - The 0xFF00 marker is not applied here. The native firmware sets 0xFF00 only on
    /// the lock keys (Caps 0x39, Scroll 0x47, Num 0x53, and only while that lock is off). That case is
    /// applied separately in MainWindow.ApplyLockKeyMarker. For any other key the marker would put it
    /// on a path that does not update HID modifier state, so Shift would never combine and everything
    /// would stay lower case. Sending the raw usage is what makes case work.
    /// </para>
    /// <para>
    /// PROVENANCE - Matches the native key path RemoteVideo.processKeyEvent -> KeyMap.VKtoHID ->
    /// keyboardAction, which writes the raw usage as a big-endian u32 keysym. VERIFIED LIVE, including upper case and shifted symbols.
    /// </para>
    /// </remarks>
    internal static class KeySymMap
    {
        /// <summary>Returns the ATEN keysym (raw USB-HID usage) for a key, or 0 when unmapped.</summary>
        /// <param name="key">The WPF key.</param>
        /// <returns>The raw USB-HID usage code, or 0 if the key has no mapping.</returns>
        public static uint ToKeySym(Key key)
        {
            int hid = HidUsage(key);
            return hid == 0 ? 0u : (uint)hid;
        }

        // Returns the raw USB-HID usage for a key, or 0 when there is no mapping.
        private static int HidUsage(Key key)
        {
            // Letters a-z = 0x04..0x1D (case comes from the Shift modifier).
            if (key >= Key.A && key <= Key.Z) return 0x04 + (key - Key.A);
            // Top-row digits: 1..9 = 0x1E..0x26, 0 = 0x27.
            if (key >= Key.D1 && key <= Key.D9) return 0x1E + (key - Key.D1);
            if (key == Key.D0) return 0x27;
            // Function keys F1..F12 = 0x3A..0x45.
            if (key >= Key.F1 && key <= Key.F12) return 0x3A + (key - Key.F1);
            // Keypad digits: 1..9 = 0x59..0x61, 0 = 0x62.
            if (key >= Key.NumPad1 && key <= Key.NumPad9) return 0x59 + (key - Key.NumPad1);

            switch (key)
            {
                case Key.NumPad0: return 0x62;
                case Key.Decimal: return 0x63;       // keypad .
                case Key.Divide: return 0x54;        // keypad /
                case Key.Multiply: return 0x55;      // keypad *
                case Key.Subtract: return 0x56;      // keypad -
                case Key.Add: return 0x57;           // keypad +

                case Key.Return: return 0x28;
                case Key.Escape: return 0x29;
                case Key.Back: return 0x2A;          // Backspace
                case Key.Tab: return 0x2B;
                case Key.Space: return 0x2C;

                case Key.OemMinus: return 0x2D;      // - _
                case Key.OemPlus: return 0x2E;       // = +
                case Key.OemOpenBrackets: return 0x2F;  // [ {
                case Key.OemCloseBrackets: return 0x30; // ] }
                case Key.OemPipe: return 0x31;       // \ |
                case Key.OemSemicolon: return 0x33;  // ; :
                case Key.OemQuotes: return 0x34;     // ' "
                case Key.OemTilde: return 0x35;      // ` ~
                case Key.OemComma: return 0x36;      // , <
                case Key.OemPeriod: return 0x37;     // . >
                case Key.OemQuestion: return 0x38;   // / ?

                case Key.CapsLock: return 0x39;
                case Key.PrintScreen: return 0x46;
                case Key.Scroll: return 0x47;
                case Key.Pause: return 0x48;
                case Key.Insert: return 0x49;
                case Key.Home: return 0x4A;
                case Key.PageUp: return 0x4B;
                case Key.Delete: return 0x4C;
                case Key.End: return 0x4D;
                case Key.PageDown: return 0x4E;
                case Key.Right: return 0x4F;
                case Key.Left: return 0x50;
                case Key.Down: return 0x51;
                case Key.Up: return 0x52;
                case Key.NumLock: return 0x53;

                // Modifiers are standard USB-HID usages, left and right distinct (the native
                // KeyMap.VKtoHID adds 4 for the right-hand twin): LCtrl 0xE0 / RCtrl 0xE4,
                // LShift 0xE1 / RShift 0xE5, LAlt 0xE2 / RAlt 0xE6, LWin 0xE3 / RWin 0xE7.
                case Key.LeftCtrl: return 0xE0;
                case Key.LeftShift: return 0xE1;
                case Key.LeftAlt: return 0xE2;
                case Key.LWin: return 0xE3;
                case Key.RightCtrl: return 0xE4;
                case Key.RightShift: return 0xE5;
                case Key.RightAlt: return 0xE6;
                case Key.RWin: return 0xE7;

                default: return 0;
            }
        }
    }
}
