using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ATENtion.App
{
    /// <summary>The WPF application object for ATENtion.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Hosts the application. The startup window and application resources are declared in
    /// App.xaml. The code-behind installs the last-resort error handlers.
    /// </para>
    /// <para>
    /// OPERATION - An exception that escapes a UI handler would otherwise end the process, and with it
    /// every open session and any disc a host is installing from. It is recorded in a crash log beside
    /// the executable, whether or not diagnostic logging is on, reported once, and the application
    /// carries on. Exceptions on other threads cannot be recovered; they are recorded before the
    /// process ends.
    /// </para>
    /// </remarks>
    public partial class App : Application
    {
        private DateTime _lastReport = DateTime.MinValue;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Record("unhandled", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, a) => { Record("unobserved task", a.Exception); a.SetObserved(); };
            base.OnStartup(e);
        }

        private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Record("UI", e.Exception);
            e.Handled = true;
            // One dialog, not one per repeat of the same failure on a timer.
            if (DateTime.UtcNow - _lastReport < TimeSpan.FromSeconds(30)) return;
            _lastReport = DateTime.UtcNow;
            MessageBox.Show(MainWindow,
                "Something went wrong: " + e.Exception.Message + "\n\nATENtion has kept running. " +
                "Details are in " + System.IO.Path.GetFileName(CrashLogPath) + " beside the program.",
                "ATENtion", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static string CrashLogPath =>
            System.IO.Path.ChangeExtension(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, ".crash.log");

        private static void Record(string where, Exception ex)
        {
            if (ex == null) return;
            Core.Diagnostics.KvmLog.Error(where, ex);
            try
            {
                System.IO.File.AppendAllText(CrashLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* nowhere left to report it */ }
        }
    }
}
