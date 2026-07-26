using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using ATENtion.Core.Net;

namespace ATENtion.App
{
    /// <summary>The Connect dialog: collects the connection details and produces the connection options.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Gathers the host, credentials, port, token, and the arm and TLS choices, then builds
    /// the <see cref="KvmConnectionOptions"/> the main window connects with. It also loads the client
    /// certificate for the mutual-TLS leg and remembers the inputs for the next run.
    /// </para>
    /// <para>
    /// OPERATION - On construction the dialog restores the last-entered values and applies the
    /// arm-mode gating. When arming through the web API, the BMC supplies the session token and the
    /// transport choice (the port and whether TLS is used), so the manual token, port, and TLS fields
    /// are disabled and the BMC login fields are enabled. When not arming, the reverse holds and the
    /// user supplies the port, token, and TLS by hand. On connect it parses the fields, loads the
    /// client certificate, and saves the inputs.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads and writes <see cref="ConnectSettings"/>. It produces a
    /// <see cref="KvmConnectionOptions"/> and loads the client certificate from disk or the embedded
    /// resource.
    /// </para>
    /// <para>
    /// PROVENANCE - The transport choice the arm fields reflect comes from the JNLP stunEnable flag.
    /// </para>
    /// </remarks>
    public partial class ConnectWindow : Window
    {
        /// <summary>The connection options built from the dialog, valid once the dialog returns true.</summary>
        public KvmConnectionOptions Options { get; private set; }

        /// <summary>True when the session should be armed through the web API rather than connected directly.</summary>
        public bool ArmViaWeb { get; private set; }
        /// <summary>The BMC web user name, used when <see cref="ArmViaWeb"/> is true.</summary>
        public string BmcUser { get; private set; }
        /// <summary>The BMC web password, used when <see cref="ArmViaWeb"/> is true.</summary>
        public string BmcPassword { get; private set; }
        /// <summary>The selected editable server profile.</summary>
        public ConnectSettings Profile { get; private set; }

        private List<ConnectSettings> _profiles;
        private string _editingProfileId = "";
        private bool _loadingProfile;

        /// <summary>Builds the dialog, restoring the last-entered values and applying the arm gating.</summary>
        public ConnectWindow(ConnectSettings initial = null)
        {
            InitializeComponent();

            _profiles = ConnectSettings.LoadProfiles();
            ProfileBox.ItemsSource = _profiles;
            bool initialStillSaved = initial != null &&
                (string.IsNullOrEmpty(initial.Id) || _profiles.Any(p => p.Id == initial.Id));
            ConnectSettings selected = initialStillSaved
                ? initial.Clone() : _profiles.FirstOrDefault() ?? new ConnectSettings();
            if (!string.IsNullOrEmpty(selected.Id))
            {
                _loadingProfile = true;
                ProfileBox.SelectedItem = _profiles.FirstOrDefault(p => p.Id == selected.Id);
                _loadingProfile = false;
            }
            LoadProfile(selected);

            // Apply the enable/disable gating now: the Checked/Unchecked events do not fire on load.
            ApplyArmGating();
        }

        private void LoadProfile(ConnectSettings value)
        {
            _loadingProfile = true;
            try
            {
                _editingProfileId = value?.Id ?? "";
                ProfileNameBox.Text = value?.Name ?? "";
                HostBox.Text = value?.Host ?? "";
                UserBox.Text = string.IsNullOrEmpty(value?.User) ? "ADMIN" : value.User;
                PortBox.Text = string.IsNullOrEmpty(value?.Port) ? "5900" : value.Port;
                TokenBox.Text = value?.Token ?? "";
                PassBox.Password = value?.Password ?? "";
                ArmBox.IsChecked = value?.Arm ?? true;
                TlsBox.IsChecked = value?.Tls ?? true;
                DeleteProfileBtn.IsEnabled = !string.IsNullOrEmpty(_editingProfileId);
            }
            finally { _loadingProfile = false; }
            ApplyArmGating();
        }

