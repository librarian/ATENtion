using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ATENtion.App
{
    /// <summary>A named BMC connection profile. Password plaintext exists only in process memory.</summary>
    public sealed class ConnectSettings
    {
        public string Id = "";
        public string Name = "";
        public string Host = "";
        public string User = "ADMIN";
        public string Port = "5900";
        public string Token = "";
        public string Password = "";
        public bool Arm = true;
        public bool Tls = true;

        public string DisplayName => !string.IsNullOrWhiteSpace(Name)
            ? Name : !string.IsNullOrWhiteSpace(Host) ? Host : "New server";

        public ConnectSettings Clone() => (ConnectSettings)MemberwiseClone();

        /// <summary>Loads all saved profiles, with the most recently connected profile first.</summary>
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

        public static ConnectSettings LoadLast()
        {
            var profiles = LoadProfiles();
            return profiles.FirstOrDefault();
        }

        /// <summary>Saves the editable profile, including a DPAPI-protected password.</summary>
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

        public static void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var store = StableSettingsStore.Get();
            store.Profiles.RemoveAll(p => p.Id == id);
            if (store.LastProfileId == id)
                store.LastProfileId = store.Profiles.FirstOrDefault()?.Id ?? "";
            store.Save();
        }

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
