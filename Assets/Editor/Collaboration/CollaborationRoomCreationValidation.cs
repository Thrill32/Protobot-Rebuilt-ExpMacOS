using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
    public static class CollaborationRoomCreationValidation {
        private const string Key = "Protobot.RoomCreationSmoke";
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
                await Check();
                Debug.Log("Collaboration room creation validation passed: actual keyed/open/admin creation controls, confirmation/cancel, private ownership, file/scene preservation, admin inspection separation, owner join/custom hydration/participant management and explicit recovery.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        [Serializable] private sealed class Admin { public int schema; public string serverId, credential; }
        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static async Task Run(params string[] args) {
            using var process = Process.Start(new ProcessStartInfo(CollaborationHost.HelperPath, string.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            });
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit()); await output; await error;
            Require(process.ExitCode == 0, "Room creation helper command failed.");
        }
        private static async Task Check() {
            var session = Object.FindFirstObjectByType<CollaborationSession>(); var manager = Object.FindFirstObjectByType<BuildsManager>();
            string root = Path.Combine(LocalPreferences.ProfileDirectory ?? Path.GetTempPath(), "room-creation-smoke"); RecoveryFiles.CreateDirectory(root);
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild); PartsManager.GeneratePart("CCHL-1x2-5", Vector3.zero, Quaternion.identity);
            string model = Path.Combine(root, "model.obj"); File.WriteAllText(model, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            CustomModel.Create(CustomModelImporter.Read(model, 1), Vector3.right * 10, Quaternion.identity);
            string path = Path.Combine(root, "independent-local.pbb"); manager.buildPath = path;
            Require(await (Task<bool>)Invoke(manager, "SaveAsync", false), "Initial save failed.");
            byte[] original = File.ReadAllBytes(path); bool saved = manager.CurrentBuildIsSaved;
            string records = Path.Combine(LocalPreferences.ProfileDirectory ?? Application.persistentDataPath, "Collaboration", "created-rooms");
            foreach (string policy in new[] { "key", "open", "admin-only" }) {
                string registry = Path.Combine(root, "server-" + policy); await Run("server-init", "--data-dir", registry, "--creation-policy", policy);
                var admin = RecoveryFiles.Read<Admin>(Path.Combine(registry, "admin.json"));
                string creator = policy == "key" ? RecoveryFiles.Read<RoomCreationKey>(Path.Combine(registry, "creator.json")).credential : null;
                var start = new ProcessStartInfo(CollaborationHost.HelperPath, "serve --multi-room --config " + Quote(Path.Combine(registry, "server.json")) + " --listen http://127.0.0.1:0") {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var server = Process.Start(start); var errors = server.StandardError.ReadToEndAsync(); Task<string> output = null;
                try {
                    var line = server.StandardOutput.ReadLineAsync(); Require(await Task.WhenAny(line, Task.Delay(30000)) == line, "Helper readiness timed out.");
                    string text = await line; Require(text != null, "Helper failed before readiness."); output = server.StandardOutput.ReadToEndAsync();
                    var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(text)); string room = "ui-" + policy;
                    if (policy == "open") await CheckCancellation(session, manager, ready.url, records, path, original);
                    Invoke(session, "OpenPanel", 3); Get<Dropdown>(session, "uiNetworkRoute").value = 1;
                    Get<InputField>(session, "uiCreateAddress").text = ready.url; Get<InputField>(session, "uiRoom").text = room;
                    Get<InputField>(session, "uiName").text = "Room owner"; Get<Dropdown>(session, "uiCreationAuthority").value = policy == "key" ? 0 : policy == "open" ? 1 : 2;
                    await Click(session, "Check Creation Policy"); await Until(() => !Get<bool>(session, "busy"));
                    Require(Get<Text>(session, "uiCreationPolicy").text.Contains(policy), "Policy feedback is missing.");
                    Get<InputField>(session, "uiCreationKey").text = creator ?? (policy == "admin-only" ? admin.credential : "");
                    Get<InputField>(session, "uiPassword").text = policy == "open" ? "" : "private join password";
                    Require(Get<InputField>(session, "uiCreationKey").contentType == InputField.ContentType.Password, "Creation key is unmasked.");
                    int before = CreatedRoomRecovery.List(records).Length;
                    await Click(session, "Prepare Room Creation"); await Click(session, "Cancel Create Room");
                    Require(CreatedRoomRecovery.List(records).Length == before && !CollaborationSession.IsShared, "Cancel created a room or membership.");
                    await Click(session, "Prepare Room Creation"); await Click(session, "Confirm Create Room"); await Until(() => !Get<bool>(session, "busy")); Invoke(session, "RefreshUI");
                    var created = CreatedRoomRecovery.List(records).Single(r => r.room == room);
                    Require(created.confirmed && created.ownerCredential != creator && created.ownerCredential != admin.credential && Get<string>(session, "creationKey") == "", "Private ownership or key clearing failed.");
                    Require(!CollaborationSession.IsShared && manager.buildPath == path && manager.CurrentBuildIsSaved == saved && original.SequenceEqual(File.ReadAllBytes(path)) && PartsManager.FindLoadedObjects().Count == 2,
                        "Creation changed scene, file association, bytes, saved marker or membership.");
                    await Click(session, "Check Created " + room); await Until(() => !Get<bool>(session, "busy"));
                    // Real admin controls reject the creation key and then inspect with the master capability.
                    await Click(session, "Server Admin Tab");
                    if (creator != null) {
                        Get<InputField>(session, "uiServerAdminKey").text = creator;
                        await Click(session, "Refresh Server Rooms"); await Until(() => !Get<bool>(session, "busy"));
                        Require(Get<string>(session, "status").Contains("administrator"), "Creator/admin refusal feedback missing.");
                    }
                    Get<InputField>(session, "uiServerAdminKey").text = admin.credential;
                    await Click(session, "Refresh Server Rooms"); await Until(() => !Get<bool>(session, "busy"));
                    Require(Get<Text>(session, "uiServerRooms").text.Contains(room) && Get<Text>(session, "uiServerRooms").text.Contains("Logical") && !CollaborationSession.IsShared, "Admin inspection created a membership or omitted resources.");
                    await Click(session, "Owned Rooms Tab");
                    await Click(session, "Join Owner " + room);
                    var unsaved = Get<Protobot.UI.UnsavedChangesUI>(manager, "unsavedChangesMenu");
                    if (unsaved.gameObject.activeSelf) await CollaborationUIValidation.Click(Get<Button>(unsaved, "discardButton").gameObject);
                    await Until(() => Get<CollaborationClient>(session, "client")?.IsConnected == true && !Get<bool>(session, "busy"));
                    var client = Get<CollaborationClient>(session, "client"); Require(client?.IsConnected == true && client.Participant.role == "owner" && manager.buildPath == "", "Owner Join did not open with Save As.");
                    Require(PartsManager.FindLoadedObjects().Count == 2 && client.Snapshot.assets.Single().vertices.Length == 9, "Created geometry did not hydrate.");
                    await Click(session, "Manage Participants"); await Until(() => !Get<bool>(session, "loadingMembers"));
                    Require(Get<Membership[]>(session, "managedMemberships").Any(m => m.participant.id == client.Participant.id), "Owner participant controls unavailable.");
                    client.Suspend("Room creation recovery test"); await Click(session, "Reconnect"); await Until(() => !Get<bool>(session, "busy"));
                    Require(client.IsConnected && client.Participant.role == "owner", "Owner membership recovery failed.");
                    await Click(session, "Leave"); await Click(session, "Confirm Leave"); await Get<Task>(session, "hostShutdown");
                    manager.buildPath = path; saved = manager.CurrentBuildIsSaved;
                    Require(original.SequenceEqual(File.ReadAllBytes(path)), "Joining or leaving altered independent saved bytes.");
                }
                finally { if (!server.HasExited) { server.Kill(); await Task.Run(() => server.WaitForExit()); } await errors; if (output != null) await output; }
            }
        }
        private static async Task CheckCancellation(CollaborationSession session, BuildsManager manager, string origin, string records, string path, byte[] original) {
            using var relay = new CollaborationConnectionValidation.SlowRelay(origin) { HoldCreation = true };
            foreach (string action in new[] { "Cancel Connection", "Close", "Escape" }) {
                Invoke(session, "OpenPanel", 3); Get<Dropdown>(session, "uiCreationAuthority").value = 1;
                string room = "cancel-" + action.Replace(" ", "-").ToLowerInvariant();
                Get<InputField>(session, "uiCreateAddress").text = relay.Url; Get<InputField>(session, "uiRoom").text = room;
                relay.Hold(); int before = relay.CreationRequests; bool saved = manager.CurrentBuildIsSaved;
                await Click(session, "Prepare Room Creation"); await Click(session, "Confirm Create Room");
                Require(await Task.WhenAny(relay.Partial.Task, Task.Delay(30000)) == relay.Partial.Task, "Creation reply was not withheld.");
                await relay.Partial.Task; var prepared = CreatedRoomRecovery.List(records).Single(r => r.room == room);
                byte[] bytes = File.ReadAllBytes(CreatedRoomRecovery.FileName(records, relay.Url, room));
                if (action == "Escape") {
                    InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(UnityEngine.InputSystem.Key.Escape)); await Task.Delay(80);
                    InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
                }
                else await Click(session, action);
                await Until(() => !Get<bool>(session, "busy")); relay.Release();
                Require(!CollaborationSession.IsShared && manager.buildPath == path && manager.CurrentBuildIsSaved == saved && original.SequenceEqual(File.ReadAllBytes(path)) &&
                    bytes.SequenceEqual(File.ReadAllBytes(CreatedRoomRecovery.FileName(records, relay.Url, room))) && !prepared.confirmed && relay.CreationRequests == before + 1,
                    "Creation " + action + " changed project/save/credential or resent publication.");
                if (action != "Cancel Connection") Require(!CollaborationSession.BlocksUIInput, "Creation Close/Escape retained modal input.");
                Invoke(session, "OpenPanel", 5); await Click(session, "Check Created " + room); await Until(() => !Get<bool>(session, "busy"));
                Require(CreatedRoomRecovery.List(records).Single(r => r.room == room).confirmed && relay.CreationRequests == before + 1, "Explicit Check did not recover ambiguous creation read-only.");
            }
        }
        private static async Task Click(CollaborationSession session, string name) {
            Invoke(session, "RefreshUI"); await CollaborationUIValidation.Click(Get<GameObject>(session, "uiRoot").GetComponentsInChildren<Button>().Single(b => b.name == name).gameObject);
        }
        private static async Task Until(Func<bool> condition) { for (int i = 0; i < 1200; i++) { if (condition()) return; await Task.Delay(25); } throw new TimeoutException("Room creation UI timed out."); }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
