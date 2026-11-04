using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

namespace ProtobotEditorTools {
    public static class FilePickerPlatformValidation {
        public static void CompilePlayers() {
            const string path = "Assets/Plugins/StandaloneFileBrowser/Plugins/StandaloneFileBrowser.bundle";
            var plugin = (PluginImporter)AssetImporter.GetAtPath(path);
            if (plugin.GetEditorData("OS") != "OSX" || !plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX) ||
                plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64) || plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneLinux64))
                throw new InvalidOperationException("macOS file-picker plugin has incorrect platform restrictions");
            foreach (var target in new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64 }) {
                string output = Path.Combine(Path.GetTempPath(), "protobot-dialog-compile-" + target);
                Directory.CreateDirectory(output);
                var result = PlayerBuildInterface.CompilePlayerScripts(new ScriptCompilationSettings {
                    target = target, group = BuildTargetGroup.Standalone,
                    subtarget = (int)StandaloneBuildSubtarget.Player
                }, output);
                if (result.assemblies == null || result.assemblies.Count == 0)
                    throw new InvalidOperationException("Player script compilation failed for " + target);
                Debug.Log($"File-picker compatibility: {target} player scripts compiled ({result.assemblies.Count} assemblies).");
            }
        }
    }
}
