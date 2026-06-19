using System;
using System.Reflection;
using System.Windows;

namespace ATENtion.App
{
    /// <summary>The About dialog: application icon, name, version, copyright, and runtime.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Shows a traditional About box. On construction it reads the assembly's version,
    /// copyright, and runtime information and fills in the dialog's text fields.
    /// </para>
    /// <para>
    /// OPERATION - The version prefers the informational version (the csproj Version) when present,
    /// falling back to the assembly version. The copyright falls back to a built-in string when the
    /// assembly attribute is absent.
    /// </para>
    /// </remarks>
    public partial class AboutWindow : Window
    {
        /// <summary>Builds the dialog and populates its version, copyright, and runtime fields.</summary>
        public AboutWindow()
        {
            InitializeComponent();

            var asm = Assembly.GetExecutingAssembly();
            string ver = asm.GetName().Version?.ToString() ?? "1.0.0";
            // Prefer the informational version (the csproj Version) when it is present.
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                ver = info.InformationalVersion;
            VersionText.Text = "Version " + ver;

            var copyright = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
            CopyrightText.Text = string.IsNullOrEmpty(copyright) ? "Copyright © 2026 Thomas Jones" : copyright;

            RuntimeText.Text = $".NET Framework {Environment.Version}  ·  {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}";
        }

        // Closes the dialog when the OK button is clicked.
        private void OnOk(object sender, RoutedEventArgs e) => Close();
    }
}
