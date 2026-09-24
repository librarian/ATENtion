namespace ATENtion.Core.Net
{
    /// <summary>Host power as reported by the BMC's host-status reply.</summary>
    public enum HostPowerState
    {
        /// <summary>Not reported yet.</summary>
        Unknown,
        /// <summary>The host is powered on.</summary>
        On,
        /// <summary>The host is powered off.</summary>
        Off,
    }
}
