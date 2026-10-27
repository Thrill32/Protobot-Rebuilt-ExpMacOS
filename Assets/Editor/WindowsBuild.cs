using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ProtobotEditorTools {
    public static class WindowsBuild {
        [MenuItem("Tools/Protobot/Build Windows Mono Player")]
        public static void BuildMono() {
            var previousBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            try {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { "Assets/Scenes/Main.unity" },
                    locationPathName = Path.GetFullPath("Builds/Windows-Mono/Protobot.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Windows Mono build failed: " + report.summary.result);
                Debug.Log("Windows x64 Mono build succeeded: " + report.summary.outputPath);
            }
            finally {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, previousBackend);
            }
        }
    }
}
