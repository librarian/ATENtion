using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using ATENtion.Core.Diagnostics;
using ATENtion.Core.Net;

namespace ATENtion.Capture
{
    internal static class Program
    {
        private sealed class Options
        {
            public string Host;
            public string User;
            public string Output;
            public string RawOutput;
            public string ClientPfx;
            public int TimeoutSeconds = 30;
            public int WebPort;
            public bool UseHttps = true;
        }

        private static int Main(string[] args)
        {
            Options cli;
            try
            {
                cli = Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                PrintUsage();
                return 2;
            }

            if (cli == null)
            {
                PrintUsage();
                return 0;
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string output = cli.Output ?? Path.Combine(
                baseDirectory, "ATENtion-screen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bmp");
            output = Path.GetFullPath(output);
            string rawOutput = string.IsNullOrEmpty(cli.RawOutput) ? null : Path.GetFullPath(cli.RawOutput);
            string outputDirectory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            if (!string.IsNullOrEmpty(rawOutput))
            {
                string rawDirectory = Path.GetDirectoryName(rawOutput);
                if (!string.IsNullOrEmpty(rawDirectory)) Directory.CreateDirectory(rawDirectory);
            }
            if (File.Exists(output))
            {
                Console.Error.WriteLine("Refusing to overwrite existing screenshot: " + output);
                return 2;
            }
            if (!string.IsNullOrEmpty(rawOutput) && File.Exists(rawOutput))
            {
                Console.Error.WriteLine("Refusing to overwrite existing raw capture: " + rawOutput);
                return 2;
            }

            string logPath = Path.ChangeExtension(output, ".log");
            KvmLog.FilePath = logPath;
            KvmLog.UnsupportedFrameFilePath = rawOutput;
            KvmLog.Enabled = true;
            KvmLog.Message += Console.WriteLine;

            Console.Write("BMC password: ");
            string password = ReadPassword();
            Console.WriteLine();

            try
            {
                Console.WriteLine("Arming the Java iKVM session...");
                ArmingResult arming = new BmcArmingClient().Arm(
                    cli.Host, cli.User, password, cli.UseHttps, cli.WebPort);
                password = null;

                var connection = new KvmConnectionOptions
                {
                    Host = cli.Host,
                    Port = arming.PreferredPort,
                    UseTls = arming.UseTls,
                    KvmUsername = arming.KvmUsername,
                    KvmPassword = arming.KvmPassword,
                    ClientCertificate = arming.UseTls ? LoadClientCertificate(cli.ClientPfx) : null
                };

                Exception fault = null;
                byte[] screenshot = null;
                int screenshotWidth = 0;
                int screenshotHeight = 0;
                var completion = new ManualResetEventSlim(false);
                var resultLock = new object();
                using (var session = new KvmVideoSession(connection))
                {
                    session.Faulted += (sender, ex) =>
                    {
                        lock (resultLock) fault = ex;
                        completion.Set();
                    };
                    session.FrameDecoded += (sender, frame) =>
                    {
                        lock (resultLock)
                        {
                            if (screenshot != null) return;
                            screenshotWidth = frame.Frame.Width;
                            screenshotHeight = frame.Frame.Height;
                            screenshot = new byte[frame.Frame.Pixels.Length];
                            Buffer.BlockCopy(frame.Frame.Pixels, 0, screenshot, 0, screenshot.Length);
                        }
                        completion.Set();
                    };
                    session.Open();
                    session.StartPump();

                    Console.WriteLine($"Connected. Waiting up to {cli.TimeoutSeconds}s for a decoded video frame...");
                    completion.Wait(TimeSpan.FromSeconds(cli.TimeoutSeconds));
                }

                lock (resultLock)
                {
                    if (screenshot != null)
                    {
                        WriteBmp(output, screenshotWidth, screenshotHeight, screenshot);
                        Console.WriteLine("Saved console screenshot: " + output);
                        Console.WriteLine("The screenshot and diagnostic log may contain sensitive console information.");
                        return 0;
                    }

                    if (fault != null)
                        throw new InvalidOperationException("The KVM session failed before a frame was decoded.", fault);
                }

                Console.Error.WriteLine("Timed out without receiving a decoded video frame.");
                if (!string.IsNullOrEmpty(rawOutput) && File.Exists(rawOutput))
                    Console.Error.WriteLine("An unsupported raw packet was saved to: " + rawOutput);
                Console.Error.WriteLine("Diagnostic log: " + logPath);
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Capture failed: " + ex.Message);
                KvmLog.Error("capture CLI", ex);
                Console.Error.WriteLine("Diagnostic log: " + logPath);
                return 1;
            }
            finally
            {
                password = null;
            }
        }

        private static Options Parse(string[] args)
        {
            if (args.Length == 0) return null;
            var result = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--help" || arg == "-h") return null;
                if (arg == "--http") { result.UseHttps = false; continue; }
                if (arg == "--host") result.Host = RequireValue(args, ref i, arg);
                else if (arg == "--user") result.User = RequireValue(args, ref i, arg);
                else if (arg == "--output") result.Output = RequireValue(args, ref i, arg);
                else if (arg == "--raw-output") result.RawOutput = RequireValue(args, ref i, arg);
                else if (arg == "--client-pfx") result.ClientPfx = RequireValue(args, ref i, arg);
                else if (arg == "--timeout") result.TimeoutSeconds = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                else if (arg == "--web-port") result.WebPort = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                else throw new ArgumentException("Unknown argument: " + arg);
            }

            if (string.IsNullOrWhiteSpace(result.Host)) throw new ArgumentException("--host is required.");
            if (string.IsNullOrWhiteSpace(result.User)) throw new ArgumentException("--user is required.");
            return result;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length) throw new ArgumentException(option + " requires a value.");
            return args[index];
        }

