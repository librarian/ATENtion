using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace ATENtion.App
{
    /// <summary>
    /// Version-independent application settings stored under %LOCALAPPDATA%\ATENtion.
    /// </summary>
    /// <remarks>
    /// ApplicationSettingsBase places user.config below a directory derived from both the executable
    /// location and assembly version. Downloading each ATENtion release into a new directory therefore
    /// made the framework treat it as a different application. This store uses one stable path instead.
    /// Password values remain DPAPI blobs; this file never contains a plaintext password.
    /// </remarks>
    internal sealed class StableSettingsStore
    {
        private static readonly object Sync = new object();
        private static StableSettingsStore _instance;
        private static string _settingsPathOverride;
        private static bool _skipLegacyMigration;

        internal sealed class StoredConnectionProfile
        {
            public string Id = "";
            public string Name = "";
            public string Host = "";
            public string User = "ADMIN";
            public string Port = "5900";
            public bool Arm = true;
            public bool Tls = true;
            public string ProtectedPassword = "";
        }

        public double WinLeft;
        public double WinTop;
        public double WinWidth;
        public double WinHeight;
        public bool Maximized;
        public bool ShowLog;
        public bool ActualSize;
        public bool SmoothScaling;
        public bool AutoReconnect = true;
        public bool EnableLogging;
        public int MouseMode = 1;
        public string LastProfileId = "";
        public readonly List<StoredConnectionProfile> Profiles = new List<StoredConnectionProfile>();

        internal static string SettingsPath => _settingsPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ATENtion", "settings.xml");

        internal static void ResetForTests(string settingsPath)
        {
            lock (Sync)
            {
                _instance = null;
                _settingsPathOverride = settingsPath;
                _skipLegacyMigration = true;
            }
        }

        public static StableSettingsStore Get()
        {
            lock (Sync)
            {
                if (_instance == null) _instance = Load();
                return _instance;
            }
        }

        public void Save()
        {
            lock (Sync)
            {
                string path = SettingsPath;
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);

                var ui = new XElement("Ui",
                    Attr("left", WinLeft), Attr("top", WinTop),
                    Attr("width", WinWidth), Attr("height", WinHeight),
                    new XAttribute("maximized", Maximized),
                    new XAttribute("showLog", ShowLog),
                    new XAttribute("actualSize", ActualSize),
                    new XAttribute("smoothScaling", SmoothScaling),
                    new XAttribute("autoReconnect", AutoReconnect),
                    new XAttribute("enableLogging", EnableLogging),
                    new XAttribute("mouseMode", MouseMode));

                var profiles = new XElement("Connections",
                    new XAttribute("lastProfileId", LastProfileId ?? ""));
                foreach (var profile in Profiles)
                {
                    profiles.Add(new XElement("Profile",
                        new XAttribute("id", profile.Id ?? ""),
                        new XAttribute("name", profile.Name ?? ""),
                        new XAttribute("host", profile.Host ?? ""),
                        new XAttribute("user", profile.User ?? ""),
                        new XAttribute("port", profile.Port ?? ""),
                        new XAttribute("arm", profile.Arm),
                        new XAttribute("tls", profile.Tls),
                        new XAttribute("password", profile.ProtectedPassword ?? "")));
                }

                var document = new XDocument(
                    new XElement("ATENtionSettings", new XAttribute("formatVersion", "1"), ui, profiles));
                string temporary = path + ".tmp";
                document.Save(temporary);
                if (!File.Exists(path))
                {
                    File.Move(temporary, path);
                    return;
                }

                try { File.Replace(temporary, path, null); }
                catch
                {
                    File.Copy(temporary, path, true);
                    File.Delete(temporary);
                }
            }
        }

        private static StableSettingsStore Load()
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return MigrateLegacy();

            try
            {
                var root = XDocument.Load(path).Root;
                if (root == null) return MigrateLegacy();
                var result = new StableSettingsStore();
                var ui = root.Element("Ui");
                if (ui != null)
                {
                    result.WinLeft = Double(ui, "left");
                    result.WinTop = Double(ui, "top");
                    result.WinWidth = Double(ui, "width");
                    result.WinHeight = Double(ui, "height");
                    result.Maximized = Bool(ui, "maximized");
                    result.ShowLog = Bool(ui, "showLog");
                    result.ActualSize = Bool(ui, "actualSize");
                    result.SmoothScaling = Bool(ui, "smoothScaling");
                    result.AutoReconnect = Bool(ui, "autoReconnect", true);
                    result.EnableLogging = Bool(ui, "enableLogging");
                    result.MouseMode = Int(ui, "mouseMode", 1);
                }

                var connections = root.Element("Connections");
                if (connections != null)
                {
                    result.LastProfileId = Text(connections, "lastProfileId");
                    foreach (var item in connections.Elements("Profile"))
                    {
                        string host = Text(item, "host").Trim();
                        if (host.Length == 0) continue;
                        result.Profiles.Add(new StoredConnectionProfile
                        {
                            Id = NonEmpty(Text(item, "id"), Guid.NewGuid().ToString("N")),
                            Name = Text(item, "name"),
                            Host = host,
                            User = NonEmpty(Text(item, "user"), "ADMIN"),
                            Port = NonEmpty(Text(item, "port"), "5900"),
                            Arm = Bool(item, "arm", true),
                            Tls = Bool(item, "tls", true),
                            ProtectedPassword = Text(item, "password"),
                        });
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Core.Diagnostics.KvmLog.Error("loading stable settings", ex);
                return MigrateLegacy();
            }
        }

        private static StableSettingsStore MigrateLegacy()
        {
            var result = new StableSettingsStore();
            if (_skipLegacyMigration) return result;
            try
            {
                var old = AppSettingsStore.Get();
                result.WinLeft = old.WinLeft;
                result.WinTop = old.WinTop;
                result.WinWidth = old.WinWidth;
                result.WinHeight = old.WinHeight;
                result.Maximized = old.Maximized;
                result.ShowLog = old.ShowLog;
                result.ActualSize = old.ActualSize;
                result.SmoothScaling = old.SmoothScaling;
                result.AutoReconnect = old.AutoReconnect;
                result.EnableLogging = old.EnableLogging;
                result.MouseMode = old.MouseMode;
                if (!string.IsNullOrWhiteSpace(old.ConnHost))
                {
                    var migrated = new StoredConnectionProfile
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = old.ConnHost.Trim(),
                        Host = old.ConnHost.Trim(),
                        User = NonEmpty(old.ConnUser, "ADMIN"),
                        Port = NonEmpty(old.ConnPort, "5900"),
                        Arm = old.ConnArm,
                        Tls = old.ConnTls,
                        ProtectedPassword = old.ConnPwd ?? "",
                    };
                    result.Profiles.Add(migrated);
                    result.LastProfileId = migrated.Id;
                }
            }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("migrating legacy settings", ex); }

            try { result.Save(); }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("saving migrated settings", ex); }
            return result;
        }

        private static XAttribute Attr(string name, double value) =>
            new XAttribute(name, value.ToString("R", CultureInfo.InvariantCulture));
        private static string Text(XElement element, string name) => (string)element.Attribute(name) ?? "";
        private static string NonEmpty(string value, string fallback) =>
            string.IsNullOrEmpty(value) ? fallback : value;
        private static double Double(XElement element, string name)
        {
            double.TryParse(Text(element, name), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value);
            return value;
        }
        private static bool Bool(XElement element, string name, bool fallback = false) =>
            bool.TryParse(Text(element, name), out bool value) ? value : fallback;
        private static int Int(XElement element, string name, int fallback) =>
            int.TryParse(Text(element, name), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    /// <summary>The old version/path-scoped user.config store, retained only for one-time migration.</summary>
    internal sealed class AppSettingsStore : ApplicationSettingsBase
    {
        private static readonly AppSettingsStore Instance =
            (AppSettingsStore)Synchronized(new AppSettingsStore());
        private static bool _upgradeChecked;

        public static AppSettingsStore Get()
        {
            if (!_upgradeChecked)
            {
                _upgradeChecked = true;
                try
                {
                    if (Instance.UpgradeRequired)
                    {
                        Instance.Upgrade();
                        Instance.UpgradeRequired = false;
                        Instance.Save();
                    }
                }
                catch { }
            }
            return Instance;
        }

        [UserScopedSetting, DefaultSettingValue("True")]
        public bool UpgradeRequired { get => (bool)this[nameof(UpgradeRequired)]; set => this[nameof(UpgradeRequired)] = value; }
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinLeft { get => (double)this[nameof(WinLeft)]; set => this[nameof(WinLeft)] = value; }
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinTop { get => (double)this[nameof(WinTop)]; set => this[nameof(WinTop)] = value; }
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinWidth { get => (double)this[nameof(WinWidth)]; set => this[nameof(WinWidth)] = value; }
        [UserScopedSetting, DefaultSettingValue("0")]
        public double WinHeight { get => (double)this[nameof(WinHeight)]; set => this[nameof(WinHeight)] = value; }
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool Maximized { get => (bool)this[nameof(Maximized)]; set => this[nameof(Maximized)] = value; }
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool ShowLog { get => (bool)this[nameof(ShowLog)]; set => this[nameof(ShowLog)] = value; }
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool ActualSize { get => (bool)this[nameof(ActualSize)]; set => this[nameof(ActualSize)] = value; }
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool SmoothScaling { get => (bool)this[nameof(SmoothScaling)]; set => this[nameof(SmoothScaling)] = value; }
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool AutoReconnect { get => (bool)this[nameof(AutoReconnect)]; set => this[nameof(AutoReconnect)] = value; }
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool EnableLogging { get => (bool)this[nameof(EnableLogging)]; set => this[nameof(EnableLogging)] = value; }
        [UserScopedSetting, DefaultSettingValue("1")]
        public int MouseMode { get => (int)this[nameof(MouseMode)]; set => this[nameof(MouseMode)] = value; }
        [UserScopedSetting, DefaultSettingValue("")]
        public string ConnHost { get => (string)this[nameof(ConnHost)]; set => this[nameof(ConnHost)] = value; }
        [UserScopedSetting, DefaultSettingValue("ADMIN")]
        public string ConnUser { get => (string)this[nameof(ConnUser)]; set => this[nameof(ConnUser)] = value; }
        [UserScopedSetting, DefaultSettingValue("5900")]
        public string ConnPort { get => (string)this[nameof(ConnPort)]; set => this[nameof(ConnPort)] = value; }
        [UserScopedSetting, DefaultSettingValue("")]
        public string ConnToken { get => (string)this[nameof(ConnToken)]; set => this[nameof(ConnToken)] = value; }
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool ConnArm { get => (bool)this[nameof(ConnArm)]; set => this[nameof(ConnArm)] = value; }
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool ConnTls { get => (bool)this[nameof(ConnTls)]; set => this[nameof(ConnTls)] = value; }
        [UserScopedSetting, DefaultSettingValue("")]
        public string ConnPwd { get => (string)this[nameof(ConnPwd)]; set => this[nameof(ConnPwd)] = value; }
    }
}
