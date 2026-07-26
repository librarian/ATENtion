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

        /// <summary>Deletes a saved profile by identifier.</summary>
        /// <param name="id">The stable profile identifier; an empty value is ignored.</param>
        public static void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var store = StableSettingsStore.Get();
            store.Profiles.RemoveAll(p => p.Id == id);
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
