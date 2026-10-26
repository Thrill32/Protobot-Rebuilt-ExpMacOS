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
                Debug.Log("Apple Silicon build succeeded: " + report.summary.outputPath);
            }
            finally {
                PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, previousArchitecture);
            }
        }
    }
}
