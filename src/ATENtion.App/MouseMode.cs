namespace ATENtion.App
{
    /// <summary>The BMC pointer mode, matching the ATEN client's three modes.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Names the pointer mode the BMC is told to use, and is also the value persisted in
    /// settings and the menu selection.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Each value is the on-wire mode byte sent in the SetMouseMode record
    /// (KvmVideoSession.SendMouseMode, [0x36][0][mode]) and is the value stored in settings, so the
    /// numeric values must not change.
    /// </para>
    /// <para>
    /// PROVENANCE - Mode values from the native setMouseMode.
    /// </para>
    /// </remarks>
    internal enum MouseMode
    {
        /// <summary>Absolute pointer mode (1): the coordinates are the absolute pointer position.</summary>
        Absolute = 1,
        /// <summary>Relative pointer mode (2): the BMC's NORMAL mode.</summary>
        Relative = 2,
        /// <summary>Single pointer mode (3).</summary>
        Single = 3,
    }
}
