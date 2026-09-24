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

        [Theory]
        [InlineData(0x10u, 0x14u)]   // Q
        [InlineData(0x15u, 0x1Cu)]   // the Y position: Z on a German keyboard
        [InlineData(0x2Cu, 0x1Du)]   // the Z position: Y on a German keyboard
        [InlineData(0x56u, 0x64u)]   // the ISO key between left Shift and Z
        [InlineData(0x0Bu, 0x27u)]   // 0
        [InlineData(0x35u, 0x38u)]   // /
        [InlineData(0x2Bu, 0x31u)]   // backslash / ISO #
        public void A_Key_Is_Sent_By_Its_Position(uint scanCode, uint expectedHid)
        {
            Assert.Equal(expectedHid, KeyPositions.HidFromScanCode(scanCode));
        }

        [Theory]
        [InlineData(0x01u)]   // Escape
        [InlineData(0x1Cu)]   // Enter
        [InlineData(0x39u)]   // Space
        [InlineData(0x4Bu)]   // left arrow / keypad 4
        [InlineData(0x1FFu)]  // out of range
        public void Keys_That_Do_Not_Depend_On_Layout_Are_Left_To_The_Fixed_Table(uint scanCode)
        {
            Assert.Equal(0u, KeyPositions.HidFromScanCode(scanCode));
        }

        [Fact]
        public void Paste_Counts_What_It_Cannot_Type()
        {
            Assert.Equal(0, HostKeys.CountUntypable("Hello, world!\r\n"));
            Assert.Equal(2, HostKeys.CountUntypable("café €"));
            Assert.Equal(0, HostKeys.CountUntypable(null));
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
    public class SessionListChangeTests
    {
        private static readonly string[] Two = { "283 ADMIN 10.0.0.2", "726 ADMIN 10.0.0.1" };

        [Fact]
        public void Someone_Joining_Is_Announced()
        {
            Assert.Equal("OPERATOR from 10.0.0.9 connected",
                SessionListChanges.Notice(Two, Two.Concat(new[] { "801 OPERATOR 10.0.0.9" }).ToArray()));
        }

        [Fact]
        public void Someone_Leaving_Is_Announced()
        {
            Assert.Equal("ADMIN from 10.0.0.2 disconnected",
                SessionListChanges.Notice(Two, new[] { "726 ADMIN 10.0.0.1" }));
        }

        [Fact]
        public void A_Reconnect_Shows_As_Both()
        {
            // The session id changes on every connection.
            Assert.Equal("ADMIN from 10.0.0.2 connected; ADMIN from 10.0.0.2 disconnected",
                SessionListChanges.Notice(Two, new[] { "900 ADMIN 10.0.0.2", "726 ADMIN 10.0.0.1" }));
        }

        [Fact]
        public void No_Change_Means_No_Notice()
        {
            Assert.Null(SessionListChanges.Notice(Two, Two.Reverse().ToArray()));
        }

        [Theory]
        [InlineData("283 ADMIN 10.0.0.2", "ADMIN from 10.0.0.2")]
        [InlineData("odd-entry", "odd-entry")]
        [InlineData(null, "")]
        public void Entries_Read_As_User_And_Address(string entry, string expected)
        {
            Assert.Equal(expected, SessionListChanges.Describe(entry));
        }
    }
}
