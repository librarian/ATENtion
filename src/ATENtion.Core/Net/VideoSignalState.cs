namespace ATENtion.Core.Net
{
    /// <summary>Whether the BMC is producing a picture for this session.</summary>
    public enum VideoSignalState
    {
        /// <summary>Nothing decided yet: no frame and no no-signal marker seen.</summary>
        Unknown = 0,
        /// <summary>Frames are arriving, so the host is powered and producing video.</summary>
        Present,
        /// <summary>The BMC reported no video signal, so the host is off or emitting nothing.</summary>
        Absent,
    }
}
