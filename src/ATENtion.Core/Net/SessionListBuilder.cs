using System.Collections.Generic;

namespace ATENtion.Core.Net
{
    /// <summary>Assembles the BMC's per-session privilege records into a list of console sessions.</summary>
    /// <remarks>
    /// The BMC sends one 0x39 record per active session, indexed downwards to 1, whenever the set
    /// changes. A record whose index is not below the previous one starts a new burst, and index 1
    /// completes it.
    /// </remarks>
    internal sealed class SessionListBuilder
    {
        private readonly List<string> _pending = new List<string>();
        private uint _lastIndex;

        /// <summary>Adds one record.</summary>
        /// <param name="completed">The whole list, when this record completed a burst.</param>
        /// <returns>True when a burst completed.</returns>
        public bool Add(uint index, string info, out IReadOnlyList<string> completed)
        {
            if (index >= _lastIndex) _pending.Clear();
            _lastIndex = index;
            if (!string.IsNullOrWhiteSpace(info)) _pending.Add(info);
            completed = null;
            if (index > 1) return false;
            completed = _pending.ToArray();
            _lastIndex = 0;
            return true;
        }
    }
}
