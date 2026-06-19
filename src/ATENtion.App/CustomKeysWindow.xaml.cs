using System.Collections.Generic;
using System.Windows;

namespace ATENtion.App
{
    /// <summary>The Custom Keys dialog: choose modifiers and a key, and send them to the host as one combination.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Lets the user assemble a key combination (any of Ctrl, Alt, Shift, and Win, plus one
    /// key from a curated list) and reports it back as the ordered USB-HID usages to press, with a
    /// readable label.
    /// </para>
    /// <para>
    /// OPERATION - The key list is built once from common keys. On send, the checked modifiers are
    /// added first, in a fixed order, followed by the selected key, and the same order is used for the
    /// readable label. The caller presses the usages in order and releases them in reverse.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Uses the modifier usages from <see cref="HostKeys"/>. The result is sent through
    /// the main window's combo sender.
    /// </para>
    /// </remarks>
    public partial class CustomKeysWindow : Window
    {
        // The display-name to USB-HID-usage list for the key picker (a curated common subset).
        private static readonly List<KeyValuePair<string, uint>> Keys = BuildKeys();

        /// <summary>The USB-HID usages to press, modifiers first then the key; empty if cancelled.</summary>
        public uint[] Combo { get; private set; } = new uint[0];
        /// <summary>The readable label for the status flash, for example "Ctrl+Shift+Esc".</summary>
        public string ComboLabel { get; private set; } = "";

        /// <summary>Builds the dialog and populates the key picker.</summary>
        public CustomKeysWindow()
        {
            InitializeComponent();
            foreach (var k in Keys) KeyCombo.Items.Add(k.Key);
            KeyCombo.SelectedIndex = 0;
        }

        // Assembles the combination from the checked modifiers and the selected key, then closes with a
        // positive result.
        private void OnSend(object sender, RoutedEventArgs e)
        {
            var hids = new List<uint>();
            var parts = new List<string>();
            if (CtrlBox.IsChecked == true) { hids.Add(HostKeys.LCtrl); parts.Add("Ctrl"); }
            if (AltBox.IsChecked == true) { hids.Add(HostKeys.LAlt); parts.Add("Alt"); }
            if (ShiftBox.IsChecked == true) { hids.Add(HostKeys.LShift); parts.Add("Shift"); }
            if (WinBox.IsChecked == true) { hids.Add(HostKeys.LWin); parts.Add("Win"); }

            int idx = KeyCombo.SelectedIndex;
            if (idx >= 0)
            {
                hids.Add(Keys[idx].Value);
                parts.Add(Keys[idx].Key);
            }

            Combo = hids.ToArray();
            ComboLabel = string.Join("+", parts);
            DialogResult = true;
            Close();
        }

        // Builds the curated key list: letters, digits, function keys, and the common editing and
        // navigation keys, each paired with its USB-HID usage code.
        private static List<KeyValuePair<string, uint>> BuildKeys()
        {
            var list = new List<KeyValuePair<string, uint>>();
            void Add(string n, uint h) => list.Add(new KeyValuePair<string, uint>(n, h));
            for (char c = 'A'; c <= 'Z'; c++) Add(c.ToString(), (uint)(0x04 + (c - 'A')));
            for (char c = '1'; c <= '9'; c++) Add(c.ToString(), (uint)(0x1E + (c - '1')));
            Add("0", 0x27);
            for (int i = 1; i <= 12; i++) Add("F" + i, (uint)(0x3A + (i - 1)));
            Add("Enter", HostKeys.Enter); Add("Esc", HostKeys.Esc); Add("Tab", HostKeys.Tab);
            Add("Space", HostKeys.Space); Add("Backspace", 0x2A); Add("Delete", HostKeys.Delete);
            Add("Insert", 0x49); Add("Home", 0x4A); Add("End", 0x4D);
            Add("PageUp", 0x4B); Add("PageDown", 0x4E);
            Add("Up", 0x52); Add("Down", 0x51); Add("Left", 0x50); Add("Right", 0x4F);
            return list;
        }
    }
}
