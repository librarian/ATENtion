using System;
using System.IO;
using ATENtion.Core.Diagnostics;
using Xunit;

namespace ATENtion.Tests
{
    public class UnsupportedFrameCaptureTests
    {
        [Fact]
        public void Capture_Writes_Only_First_Packet_Beside_Log()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ATENtion.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string logPath = Path.Combine(directory, "ATENtion.log");
            string dumpPath = Path.Combine(directory, "ATENtion-unsupported-frame.bin");
            byte[] first = { 4, 7, 1, 0, 0, 0, 0, 0, 0, 3, 0xaa, 0xbb, 0xcc };
            byte[] second = { 9, 9, 9 };

            try
            {
                KvmLog.FilePath = logPath;
                KvmLog.UnsupportedFrameFilePath = null;
                KvmLog.Enabled = true;
                KvmLog.ResetUnsupportedFrameCaptureForTests();

                Assert.Equal(dumpPath, KvmLog.TryCaptureUnsupportedFrame(first));
                Assert.Null(KvmLog.TryCaptureUnsupportedFrame(second));
                Assert.Equal(first, File.ReadAllBytes(dumpPath));
                Assert.Contains("Captured first unsupported video packet", File.ReadAllText(logPath));
            }
            finally
            {
                KvmLog.Enabled = false;
                KvmLog.FilePath = null;
                KvmLog.UnsupportedFrameFilePath = null;
                try { Directory.Delete(directory, true); } catch { }
            }
        }
    }
}