        private static int ParsePositiveInt(string text, string option)
        {
            if (!int.TryParse(text, out int value) || value <= 0)
                throw new ArgumentException(option + " requires a positive integer.");
            return value;
        }

        private static string ReadPassword()
        {
            if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

            var password = new System.Text.StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0) password.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
            }
            return password.ToString();
        }

        private static X509Certificate2 LoadClientCertificate(string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath))
                return LoadPfx(File.ReadAllBytes(overridePath));

            string besideExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.pfx");
            if (File.Exists(besideExe)) return LoadPfx(File.ReadAllBytes(besideExe));

            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("client.pfx"))
            {
                if (stream == null) throw new FileNotFoundException("Embedded client.pfx was not found.");
                var bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                return LoadPfx(bytes);
            }
        }

        private static X509Certificate2 LoadPfx(byte[] bytes)
        {
            const X509KeyStorageFlags flags =
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable;
            return new X509Certificate2(bytes, "", flags);
        }

        private static void WriteBmp(string path, int width, int height, byte[] bgra)
        {
            int stride = checked(width * 4);
            int imageBytes = checked(stride * height);
            if (bgra == null || bgra.Length < imageBytes)
                throw new ArgumentException("Decoded framebuffer is shorter than its dimensions.", nameof(bgra));

            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'B');
                writer.Write((byte)'M');
                writer.Write(checked(54 + imageBytes));
                writer.Write(0);
                writer.Write(54);
                writer.Write(40);
                writer.Write(width);
                writer.Write(height);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);
                writer.Write(imageBytes);
                writer.Write(2835);
                writer.Write(2835);
                writer.Write(0);
                writer.Write(0);
                for (int y = height - 1; y >= 0; y--)
                    writer.Write(bgra, y * stride, stride);
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Log in to an ATEN iKVM console and save the first decoded frame.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  ATENtion.Capture.exe --host HOST --user USER [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --output FILE       BMP screenshot path (default: timestamped beside exe)");
            Console.WriteLine("  --raw-output FILE   Also save the first unsupported raw packet, if one occurs");
            Console.WriteLine("  --timeout SECONDS   Capture timeout (default: 30)");
            Console.WriteLine("  --web-port PORT     BMC web port (default: 443 for HTTPS)");
            Console.WriteLine("  --http              Use HTTP for BMC web login");
            Console.WriteLine("  --client-pfx FILE   Override the embedded mutual-TLS client certificate");
            Console.WriteLine("  --help              Show this help");
            Console.WriteLine();
            Console.WriteLine("The BMC password is prompted securely and is never accepted on the command line.");
        }
    }
}
