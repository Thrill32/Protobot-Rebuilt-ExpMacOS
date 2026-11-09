using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace Protobot {
    internal static class CollaborationTestWindow {
        public static Process Launch(string profile, string endpoint, string room) => Process.Start(StartInfo(profile, endpoint, room)) ?? throw new InvalidOperationException("Could not open another player window.");
        internal static ProcessStartInfo StartInfo(string profile, string endpoint, string room) {
            if (Application.isEditor) throw new InvalidOperationException("Open additional windows from a built desktop player.");
            if (Application.platform != RuntimePlatform.OSXPlayer && Application.platform != RuntimePlatform.WindowsPlayer)
                throw new InvalidOperationException("Local test windows require macOS or Windows.");
            endpoint = CollaborationInvite.ValidateOrigin(endpoint);
            if (!Collaboration.DocumentModel.ValidId(room)) throw new ArgumentException("Enter a valid room ID first.");
            string executable;
            using (var current = Process.GetCurrentProcess()) executable = current.MainModule.FileName;
            Directory.CreateDirectory(profile);
            return new ProcessStartInfo {
                FileName = executable, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable),
                Arguments = "--collaboration --collab-profile " + CollaborationHost.Quote(profile) +
                    " --collab-server " + CollaborationHost.Quote(endpoint) + " --collab-room " + CollaborationHost.Quote(room) +
                    " --collab-name " + CollaborationHost.Quote(Environment.UserName + " (test)") +
                    " -logFile " + CollaborationHost.Quote(Path.Combine(profile, "Player.log"))
            };
        }
    }
}
