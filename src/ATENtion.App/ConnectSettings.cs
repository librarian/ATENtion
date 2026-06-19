using System;
using System.Security.Cryptography;
using System.Text;

namespace ATENtion.App
{
    /// <summary>The Connect dialog inputs, persisted between runs, with the password protected at rest.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the values the <see cref="ConnectWindow"/> collects so they can be offered
    /// again on the next run: the host, user, port, token, password, and the arm and TLS toggles.
    /// </para>
    /// <para>
    /// OPERATION - A thin data carrier mapped onto <see cref="AppSettingsStore"/>. Every field is
    /// stored as plain text except the BMC password, which is sealed with DPAPI scoped to the current
    /// Windows user, so it is never written or read as plain text. The session token is per-session
    /// and expires, but is remembered for convenience.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Backed by <see cref="AppSettingsStore"/>. The password protection uses the
    /// Windows Data Protection API.
    /// </para>
    /// <para>
    /// RESTRICTIONS - <see cref="Password"/> holds plain text only in memory. On disk it exists solely
    /// as the DPAPI blob. A protect or unprotect failure yields an empty value rather than exposing or
    /// retaining plain text.
    /// </para>
    /// </remarks>
    public sealed class ConnectSettings
    {
        /// <summary>The BMC host name or address.</summary>
        public string Host = "";
        /// <summary>The BMC user name; defaults to "ADMIN".</summary>
        public string User = "ADMIN";
        /// <summary>The connection port; defaults to "5900".</summary>
        public string Port = "5900";
        /// <summary>The per-session KVM token.</summary>
        public string Token = "";
        /// <summary>The BMC password; plain text in memory only, persisted DPAPI-protected as ConnPwd.</summary>
        public string Password = "";
        /// <summary>True to arm the session through the web API before connecting.</summary>
        public bool Arm = true;
        /// <summary>True to use TLS for the connection.</summary>
        public bool Tls = true;

        /// <summary>Loads the Connect settings from the store, unprotecting the password.</summary>
        /// <returns>The persisted settings, or the defaults on first run.</returns>
        public static ConnectSettings Load()
        {
            var st = AppSettingsStore.Get();
            return new ConnectSettings
            {
                Host = st.ConnHost ?? "",
                User = string.IsNullOrEmpty(st.ConnUser) ? "ADMIN" : st.ConnUser,
                Port = string.IsNullOrEmpty(st.ConnPort) ? "5900" : st.ConnPort,
                Token = st.ConnToken ?? "",
                Arm = st.ConnArm,
                Tls = st.ConnTls,
                Password = Unprotect(st.ConnPwd),
            };
        }

        /// <summary>Saves the current Connect settings to the store, protecting the password.</summary>
        public void Save()
        {
            var st = AppSettingsStore.Get();
            st.ConnHost = Host ?? "";
            st.ConnUser = User ?? "";
            st.ConnPort = Port ?? "";
            st.ConnToken = Token ?? "";
            st.ConnArm = Arm;
            st.ConnTls = Tls;
            st.ConnPwd = Protect(Password);
            st.Save();
        }

        // Seals a plain-text secret with DPAPI (current-user scope) and returns it base64-encoded.
        // On failure it returns empty, so nothing is persisted rather than persisting plain text.
        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] enc = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("DPAPI protect", ex); return ""; } // on failure, persist nothing rather than plain text
        }

        // Unseals a base64 DPAPI blob back to plain text, returning empty if it cannot be read.
        private static string Unprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(
                    Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("DPAPI unprotect", ex); return ""; }
        }
    }
}
