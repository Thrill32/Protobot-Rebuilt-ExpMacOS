using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ProtobotEditorTools {
    public static class MacBuild {
        [MenuItem("Tools/Protobot/Build Apple Silicon App")]
        public static void BuildAppleSilicon() {
            int previousArchitecture = PlayerSettings.GetArchitecture(NamedBuildTarget.Standalone);
            try {
                PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, 1); // ARM64
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { "Assets/Scenes/Main.unity" },
                    locationPathName = Path.GetFullPath("build.app"),
                    target = BuildTarget.StandaloneOSX,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Apple Silicon build failed: " + report.summary.result);
                // Unity strips copied native libraries to ARM64. Refresh nested
                // signatures afterwards so the built app is consistently valid.
                var quotedPath = "\"" + report.summary.outputPath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                Codesign("--force --deep --sign - --preserve-metadata=identifier,entitlements,flags,runtime " + quotedPath);
                Codesign("--verify --deep --strict " + quotedPath);
                Debug.Log("Apple Silicon build succeeded and signature verified: " + report.summary.outputPath);
            }
            finally {
                PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, previousArchitecture);
            }
        }

        private static void Codesign(string arguments) {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "/usr/bin/codesign",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process == null) throw new InvalidOperationException("Could not start codesign.");
            string output = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("macOS code signature failed: " + output);
            if (!string.IsNullOrWhiteSpace(output)) Debug.Log(output.Trim());
        }
    }
}