        private void OnProfileChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loadingProfile || !(ProfileBox.SelectedItem is ConnectSettings selected)) return;
            LoadProfile(selected);
        }

        private void OnNewProfile(object sender, RoutedEventArgs e)
        {
            ProfileBox.SelectedItem = null;
            LoadProfile(new ConnectSettings());
            ProfileNameBox.Focus();
        }

        private void OnDeleteProfile(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_editingProfileId)) return;
            string display = string.IsNullOrWhiteSpace(ProfileNameBox.Text)
                ? HostBox.Text.Trim() : ProfileNameBox.Text.Trim();
            if (MessageBox.Show(this, $"Delete saved server \"{display}\"?", "Delete server profile",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            ConnectSettings.Delete(_editingProfileId);
            _profiles = ConnectSettings.LoadProfiles();
            ProfileBox.ItemsSource = _profiles;
            if (_profiles.Count > 0)
            {
                ProfileBox.SelectedIndex = 0;
                LoadProfile(_profiles[0]);
            }
            else
            {
                ProfileBox.SelectedItem = null;
                LoadProfile(new ConnectSettings());
            }
        }

        // Re-applies the field gating whenever the Arm checkbox is toggled.
        private void OnArmToggled(object sender, RoutedEventArgs e) => ApplyArmGating();

        // Enables the fields that apply to the current mode and disables the rest. When arming, the BMC
        // supplies the token and the transport (the port and whether TLS is used, from the JNLP
        // stunEnable), so those manual fields are disabled and the BMC login fields are
        // enabled. When not arming, the user supplies the port, token, and TLS by hand. Keeping the
        // manual fields editable while arming would let arming silently overwrite them.
        private void ApplyArmGating()
        {
            bool arm = ArmBox.IsChecked == true;
            if (TokenBox != null) TokenBox.IsEnabled = !arm;
            if (PortBox != null) PortBox.IsEnabled = !arm;
            if (TlsBox != null) TlsBox.IsEnabled = !arm;
            if (UserBox != null) UserBox.IsEnabled = arm;
            if (PassBox != null) PassBox.IsEnabled = arm;
        }

        // Loads Supermicro's generic iKVM client certificate for the mutual-TLS leg (which replaces the
        // bundled stunnel and its client.crt/key). The same public vendor certificate ships in every
        // iKVM jar (CN=IPMI, O=Super Micro Computer), so it is embedded in the binary. A client.pfx next
        // to the executable overrides it, for a renewed certificate or a different firmware generation.
        private static X509Certificate2 LoadClientCert()
        {
            // 1. A disk override next to the executable.
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.pfx");
                if (File.Exists(path))
                {
                    var cert = LoadPfx(File.ReadAllBytes(path));
                    if (cert != null)
                    {
                        Core.Diagnostics.KvmLog.Write("Loaded client certificate (disk override): " + cert.Subject);
                        return cert;
                    }
                }
            }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("loading client.pfx from disk", ex); }

            // 2. The embedded vendor certificate.
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("client.pfx"))
                {
                    if (s != null)
                    {
                        var buf = new byte[s.Length];
                        int off = 0, n;
                        while (off < buf.Length && (n = s.Read(buf, off, buf.Length - off)) > 0) off += n;
                        var cert = LoadPfx(buf);
                        if (cert != null)
                        {
                            Core.Diagnostics.KvmLog.Write("Loaded embedded client certificate: " + cert.Subject);
                            return cert;
                        }
                    }
                }
                Core.Diagnostics.KvmLog.Write("No embedded client.pfx and none next to exe; connecting without a client cert.");
            }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("loading embedded client.pfx", ex); }
            return null;
        }

        // Loads a PFX from bytes. The embedded vendor PFX and any disk override are password-less.
        private static X509Certificate2 LoadPfx(byte[] bytes)
        {
            const X509KeyStorageFlags flags =
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable;
            return new X509Certificate2(bytes, "", flags);
        }

        // Builds the connection options from the fields, loads the client certificate, saves the
        // inputs, and closes the dialog with a positive result.
        private void OnConnect(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortBox.Text, out int port)) port = 5900;
            ArmViaWeb = ArmBox.IsChecked == true;
            BmcUser = UserBox.Text.Trim();
            BmcPassword = PassBox.Password;
            string host = HostBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                MessageBox.Show(this, "Enter a BMC host name or IP address.", "Host required",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                HostBox.Focus();
                return;
            }
            Options = new KvmConnectionOptions
            {
                Host = host,
                Port = port,
                Token = TokenBox.Text.Trim(),
                UseTls = TlsBox.IsChecked == true,
                ClientCertificate = LoadClientCert(),
            };

            // Save the editable profile immediately. If a credential is wrong, Connection > Connect /
            // Change server opens this same profile so the user can correct it before another attempt.
            Profile = new ConnectSettings
            {
                Id = _editingProfileId,
                Name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? host : ProfileNameBox.Text.Trim(),
                Host = host,
                User = BmcUser,
                Port = PortBox.Text.Trim(),
                Token = TokenBox.Text.Trim(),
                Password = BmcPassword,
                Arm = ArmViaWeb,
                Tls = TlsBox.IsChecked == true,
            };
            Profile.Save();

            DialogResult = true;
            Close();
        }
    }
}
