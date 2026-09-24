using System.Collections.Generic;

namespace ATENtion.Core.Net
{
    /// <summary>The kinds of privilege record (0x39), from the record's second word.</summary>
    /// <remarks>Names follow how the vendor viewer (iKVM 1.69) handles each value.</remarks>
    public enum PrivilegeRecordType : uint
    {
        /// <summary>A console session is present: "&lt;sid&gt; &lt;user&gt; &lt;client address&gt;".</summary>
        SessionPresent = 1,
        /// <summary>The console session with this sid has ended.</summary>
        SessionEnded = 2,
        /// <summary>The web session that armed this console was logged out; the vendor viewer closes.</summary>
        WebLogout = 3,
        /// <summary>The BMC refused this console: the maximum number of sessions is reached.</summary>
        SessionLimit = 4,
        /// <summary>Keyboard lock-light state.</summary>
        KeyboardLights = 7,
        /// <summary>Disconnected for a BIOS upgrade.</summary>
        BiosUpgrade = 8,
        /// <summary>Disconnected for a BMC firmware upgrade.</summary>
        FirmwareUpgrade = 9,
        /// <summary>Disconnected for a RAID upgrade.</summary>
        RaidUpgrade = 10,
    }

    /// <summary>Keeps the BMC's list of console sessions from its privilege records.</summary>
    /// <remarks>
    /// Checked against an X10 board. When someone joins, the BMC sends every session as a
    /// SessionPresent record indexed downwards to 1, and that complete burst replaces the list; it
    /// then repeats the newcomer alone at index 1, which only updates that entry. When someone
    /// leaves it sends one SessionEnded record with their sid. Records of other kinds are not
    /// sessions and leave the list alone.
    /// </remarks>
    internal sealed class SessionListBuilder
    {
        private readonly List<string> _current = new List<string>();
        private readonly List<string> _burst = new List<string>();
        private uint _lastIndex;

        /// <summary>Applies one record.</summary>
        /// <param name="list">The whole list, when this record changed it.</param>
        /// <returns>True when the list changed.</returns>
        public bool Add(uint index, uint type, string info, out IReadOnlyList<string> list)
        {
            list = null;
            if (type == (uint)PrivilegeRecordType.SessionPresent)
            {
                bool inBurst = _lastIndex > 1 && index < _lastIndex;
                if (index > 1)
                {
                    if (!inBurst) _burst.Clear();   // not lower than the last: a new burst
                    _lastIndex = index;
                    if (!string.IsNullOrWhiteSpace(info)) _burst.Add(info);
                    return false;
                }
                _lastIndex = 0;
                if (inBurst)
                {
                    if (!string.IsNullOrWhiteSpace(info)) _burst.Add(info);
                    return Replace(_burst, out list);
                }
                // A lone record: this one session, added or brought up to date.
                if (string.IsNullOrWhiteSpace(info)) return false;
                string sid = SessionId(info);
                var updated = _current.FindAll(e => SessionId(e) != sid);
                updated.Add(info);
                return Replace(updated, out list);
            }
            if (type == (uint)PrivilegeRecordType.SessionEnded)
            {
                string sid = SessionId(info);
                var remaining = _current.FindAll(e => SessionId(e) != sid);
                return Replace(remaining, out list);
            }
            return false;
        }

        private bool Replace(List<string> entries, out IReadOnlyList<string> list)
        {
            list = null;
            if (entries.Count == _current.Count && entries.TrueForAll(_current.Contains)) return false;
            var copy = entries.ToArray();
            _current.Clear();
            _current.AddRange(copy);
            list = copy;
            return true;
        }

        private static string SessionId(string entry)
        {
            string trimmed = (entry ?? "").Trim();
            int space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed.Substring(0, space);
        }
    }
}
