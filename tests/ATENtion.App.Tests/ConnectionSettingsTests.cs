using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ATENtion.App.Tests
{
    // The settings store is a process-wide singleton, so tests that point it at a file must not
    // run alongside each other.
    [Collection(SettingsStoreCollection.Name)]
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
        public void Typing_A_New_Host_Into_A_Loaded_Profile_Saves_A_New_Profile()
        {
            var first = new ConnectSettings { Name = "Rack A", Host = "10.8.54.20", User = "admin" };
            first.Save();

            // The dialog opens on "Rack A"; the user types a different host over its fields.
            var edited = first.Clone();
            edited.Host = "10.8.54.21";
            edited.ResolveIdentity(first.Name);
            edited.Save();

            StableSettingsStore.ResetForTests(_settingsPath);
            var profiles = ConnectSettings.LoadProfiles();
            Assert.Equal(2, profiles.Count);
            Assert.Equal("Rack A", profiles.Single(p => p.Host == "10.8.54.20").Name);
            Assert.Equal("10.8.54.21", profiles.Single(p => p.Host == "10.8.54.21").Name);
            Assert.NotEqual(first.Id, profiles.Single(p => p.Host == "10.8.54.21").Id);
        }

        [Fact]
        public void Same_Host_Updates_The_Existing_Profile_And_Keeps_A_Rename()
        {
            var first = new ConnectSettings { Name = "Rack A", Host = "10.8.54.20", User = "admin" };
            first.Save();

            var edited = first.Clone();
            edited.Name = "Rack A (spare)";
            edited.User = "operator";
            edited.ResolveIdentity(first.Name);
            edited.Save();

            StableSettingsStore.ResetForTests(_settingsPath);
            var loaded = ConnectSettings.LoadProfiles().Single();
            Assert.Equal(first.Id, loaded.Id);
            Assert.Equal("Rack A (spare)", loaded.Name);
            Assert.Equal("operator", loaded.User);
        }

        [Fact]
        public void A_Host_Matching_Another_Saved_Profile_Updates_That_Profile()
        {
            var a = new ConnectSettings { Name = "Rack A", Host = "10.8.54.20" };
            a.Save();
            var b = new ConnectSettings { Name = "Rack B", Host = "10.8.54.21" };
            b.Save();

            var edited = a.Clone();
            edited.Host = "10.8.54.21";
            edited.ResolveIdentity(a.Name);
            edited.Save();

            StableSettingsStore.ResetForTests(_settingsPath);
            var profiles = ConnectSettings.LoadProfiles();
            Assert.Equal(2, profiles.Count);
            Assert.Equal("Rack B", profiles.Single(p => p.Id == b.Id).Name);
        }

        [Fact]
        public void Open_Tabs_Round_Trip_In_Order()
        {
            var ui = new UiSettings
            {
                ReopenTabs = true,
                OpenProfileIds = new System.Collections.Generic.List<string> { "b", "a", "c" },
                ActiveProfileId = "a",
            };
            ui.Save();

            StableSettingsStore.ResetForTests(_settingsPath);
            var loaded = UiSettings.Load();
            Assert.True(loaded.ReopenTabs);
            Assert.Equal(new[] { "b", "a", "c" }, loaded.OpenProfileIds);
            Assert.Equal("a", loaded.ActiveProfileId);
        }

        [Fact]
        public void An_Unreadable_Settings_File_Is_Kept_Rather_Than_Overwritten()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_settingsPath, "<ATENtionSettings><Connections><Profile host=");

            StableSettingsStore.ResetForTests(_settingsPath);
            StableSettingsStore.Get();

            Assert.Contains(Directory.GetFiles(_directory),
                f => Path.GetFileName(f).StartsWith("settings.xml.unreadable-", StringComparison.Ordinal));
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

    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SettingsStoreCollection
    {
        public const string Name = "Settings store";
    }
}
