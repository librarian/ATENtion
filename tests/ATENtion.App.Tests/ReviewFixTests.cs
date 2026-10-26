using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace ATENtion.App.Tests
{
    /// <summary>Typed host names, key positions and paste coverage.</summary>
    public class InputNormalisationTests
    {
        [Theory]
        [InlineData("10.0.0.5", "10.0.0.5")]
        [InlineData("  bmc.example  ", "bmc.example")]
        [InlineData("https://10.0.0.5/", "10.0.0.5")]
        [InlineData("https://10.0.0.5/cgi/url_redirect.cgi?url_name=topmenu", "10.0.0.5")]
        [InlineData("10.0.0.5:443", "10.0.0.5")]
        [InlineData("ADMIN@10.0.0.5", "10.0.0.5")]
        [InlineData("fd00::5", "fd00::5")]
        [InlineData("[fd00::5]", "fd00::5")]
        [InlineData("https://[fd00::5]:443/", "fd00::5")]
        [InlineData("", "")]
        public void A_Typed_Host_Becomes_A_Bare_Address(string typed, string expected)
        {
            Assert.Equal(expected, ConnectSettings.NormalizeHost(typed));
        }




    }

    /// <summary>Two running copies of ATENtion sharing one settings file.</summary>
    [Collection(SettingsStoreCollection.Name)]
    public sealed class SharedSettingsFileTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _settingsPath;

        public SharedSettingsFileTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ATENtion.App.Tests-" + Guid.NewGuid().ToString("N"));
            _settingsPath = Path.Combine(_directory, "settings.xml");
            StableSettingsStore.ResetForTests(_settingsPath);
        }

        [Fact]
        public void Saving_Keeps_Servers_Another_Window_Added()
        {
            new ConnectSettings { Name = "Mine", Host = "10.0.0.1" }.Save();
            AddProfileAsAnotherWindowWould("other-id", "Theirs", "10.0.0.2");

            new ConnectSettings { Name = "Mine too", Host = "10.0.0.3" }.Save();

            StableSettingsStore.ResetForTests(_settingsPath);
            var hosts = ConnectSettings.LoadProfiles().Select(p => p.Host).OrderBy(h => h).ToArray();
            Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" }, hosts);
        }

        [Fact]
        public void A_Deleted_Server_Is_Not_Brought_Back_From_The_File()
        {
            var mine = new ConnectSettings { Name = "Mine", Host = "10.0.0.1" };
            mine.Save();
            new ConnectSettings { Name = "Keep", Host = "10.0.0.2" }.Save();

            ConnectSettings.Delete(mine.Id);

            StableSettingsStore.ResetForTests(_settingsPath);
            Assert.Equal(new[] { "10.0.0.2" }, ConnectSettings.LoadProfiles().Select(p => p.Host).ToArray());
        }

        // Writes a profile straight into the file, as a second running copy saving would.
        private void AddProfileAsAnotherWindowWould(string id, string name, string host)
        {
            var doc = XDocument.Load(_settingsPath);
            doc.Root.Element("Connections").Add(new XElement("Profile",
                new XAttribute("id", id), new XAttribute("name", name), new XAttribute("host", host),
                new XAttribute("user", "ADMIN"), new XAttribute("port", "5900"),
                new XAttribute("arm", true), new XAttribute("tls", true), new XAttribute("password", "")));
            doc.Save(_settingsPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}

namespace ATENtion.App.Tests
{
    /// <summary>The status-bar notice for users joining and leaving a BMC.</summary>

}
