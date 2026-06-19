namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// The RFB message-type bytes used by the ATEN protocol, in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Names the leading message-type byte for each message the client reads or sends,
    /// so the pump and the send paths can dispatch on a name rather than a bare number.
    /// </para>
    /// <para>
    /// OPERATION - The server-to-client values are the cases the receive pump switches on. The
    /// client-to-server values prefix the records the input and request paths build. Several of
    /// the server-to-client types are ATEN extensions beyond standard RFB and exist only to be
    /// drained by the exact byte count their handlers consume (see <see cref="ServerMessageReader"/>).
    /// </para>
    /// <para>
    /// PROVENANCE - Server-to-client values from the native receive pump iKVM64.dll FUN_180012cb0
    /// (RFBProtocol vtable+0x18); client-to-server values from the send paths.
    /// </para>
    /// </remarks>
    public static class RfbMessageType
    {
        // ----- server to client (read by the receive pump) -----

        /// <summary>FramebufferUpdate (0): carries a video tile packet. Handler FUN_180013b00.</summary>
        public const byte FramebufferUpdate = 0;
        /// <summary>Cursor-shape / secondary screen update (4). Handler FUN_1800139c0.</summary>
        public const byte ScreenUpdate4 = 4;
        /// <summary>ATEN status message (0x16): a one-byte body. Handler FUN_180009830.</summary>
        public const byte Server0x16 = 0x16;
        /// <summary>ATEN keyboard+mouse status (0x35): a five-byte body (2 + 3 fields).</summary>
        public const byte Keyboard0x35 = 0x35;
        /// <summary>ATEN mouse status (0x37): a three-byte body.</summary>
        public const byte Screen0x37 = 0x37;
        /// <summary>ATEN privilege/control grant (0x39): two words plus a 256-byte role string. Handler FUN_180011d80.</summary>
        public const byte Privilege0x39 = 0x39;
        /// <summary>ATEN screen status (0x3c): an eight-byte body (two words).</summary>
        public const byte Screen0x3c = 0x3c;

        // ----- client to server (built by the send paths) -----

        /// <summary>KeyEvent (4): a keyboard key transition. Builder FUN_18000e030.</summary>
        public const byte KeyEvent = 4;
        /// <summary>PointerEvent (5): a mouse move or click. Builder FUN_180011700.</summary>
        public const byte PointerEvent = 5;
        /// <summary>Client OEM request (0x1a): prefixes the power-control record. Send path FUN_180013150.</summary>
        public const byte ClientRequest0x1a = 0x1a;
    }
}
