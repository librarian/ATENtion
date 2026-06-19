using System.Configuration;

namespace ATENtion.App
{
    /// <summary>
    /// The single user-scoped settings store, persisted to user.config by the framework's settings
    /// provider, onto which the UI and connection preferences are mapped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Holds every persisted setting in one place. <see cref="UiSettings"/> and
    /// <see cref="ConnectSettings"/> map their fields onto these keys, so all application state lives
    /// in one file.
    /// </para>
    /// <para>
    /// OPERATION - The store is a synchronised singleton over ApplicationSettingsBase, written to
    /// user.config under the local application data folder. On first use it applies the standard
    /// upgrade step: user.config is pathed by assembly version, so after a version bump the previous
    /// version's values are carried forward by Upgrade, guarded so a first run or an unreadable prior
    /// config falls back to defaults. Persisting to user.config rather than the generated exe.config
    /// means settings survive a rebuild.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Process-wide singleton. The connection password is stored only as the
    /// DPAPI-protected <see cref="ConnPwd"/> blob. No setting here holds a secret in plain text.
    /// </para>
    /// </remarks>
    internal sealed class AppSettingsStore : ApplicationSettingsBase
    {
        private static readonly AppSettingsStore _instance =
            (AppSettingsStore)Synchronized(new AppSettingsStore());

        private static bool _upgradeChecked;

        /// <summary>
        /// Returns the shared instance, performing the one-time version-upgrade carry-forward on first
        /// use.
        /// </summary>
        /// <returns>The singleton settings store.</returns>
        public static AppSettingsStore Get()
        {
            if (!_upgradeChecked)
            {
                _upgradeChecked = true;
                try
                {
                    if (_instance.UpgradeRequired)
                    {
                        _instance.Upgrade();
                        _instance.UpgradeRequired = false;
                        _instance.Save();
                    }
                }
                catch { /* first run or unreadable prior config: defaults apply */ }
            }
            return _instance;
        }

        /// <summary>True until the first run carries settings forward from a previous version.</summary>
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool UpgradeRequired
        {
            get => (bool)this[nameof(UpgradeRequired)];
            set => this[nameof(UpgradeRequired)] = value;
        }

        // --- Window geometry (0 = unset; the main window guards on width > 200 and on-screen position). ---

        /// <summary>The saved window left edge; 0 means unset.</summary>
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinLeft { get => (double)this[nameof(WinLeft)]; set => this[nameof(WinLeft)] = value; }

        /// <summary>The saved window top edge; 0 means unset.</summary>
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinTop { get => (double)this[nameof(WinTop)]; set => this[nameof(WinTop)] = value; }

        /// <summary>The saved window width; 0 means unset.</summary>
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinWidth { get => (double)this[nameof(WinWidth)]; set => this[nameof(WinWidth)] = value; }

        /// <summary>The saved window height; 0 means unset.</summary>
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinHeight { get => (double)this[nameof(WinHeight)]; set => this[nameof(WinHeight)] = value; }

        /// <summary>True if the window was maximised.</summary>
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool Maximized { get => (bool)this[nameof(Maximized)]; set => this[nameof(Maximized)] = value; }

        // --- View preferences. ---

        /// <summary>True to show the log panel.</summary>
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool ShowLog { get => (bool)this[nameof(ShowLog)]; set => this[nameof(ShowLog)] = value; }

        /// <summary>True to display the framebuffer at actual size.</summary>
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool ActualSize { get => (bool)this[nameof(ActualSize)]; set => this[nameof(ActualSize)] = value; }

        /// <summary>True for smooth scaling; false for crisp nearest-neighbour.</summary>
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool SmoothScaling { get => (bool)this[nameof(SmoothScaling)]; set => this[nameof(SmoothScaling)] = value; }

        /// <summary>True to auto-reconnect after a drop.</summary>
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool AutoReconnect { get => (bool)this[nameof(AutoReconnect)]; set => this[nameof(AutoReconnect)] = value; }

        /// <summary>True to enable diagnostic logging.</summary>
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool EnableLogging { get => (bool)this[nameof(EnableLogging)]; set => this[nameof(EnableLogging)] = value; }

        /// <summary>The BMC pointer mode (1 = Absolute, 2 = Relative, 3 = Single).</summary>
        [UserScopedSetting, DefaultSettingValue("1")]
        public int MouseMode { get => (int)this[nameof(MouseMode)]; set => this[nameof(MouseMode)] = value; }

        // --- Connect dialog inputs (ConnPwd is the DPAPI-protected blob, never plain text). ---

        /// <summary>The saved BMC host.</summary>
        [UserScopedSetting, DefaultSettingValue("")]
        public string ConnHost { get => (string)this[nameof(ConnHost)]; set => this[nameof(ConnHost)] = value; }

        /// <summary>The saved BMC user name.</summary>
        [UserScopedSetting, DefaultSettingValue("ADMIN")]
        public string ConnUser { get => (string)this[nameof(ConnUser)]; set => this[nameof(ConnUser)] = value; }

        /// <summary>The saved connection port.</summary>
        [UserScopedSetting, DefaultSettingValue("5900")]
        public string ConnPort { get => (string)this[nameof(ConnPort)]; set => this[nameof(ConnPort)] = value; }

        /// <summary>The saved session token.</summary>
        [UserScopedSetting, DefaultSettingValue("")]
        public string ConnToken { get => (string)this[nameof(ConnToken)]; set => this[nameof(ConnToken)] = value; }

        /// <summary>True to arm the session through the web API before connecting.</summary>
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool ConnArm { get => (bool)this[nameof(ConnArm)]; set => this[nameof(ConnArm)] = value; }

        /// <summary>True to use TLS for the connection.</summary>
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool ConnTls { get => (bool)this[nameof(ConnTls)]; set => this[nameof(ConnTls)] = value; }

        /// <summary>The DPAPI-protected BMC password blob (base64); never plain text.</summary>
        [UserScopedSetting, DefaultSettingValue("")]
        public string ConnPwd { get => (string)this[nameof(ConnPwd)]; set => this[nameof(ConnPwd)] = value; }
    }
}
