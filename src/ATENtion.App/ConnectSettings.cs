using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ATENtion.App
{
    /// <summary>A named BMC connection profile. Password plaintext exists only in process memory.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the editable values the <see cref="ConnectWindow"/> collects for one
    /// BMC: its profile identity and name, host, user, port, session token, password, and the arm
    /// and TLS toggles.
    /// </para>
    /// <para>
    /// OPERATION - Maps a profile onto <see cref="StableSettingsStore.StoredConnectionProfile"/>.
    /// <see cref="Save"/> inserts or updates by <see cref="Id"/>, while <see cref="LoadProfiles"/>
    /// returns independent in-memory copies with the most recently connected profile first.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Backed by <see cref="StableSettingsStore"/>. Password protection uses the
    /// Windows Data Protection API scoped to the current Windows user.
    /// </para>
    /// <para>
    /// RESTRICTIONS - <see cref="Password"/> holds plaintext only in memory; the store receives
    /// only its DPAPI blob. <see cref="Token"/> is a short-lived session value and is not persisted.
    /// A protect or unprotect failure yields an empty password rather than storing plaintext.
    /// </para>
    /// </remarks>
    public sealed class ConnectSettings
    {
        /// <summary>The stable profile identifier used to update an existing stored profile.</summary>
        public string Id = "";
        /// <summary>The user-visible profile name; defaults to the host when saved blank.</summary>
        public string Name = "";
        /// <summary>The BMC host name or address.</summary>
        public string Host = "";
        /// <summary>The BMC user name; defaults to "ADMIN".</summary>
        public string User = "ADMIN";
        /// <summary>The connection port; defaults to "5900".</summary>
        public string Port = "5900";
        /// <summary>The short-lived KVM session token; retained in memory only.</summary>
        public string Token = "";
        /// <summary>The BMC password in memory; persisted only as a DPAPI-protected blob.</summary>
        public string Password = "";
        /// <summary>True to log in through the BMC web API and arm a fresh session.</summary>
        public bool Arm = true;
        /// <summary>True to use TLS for the KVM connection.</summary>
        public bool Tls = true;

        /// <summary>The profile name shown in the UI, falling back to the host or "New server".</summary>
        public string DisplayName => !string.IsNullOrWhiteSpace(Name)
            ? Name : !string.IsNullOrWhiteSpace(Host) ? Host : "New server";

        /// <summary>Returns a shallow copy suitable for editing without changing the selected profile.</summary>
        public ConnectSettings Clone() => (ConnectSettings)MemberwiseClone();

        /// <summary>Loads all saved profiles, with the most recently connected profile first.</summary>
        /// <returns>Independent profile objects with DPAPI-protected passwords unsealed in memory.</returns>
        public static List<ConnectSettings> LoadProfiles()
        {
            var store = StableSettingsStore.Get();
            return store.Profiles
                .OrderByDescending(p => p.Id == store.LastProfileId)
                .ThenBy(p => string.IsNullOrEmpty(p.Name) ? p.Host : p.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(FromStored)
                .ToList();
        }

        /// <summary>Loads the most recently connected profile, or the first sorted profile.</summary>
        /// <returns>The preferred profile, or <see langword="null"/> when none is saved.</returns>
        public static ConnectSettings LoadLast()
        {
            var profiles = LoadProfiles();
            return profiles.FirstOrDefault();
        }

        /// <summary>Saves the editable profile, including a DPAPI-protected password.</summary>
        /// <remarks>Creates an identifier for a new profile and makes this the last-used profile.</remarks>
        public void Save()
        {
            var store = StableSettingsStore.Get();
            if (string.IsNullOrEmpty(Id)) Id = Guid.NewGuid().ToString("N");
            var existing = store.Profiles.FirstOrDefault(p => p.Id == Id);
            if (existing == null)
            {
                existing = new StableSettingsStore.StoredConnectionProfile { Id = Id };
                store.Profiles.Add(existing);
            }
            existing.Name = string.IsNullOrWhiteSpace(Name) ? (Host ?? "").Trim() : Name.Trim();
            existing.Host = (Host ?? "").Trim();
            existing.User = string.IsNullOrWhiteSpace(User) ? "ADMIN" : User.Trim();
            existing.Port = string.IsNullOrWhiteSpace(Port) ? "5900" : Port.Trim();
            existing.Arm = Arm;
            existing.Tls = Tls;
            existing.ProtectedPassword = Protect(Password);
            store.LastProfileId = Id;
            store.Save();
            Name = existing.Name;
        }

        /// <summary>Decides which saved profile, if any, this one should be saved over.</summary>
        /// <param name="loadedName">The name of the profile the dialog was opened on, or empty.</param>
        /// <remarks>
        /// Identity follows the address. The dialog opens pre-filled with a saved profile, so typing a
        /// different host into it must not overwrite that profile. A host and port that already have
        /// a profile update it; any other address becomes a new profile. A name left over from the
        /// profile the dialog opened on is replaced, since it described a different server.
        /// </remarks>
        internal void ResolveIdentity(string loadedName)
        {
            var store = StableSettingsStore.Get();
            string previousId = Id ?? "";
            var match = store.Profiles.FirstOrDefault(p => SameEndpoint(p.Host, p.Port, Host, Port));
            Id = match?.Id ?? "";

            if (Id != previousId &&
                string.Equals((Name ?? "").Trim(), (loadedName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                Name = match != null ? match.Name : (Host ?? "").Trim();
        }

        /// <summary>The bare host name or address from what was typed in the host box.</summary>
        /// <remarks>
        /// Accepts a pasted web address ("https://10.0.0.5/"), a user prefix, a bracketed IPv6
        /// literal, or "host:port". The port is dropped there because the web login always uses
        /// HTTPS on 443 and the KVM port has its own field; a bare IPv6 address has several colons
        /// and is left alone.
        /// </remarks>
        internal static string NormalizeHost(string input)
        {
            string host = (input ?? "").Trim();
            int scheme = host.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) host = host.Substring(scheme + 3);
            int slash = host.IndexOf('/');
            if (slash >= 0) host = host.Substring(0, slash);
            int at = host.LastIndexOf('@');
            if (at >= 0) host = host.Substring(at + 1);
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                int close = host.IndexOf(']');
                return close > 1 ? host.Substring(1, close - 1) : host.Trim('[', ']');
            }
            int colon = host.IndexOf(':');
            if (colon >= 0 && colon == host.LastIndexOf(':')) host = host.Substring(0, colon);
            return host.Trim();
        }

        private static bool SameEndpoint(string hostA, string portA, string hostB, string portB) =>
            string.Equals((hostA ?? "").Trim(), (hostB ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
            NormalPort(portA) == NormalPort(portB);

        private static string NormalPort(string port) =>
            string.IsNullOrWhiteSpace(port) ? "5900" : port.Trim();

        /// <summary>Deletes a saved profile by identifier.</summary>
        /// <param name="id">The stable profile identifier; an empty value is ignored.</param>
        public static void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var store = StableSettingsStore.Get();
            store.RemoveProfile(id);
            if (store.LastProfileId == id)
                store.LastProfileId = store.Profiles.FirstOrDefault()?.Id ?? "";
            store.Save();
        }

        // Converts a persisted record into an independent editable profile and unseals its password.
        private static ConnectSettings FromStored(StableSettingsStore.StoredConnectionProfile value) =>
            new ConnectSettings
            {
                Id = value.Id,
                Name = value.Name,
                Host = value.Host,
                User = value.User,
                Port = value.Port,
                Password = Unprotect(value.ProtectedPassword),
                Arm = value.Arm,
                Tls = value.Tls,
            };

        // Seals a plaintext password with current-user DPAPI and returns a base64 blob.
        // On failure it returns empty so plaintext is never used as a persistence fallback.
        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.KvmLog.Error("DPAPI protect", ex);
                return "";
            }
        }

        // Unseals a base64 DPAPI blob in memory, returning empty if it cannot be decoded.
        private static string Unprotect(string protectedValue)
        {
            if (string.IsNullOrEmpty(protectedValue)) return "";
            try
            {
                byte[] decrypted = ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.KvmLog.Error("DPAPI unprotect", ex);
                return "";
            }
        }
    }
}
