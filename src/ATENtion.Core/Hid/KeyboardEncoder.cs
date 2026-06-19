namespace ATENtion.Core.Hid
{
    /// <summary>
    /// Encodes one keyboard key transition (a press or a release) into the fixed-length
    /// ATEN/RFB KeyEvent record that the BMC's virtual USB-HID keyboard consumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Produces the eighteen-byte client-to-server record that reports a single
    /// change of state for one key. One call encodes one transition. The caller expresses a
    /// keystroke as a down record and, later, the matching up record.
    /// </para>
    /// <para>
    /// OPERATION - The record is the standard RFB KeyEvent (message type 4) carrying a
    /// down/up flag and a four-byte keysym. The ATEN firmware reads its input channel in
    /// fixed-size units, so the record is padded with trailing zero bytes to the same
    /// eighteen-byte length as the PointerEvent record (see <see cref="MouseEncoder"/>).
    /// The padding bytes are reserved and carry no meaning. The keysym is written
    /// big-endian. The whole RFB stream uses that byte order.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   [4][0][down][0][0][keysym:u32 BE][9 x 00]   = 18 bytes
    /// </code>
    /// Byte 0 is the message type (4). Byte 2 is 1 for a press and 0 for a release. Bytes
    /// 5..8 are the keysym, most-significant byte first. Bytes 1, 3, 4, and 9..17 are
    /// reserved and sent as zero.
    /// </para>
    /// <para>
    /// DEPENDENCIES - The caller supplies the keysym already resolved to the value the BMC
    /// expects. That value is a USB-HID usage code, not an X11 keysym: the native keyboard
    /// path maps the Windows virtual-key to a HID usage and sends it raw. This type performs
    /// no key mapping. <see cref="ATENtion.App.KeySymMap"/> owns that translation.
    /// </para>
    /// <para>
    /// RESTRICTIONS - The encoder is stateless and holds no key-repeat or modifier state.
    /// The modifier keys are reported as their own KeyEvent records, not as flags on a
    /// character event. The returned array is freshly allocated and owned by the caller.
    /// </para>
    /// <para>
    /// PROVENANCE - Native record builder iKVM64.dll FUN_18000e030, reached from
    /// RFBKeyboard vtable+8 (FUN_18000ddc0). VERIFIED LIVE
    /// (typing, including upper case, shifted symbols, both Shift keys, and Ctrl+Alt+Del).
    /// </para>
    /// </remarks>
    public sealed class KeyboardEncoder
    {
        /// <summary>RFB client-to-server message type for a KeyEvent record.</summary>
        public const int MessageType = 4;

        /// <summary>
        /// Builds the eighteen-byte KeyEvent record for one key transition.
        /// </summary>
        /// <param name="keysym">The key identifier the BMC expects, big-endian on the wire.
        /// This is a raw USB-HID usage code as resolved by <see cref="ATENtion.App.KeySymMap"/>,
        /// not an X11 keysym.</param>
        /// <param name="down">True for a key press, false for a key release.</param>
        /// <returns>A new eighteen-byte array ready to write to the RFB stream.</returns>
        public byte[] BuildKeyEvent(uint keysym, bool down)
        {
            var frame = new byte[18];
            frame[0] = MessageType;
            frame[1] = 0;                            // reserved
            frame[2] = (byte)(down ? 1 : 0);         // press = 1, release = 0
            // frame[3], frame[4]: reserved, left zero.
            // Keysym, most-significant byte first (big-endian, as for the whole RFB stream).
            frame[5] = (byte)(keysym >> 24);
            frame[6] = (byte)(keysym >> 16);
            frame[7] = (byte)(keysym >> 8);
            frame[8] = (byte)keysym;
            // frame[9..17]: reserved padding to the fixed 18-byte record length, left zero.
            return frame;
        }
    }
}
