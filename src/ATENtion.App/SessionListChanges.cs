using System.Collections.Generic;
using System.Linq;

namespace ATENtion.App
{
    /// <summary>Who joined or left between two of the BMC's console-session lists.</summary>
    /// <remarks>
    /// Each entry is the BMC's "&lt;session id&gt; &lt;user&gt; &lt;client address&gt;" string. The session id
    /// changes on every connection, so a user who reconnects shows as leaving and joining.
    /// </remarks>
    internal static class SessionListChanges
    {
        /// <summary>A short notice for the status bar, or null when nobody joined or left.</summary>
        internal static string Notice(IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            var joined = after.Except(before).Select(Describe).ToList();
            var left = before.Except(after).Select(Describe).ToList();
            var parts = new List<string>();
            if (joined.Count > 0) parts.Add(string.Join(", ", joined) + " connected");
            if (left.Count > 0) parts.Add(string.Join(", ", left) + " disconnected");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        /// <summary>"ADMIN from 10.0.0.2" from "283 ADMIN 10.0.0.2"; anything else unchanged.</summary>
        internal static string Describe(string entry)
        {
            string[] fields = (entry ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 3 ? $"{fields[1]} from {fields[2]}" : (entry ?? "").Trim();
        }
    }
}
