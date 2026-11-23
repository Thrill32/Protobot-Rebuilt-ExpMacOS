using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.StateSystems;
using Protobot.Tools;
using Protobot.Transformations;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;
using Key = UnityEngine.InputSystem.Key;
using InputEvent = Protobot.InputEvents.InputEvent;

namespace ProtobotEditorTools {
    public static class MirrorSelectionValidation {
        private const string RunKey = "Protobot.MirrorSelectionValidation";
        private const string Prompt = "Mirror across X, Y, or Z · Esc to cancel.";
        private static Keyboard keyboard;
        private static Mouse mouse;
        private static KeyboardTransformTool tool;
        private static SelectionManager selection;

        public static void Smoke() => Start("offline");
        public static void SharedSmoke() => Start("shared");

        private static void Start(string mode) {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetString(RunKey, mode);
            EditorApplication.EnterPlaymode();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (SessionState.GetString(RunKey, "") == "") return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
        }

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (SessionState.GetString(RunKey, "") != "") EditorApplication.update += Begin;
        }

        private static async void Begin() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || Time.frameCount < 12) return;
            EditorApplication.update -= Begin;
            var mode = SessionState.GetString(RunKey, "");
            SessionState.EraseString(RunKey);
            try {
                tool = Object.FindFirstObjectByType<KeyboardTransformTool>();
                selection = Field<SelectionManager>(tool, "selectionManager");
                var input = Field<InputEvent>(tool, "mirrorInput");
                Require(input.GetCurrentKeybind() == "K" && Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Any(row => Field<InputEvent>(row, "inputEvent") == input), "Main Mirror input or Preferences row is missing.");
                EventSystem.current.SetSelectedGameObject(null);
                InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(Screen.width * .5f, Screen.height * .5f) });
                await Frames();
                // Batch Mode has no focused game window. Deliver its focus
                // notification while exercising the real keyboard InputEvents.
                Invoke(tool, "OnApplicationFocus", true);
                if (mode == "shared") await Shared();
                else await Offline(input);
                Debug.Log("Mirror Selection " + mode + " validation passed.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        private static async Task Offline(InputEvent input) {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var body = PartsManager.GeneratePart("CCHL-1x2-5", new Vector3(4, 3, 2), Quaternion.Euler(16, 42, -20));
            Select(body, true);
            var source = Vertices(body);
            var pivot = body.transform.position;
            var axes = new[] { (Key.X, Vector3.right), (Key.Y, Vector3.up), (Key.Z, Vector3.forward) };
            var windowsGrouping = Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None).Single(binding => binding.name == "Grouping Bypass");
            ModifierBindingDefaults.ConfigureGroupingDefault(windowsGrouping.defaultAction, true);
            windowsGrouping.defaultAction.Enable();
            foreach (var (key, axis) in axes) {
                int state = StateSystem.curIndex;
                await OpenMirror();
                await Keys(key);
                Require(!GroupingBypassInput.IsHeld, "Z leaked to Windows Grouping Bypass on the commit frame.");
                await Keys();
                Require(Mirrored(body) && StateSystem.curIndex == state + 1 && selection.current.individual, "Mirror must commit once and retain bypass scope.");
                CheckReflected(source, Vertices(body), pivot, axis);
                StateSystem.instance.Undo();
                Near(Vertices(body)[0], source[0], "Undo mesh");
                Require(!Mirrored(body), "Undo must restore handedness.");
                StateSystem.instance.Redo();
                CheckReflected(source, Vertices(body), pivot, axis);
                StateSystem.instance.Undo();
            }

            await OpenMirror();
            await Tap(Key.Escape);
            Require(selection.current?.gameObject == body && !Mirrored(body) && KeyboardTransformTool.ControlsText == null, "Escape left a mirror pending or changed selection.");
            await OpenMirror();
            Invoke(tool, "OnApplicationFocus", false);
            await Tap(Key.Y);
            Require(!Mirrored(body) && KeyboardTransformTool.ControlsText == null, "Focus loss left a mirror pending.");
            Invoke(tool, "OnApplicationFocus", true);
            await OpenMirror();
            selection.ClearCurrent(false, true, true);
            await Tap(Key.Z);
            Require(!Mirrored(body) && KeyboardTransformTool.ControlsText == null, "Invalid selection still committed Mirror.");

            // Exercise the regular override path and Restore Defaults.
            Select(body, true);
            input.rebindAction.action.ApplyBindingOverride(3, "<Keyboard>/j");
            input.rebindAction.SaveRebinds();
            await Tap(Key.K);
            Require(KeyboardTransformTool.ControlsText == null, "K still fired after rebinding.");
            await Tap(Key.J);
            Require(KeyboardTransformTool.ControlsText == Prompt, "The rebound Mirror key did not arm.");
            await Tap(Key.Escape);
            input.rebindAction.ResetRebinds();
            await OpenMirror();
            Object.Destroy(body);
            await Frames();
            await Tap(Key.X);
            Require(selection.current == null && KeyboardTransformTool.ControlsText == null, "A destroyed source left a stale selection.");

            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var original = await FastenedAssembly(Vector3.zero);
            var totals = original.Select(part => part.transform.position).ToArray();
            foreach (var (key, axis) in axes) {
                Select(original[0], false);
                var vertices = Vertices(original[0]);
                var center = original[0].transform.position;
                await OpenMirror(duplicate: true);
                await Tap(key);
                var copies = BuildTransfer.SelectedBodies(selection.current);
                Require(selection.current.individual && copies.Count == original.Length && copies.All(Mirrored) &&
                    copies.All(part => !original.Contains(part)), "Mirror-copy selected originals or omitted copies.");
                CheckReflected(vertices, Vertices(copies.Single(part => part.GetComponent<Screw>() != null)), center, axis);
                CheckGroup(original);
                CheckGroup(copies);
                var starts = copies.Select(part => part.transform.position).ToArray();
                await GrabOneInch();
                for (int i = 0; i < original.Length; i++) Near(original[i].transform.position, totals[i], "G moved an original");
                for (int i = 0; i < copies.Count; i++) Near(copies[i].transform.position, starts[i] + Vector3.right, "G did not move a copy");
                CheckGroup(copies);
                StateSystem.instance.Undo();
                await Task.Delay(330);
                StateSystem.instance.Undo();
                Require(BuildTransfer.SelectedBodies(selection.current).ToHashSet().SetEquals(original) &&
                    PartsManager.FindLoadedObjects().Count == original.Length, "Undo left copy bodies or stale selection.");
                StateSystem.instance.Redo();
                Require(BuildTransfer.SelectedBodies(selection.current).ToHashSet().SetEquals(copies), "Redo failed to select exactly the copies.");
                StateSystem.instance.Undo();
                await Frames();
            }
            await MoveAndLink(original, totals);
            await CustomRoundTrip();
            await Routes();
        }

        private static async Task MoveAndLink(GameObject[] original, Vector3[] originalPositions) {
            Select(original[0], false);
            await OpenMirror(duplicate: true);
            await Tap(Key.X);
            var copies = BuildTransfer.SelectedBodies(selection.current);
            var screw = copies.Single(body => body.GetComponent<Screw>() != null);
            var starts = copies.Select(body => body.transform.position).ToArray();
            var move = Field<MoveToolManager>(tool, "moveToolManager");
            var placement = Field<Placement>(tool, "placement");
            var movement = Field<MovementManager>(placement, "movementManager");
            var previewTarget = selection.current.gameObject.transform.position + Vector3.right * 3;

            move.StartMoving();
            Require(placement.placing, "Move did not accept the mirror-copy selection.");
            var external = Mount("PLTE-1-1", screw.transform.position + Vector3.right * 3 - screw.transform.forward * .1f,
                HoleCollider.HoleType.Normal);
            var externalPosition = external.transform.position;
            Set(placement, "currentDisplacement", new Displacement(previewTarget, selection.current.gameObject.transform.rotation));
            Invoke(placement, "Place");
            await Until(() => Vector3.Distance(screw.transform.position, starts[copies.IndexOf(screw)] + Vector3.right * 3) < .002f);
            if (placement.placing) placement.CancelPlacement();
            await Until(() => screw.TryGetGroup(out List<GameObject> group) && group.Contains(external));
            for (int i = 0; i < copies.Count; i++) Near(copies[i].transform.position, starts[i] + Vector3.right * 3, "Move omitted a mirror copy");

            var from = selection.current.gameObject.transform.position;
            movement.StartContinuous();
            movement.MoveTo(from + Vector3.up * 2);
            movement.StopContinuous();
            await Until(() => Vector3.Distance(selection.current.gameObject.transform.position, from + Vector3.up * 2) < .002f);
            for (int i = 0; i < original.Length; i++) Near(original[i].transform.position, originalPositions[i], "Move or handle motion moved an original");
            for (int i = 0; i < copies.Count; i++) Near(copies[i].transform.position, starts[i] + Vector3.right * 3 + Vector3.up * 2, "Handle movement omitted a mirror copy");
            Near(external.transform.position, externalPosition, "The handle dragged an attached unselected part");
        }

        private static async Task Routes() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var sprockets = ChainContourValidation.AdvancedFixture();
            var originalChain = ChainLoop.FromData(ChainContourValidation.Route(sprockets));
            Require(originalChain != null, "Could not create the explicit chain fixture.");
            var drum = PartsManager.GeneratePart("WDRM", Vector3.up * 10, Quaternion.identity).GetComponent<SavedObject>();
            var pulley = PartsManager.GeneratePart("PULY", new Vector3(4, 10, .025f), Quaternion.identity).GetComponent<SavedObject>();
            var attachment = PartsManager.GeneratePart("PLTE-1-1", new Vector3(10, 14, 0), Quaternion.identity).GetComponent<SavedObject>();
            var groove = drum.GetComponent<RopeGroove>();
            var rope = RopeRoute.FromData(new RopeRouteData {
                start = RopeAttachment.At(drum, groove.Center - Vector3.right * groove.EffectiveRadius()),
                end = RopeAttachment.At(attachment, attachment.transform.position),
                pulleys = new[] { pulley.ConnectionId }, wraps = new[] { 0, 0 }
            });
            float length = RopeLength(rope);
            var sources = sprockets.Select(part => part.gameObject)
                .Concat(new[] { drum.gameObject, pulley.gameObject, attachment.gameObject }).ToList();
            selection.SetCurrent(new MultiSelection(null, sources) { individual = true }, true, true);
            await OpenMirror(duplicate: true);
            await Tap(Key.X);
            var copyIds = BuildTransfer.SelectedBodies(selection.current).Select(Id).ToHashSet();
            var copiedChain = Object.FindObjectsByType<ChainLoop>().SingleOrDefault(chain => chain != originalChain);
            Require(copiedChain != null && copiedChain.TryContour(out _, out _) && copiedChain.MemberIds.All(copyIds.Contains),
                "An explicit chain was omitted or retained original endpoint IDs.");
            var copiedRope = Object.FindObjectsByType<RopeRoute>().SingleOrDefault(route => route != rope);
            Require(copiedRope != null && copiedRope.MemberIds.All(copyIds.Contains) && Mathf.Abs(RopeLength(copiedRope) - length) < .002f,
                "Mirroring a complete rope changed its contour length or retained original endpoint IDs.");
            StateSystem.instance.Undo();
            Require(!copiedChain.gameObject.activeSelf && !copiedRope.gameObject.activeSelf, "Undo kept copied routes.");
            StateSystem.instance.Redo();
            Require(copiedChain.gameObject.activeSelf && copiedRope.gameObject.activeSelf, "Redo failed to restore copied routes.");
        }

        private static float RopeLength(RopeRoute route) {
            var parts = route.MemberIds.Select(id => ById(id).GetComponent<SavedObject>()).ToArray();
            object[] args = { route.Data, parts, null, null };
            bool valid = (bool)typeof(RopeRoute).GetMethod("TryContour", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
            Require(valid, "Invalid rope fixture or reflected contour. " + args[3]);
            return (float)args[2].GetType().GetField("length").GetValue(args[2]);
        }

        private static async Task CustomRoundTrip() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var body = CustomPart();
            var id = Id(body);
            var original = Vertices(body);
            var mount = body.GetComponentInChildren<CustomMountPoint>();
            var surface = mount.Surface;
            var direction = mount.transform.forward;
            var marker = new GameObject("Mirror validation hole face", typeof(MeshFilter), typeof(HoleFace)).GetComponent<HoleFace>();
            selection.SetCurrent(new HoleSelection {
                gameObject = marker.gameObject, holeFace = marker,
                holeData = mount.GetComponent<HoleCollider>().holeData, faceDirection = direction, individual = true
            }, true, true);
            var pivot = marker.transform.position;
            await OpenMirror();
            await Tap(Key.Z);
            var mirroredSurface = Reflected(surface, pivot, Vector3.forward);
            Near(mount.Surface, mirroredSurface, "Custom mount face position");
            Near(mount.transform.forward, Vector3.Reflect(direction, Vector3.forward), "Custom mount face normal");
            Near(marker.direction, Vector3.Reflect(direction, Vector3.forward), "Selected face normal");
            CheckReflected(original, Vertices(body), pivot, Vector3.forward);
            CheckMeshCollider(body);
            var captured = BuildTransfer.Capture(selection.current);
            var clipboard = BuildTransfer.FromClipboard(BuildTransfer.ToClipboard(captured));
            Require(clipboard.parts.Single().mirrored, "Clipboard dropped handedness.");
            var placement = Object.FindFirstObjectByType<Placement>();
            using (var paste = SelectionPlacementData.FromBuild(clipboard, placement.transform, Object.FindFirstObjectByType<ObjectDuplicator>(), selection)) {
                var pasted = paste.Generate(captured.placementAnchor.GetPose().position, captured.placementAnchor.GetPose().rotation);
                Near(pasted.Single().GetComponentInChildren<CustomMountPoint>().Surface, mirroredSurface, "Pasted custom mount face");
                Require(Mirrored(pasted.Single()), "Paste dropped handedness.");
                ObjectDuplicator.DiscardCopies(pasted);
            }
            var path = Path.Combine(Path.GetTempPath(), "protobot-mirror-" + Guid.NewGuid().ToString("N") + ".pbb");
            try {
                var builds = Object.FindFirstObjectByType<BuildsManager>();
                builds.buildPath = path;
                Require(await (Task<bool>)Invoke(builds, "SaveAsync", false), "Could not save the mirrored build.");
                Require(await builds.OpenPathAsync(path), "Could not reopen the mirrored build.");
                await Frames();
                body = ById(id);
                Require(Mirrored(body), "Reopen dropped handedness.");
                Near(body.GetComponentInChildren<CustomMountPoint>().Surface, mirroredSurface, "Reopened custom mount face");
                CheckReflected(original, Vertices(body), pivot, Vector3.forward);
                CheckMeshCollider(body);
            }
            finally { File.Delete(path); }
        }

        private static async Task Shared() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var body = CustomPart();
            var id = Id(body);
            var originalVertices = Vertices(body);
            var pivot = body.transform.position;
            CollaborationSession.EnableDevelopment();
            var session = Object.FindFirstObjectByType<CollaborationSession>();
            Set(session, "port", "0");
            await (Task)Invoke(session, "HostAsync");
            Require(CollaborationSession.IsShared, "Unable to start the matching owned helper.");
            using var peer = new CollaborationClient(Field<string>(session, "endpoint"), Field<string>(session, "room"),
                CollaborationScene.Catalog, CollaborationTransport.LongPolling);
            await peer.JoinAsync("Mirror observer");
            Require(peer.SupportsMirror, "The helper did not advertise Mirror support.");
            body = ById(id);
            Select(body, true);

            var held = await peer.ReserveAsync(new[] { id });
            await OpenMirror(duplicate: true);
            await Tap(Key.X);
            await Until(() => !CollaborationSession.HasReservation && KeyboardTransformTool.ControlsText == null);
            Require(PartsManager.FindLoadedObjects().Count == 1 && selection.current.gameObject == body && peer.Snapshot.revision == 0,
                "Reservation refusal created copies or changed selection.");
            await peer.ReleaseAsync(held.token);

            await OpenMirror(duplicate: true);
            await Tap(Key.Escape);
            await Tap(Key.Y);
            Require(peer.Snapshot.revision == 0 && PartsManager.FindLoadedObjects().Count == 1 && selection.current.gameObject == body,
                "Canceled shared mirror-copy changed the room or selection.");

            await OpenMirror();
            await Tap(Key.Y);
            await Until(() => Mirrored(body) && peer.Snapshot.parts.Single().mirrored && !CollaborationSession.HasReservation);
            CheckReflected(originalVertices, Vertices(body), pivot, Vector3.up);
            Require(DocumentModel.Hash(peer.Snapshot) == CollaborationSession.CommittedHash, "Two clients saw different content after Mirror.");
            StateSystem.instance.Undo();
            await Until(() => !Mirrored(body) && !peer.Snapshot.parts.Single().mirrored && CollaborationSession.CanEdit);
            StateSystem.instance.Redo();
            await Until(() => Mirrored(body) && peer.Snapshot.parts.Single().mirrored && CollaborationSession.CanEdit);

            var mirrored = Vertices(body);
            await OpenMirror(duplicate: true);
            await Tap(Key.Z);
            await Until(() => peer.Snapshot.parts.Length == 2 && BuildTransfer.SelectedBodies(selection.current).SingleOrDefault() != body && !CollaborationSession.HasReservation);
            var clone = BuildTransfer.SelectedBodies(selection.current).Single();
            var cloneId = Id(clone);
            Require(Mirrored(body) && !Mirrored(clone) && !peer.Snapshot.parts.Single(part => part.id == cloneId).mirrored &&
                DocumentModel.Hash(peer.Snapshot) == CollaborationSession.CommittedHash, "Second client received different handedness or selection followed an original.");
            CheckReflected(mirrored, Vertices(clone), pivot, Vector3.forward);
            var clonePosition = clone.transform.position;
            await GrabOneInch();
            await Until(() => Math.Abs(peer.Snapshot.parts.Single(part => part.id == cloneId).position[0] - clonePosition.x - 1) < .001);
            Near(body.transform.position, pivot, "Shared G moved the original");
            Near(clone.transform.position, clonePosition + Vector3.right, "Shared G did not move its copy");
            var remote = CollaborationScene.ToBuild(peer.Snapshot);
            CollaborationSession.LeaveForFileOpen();
            SceneBuild.GenerateBuild(remote);
            CheckMeshCollider(ById(id));
            CheckReflected(originalVertices, Vertices(ById(id)), pivot, Vector3.up);
            Require(Mirrored(ById(id)) && !Mirrored(ById(cloneId)), "The second client's accepted snapshot materialized different handedness.");
        }

        private static GameObject CustomPart() {
            var body = CustomModel.Create(new CustomModelData {
                name = "Mirror tetrahedron", vertices = new[] { 0f, 0f, 0f, 2f, 0f, 0f, 0f, 1f, 0f, .4f, .2f, 1.5f },
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 }
            }, new Vector3(-1, .25f, 2), Quaternion.Euler(-21, 33, 18));
            var frame = body.transform;
            body.GetComponent<CustomModel>().AddMount(frame.TransformPoint(new Vector3(.6f, .3f, 0)),
                frame.TransformDirection(Vector3.back), .169f, .046f, true);
            return body;
        }

        private static async Task<GameObject[]> FastenedAssembly(Vector3 pivot) {
            var screw = PartsManager.GeneratePart("SCRW-1.00in", pivot, Quaternion.identity);
            var plate = Mount("PLTE-3-3", pivot - Vector3.forward * .3f, HoleCollider.HoleType.Normal);
            var nut = Mount("NUT-Hex", pivot - Vector3.forward * .7f, HoleCollider.HoleType.Threaded);
            var result = new[] { screw, plate, nut };
            await Until(() => screw.TryGetGroup(out List<GameObject> group) && group.ToHashSet().SetEquals(result));
            return result;
        }

        private static GameObject Mount(string id, Vector3 center, HoleCollider.HoleType type) {
            var body = PartsManager.GeneratePart(id, center, Quaternion.identity);
            var hole = body.GetComponentsInChildren<HoleCollider>().Where(item => item.holeType == type)
                .OrderBy(item => (item.transform.position - center).sqrMagnitude).First();
            body.transform.rotation = Quaternion.FromToRotation(hole.transform.forward, Vector3.forward);
            body.transform.position += center - hole.transform.position;
            Physics.SyncTransforms();
            return body;
        }

        private static void CheckGroup(IReadOnlyCollection<GameObject> expected) {
            var screw = expected.Single(body => body.GetComponent<Screw>() != null);
            Require(screw.TryGetGroup(out List<GameObject> actual) && actual.ToHashSet().SetEquals(expected),
                "A fastener lost an internal link or formed a cross-link between overlapping assemblies.");
        }

        private static void CheckMeshCollider(GameObject body) {
            var collider = body.GetComponent<MeshCollider>();
            var mesh = body.GetComponent<MeshFilter>().sharedMesh;
            Require(collider.sharedMesh == mesh, "Custom collider and rendering use different meshes.");
            var tri = mesh.triangles;
            var vertices = Vertices(body);
            var a = vertices[tri[0]]; var b = vertices[tri[1]]; var c = vertices[tri[2]];
            var face = (a + b + c) / 3;
            var normal = Vector3.Cross(b - a, c - a).normalized;
            Physics.SyncTransforms();
            Require(collider.Raycast(new Ray(face + normal, -normal), out var hit, 1.01f), "Reflected collider could not be picked from outside.");
            Near(hit.point, face, "Reflected collider ray hit");
        }

        private static async Task OpenMirror(bool duplicate = false) {
            await Keys(duplicate ? new[] { Key.LeftAlt, Key.K } : new[] { Key.K });
            Require(KeyboardTransformTool.ControlsText == Prompt, "K did not activate the actual Mirror input.");
            await Keys(); // Duplication must be latched before the axis press.
        }

        private static async Task GrabOneInch() {
            await Tap(Key.G);
            await Until(() => KeyboardTransformTool.ControlsText?.StartsWith("Grab", StringComparison.Ordinal) == true);
            await Tap(Key.X);
            await Tap(Key.Slash);
            InputSystem.QueueTextEvent(keyboard, '1');
            await Frames();
            await Tap(Key.Enter);
            await Until(() => KeyboardTransformTool.ControlsText == null && !CollaborationSession.HasReservation);
        }

        private static async Task Tap(Key key) { await Keys(key); await Keys(); }
        private static async Task Keys(params Key[] keys) {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            await Frames();
        }
        private static async Task Frames() => await InteractionOverhaulValidation.Frames();
        private static async Task Until(Func<bool> condition) {
            var timeout = EditorApplication.timeSinceStartup + 12;
            while (!condition()) {
                Require(EditorApplication.timeSinceStartup < timeout, "Timed out waiting for Main or the room.");
                await Frames();
            }
        }
        private static void Select(GameObject body, bool individual) => selection.SetCurrent(
            new ObjectSelection { gameObject = body, individual = individual }, true, bypassResponseSelectors: true);
        private static bool Mirrored(GameObject body) => body.GetComponent<SavedObject>().Mirrored;
        private static string Id(GameObject body) => body.GetComponent<SavedObject>().ConnectionId;
        private static GameObject ById(string id) => PartsManager.FindLoadedObjects().Single(body => Id(body) == id);
        private static Vector3[] Vertices(GameObject body) => body.GetComponent<MeshFilter>().sharedMesh.vertices.Select(body.transform.TransformPoint).ToArray();
        private static Vector3 Reflected(Vector3 point, Vector3 pivot, Vector3 axis) => pivot + Vector3.Reflect(point - pivot, axis);
        private static void CheckReflected(Vector3[] original, Vector3[] actual, Vector3 pivot, Vector3 axis) {
            Require(original.Length == actual.Length, "The reflected mesh changed topology.");
            for (int i = 0; i < original.Length; i++) Near(actual[i], Reflected(original[i], pivot, axis), "Reflected mesh vertex " + i);
        }
        private static void Near(Vector3 actual, Vector3 expected, string label) =>
            Require(Vector3.Distance(actual, expected) < .002f, $"{label}: expected {expected}, got {actual}.");
        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Fields).Invoke(target, args);
    }
}
