using System.Collections.Generic;

namespace ATENtion.App
{
    /// <summary>
    /// USB-HID usage codes and a US-layout character map for injecting text and key combinations into
    /// the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Provides the named usage constants and a character-to-usage mapping used by the
    /// "paste clipboard as keystrokes" and "send keys" features, plus a helper that expands a string
    /// into the key-event sequence that types it.
    /// </para>
    /// <para>
    /// OPERATION - <see cref="TryMapChar"/> maps a character to its USB-HID usage and whether Shift is
    /// required, on a US layout. <see cref="TypeSequence"/> walks a string and yields press and release
    /// events, pressing or releasing Shift only when the required shift state changes between
    /// characters, so a run of upper-case letters holds Shift once rather than toggling per key.
    /// </para>
    /// <para>
    /// DEPENDENCIES - The usages it yields are sent through the keyboard input path, the same raw
    /// USB-HID usages <see cref="KeySymMap"/> produces.
    /// </para>
    /// <para>
    /// RESTRICTIONS - US layout only. Characters with no mapping, and carriage returns, are skipped
    /// rather than guessed.
    /// </para>
    /// <para>
    /// PROVENANCE - Raw USB-HID usage codes as sent on the wire.
    /// </para>
    /// </remarks>
    internal static class HostKeys
    {
        /// <summary>Left Ctrl USB-HID usage.</summary>
        public const uint LCtrl = 0xE0;
        /// <summary>Left Shift USB-HID usage.</summary>
        public const uint LShift = 0xE1;
        /// <summary>Left Alt USB-HID usage.</summary>
        public const uint LAlt = 0xE2;
        /// <summary>Left Windows-key USB-HID usage.</summary>
        public const uint LWin = 0xE3;
        /// <summary>Enter/Return USB-HID usage.</summary>
        public const uint Enter = 0x28;
        /// <summary>Escape USB-HID usage.</summary>
        public const uint Esc = 0x29;
        /// <summary>Tab USB-HID usage.</summary>
        public const uint Tab = 0x2B;
        /// <summary>Space USB-HID usage.</summary>
        public const uint Space = 0x2C;
        /// <summary>Delete USB-HID usage.</summary>
        public const uint Delete = 0x4C;
        /// <summary>F4 USB-HID usage.</summary>
        public const uint F4 = 0x3D;
        /// <summary>Print Screen USB-HID usage.</summary>
        public const uint PrintScreen = 0x46;

        /// <summary>Maps a character to its USB-HID usage and required shift state, on a US layout.</summary>
        /// <param name="c">The character to map.</param>
        /// <param name="hid">The mapped USB-HID usage; 0 when unmapped.</param>
        /// <param name="shift">True when Shift must be held to produce the character.</param>
        /// <returns>True when the character maps, false when it has no US-layout mapping.</returns>
        public static bool TryMapChar(char c, out uint hid, out bool shift)
        {
            shift = false; hid = 0;
            if (c >= 'a' && c <= 'z') { hid = (uint)(0x04 + (c - 'a')); return true; }
            if (c >= 'A' && c <= 'Z') { hid = (uint)(0x04 + (c - 'A')); shift = true; return true; }
            if (c >= '1' && c <= '9') { hid = (uint)(0x1E + (c - '1')); return true; }
            if (c == '0') { hid = 0x27; return true; }
            switch (c)
            {
                case ' ': hid = Space; return true;
                case '\t': hid = Tab; return true;
                case '\n': hid = Enter; return true;
                // Shifted top-row symbols.
                case '!': hid = 0x1E; shift = true; return true;
                case '@': hid = 0x1F; shift = true; return true;
                case '#': hid = 0x20; shift = true; return true;
                case '$': hid = 0x21; shift = true; return true;
                case '%': hid = 0x22; shift = true; return true;
                case '^': hid = 0x23; shift = true; return true;
                case '&': hid = 0x24; shift = true; return true;
                case '*': hid = 0x25; shift = true; return true;
                case '(': hid = 0x26; shift = true; return true;
                case ')': hid = 0x27; shift = true; return true;
                // OEM keys, unshifted then shifted.
                case '-': hid = 0x2D; return true;
                case '_': hid = 0x2D; shift = true; return true;
                case '=': hid = 0x2E; return true;
                case '+': hid = 0x2E; shift = true; return true;
                case '[': hid = 0x2F; return true;
                case '{': hid = 0x2F; shift = true; return true;
                case ']': hid = 0x30; return true;
                case '}': hid = 0x30; shift = true; return true;
                case '\\': hid = 0x31; return true;
                case '|': hid = 0x31; shift = true; return true;
                case ';': hid = 0x33; return true;
                case ':': hid = 0x33; shift = true; return true;
                case '\'': hid = 0x34; return true;
                case '"': hid = 0x34; shift = true; return true;
                case '`': hid = 0x35; return true;
                case '~': hid = 0x35; shift = true; return true;
                case ',': hid = 0x36; return true;
                case '<': hid = 0x36; shift = true; return true;
                case '.': hid = 0x37; return true;
                case '>': hid = 0x37; shift = true; return true;
                case '/': hid = 0x38; return true;
                case '?': hid = 0x38; shift = true; return true;
                default: return false;
            }
        }

        /// <summary>
        /// Expands a string into the key-event sequence that types it, holding Shift across a run and
        /// releasing it only when the required shift state changes.
        /// </summary>
        /// <param name="text">The text to type.</param>
        /// <returns>The ordered (usage, isDown) events; unmappable characters and carriage returns are skipped.</returns>
        public static IEnumerable<(uint hid, bool down)> TypeSequence(string text)
        {
            bool shiftHeld = false;
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (!TryMapChar(c, out uint hid, out bool needShift)) continue;
                if (needShift != shiftHeld)
                {
                    yield return (LShift, needShift); // press or release Shift to match this character
                    shiftHeld = needShift;
                }
                yield return (hid, true);
                yield return (hid, false);
            }
            if (shiftHeld) yield return (LShift, false); // release Shift left held at the end
        }
    }
}
