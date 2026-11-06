using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Protobot;
using Protobot.Collaboration;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ProtobotEditorTools {
    public static class CollaborationSetup {
        [MenuItem("Tools/Protobot/Collaboration/Enable Development UI")]
        public static void Enable() {
            EditorPrefs.SetBool("Protobot.Collaboration.Development", true);
            if (Application.isPlaying) CollaborationSession.EnableDevelopment();
            Debug.Log("Collaboration development UI enabled for Play Mode. Player builds require --collaboration.");
        }
        [MenuItem("Tools/Protobot/Collaboration/Disable Development UI")]
        public static void Disable() => EditorPrefs.SetBool("Protobot.Collaboration.Development", false);

        [MenuItem("Tools/Protobot/Collaboration/Export Catalog")]
        public static void ExportCatalog() {
            PartsManager.LoadPartTypes();
            var entries = new List<CatalogEntry>();
            foreach (var type in PartsManager.partTypes.OrderBy(t => t.id, StringComparer.Ordinal)) {
                var generator = type.GetComponent<PartGenerator>();
                string old1 = generator.param1.value, old2 = generator.param2.value;
                var oldLimits = generator.param2.customLimits;
                try {
                    generator.InitParamValues();
                    var entry = new CatalogEntry {
                        id = type.id, first = Rule(generator.param1, generator.UsesParams, generator.GetParam1Options()),
                        second = Rule(generator.param2, generator.UsesTwoParams, generator.GetParam2Options())
                    };
                    var variants = new List<CatalogVariant>();
                    if (generator.UsesTwoParams && !generator.param1.custom) foreach (var first in generator.GetParam1Options()) {
                        generator.param1.value = first;
                        PartParameterEditing.Normalize(generator);
                        variants.Add(new CatalogVariant { first = first, second = Rule(generator.param2, true, generator.GetParam2Options()) });
                    }
                    entry.variants = variants.ToArray(); entries.Add(entry);
                }
                finally { generator.param1.value = old1; generator.param2.value = old2; generator.param2.customLimits = oldLimits; }
            }
            var paths = Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("Packages/com.protobot.collaboration/Runtime", "*.cs", SearchOption.AllDirectories))
                .Concat(AssetDatabase.GetDependencies(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Resources/Part Prefabs" }).Select(AssetDatabase.GUIDToAssetPath).ToArray(), true))
                .Select(p => p.Replace('\\', '/'))
                .Where(p => File.Exists(p) && !p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                .Distinct().OrderBy(p => p, StringComparer.Ordinal);
            using var hash = SHA256.Create();
            using var bytes = new MemoryStream();
            foreach (var path in paths) {
                var name = System.Text.Encoding.UTF8.GetBytes(path.Replace('\\', '/'));
                bytes.Write(name, 0, name.Length); bytes.WriteByte(0);
                var data = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                    ? System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n")) : File.ReadAllBytes(path);
                bytes.Write(data, 0, data.Length); bytes.WriteByte(0);
            }
            var manifest = new CatalogManifest { hash = DocumentModel.Hex(hash.ComputeHash(bytes.ToArray())), entries = entries.ToArray() };
            Directory.CreateDirectory("Assets/Resources/Collaboration");
            File.WriteAllBytes("Assets/Resources/Collaboration/catalog.json", WireJson.Encode(manifest));
            AssetDatabase.Refresh();
            Debug.Log("Collaboration catalog exported: " + manifest.entries.Length + " families; " + manifest.hash);
        }

        private static ParameterRule Rule(Parameter parameter, bool used, List<string> options) => new ParameterRule {
            used = used, custom = parameter.custom, integer = parameter.customUnit == "Holes", min = parameter.customLimits.x, max = parameter.customLimits.y,
            options = options?.ToArray() ?? Array.Empty<string>()
        };

        public static void BuildMacPlayer() => BuildPlayer(BuildTarget.StandaloneOSX, "Collaboration/artifacts/players/Protobot.app");
        public static void BuildWindowsPlayer() => BuildPlayer(BuildTarget.StandaloneWindows64, "Collaboration/artifacts/players/Windows/Protobot.exe");
        private static void BuildPlayer(BuildTarget target, string output) {
            string previous = Environment.GetEnvironmentVariable("PROTOBOT_BUNDLE_COLLABORATION");
            var previousBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            int previousArchitecture = PlayerSettings.GetArchitecture(NamedBuildTarget.Standalone);
            try {
                Environment.SetEnvironmentVariable("PROTOBOT_BUNDLE_COLLABORATION", "1");
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                if (target == BuildTarget.StandaloneOSX) PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, 1);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { "Assets/Scenes/Main.unity" }, locationPathName = Path.GetFullPath(output), target = target, options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("Collaboration player build failed: " + report.summary.result);
                if (target == BuildTarget.StandaloneOSX) {
                    RunBuildCommand("/usr/bin/codesign", "--force --deep --sign - \"" + report.summary.outputPath + "\"");
                    RunBuildCommand("/usr/bin/codesign", "--verify --deep --strict \"" + report.summary.outputPath + "\"");
                }
            }
            finally {
                Environment.SetEnvironmentVariable("PROTOBOT_BUNDLE_COLLABORATION", previous);
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, previousBackend);
                PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, previousArchitecture);
            }
        }
        private static void RunBuildCommand(string file, string args) {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = false });
            process.WaitForExit(); if (process.ExitCode != 0) throw new BuildFailedException("Build command failed: " + file);
        }
    }

    // The helper is optional for ordinary offline builds. Collaboration release builds opt in explicitly.
    public sealed class CollaborationPackaging : IPreprocessBuildWithReport, IPostprocessBuildWithReport {
        public int callbackOrder => 0;
        private static bool Required => Environment.GetEnvironmentVariable("PROTOBOT_BUNDLE_COLLABORATION") == "1";
        public void OnPreprocessBuild(BuildReport report) {
            if (!Required) return;
            CollaborationSetup.ExportCatalog();
            if (!File.Exists(Source(report.summary.platform))) throw new BuildFailedException("Publish the matching collaboration helper before building the player.");
        }
        public void OnPostprocessBuild(BuildReport report) {
            if (!Required) return;
            var source = Source(report.summary.platform);
            bool mac = report.summary.platform == BuildTarget.StandaloneOSX;
            string destination = mac ? Path.Combine(report.summary.outputPath, "Contents/Helpers/Collaboration") : Path.Combine(Path.GetDirectoryName(report.summary.outputPath), "Collaboration");
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(source))) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            File.Copy("Assets/Resources/Collaboration/catalog.json", Path.Combine(destination, "catalog.json"), true);
            if (mac) {
                Run("/bin/chmod", "+x \"" + Path.Combine(destination, "protobot-collab") + "\"");
                Run("/usr/bin/codesign", "--force --deep --sign - \"" + Path.Combine(destination, "protobot-collab") + "\"");
            }
            Debug.Log("Bundled matching collaboration helper at " + destination);
        }
        private static string Source(BuildTarget target) {
            string rid = target == BuildTarget.StandaloneOSX ? "osx-arm64" : target == BuildTarget.StandaloneWindows64 ? "win-x64" : throw new BuildFailedException("Unsupported collaboration player target.");
            return Path.GetFullPath(Path.Combine("Collaboration/artifacts", rid, target == BuildTarget.StandaloneWindows64 ? "protobot-collab.exe" : "protobot-collab"));
        }
        private static void Run(string file, string args) {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = false });
            process.WaitForExit(); if (process.ExitCode != 0) throw new BuildFailedException("Collaboration helper packaging failed: " + file);
        }
    }
}
