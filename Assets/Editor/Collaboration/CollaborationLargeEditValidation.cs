using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using Protobot.Tools;
using Protobot.UI;
using Protobot.StateSystems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CollaborationLargeEditValidation {
        private const string Key = "Protobot.CollaborationLargeEditSmoke";
        private static int frames;

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(Key, false)) return;
            frames = 0;
            EditorApplication.update += Tick;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }

        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(Key, false);
            try {
                await Check();
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        private static async Task Check() {
            var path = Path.GetFullPath("Notes/Drivechain1.pbb");
            var build = BuildsManager.ParsePath(path);
            Require(build.parts.Length == 419, "Expected the original 419-part fixture.");
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            manager.AttemptLoad(build, path);
            await Until(() => PartsManager.FindLoadedObjects().Count == build.parts.Length);
            var session = Object.FindFirstObjectByType<CollaborationSession>();
            Set(session, "port", "0");
            await (Task)Invoke(session, "HostAsync");
            var client = Get<CollaborationClient>(session, "client");
            Require(client?.IsConnected == true && client.IsDurable, "The real helper did not host the fixture.");
            var keyboard = Object.FindFirstObjectByType<KeyboardTransformTool>();
            keyboard.enabled = false;
            var selection = Get<SelectionManager>(keyboard, "selectionManager");
            await Until(() => PartsManager.FindLoadedObjects().Any(body => body.TryGetGroup(out List<GameObject> _)));
            var remaining = new HashSet<GameObject>(PartsManager.FindLoadedObjects());
            int seedCount = 0;
            var expanded = new HashSet<GameObject>();
            var addBody = typeof(BoxSelector).GetMethod("AddBodies", BindingFlags.Static | BindingFlags.NonPublic);
            while (remaining.Count > 0) {
                var root = remaining.OrderBy(body => body.GetComponent<SavedObject>().ConnectionId, StringComparer.Ordinal).First();
                seedCount++;
                addBody.Invoke(null, new object[] { expanded, root, true });
                remaining.ExceptWith(expanded);
            }
            var grouped = new MultiSelection(null, expanded.ToList());
            selection.SetCurrent(grouped, true, true);
            int resolved = CollaborationScene.Bodies(new[] { grouped.gameObject })
                .Select(body => body.GetComponent<SavedObject>().ConnectionId).Distinct().Count();
            Require(resolved == build.parts.Length && resolved > seedCount, "The grouped gesture did not resolve the full build.");
            Debug.Log($"Drivechain1: {build.parts.Length} saved parts, {seedCount} selected assembly representatives, {resolved} resolved unique parts ({resolved - seedCount} through grouping).");
            var initial = client.Snapshot;
            var before = initial.parts.ToDictionary(part => part.id, Position);
            var host = Get<CollaborationHost>(session, "host");
            using var peer = new CollaborationClient(host.Url, "team", CollaborationScene.Catalog, CollaborationTransport.LongPolling);
            await peer.JoinAsync("Large edit observer");
            Require(client.ReservationPartLimit == DocumentModel.MaxReservedParts && client.TransactionMutationLimit == DocumentModel.MaxMutations,
                "The updated helper did not negotiate the supported limits.");
            var offset = Vector3.up * 3;
            var observed = new TaskCompletionSource<Preview>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Presence += batch => {
                var preview = batch.previews.FirstOrDefault(item => item.parts.Length == resolved);
                if (preview != null && preview.parts.All(part => before.TryGetValue(part.id, out var start) &&
                    Close(Position(part), start + offset))) observed.TrySetResult(preview);
            };
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            Require(Get<string[]>(session, "reserved").ToHashSet().SetEquals(before.Keys), "The reservation differed from the grouped selection.");
            Set(keyboard, "displayedOffset", offset);
            Invoke(keyboard, "ApplyDisplayedTransform");
            CheckScene(before, offset);
            await Until(() => observed.Task.IsCompleted);
            var remotePreview = await observed.Task;
            int previewBytes = WireJson.Encode(remotePreview).Length;
            Require(previewBytes < 256 * 1024 && remotePreview.parts.All(part => part.catalogId == null && part.mounts.Length == 0),
                "The large preview carried full part records or exceeded the measured size ceiling.");
            Require(peer.Snapshot.revision == initial.revision && CollaborationSession.SaveSnapshot().parts.All(part =>
                Close(new Vector3((float)part.xPos, (float)part.yPos, (float)part.zPos), before[part.connectionId])),
                "A preview entered the peer's accepted document or the local Save snapshot.");
            Invoke(keyboard, "SetTarget", offset, Quaternion.identity);
            Invoke(keyboard, "CompleteConfirmation");
            await Applied(peer, initial.revision + 1);
            Require(peer.Snapshot.parts.All(part => part.version == 2), "The entire move did not commit in one version per part.");
            CheckScene(before, offset);
            StateSystem.instance.Undo();
            await Applied(peer, initial.revision + 2);
            CheckScene(before, Vector3.zero);
            StateSystem.instance.Redo();
            await Applied(peer, initial.revision + 3);
            CheckScene(before, offset);

            var acceptedHash = CollaborationSession.CommittedHash;
            string ownerId = client.Participant.id, peerId = peer.Participant.id;
            var process = Get<System.Diagnostics.Process>(host, "process");
            process.Kill();
            await Task.Run(() => process.WaitForExit());
            await Until(() => !client.IsConnected);
            await (Task)Invoke(session, "ReconnectAsync");
            await peer.PrepareReconnectAsync();
            await peer.JoinAsync("Large edit observer");
            await Applied(peer, initial.revision + 3);
            Require(host.Recovered && client.Participant.id == ownerId && peer.Participant.id == peerId &&
                CollaborationSession.CommittedHash == acceptedHash && manager.buildPath == path,
                "Helper recovery changed the accepted room, membership or file association.");
            CheckScene(before, offset);
            StateSystem.instance.Undo();
            await Applied(peer, initial.revision + 4);
            CheckScene(before, Vector3.zero);
            StateSystem.instance.Redo();
            await Applied(peer, initial.revision + 5);
            CheckScene(before, offset);

            var moved = PartsManager.FindLoadedObjects();
            selection.SetCurrent(new MultiSelection(null, moved) { individual = true }, true, true);
            await HoldAlt(true);
            Invoke(keyboard, "BeginSelection", false);
            await Until(() => Get<bool>(keyboard, "active"));
            await HoldAlt(false);
            Require(PartsManager.FindLoadedObjects().Count == 2 * resolved, "Alt+G did not create the full temporary copy.");
            Invoke(keyboard, "SetTarget", offset, Quaternion.identity);
            Invoke(keyboard, "CompleteConfirmation");
            await Applied(peer, initial.revision + 6);
            var copied = peer.Snapshot;
            Require(copied.parts.Length == 2 * resolved &&
                DocumentModel.Chains(copied).Length == 2 * DocumentModel.Chains(initial).Length,
                "The compound edit did not publish all copies and routes together.");
            CheckScene(before, offset * 2);
            var overLimit = PartsManager.FindLoadedObjects().Take(DocumentModel.MaxReservedParts + 1).ToList();
            selection.SetCurrent(new MultiSelection(null, overLimit) { individual = true }, true, true);
            Invoke(keyboard, "BeginSelection", false);
            var message = Get<string>(session, "status");
            Require(message.Contains($"reserve {overLimit.Count} unique parts") && message.Contains($"limit is {DocumentModel.MaxReservedParts}") &&
                Notification() == message && !Get<bool>(keyboard, "active") && !CollaborationSession.HasReservation &&
                client.Snapshot.revision == copied.revision,
                "The over-limit gesture did not report both counts before reserving or previewing.");
            CheckScene(before, offset * 2);

            var sprockets = new[] {
                new Part { id = "boundary-sprocket-a", catalogId = "SPKT-Normal-10T", position = new double[] { 100, 30, -50 } },
                new Part { id = "boundary-sprocket-b", catalogId = "SPKT-Normal-40T", position = new double[] { 104, 30, -50 } }
            };
            var route = new ChainRecord { id = "boundary-chain", members = sprockets.Select(part => part.id).ToArray() };
            var seeded = await peer.SubmitAsync(peer.CreateTransaction("AddParts", sprockets, chainWrites: new[] { route }));
            Require(seeded.accepted, "Could not seed a chain for the mutation boundary check.");
            await Applied(peer, initial.revision + 7);
            var parts = PartsManager.FindLoadedObjects();
            var atLeaseLimit = parts.Where(body => route.members.Contains(body.GetComponent<SavedObject>().ConnectionId))
                .Concat(parts.Where(body => !route.members.Contains(body.GetComponent<SavedObject>().ConnectionId)).Take(DocumentModel.MaxReservedParts - 2)).ToList();
            selection.SetCurrent(new MultiSelection(null, atLeaseLimit) { individual = true }, true, true);
            await HoldAlt(true);
            Invoke(keyboard, "BeginSelection", false);
            await HoldAlt(false);
            message = Get<string>(session, "status");
            Require(message.Contains($"{DocumentModel.MaxMutations + 1} mutations") && message.Contains($"limit is {DocumentModel.MaxMutations}") &&
                Notification() == message && !Get<bool>(keyboard, "active") && !CollaborationSession.HasReservation &&
                PartsManager.FindLoadedObjects().Count == 2 * resolved + 2 && client.Snapshot.revision == seeded.revision,
                "Alt+G did not count the copied route and refuse the edit before preview or duplication.");
            CheckScene(before, offset * 2);
            StateSystem.instance.Undo();
            await Applied(peer, initial.revision + 8);
            Require(PartsManager.FindLoadedObjects().Count == resolved + 2, "One undo did not remove the entire compound copy.");
            CheckScene(before, offset);
            Debug.Log($"Drivechain1 passed. The {resolved}-part peer preview encoded to {previewBytes} bytes. Move, copy, undo, redo and kill/restart recovered atomically.");
            await peer.LeaveAsync();
            Invoke(session, "Detach");
            await Get<Task>(session, "hostShutdown");
        }

        private static async Task Applied(CollaborationClient peer, long revision) {
            await Until(() => CollaborationSession.CommittedRevision >= revision && !CollaborationSession.HasPending && CollaborationSession.CanEdit);
            await peer.WaitForRevisionAsync(revision);
            Require(CollaborationSession.CommittedRevision == revision && CollaborationSession.CommittedHash == DocumentModel.Hash(peer.Snapshot),
                "The edit took more than one revision or participants diverged.");
        }

        private static async Task HoldAlt(bool held) {
            foreach (var device in InputSystem.devices.OfType<Keyboard>())
                InputSystem.QueueStateEvent(device, held ? new KeyboardState(UnityEngine.InputSystem.Key.LeftAlt) : new KeyboardState());
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
            await Until(() => duplicator.IsDuplicateHeld == held);
        }

        private static string Notification() => (string)typeof(InteractionFeedback)
            .GetField("notification", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        private static Vector3 Position(Part part) => new Vector3((float)part.position[0], (float)part.position[1], (float)part.position[2]);
        private static bool Close(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < .001f;
        private static void CheckScene(IReadOnlyDictionary<string, Vector3> baseline, Vector3 offset) {
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(body => body.GetComponent<SavedObject>().ConnectionId);
            Require(baseline.All(pair => bodies.TryGetValue(pair.Key, out var body) && Close(body.transform.position, pair.Value + offset)),
                "The scene did not move every original part by exactly the same offset.");
        }

        private static async Task Until(Func<bool> predicate) {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate()) {
                if (timer.Elapsed.TotalSeconds > 45) throw new TimeoutException("Large collaboration edit validation timed out.");
                await Task.Delay(20);
            }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    }
}
