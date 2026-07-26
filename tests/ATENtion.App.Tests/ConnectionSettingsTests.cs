using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ATENtion.App.Tests
{
    public sealed class ConnectionSettingsTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _settingsPath;

        public ConnectionSettingsTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ATENtion.App.Tests-" + Guid.NewGuid().ToString("N"));
            _settingsPath = Path.Combine(_directory, "settings.xml");
            StableSettingsStore.ResetForTests(_settingsPath);
        }

        [Fact]
        public void Profiles_Persist_Multiple_Servers_And_Corrected_Password()
        {
            var first = new ConnectSettings
            {
                Name = "Primary BMC",
                Host = "10.8.54.20",
                User = "admin",
                Password = "mistyped-password",
            };
            first.Save();
            first.Password = "corrected-password";
            first.Save();

            new ConnectSettings
            {
                Name = "Backup BMC",
                Host = "10.8.54.21",
                User = "operator",
                Password = "another-password",
            }.Save();

            StableSettingsStore.ResetForTests(_settingsPath);
            var profiles = ConnectSettings.LoadProfiles();

            Assert.Equal(2, profiles.Count);
            var loadedFirst = profiles.Single(p => p.Host == "10.8.54.20");
            Assert.Equal(first.Id, loadedFirst.Id);
            Assert.Equal("corrected-password", loadedFirst.Password);
            Assert.Equal("Primary BMC", loadedFirst.DisplayName);
            Assert.Equal("10.8.54.21", profiles[0].Host);

            string xml = File.ReadAllText(_settingsPath);
            Assert.DoesNotContain("mistyped-password", xml);
            Assert.DoesNotContain("corrected-password", xml);
            Assert.DoesNotContain("another-password", xml);
        }

        [Fact]
        public void UiSettings_Reload_From_Stable_File()
        {
            new UiSettings
            {
                Left = 123,
                Top = 456,
                Width = 1024,
                Height = 768,
                ShowLog = true,
                AutoReconnect = false,
            }.Save();

            StableSettingsStore.ResetForTests(_settingsPath);
            UiSettings loaded = UiSettings.Load();

            Assert.Equal(123, loaded.Left);
            Assert.Equal(456, loaded.Top);
            Assert.Equal(1024, loaded.Width);
            Assert.Equal(768, loaded.Height);
            Assert.True(loaded.ShowLog);
            Assert.False(loaded.AutoReconnect);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}
