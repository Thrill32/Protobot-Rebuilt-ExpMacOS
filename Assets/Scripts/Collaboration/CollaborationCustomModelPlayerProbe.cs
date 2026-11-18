#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using Protobot.Tools;
using Protobot.UI;
using SFB;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        [Serializable] private sealed class ModelProbeCommand { public string id, action, path; }
        [Serializable] private sealed class ModelProbeBody {
            public string id, name;
            public bool renderer, shaderSupported, collider, surfaceHit, selected;
            public int vertices, triangles;
            public double[] position;
        }
        [Serializable] private sealed class ModelProbeResult {
            public string id, error, status, notification, role, participant, hash, clientHash, catalog, helper;
            public string localHash, localFile;
            public bool localSaved, shared, replacementConfirmation, requiresResynchronization;
            public bool connected, pending, capturing;
            public long revision;
            public int assets, geometryAssetBytes;
            public Document accepted;
            public ModelProbeBody[] bodies;
        }
        private sealed class ModelProbeDialogs : IStandaloneFileBrowser {
            public string path;
            public Action<string[]> pendingOpen;
            public string[] OpenFilePanel(string t, string d, ExtensionFilter[] e, bool m) => throw new NotSupportedException();
            public string[] OpenFolderPanel(string t, string d, bool m) => throw new NotSupportedException();
            public string SaveFilePanel(string t, string d, string n, ExtensionFilter[] e) => throw new NotSupportedException();
            public void OpenFilePanelAsync(string t, string d, ExtensionFilter[] e, bool m, Action<string[]> cb) => pendingOpen = cb;
            public void OpenFolderPanelAsync(string t, string d, bool m, Action<string[]> cb) => throw new NotSupportedException();
            public void SaveFilePanelAsync(string t, string d, string n, ExtensionFilter[] e, Action<string> cb) => cb(path);
        }
        // Explicit, disposable-profile development automation. No listener or release-build path.
        // The OS picker alone is substituted; File menu, unit/Choose controls, callback dispatch,
        // parser, upload, authority, scene application and save/load use production code.
        internal async Task RunCustomModelProbeAsync(string output) {
            if (LocalPreferences.ProfileDirectory == null) throw new InvalidOperationException("A disposable profile is required.");
            string commandPath = output + ".command";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            var wrapper = typeof(StandaloneFileBrowser).GetField("_platformWrapper", BindingFlags.NonPublic | BindingFlags.Static);
            var previous = wrapper.GetValue(null);
            var dialogs = new ModelProbeDialogs();
            wrapper.SetValue(null, dialogs);
            string last = null;
            try {
                // Remove only transient launch UI, as in the ordinary editor input probes.
                GameObject.Find("Launch Canvas")?.SetActive(false);
                GameObject.Find("Splash Screen")?.SetActive(false);
                WriteModelProbe(output, "ready", null);
                while (!closing) {
                    await Task.Delay(50);
                    if (!File.Exists(commandPath)) continue;
                    var command = WireJson.Decode<ModelProbeCommand>(File.ReadAllBytes(commandPath));
                    if (command.id == last) continue;
                    last = command.id;
                    try {
                        switch (command.action) {
                            case "host-editors": uiHostEditorReplacement.value = 1; goto case "host";
                            case "host":
                                OpenPanel(0); uiPort.text = "0";
                                uiRoot.GetComponentsInChildren<Button>(true).Single(b => b.name == "Start Hosting").onClick.Invoke();
                                await ModelProbeWait(() => !busy); ClosePanel();
                                File.WriteAllText(output + ".endpoint", endpoint);
                                break;
                            case "join":
                                OpenPanel(1); uiName.text = "Model probe editor";
                                uiRoot.GetComponentsInChildren<Button>(true).Single(b => b.name == "Join Room").onClick.Invoke();
                                await ModelProbeWait(() => !busy); ClosePanel(); break;
                            case "import":
                                var tool = FindFirstObjectByType<CustomModelTool>();
                                FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                                    .Single(b => b.name == "Import Custom Model Button").onClick.Invoke();
                                var panelField = typeof(CustomModelTool).GetField("panelRoot", BindingFlags.Instance | BindingFlags.NonPublic);
                                var panelObject = panelField.GetValue(tool) as GameObject;
                                if (panelObject == null) throw new InvalidOperationException("Import settings did not open.");
                                var buttons = panelObject.GetComponentsInChildren<Button>();
                                var unitsButton = buttons.Single(b => b.GetComponentInChildren<Text>().text.StartsWith("Source units:"));
                                while (!unitsButton.GetComponentInChildren<Text>().text.EndsWith("Inches")) unitsButton.onClick.Invoke();
                                buttons.Single(b => b.GetComponentInChildren<Text>().text.StartsWith("Choose OBJ")).onClick.Invoke();
                                if (dialogs.pendingOpen == null) throw new InvalidOperationException("The model picker was not requested.");
                                var complete = dialogs.pendingOpen; dialogs.pendingOpen = null;
                                await Task.Run(() => complete(new[] { command.path }));
                                await ModelProbeWait(() => !CustomModelTool.IsCapturingInput && !HasPending);
                                await Task.Delay(100); break;
                            case "edit":
                                var body = PartsManager.FindLoadedObjects().Single(b => b.GetComponent<SavedObject>().ConnectionId == command.path);
                                var keyboard = FindFirstObjectByType<KeyboardTransformTool>(); keyboard.enabled = false;
                                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                                var selection = (SelectionManager)typeof(KeyboardTransformTool).GetField("selectionManager", flags).GetValue(keyboard);
                                selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true, true);
                                typeof(KeyboardTransformTool).GetMethod("BeginSelection", flags).Invoke(keyboard, new object[] { false });
                                await ModelProbeWait(() => (bool)typeof(KeyboardTransformTool).GetField("active", flags).GetValue(keyboard));
                                typeof(KeyboardTransformTool).GetMethod("SetTarget", flags).Invoke(keyboard, new object[] { Vector3.up * 2, Quaternion.identity });
                                typeof(KeyboardTransformTool).GetMethod("CompleteConfirmation", flags).Invoke(keyboard, null);
                                if (!await WaitForSaveAsync()) throw new InvalidOperationException("The edit did not settle.");
                                break;
                            case "save":
                                dialogs.path = command.path;
                                bool saved = await (Task<bool>)typeof(BuildsManager).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                                    .Invoke(FindFirstObjectByType<BuildsManager>(), new object[] { true });
                                if (!saved) throw new InvalidOperationException("Save failed."); break;
                            case "reload":
                                if (!await FindFirstObjectByType<BuildsManager>().OpenPathAsync(command.path)) throw new InvalidOperationException("Reload failed.");
                                break;
                            case "reconnect": await ReconnectAsync(); break;
                            case "replacement-inspect":
                                OpenPanel(1); await InspectParticipantReplacementAsync(); break;
                            case "replacement-confirm":
                                uiRoot.GetComponentsInChildren<Button>(true).Single(b => b.name == "Confirm Replace Online Room").onClick.Invoke();
                                await ModelProbeWait(() => !busy); break;
                            case "replacement-cancel":
                                uiRoot.GetComponentsInChildren<Button>(true).Single(b => b.name == "Cancel Replace Online Room").onClick.Invoke(); break;
                            case "replacement-check": await CheckParticipantReplacementAsync(command.path); break;
                            case "resynchronize":
                                OpenPanel();
                                uiRoot.GetComponentsInChildren<Button>(true).Single(b => b.name == "Resynchronize Replaced Room").onClick.Invoke();
                                uiRoot.GetComponentsInChildren<Button>(true).Single(b => b.name == "Confirm Resynchronize Room").onClick.Invoke();
                                await ModelProbeWait(() => !busy); break;
                            case "capture":
                                var models = PartsManager.FindLoadedObjects().Where(b => b.GetComponent<CustomModel>() != null).ToArray();
                                if (models.Length > 0) PivotCamera.Main.SetTransform(models[0].transform.position, new Vector3(20, 30, 0), -8);
                                await Task.Delay(100);
                                ScreenCapture.CaptureScreenshot(command.path);
                                await ModelProbeWait(() => File.Exists(command.path)); break;
                            case "inspect": break;
                            default: throw new InvalidOperationException("Unknown model-probe action.");
                        }
                        WriteModelProbe(output, command.id, null);
                    }
                    catch (Exception error) { WriteModelProbe(output, command.id, error.ToString()); }
                }
            }
            finally { wrapper.SetValue(null, previous); }
        }
        private static async Task ModelProbeWait(Func<bool> condition) {
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Model UI probe timed out."); await Task.Delay(25); }
        }
        private void WriteModelProbe(string output, string id, string error) {
            var snapshot = client?.Snapshot;
            Document local = null;
            if (CollaborationArguments.Get("--collab-probe") == "editor-replacement") {
                local = CaptureRoomExport().document; local.generation = "local-probe"; local.revision = 0;
            }
            var builds = FindFirstObjectByType<BuildsManager>();
            var selection = GameObject.Find("Primary Selection Manager")?.GetComponent<SelectionManager>();
            var selected = selection?.current == null ? Array.Empty<GameObject>() : BuildTransfer.SelectedBodies(selection.current).ToArray();
            var result = new ModelProbeResult { id = id, error = error, status = status,
                localHash = local == null ? null : DocumentModel.Hash(local), localFile = builds.buildPath, localSaved = builds.CurrentBuildIsSaved,
                shared = IsShared, replacementConfirmation = uiReplacementConfirmation.activeSelf, requiresResynchronization = NeedsSceneResynchronization,
                notification = (string)typeof(InteractionFeedback).GetField("notification", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null),
                connected = client?.IsConnected == true, pending = HasPending, capturing = CustomModelTool.IsCapturingInput,
                role = client?.Participant?.role, participant = client?.Participant?.id, hash = committed == null ? null : DocumentModel.Hash(committed),
                clientHash = snapshot == null ? null : DocumentModel.Hash(snapshot), catalog = catalog?.hash,
                geometryAssetBytes = CustomGeometryByteLimit,
                helper = CollaborationHost.HelperPath, revision = committed?.revision ?? 0, assets = committed?.assets?.Length ?? 0,
                accepted = committed == null ? null : GeometryTransfer.References(committed),
                bodies = PartsManager.FindLoadedObjects().Select(b => new ModelProbeBody { id = b.GetComponent<SavedObject>().ConnectionId,
                    name = b.name, renderer = b.GetComponentsInChildren<Renderer>().Any(r => r.enabled && r.sharedMaterial != null),
                    shaderSupported = b.GetComponentsInChildren<Renderer>().Any(r => r.sharedMaterial != null && r.sharedMaterial.shader.isSupported),
                    collider = b.GetComponentsInChildren<Collider>().Any(c => c.enabled), selected = selected.Contains(b),
                    surfaceHit = ModelProbeSurfaceHit(b),
                    vertices = b.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount ?? 0,
                    triangles = (int)(b.GetComponent<MeshFilter>()?.sharedMesh?.GetIndexCount(0) ?? 0) / 3,
                    position = new double[] { b.transform.position.x, b.transform.position.y, b.transform.position.z } }).ToArray() };
            File.WriteAllBytes(output + ".tmp", WireJson.Encode(result));
            if (File.Exists(output)) File.Replace(output + ".tmp", output, null); else File.Move(output + ".tmp", output);
        }
        private static bool ModelProbeSurfaceHit(GameObject body) {
            var collider = body.GetComponent<MeshCollider>();
            if (collider == null || !collider.enabled) return false;
            Physics.SyncTransforms();
            var bounds = collider.bounds;
            return collider.Raycast(new Ray(bounds.center + Vector3.forward * (bounds.extents.z + 10), Vector3.back), out _, 30);
        }
    }
}
#endif
