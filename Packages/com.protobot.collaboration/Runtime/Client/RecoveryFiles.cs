using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Protobot.Collaboration {
    // Local recovery files are separate from preferences and portable build files.
    public static class RecoveryFiles {
        [DllImport("libc", SetLastError = true)] private static extern int chmod(string path, uint mode);
        public static void CreateDirectory(string path) {
            if (Directory.Exists(path)) return;
            Directory.CreateDirectory(path);
            Private(path, 448); // 0700; Windows inherits the containing user-profile ACL.
        }
        public static void PrivateFile(string path) => Private(path, 384); // 0600
        private static void Private(string path, uint mode) {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && chmod(path, mode) != 0)
                throw new IOException("Could not protect the local recovery file.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        public static void Write<T>(string path, T value) {
            path = Path.GetFullPath(path);
            CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    PrivateFile(temporary);
                    var bytes = WireJson.Encode(value); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static T Read<T>(string path) => WireJson.Decode<T>(File.ReadAllBytes(path));
    }
}
