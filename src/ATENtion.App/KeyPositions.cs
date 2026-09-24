namespace ATENtion.App
{
    /// <summary>USB HID usages for the keys whose meaning depends on the keyboard layout, by position.</summary>
    /// <remarks>
    /// A KVM should send which key was pressed, not which character it makes: the host applies its
    /// own layout. Mapping from the Windows virtual key instead typed the US character, so on a
    /// German keyboard Y and Z came out swapped and the key between left Shift and Z sent nothing.
    /// Scan codes (set 1) name the physical key, and each maps to the HID usage at that position.
    /// </remarks>
    internal static class KeyPositions
    {
        private static readonly byte[] Table = Build();

        /// <summary>The HID usage for a main-block scan code, or 0 when it is not a layout-dependent key.</summary>
        internal static uint HidFromScanCode(uint scanCode) =>
            scanCode < (uint)Table.Length ? Table[scanCode] : 0u;

        private static byte[] Build()
        {
            var t = new byte[0x80];
            // Number row: 1..9, 0, then - and =.
            for (int i = 0; i < 9; i++) t[0x02 + i] = (byte)(0x1E + i);
            t[0x0B] = 0x27; t[0x0C] = 0x2D; t[0x0D] = 0x2E;
            // Q W E R T Y U I O P [ ]
            byte[] top = { 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30 };
            for (int i = 0; i < top.Length; i++) t[0x10 + i] = top[i];
            // A S D F G H J K L ; ' `
            byte[] home = { 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0x35 };
            for (int i = 0; i < home.Length; i++) t[0x1E + i] = home[i];
            // The key beside Enter: backslash on ANSI boards, # on ISO ones. Hosts treat the two
            // usages alike, so the ANSI one serves both.
            t[0x2B] = 0x31;
            // Z X C V B N M , . /
            byte[] bottom = { 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38 };
            for (int i = 0; i < bottom.Length; i++) t[0x2C + i] = bottom[i];
            t[0x56] = 0x64;   // ISO key between left Shift and Z (< > |)
            t[0x73] = 0x87;   // Japanese Ro
            t[0x7D] = 0x89;   // Japanese Yen
            return t;
        }
    }
}
