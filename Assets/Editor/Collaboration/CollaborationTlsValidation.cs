using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationTlsValidation {
        private const string Key = "Protobot.TlsSmoke";
        private static int frames;
        public static void Smoke() {
            CollaborationSetup.ExportCatalog(); EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod] private static void Resume() { if (SessionState.GetBool(Key, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>(); if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try {
                await Check(); await CollaborationConnectionValidation.Check();
                Debug.Log("Collaboration TLS validation passed: configured standalone HTTPS listener; production Unity test/Join rejects untrusted TLS on all transports, pointer Cancel/Close and Escape preserve local scene/file/membership; ordinary connection/reconnect and Direct owned-helper recovery regressions. Trusted public HTTPS remains a field check.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        [Serializable] private sealed class Input { public string format = "protobot-tls"; public int schema = 1; public string certificate = "server.pfx", password = ""; }
        private static async Task Check() {
            string fixture = Environment.GetEnvironmentVariable("PROTOBOT_TEST_CERTIFICATE");
            Require(File.Exists(fixture), "Generate the ephemeral PFX with the lab --make-test-certificate first.");
            string root = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "tls-check"); RecoveryFiles.CreateDirectory(root);
            string pfx = Path.Combine(root, "server.pfx"), config = Path.Combine(root, "tls.json"), bootstrap = Path.Combine(root, "bootstrap.json");
            File.Copy(fixture, pfx); RecoveryFiles.PrivateFile(pfx);
            File.WriteAllText(config, JsonUtility.ToJson(new Input())); RecoveryFiles.PrivateFile(config);
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild); PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            var seed = new Bootstrap { room = "tls-team", catalog = CollaborationScene.Catalog, ownerCredential = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), document = CollaborationScene.CaptureDocument(CollaborationScene.Catalog) };
            RecoveryFiles.Write(bootstrap, seed);
            var start = new ProcessStartInfo { FileName = CollaborationHost.HelperPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "serve --ephemeral --listen https://127.0.0.1:0 --config " + Quote(bootstrap) + " --tls-file " + Quote(config) };
            using var process = Process.Start(start); process.StandardInput.Close(); var errors = process.StandardError.ReadToEndAsync();
            try {
                var line = process.StandardOutput.ReadLineAsync();
                Require(await Task.WhenAny(line, Task.Delay(15000)) == line, "Configured TLS helper readiness timed out.");
                string text = await line; Require(text != null, "Configured TLS helper exited before readiness.");
                var ready = WireJson.Decode<Ready>(System.Text.Encoding.UTF8.GetBytes(text));
                Require(ready.url.StartsWith("https://127.0.0.1:", StringComparison.Ordinal), "Configured helper did not bind HTTPS.");
                var session = Object.FindFirstObjectByType<CollaborationSession>(); var manager = Object.FindFirstObjectByType<BuildsManager>();
                string saved = Path.Combine(root, "original.pbb"); manager.buildPath = saved;
                Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "TLS fixture local save failed."); byte[] bytes = File.ReadAllBytes(saved);
                var baseline = CollaborationScene.CaptureDocument(seed.catalog); string hash = DocumentModel.Hash(baseline);
                Set(session, "endpoint", ready.url); Set(session, "room", seed.room); Invoke(session, "OpenPanel", 1);
                Get<Dropdown>(session, "uiNetworkRoute").value = 1;
                await Click(session, "Test Connection"); await Until(() => !Get<bool>(session, "busy"));
                Require(Get<Text>(session, "uiTestReport").text.Contains("tls-trust-or-name-failed"), "Unity connection test accepted an untrusted configured certificate.");
                foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
                    Get<Dropdown>(session, "uiTransport").value = (int)transport;
                    await Click(session, "Join Room"); await Until(() => !Get<bool>(session, "busy"));
                    var diagnostic = WireJson.Text(Get<DiagnosticReport>(session, "lastDiagnostic"));
                    Require(diagnostic.Contains("tls-trust-or-name-failed") && !diagnostic.Contains(config) && !diagnostic.Contains(pfx), "Unity TLS rejection was missing or exposed server file input.");
                    Preserved();
                }
                using (var proxy = new OutboundProxyFixture(ready.url) { Pause = true }) {
                    Get<Dropdown>(session, "uiNetworkRoute").value = 2; Get<InputField>(session, "uiProxyAddress").text = proxy.Url;
                    foreach (string control in new[] { "Cancel Connection", "Close", "Escape" }) {
                        Invoke(session, "OpenPanel", 1); int before = proxy.Connections;
                        await Click(session, "Join Room"); await Until(() => proxy.Connections > before);
                        if (control == "Escape") {
                            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.Escape)); await Task.Delay(80);
                            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
                        }
                        else await Click(session, control);
                        await Until(() => !Get<bool>(session, "busy")); Preserved();
                        Require(!process.HasExited, "Cancel stopped an independent configured TLS server.");
                    }
                }
                Invoke(session, "OpenPanel", 1); Get<Dropdown>(session, "uiNetworkRoute").value = 1;
                void Preserved() {
                    var current = CollaborationScene.CaptureDocument(seed.catalog); current.generation = baseline.generation;
                    Require(!CollaborationSession.IsShared && manager.buildPath == saved && bytes.SequenceEqual(File.ReadAllBytes(saved)), "TLS rejection/cancellation changed the local file or connection.");
                    Require(manager.CurrentBuildIsSaved, "TLS rejection/cancellation changed saved state.");
                    Require(DocumentModel.Hash(current) == hash, "TLS rejection/cancellation changed accepted scene content.");
                    Require(!File.Exists(CollaborationRecovery.SessionFile(ready.url, seed.room, seed.catalog.hash)), "TLS rejection/cancellation created membership.");
                }
            }
            finally {
                if (!process.HasExited) { process.Kill(); await Task.Run(() => process.WaitForExit()); }
                Require(string.IsNullOrWhiteSpace(await errors), "Configured TLS service unexpectedly wrote stderr.");
                File.Delete(bootstrap); File.Delete(config); File.Delete(pfx);
            }
        }
        private static string Quote(string value) => (string)typeof(CollaborationHost).GetMethod("Quote", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { value });
        private static async Task Click(CollaborationSession session, string name) { Invoke(session, "RefreshUI"); await CollaborationUIValidation.Click(Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>(true).Single(b => b.name == name).gameObject); }
        private static async Task Until(Func<bool> condition) { for (int i = 0; i < 1200; i++) { if (condition()) return; await Task.Delay(25); } throw new TimeoutException("TLS UI condition timed out."); }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
