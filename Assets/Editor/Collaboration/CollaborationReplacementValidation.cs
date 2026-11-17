using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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
    public static class CollaborationReplacementValidation {
        private const string Key = "Protobot.RoomReplacementSmoke";
        private static int frames, assertions;
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
                await Check();
                Debug.Log("Collaboration replacement validation passed: " + assertions + " assertions; actual target/confirm/cancel/Close/Escape, conflict, ambiguous outcome Check, owner Join, resync and independent scene/file/camera/save-state controls.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static async Task Run(params string[] args) {
            using var process = Process.Start(new ProcessStartInfo(CollaborationHost.HelperPath, string.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            });
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit()); await output; await error;
            Require(process.ExitCode == 0, "Replacement helper command failed.");
        }
        private static void Project(string root, bool second) {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            PartsManager.GeneratePart("CCHL-1x2-5", second ? Vector3.right * 3 : Vector3.zero, Quaternion.identity);
            if (second) PartsManager.GeneratePart("CCHL-1x2-5", Vector3.up * 7, Quaternion.identity);
            string file = Path.Combine(root, second ? "model-b.obj" : "model-a.obj");
            File.WriteAllText(file, second ? "v 0 0 0\nv 2 0 0\nv 0 2 0\nf 1 2 3\n" : "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            var model = CustomModelImporter.Read(file, 1);
            model.mounts = new[] { new CustomMountData { x = .25f, qw = 1, diameter = .169f, depth = .046f, threaded = true } };
            CustomModel.Create(model, Vector3.right * 12, Quaternion.identity);
        }
        private static string SceneHash(CollaborationSession session, Document scope) {
            var captured = ((RoomExport)Invoke(session, "CaptureRoomExport")).document;
            captured.generation = scope.generation; captured.revision = scope.revision;
            return DocumentModel.Hash(captured);
        }
        private static async Task Check() {
            CollaborationSession.EnableDevelopment(); var session = Object.FindFirstObjectByType<CollaborationSession>();
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            Invoke(session, "RefreshUI"); Invoke(session, "PositionCollaborationButton");
            var badge = Get<RectTransform>(session, "uiBadge");
            var title = (RectTransform)Object.FindFirstObjectByType<Protobot.UI.BuildNameText>().transform;
            var badgeCorners = new Vector3[4]; var titleCorners = new Vector3[4];
            badge.GetWorldCorners(badgeCorners); title.GetWorldCorners(titleCorners);
            Require(Get<Text>(session, "uiBadgeText").text == "Collaboration" && badgeCorners[0].x > titleCorners[2].x &&
                Mathf.Abs(badge.position.y - title.position.y) < 2, "Collaboration entry must sit to the right of the filename in the top bar.");
            string root = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "replacement-smoke"); RecoveryFiles.CreateDirectory(root);
            Project(root, false); var originalContent = (RoomExport)Invoke(session, "CaptureRoomExport");
            string registry = Path.Combine(root, "server"); await Run("server-init", "--data-dir", registry, "--creation-policy", "open");
            var start = new ProcessStartInfo(CollaborationHost.HelperPath, "serve --multi-room --config " + Quote(Path.Combine(registry, "server.json")) + " --listen http://127.0.0.1:0") {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var server = Process.Start(start); var errors = server.StandardError.ReadToEndAsync(); Task<string> output = null;
            try {
                var line = server.StandardOutput.ReadLineAsync(); Require(await Task.WhenAny(line, Task.Delay(30000)) == line, "Replacement helper readiness timed out.");
                var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(await line ?? "")); output = server.StandardOutput.ReadToEndAsync();
                using var relay = new CollaborationConnectionValidation.SlowRelay(ready.url);
                Invoke(session, "OpenPanel", 3); Get<Dropdown>(session, "uiNetworkRoute").value = 1;
                Get<InputField>(session, "uiCreateAddress").text = relay.Url; Get<InputField>(session, "uiRoom").text = "replacement-ui";
                Get<Dropdown>(session, "uiCreationAuthority").value = 1; Get<InputField>(session, "uiName").text = "Replacement owner";
                Get<Dropdown>(session, "uiTransport").value = 2;
                await Click(session, "Prepare Room Creation"); await Click(session, "Confirm Create Room"); await Idle(session);
                string ownerRoot = Path.Combine(LocalPreferences.ProfileDirectory ?? Application.persistentDataPath, "Collaboration/created-rooms");
                var owner = CreatedRoomRecovery.List(ownerRoot).Single(r => r.room == "replacement-ui");
                byte[] ownerBytes = File.ReadAllBytes(CreatedRoomRecovery.FileName(ownerRoot, relay.Url, owner.room));
                using var observer = new CollaborationClient(ready.url, owner.room, CollaborationScene.Catalog, CollaborationTransport.LongPolling);
                await observer.JoinAsync("Other participant");
                Project(root, true); var local = (RoomExport)Invoke(session, "CaptureRoomExport"); string localHash = DocumentModel.Hash(local.document);
                string localPath = Path.Combine(root, "local-project-b.pbb"); manager.buildPath = localPath;
                Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Local project save failed.");
                byte[] localBytes = File.ReadAllBytes(localPath); var camera = Camera.main.transform; var cameraPosition = camera.position; var cameraRotation = camera.rotation;
                void LocalUnchanged() {
                    Require(!CollaborationSession.IsShared && manager.buildPath == localPath && manager.CurrentBuildIsSaved &&
                        localBytes.SequenceEqual(File.ReadAllBytes(localPath)) && SceneHash(session, local.document) == localHash, "Replacement operation changed local scene/path/bytes/save marker.");
                    Require(Vector3.Distance(camera.position, cameraPosition) < .0001f && Quaternion.Angle(camera.rotation, cameraRotation) < .01f, "Replacement operation changed local camera.");
                }
                await Click(session, "Replace Owned " + owner.room); await Idle(session);
                Require(Get<GameObject>(session, "uiReplacementConfirmation").activeSelf && Get<Text>(session, "uiReplacementWarning").text.Contains(owner.room) &&
                    Get<Text>(session, "uiReplacementWarning").text.Contains(relay.Url), "Replacement confirmation omitted the exact owned target.");
                await Click(session, "Cancel Replace Online Room"); LocalUnchanged();
                Require(observer.IsConnected && !observer.RequiresResynchronization, "Cancel sent a replacement.");
                await Click(session, "Replace Owned " + owner.room); await Idle(session);
                var intervening = WireJson.Clone(observer.Snapshot.parts.First(p => p.catalogId != "CUSTOM")); intervening.id = Guid.NewGuid().ToString("N"); intervening.position[0] = 40;
                Require((await observer.SubmitAsync(observer.CreateTransaction("AddParts", new[] { intervening }))).accepted, "Intervening edit failed.");
                await Click(session, "Confirm Replace Online Room"); await Idle(session);
                Require(Get<string>(session, "status").Contains("online room changed"), "Stale confirmation did not report the intervening edit."); LocalUnchanged();
                string attemptsRoot = Path.Combine(LocalPreferences.ProfileDirectory ?? Application.persistentDataPath, "Collaboration/room-replacements");
                relay.HoldReplacement = true;
                foreach (string action in new[] { "Cancel Connection", "Close", "Escape" }) {
                    Invoke(session, "OpenPanel", 5); relay.Hold(); int before = relay.ReplacementRequests;
                    await Click(session, "Replace Owned " + owner.room); await Idle(session); await Click(session, "Confirm Replace Online Room");
                    Require(await Task.WhenAny(relay.Partial.Task, Task.Delay(30000)) == relay.Partial.Task, "Replacement reply was not held.");
                    var file = Directory.GetFiles(attemptsRoot, "*.json").Single(p => !RemoteRoomReplacement.ReadAttempt(p).confirmed);
                    var attempt = RemoteRoomReplacement.ReadAttempt(file); byte[] retained = File.ReadAllBytes(file);
                    if (action == "Escape") await Escape(); else await Click(session, action);
                    await Idle(session); relay.Release(); LocalUnchanged();
                    Require(relay.ReplacementRequests == before + 1 && retained.SequenceEqual(File.ReadAllBytes(file)) &&
                        ownerBytes.SequenceEqual(File.ReadAllBytes(CreatedRoomRecovery.FileName(ownerRoot, relay.Url, owner.room))), "Canceled replacement changed operation/owner recovery or resent.");
                    if (action != "Cancel Connection") Require(!CollaborationSession.BlocksUIInput, "Close/Escape kept modal input.");
                    Invoke(session, "OpenPanel", 5); await Click(session, "Check Replacement " + owner.room + " " + attempt.operationId); await Idle(session);
                    Require(RemoteRoomReplacement.ReadAttempt(file).confirmed && relay.ReplacementRequests == before + 1, "Check did not recover the lost replacement read-only."); LocalUnchanged();
                }
                relay.HoldReplacement = false; relay.Release();
                await Click(session, "Replace Owned " + owner.room); await Idle(session); await Click(session, "Confirm Replace Online Room"); await Idle(session);
                Require(Get<string>(session, "status").Contains("Replaced room"), "Normal confirmed replacement failed."); LocalUnchanged();
                await Click(session, "Join Owner " + owner.room); await Unsaved(manager); await Until(() => Get<CollaborationClient>(session, "client")?.IsConnected == true && !Get<bool>(session, "busy"));
                var client = Get<CollaborationClient>(session, "client"); string ownerId = client.Participant.id;
                Require(client.Participant.role == "owner" && client.Snapshot.parts.Length == 3 && manager.buildPath == "" && localBytes.SequenceEqual(File.ReadAllBytes(localPath)),
                    "Owner Join failed to retain ownership/hydrate the replacement with independent Save As.");
                string savedPath = Path.Combine(root, "participant-accepted.pbb"); manager.buildPath = savedPath;
                Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Joined accepted save failed."); byte[] savedBytes = File.ReadAllBytes(savedPath);
                using var remote = new RemoteRoomReplacement(relay.Url);
                var target = await remote.InspectAsync(owner.room, owner.ownerCredential, CancellationToken.None);
                await remote.ReplaceAsync(originalContent, target, owner.ownerCredential, Path.Combine(root, "outside-owner-attempt.json"), CancellationToken.None);
                await Until(() => !client.IsConnected); Invoke(session, "RefreshUI");
                Require(Get<Button>(session, "uiResynchronize").gameObject.activeInHierarchy && PartsManager.FindLoadedObjects().Count == 3 &&
                    manager.buildPath == savedPath && manager.CurrentBuildIsSaved && savedBytes.SequenceEqual(File.ReadAllBytes(savedPath)), "Live replacement failed to pause while retaining the old accepted scene/file.");
                await Click(session, "Resynchronize Replaced Room"); await Click(session, "Cancel Resynchronize Room");
                Require(!client.IsConnected && PartsManager.FindLoadedObjects().Count == 3 && manager.CurrentBuildIsSaved, "Resync cancel changed the accepted project.");
                await Click(session, "Resynchronize Replaced Room"); await Click(session, "Confirm Resynchronize Room"); await Idle(session);
                Require(client.IsConnected && client.Participant.id == ownerId && PartsManager.FindLoadedObjects().Count == 2 && manager.buildPath == savedPath &&
                    !manager.CurrentBuildIsSaved && savedBytes.SequenceEqual(File.ReadAllBytes(savedPath)), "Explicit resync changed membership/path/bytes or lost the independent dirty marker.");
                Require(Vector3.Distance(camera.position, cameraPosition) < .0001f && Quaternion.Angle(camera.rotation, cameraRotation) < .01f, "Resync changed the participant camera.");
                var custom = client.Snapshot.parts.Single(p => p.catalogId == "CUSTOM");
                Require(custom.mounts.Length == 1 && PartsManager.FindLoadedObjects().Single(p => p.GetComponent<SavedObject>().ConnectionId == custom.id)
                    .GetComponentsInChildren<MeshCollider>(true).Any(c => c.sharedMesh != null && c.sharedMesh.vertexCount == 3), "Resync failed actual custom mesh/collider/mount materialization.");
                Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Accepted generation save failed."); savedBytes = File.ReadAllBytes(savedPath);
                var third = new RoomExport { catalog = originalContent.catalog, document = GeometryTransfer.CloneDocument(originalContent.document) };
                var asset = third.document.assets.Single(); asset.vertices[3] = 3; asset.hash = DocumentModel.GeometryHash(asset);
                foreach (var part in third.document.parts.Where(p => p.catalogId == "CUSTOM")) part.assetHash = asset.hash;
                target = await remote.InspectAsync(owner.room, owner.ownerCredential, CancellationToken.None);
                await remote.ReplaceAsync(third, target, owner.ownerCredential, Path.Combine(root, "outside-owner-third.json"), CancellationToken.None);
                await Until(() => !client.IsConnected); string accepted = CollaborationSession.CommittedHash;
                relay.Hold(); await Click(session, "Resynchronize Replaced Room"); await Click(session, "Confirm Resynchronize Room");
                Require(await Task.WhenAny(relay.Partial.Task, Task.Delay(30000)) == relay.Partial.Task, "Resync model transfer was not held.");
                await Click(session, "Close"); await Idle(session); relay.Release();
                Require(CollaborationSession.CommittedHash == accepted && !client.IsConnected && manager.buildPath == savedPath && manager.CurrentBuildIsSaved &&
                    savedBytes.SequenceEqual(File.ReadAllBytes(savedPath)) && !CollaborationSession.BlocksUIInput, "Canceled resync changed accepted scene/file/marker or kept input blocked.");
                Invoke(session, "OpenPanel", -1); await Click(session, "Resynchronize Replaced Room"); await Click(session, "Confirm Resynchronize Room"); await Idle(session);
                Require(client.IsConnected && !manager.CurrentBuildIsSaved && savedBytes.SequenceEqual(File.ReadAllBytes(savedPath)), "Resync retry failed to preserve independent local saved bytes.");
                int replacements = relay.ReplacementRequests; string generation = client.Snapshot.generation;
                Invoke(session, "ClosePanel"); Require(await manager.OpenPathAsync(localPath), "Ordinary Open failed."); await Unsaved(manager);
                await Until(() => !CollaborationSession.IsShared); await Get<Task>(session, "hostShutdown");
                manager.CreateNewBuild(); await Unsaved(manager);
                Require(!CollaborationSession.IsShared && manager.buildPath == "" && relay.ReplacementRequests == replacements &&
                    (await remote.InspectAsync(owner.room, owner.ownerCredential, CancellationToken.None)).generation == generation, "Ordinary Open/New silently replaced online content.");
            }
            finally { if (!server.HasExited) { server.Kill(); await Task.Run(() => server.WaitForExit()); } await errors; if (output != null) await output; }
        }
        private static async Task Unsaved(BuildsManager manager) {
            var unsaved = Get<Protobot.UI.UnsavedChangesUI>(manager, "unsavedChangesMenu");
            if (unsaved.gameObject.activeInHierarchy) {
                // Direct OpenPathAsync can return in the same frame that enables the
                // dialog. Allow its ordinary canvas/raycast rebuild before pointer input.
                await Task.Delay(100); Canvas.ForceUpdateCanvases();
                await CollaborationUIValidation.Click(Get<Button>(unsaved, "discardButton").gameObject);
            }
        }
        private static async Task Escape() {
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.Escape)); await Task.Delay(80);
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); await Task.Delay(80);
        }
        private static async Task Click(CollaborationSession session, string name) {
            Invoke(session, "RefreshUI"); await CollaborationUIValidation.Click(Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>().Single(b => b.name == name).gameObject);
        }
        private static async Task Idle(CollaborationSession session) => await Until(() => !Get<bool>(session, "busy"));
        private static async Task Until(Func<bool> condition) { for (int i = 0; i < 1200; i++) { if (condition()) return; await Task.Delay(25); } throw new TimeoutException("Replacement UI condition timed out."); }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
    }
}
