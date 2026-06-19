namespace ATENtion.Core.Protocol
{
    /// <summary>The chassis power actions the BMC accepts over the RFB control channel.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Enumerates the four power operations, with each value being the exact code byte
    /// sent on the wire.
    /// </para>
    /// <para>
    /// PROVENANCE - Codes from the native power exports; each setPower* routine calls RFBScreen
    /// vtable+8 (iKVM64.dll FUN_180013150) to send the record. VERIFIED
    /// LIVE: power reset operates the target chassis.
    /// </para>
    /// </remarks>
    public enum PowerCommand : byte
    {
        /// <summary>Immediate power off (native setPowerOff).</summary>
        Off = 0,
        /// <summary>Power on (native setPowerOn).</summary>
        On = 1,
        /// <summary>Hard reset (native setPowerReset).</summary>
        Reset = 2,
        /// <summary>Graceful ACPI shutdown request (native setSoftPowerOff).</summary>
        SoftOff = 3,
    }

    /// <summary>Builds the two-byte OEM power-control record sent to the BMC.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Frames a <see cref="PowerCommand"/> as the on-wire record the BMC's power path
    /// expects.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   [0x1a][code]   = 2 bytes
    /// </code>
    /// </para>
    /// <para>
    /// PROVENANCE - Native power send path iKVM64.dll FUN_180013150.
    /// VERIFIED LIVE.
    /// </para>
    /// </remarks>
    public static class PowerControl
    {
        /// <summary>The client OEM request message type that prefixes a power record.</summary>
        public const byte MessageType = RfbMessageType.ClientRequest0x1a; // 0x1a

        /// <summary>Builds the two-byte power record for the given command.</summary>
        /// <param name="command">The power action to encode.</param>
        /// <returns>A new two-byte record, <c>[0x1a][code]</c>.</returns>
        public static byte[] Build(PowerCommand command) =>
            new byte[] { MessageType, (byte)command };
    }
}
