using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.StateSystems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using InputEvent = Protobot.InputEvents.InputEvent;
using static ProtobotEditorTools.CameraNavigationValidation;
using static ProtobotEditorTools.InteractionOverhaulValidation;

namespace ProtobotEditorTools {
    public static class RopeRoutingValidation {
        private const string SmokeKey = "Protobot.RopeRoutingSmoke";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int frames;

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0;
            EditorApplication.update += Tick;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Rope routing smoke passed: camera input with picks armed, marker projection, panel focus/scrolling, rebound camera release, picks after moving, Apply/Cancel, signed wraps, budgets, undo/redo, copies and .pbb save/reopen.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        private static async Task Check() {
            GameObject.Find("Launch Canvas")?.SetActive(false);
            GameObject.Find("Splash Screen")?.SetActive(false);
            FocusRouteGameView();
            await Frames();
            Require(Application.isFocused, "Rope routing input checks require a focused Game View.");
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            var selection = (SelectionManager)typeof(SimulationTool).GetField("selectionManager", PrivateInstance).GetValue(tool);
            var origin = new Vector3(15, 30, 40);
            var drum = Part("WDRM", 0, 0);
            var first = Part("PULY", 4, 0, .025f);
            var flipped = Quaternion.Euler(0, 180, 0);
            var second = PartsManager.GeneratePart("PULY", origin + new Vector3(7, 1.5f, -.025f), flipped);
            var plate = Part("PLTE-1-1", 10, 4);
            var hole = plate.GetComponentsInChildren<HoleCollider>().First();
            plate.transform.position += origin + new Vector3(10, 4, 0) - hole.transform.position;

            selection.SetCurrent(new MultiSelection(null, new() { drum, first }) { individual = true }, true, true);
            var input = (Protobot.InputEvents.InputEvent)typeof(SimulationTool).GetField("chainInput", PrivateInstance).GetValue(tool);
            SetKeys(Key.LeftShift, Key.C); input.Update();
            Require(tool.RouteEditor?.IsRope == true && SimulationTool.CapturesSelection, "Shift+C with a drum did not activate rope mode.");
            var editor = tool.RouteEditor;
            var length = CanvasObject().GetComponentsInChildren<InputField>().Single();
            Require(length.text == "48" && !editor.IsValid, "The kit length or unset attachment guard is wrong.");
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
            await Frames();
            var drumPoint = origin + Vector3.left * .50f;
            AimPick(drumPoint);
            await RouteClick(Button("Pick Drum"));
            var pending = Status();
            Require(pending.Contains("Click the winch drum"), "Click did not arm the drum pick.");
            await CheckRouteNavigation(CanvasObject(), () => {
                Require(tool.RouteEditor == editor && Status() == pending, "Camera input consumed the drum pick.");
                CheckRouteMarker(CanvasObject(), 1, drum.GetComponent<RopeGroove>().Center);
            });
            AimPick(drumPoint);
            var beforePan = ScreenPoint(drumPoint);
            await RouteMouse(beforePan, MouseButton.Middle);
            await RouteMouse(beforePan + Vector2.right * 12, MouseButton.Middle, Vector2.right * 12);
            await RouteMouse(beforePan);
            Require(Vector2.Distance(beforePan, ScreenPoint(drumPoint)) > 1 && Status() == pending, "Pan did not move the target or disarmed the drum pick.");
            await Pick(drumPoint);
            Require(Status().Contains("Pick both attachments"), "The drum point was not picked from the moved view: " + Status());

            var endPoint = hole.transform.position;
            AimPick(endPoint);
            await RouteClick(Button("Pick End"));
            pending = Status();
            var orbit = Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None).Single(action => action.name == "Orbit");
            orbit.defaultAction.ApplyBindingOverride("<Mouse>/leftButton");
            try {
                var point = ScreenPoint(endPoint);
                var rotation = PivotCamera.Main.transform.rotation;
                await RouteMouse(point, MouseButton.Left);
                await RouteMouse(point + Vector2.right * 12, MouseButton.Left, Vector2.right * 12);
                Require(Quaternion.Angle(rotation, PivotCamera.Main.transform.rotation) > .1f, "Rebound left-button orbit did not move the camera.");
                await RouteMouse(ScreenPoint(endPoint));
                Require(Status() == pending, "Releasing left-button orbit picked the rope end attachment.");
                await Pick(endPoint);
            }
            finally { orbit.defaultAction.RemoveBindingOverride(0); }
            Require(editor.IsValid, "Single-pulley route is invalid: " + Status());
            CheckPreview();
            const float drumRadius = .4210216f, pulleyRadius = .3625f;
            float drumTangent = -Mathf.Acos((drumRadius - pulleyRadius) / 4f);
            float endTangent = Mathf.Atan2(4, 6) - Mathf.Acos(pulleyRadius / Mathf.Sqrt(52));
            float singleLength = Mathf.Sqrt(16 - Mathf.Pow(drumRadius - pulleyRadius, 2)) + Mathf.Sqrt(52 - pulleyRadius * pulleyRadius) +
                (Mathf.PI + drumTangent) * drumRadius + (endTangent - drumTangent) * pulleyRadius;
            Require(Status().Contains($"Routed {singleLength:0.00} in   ·   {48 - singleLength:0.00} in remaining"), "The routed length or remaining budget is wrong: " + Status());
            var platePosition = plate.transform.position;
            plate.transform.position += new Vector3(-3, -6, 0);
            ClickRow("Rope point 2", "Wrap");
            Require(editor.IsValid, "A clear inner wrap did not preview: " + Status());
            CheckPreview();
            plate.transform.position = platePosition;
            ClickRow("Rope point 2", "Wrap");
            var pulleyPoint = second.transform.position + Vector3.down * .3f;
            AimPick(pulleyPoint);
            await RouteClick(Button("Add Pulley"));
            pending = Status();
            await RouteMouse(ScreenPoint(pulleyPoint), wheel: 120);
            Require(Status() == pending, "Wheel zoom consumed the pulley pick.");
            var noclip = Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None).Single(action => action.name == "Noclip Camera");
            noclip.defaultAction.ApplyBindingOverride("<Mouse>/leftButton");
            try {
                var point = ScreenPoint(pulleyPoint);
                await RouteMouse(point, MouseButton.Left);
                Require(!InputEvent.IsNoclipMovementCaptured, "A stationary pick press locked the cursor.");
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.W));
                await Frames();
                Require(InputEvent.IsNoclipMovementCaptured, "The rebound free-fly binding did not start when movement began.");
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
                await RouteMouse(ScreenPoint(pulleyPoint));
                Require(!InputEvent.IsNoclipMovementCaptured && Status() == pending, "Releasing rebound free-fly picked a pulley.");
                await Pick(pulleyPoint);
            }
            finally { noclip.defaultAction.RemoveBindingOverride(0); }
            Require(editor.IsValid && CanvasObject().GetComponentsInChildren<RectTransform>().Count(row => row.name.StartsWith("Rope point ")) >= 4,
                "A second pulley did not appear in numbered order: " + Status());
            Click("Reverse"); Require(!editor.IsValid, "An obstructed reversed route was accepted.");
            Click("Reverse"); Require(editor.IsValid, "Restoring order did not restore route validity.");
            ClickRow("Rope point 2", "Wrap");
            Require(editor.IsValid || GameObject.Find("Rope route preview").GetComponent<MeshRenderer>().enabled == false,
                "An invalid inner wrap left an active preview.");
            ClickRow("Rope point 2", "Wrap");
            Require(editor.IsValid, "Restoring the outer wrap did not restore route validity.");
            Require(SceneBuild.ToBuildData().ropes.Length == 0, "Private rope preview entered persistence.");
            SetKeys(); input.Update();

            length.text = "2.5";
            Require(!editor.IsValid && Status().Contains("over length") && !Button("Apply").interactable, "Overlength did not disable Apply.");
            editor.Apply();
            Require(tool.RouteEditor == editor && SceneBuild.ToBuildData().ropes.Length == 0, "Overlength route mutated the build.");
            CheckPreview();
            length.text = "24.5";
            Require(editor.IsValid && Status().Contains("Routed") && Status().Contains("remaining"), "Variable-length rope status is missing.");
            await RouteClick(length);
            Require(length.isFocused, "The routing length field did not receive focus.");
            await RouteClick(Button("Apply"));
            var rope = RopeRoute.Find(Id(drum));
            Require(rope != null && tool.RouteEditor == null, "Apply did not create and close the rope route.");
            var original = rope.Data;
            Require(original.availableLength == 24.5f && original.start.part == Id(drum) && original.end.part == Id(plate) &&
                original.pulleys.SequenceEqual(new[] { Id(first), Id(second) }), "Rope route lost order, attachments, or available length.");
            Near(Vector3.Distance(drum.transform.TransformPoint(original.start.LocalPoint), drum.GetComponent<RopeGroove>().Center), .4210216f, "Drum attachment radius");
            CheckTube(rope.gameObject);

            StateSystem.instance.Undo();
            Require(RopeRoute.Find(Id(drum)) == null, "Undo did not remove the newly created rope.");
            StateSystem.instance.Redo();
            Require(RopeRoute.Find(Id(drum))?.Data.id == original.id, "Redo lost rope identity.");

            tool.EditRopeRoute();
            Require(tool.RouteEditor?.IsValid == true, "The existing route did not reopen.");
            CanvasObject().GetComponentInChildren<InputField>().text = "31.25";
            Click("Apply");
            Require(rope.Data.id == original.id && rope.Data.availableLength == 31.25f, "Editing did not retain identity or changed the wrong length.");
            StateSystem.instance.Undo(); Near(rope.Data.availableLength, 24.5f, "Undo rope edit");
            StateSystem.instance.Redo(); Near(rope.Data.availableLength, 31.25f, "Redo rope edit");

            tool.EditRopeRoute();
            CanvasObject().GetComponentInChildren<InputField>().text = "9";
            await Frames();
            await RouteClick(Button("Cancel"));
            Near(rope.Data.availableLength, 31.25f, "Cancel changed the rope");
            tool.EditRopeRoute();
            Click("Remove");
            Require(RopeRoute.Find(Id(drum)) == null, "Remove left the rope active.");
            StateSystem.instance.Undo(); Require(RopeRoute.Find(Id(drum)) == rope, "Undo did not restore the removed rope.");
            StateSystem.instance.Redo(); Require(RopeRoute.Find(Id(drum)) == null, "Redo did not remove the rope again.");
            StateSystem.instance.Undo();

            var refresh = typeof(RopeRoute).GetMethod("LateUpdate", PrivateInstance);
            ObjectElement.SetExistence(plate, true, false);
            refresh.Invoke(rope, null);
            Require(!rope.GetComponent<MeshRenderer>().enabled && SceneBuild.ToBuildData().ropes.Length == 0, "Deleted attachment left a visible or saved dangling rope.");
            StateSystem.instance.Undo();
            refresh.Invoke(rope, null);
            Require(rope.GetComponent<MeshRenderer>().enabled && rope.Data.id == original.id, "Restoring attachment lost the rope.");
            var beforePoseChange = second.transform.position;
            second.transform.position += Vector3.forward * .25f;
            refresh.Invoke(rope, null);
            Require(!rope.GetComponent<MeshRenderer>().enabled && SceneBuild.ToBuildData().ropes.Single().id == original.id, "Nonplanar rope was not hidden or its data was discarded.");
            second.transform.position = beforePoseChange;
            refresh.Invoke(rope, null);
            CheckTube(rope.gameObject);

            var all = new[] { drum, first, second, plate };
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
            var partial = duplicator.CreateCopies(all.Take(3));
            Require(Object.FindObjectsByType<RopeRoute>(FindObjectsSortMode.None).Length == 1, "An incomplete direct duplication copied a rope.");
            ObjectDuplicator.DiscardCopies(partial);
            var alt = duplicator.CreateCopies(all);
            CheckCopy(Object.FindObjectsByType<RopeRoute>(FindObjectsSortMode.None).Single(copy => copy != rope), alt);
            ObjectDuplicator.DiscardCopies(alt);

            var build = SceneBuild.ToBuildData();
            Require(build.ropes.Single().id == original.id && SceneBuild.ToBuildData(all.Take(3)).ropes.Length == 0,
                "Selection capture dropped a complete route or included a dangling route.");
            selection.SetCurrent(new MultiSelection(null, all.ToList()) { individual = true }, true, true);
            var placement = Object.FindFirstObjectByType<Placement>();
            using (var frozen = SelectionPlacementData.Capture(selection.current, placement.transform, duplicator)) {
                var copies = frozen.Generate(origin + Vector3.up * 20, Quaternion.Euler(25, 30, 15));
                CheckCopy(frozen.GeneratedRopes.Single().GetComponent<RopeRoute>(), copies);
                ObjectDuplicator.DiscardCopies(copies);
            }

            var transfer = Object.FindFirstObjectByType<BuildTransferTool>();
            var previousClipboard = GUIUtility.systemCopyBuffer;
            try {
                await Task.Delay(50);
                Require(await transfer.CopySelectedAsync() && await transfer.PasteAsync(), "The Main clipboard tool could not copy and paste the route.");
            }
            finally { GUIUtility.systemCopyBuffer = previousClipboard; }
            var paste = placement.CurrentSelectionPlacement;
            typeof(Placement).GetMethod("Place", PrivateInstance).Invoke(placement, null);
            var pasted = paste.GeneratedRopes.Single().GetComponent<RopeRoute>();
            var copiedParts = PartsManager.FindLoadedObjects().Where(obj => pasted.MemberIds.Contains(Id(obj))).ToList();
            CheckCopy(pasted, copiedParts);
            StateSystem.instance.Undo(); Require(!pasted.gameObject.activeSelf, "Undo did not remove a pasted route.");
            StateSystem.instance.Redo(); Require(pasted.gameObject.activeSelf, "Redo did not restore a pasted route.");
            StateSystem.instance.Undo();
            placement.CancelPlacement();
            selection.ClearCurrent(false, true, true);

            var builds = Object.FindFirstObjectByType<BuildsManager>();
            string path = Path.Combine(Path.GetTempPath(), "Protobot rope " + Guid.NewGuid().ToString("N") + ".pbb");
            try {
                builds.buildPath = path;
                var save = (Task<bool>)typeof(BuildsManager).GetMethod("SaveAsync", PrivateInstance).Invoke(builds, new object[] { false });
                Require(await save && File.Exists(path) && builds.CurrentBuildIsSaved, "Main did not save the rope build.");
                builds.CreateNewBuild();
                Require(Object.FindObjectsByType<RopeRoute>(FindObjectsSortMode.None).Length == 0, "New Build retained an active rope.");
                Require(await builds.OpenPathAsync(path) && builds.CurrentBuildIsSaved, "Main did not reopen the rope build cleanly.");
                var reopened = RopeRoute.Find(original.start.part);
                Require(reopened?.Data.id == original.id && reopened.Data.pulleys.SequenceEqual(original.pulleys) &&
                    reopened.Data.availableLength == 31.25f, "Rope identity or route data changed on reopening.");
                CheckTube(reopened.gameObject);
                var malformed = BuildsManager.ParsePath(path);
                malformed.ropes[0].pulleys[0] = malformed.ropes[0].end.part;
                bool rejected = false;
                try { SceneBuild.GenerateBuild(malformed); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && RopeRoute.Find(original.start.part) == reopened, "Malformed rope replaced the live build.");
            }
            finally { File.Delete(path); }

            GameObject Part(string id, float x, float y, float z = 0) =>
                PartsManager.GeneratePart(id, origin + new Vector3(x, y, z), Quaternion.identity);

            string Status() => CanvasObject().GetComponentsInChildren<Text>().Single(t => t.name == "Route status").text;
            void CheckCopy(RopeRoute copy, System.Collections.Generic.IEnumerable<GameObject> bodies) {
                Require(copy.Data.id != original.id && copy.Data.availableLength == 31.25f &&
                    copy.Data.wraps.SequenceEqual(original.wraps) && !copy.MemberIds.Intersect(original.MemberIds).Any() &&
                    copy.MemberIds.ToHashSet().SetEquals(bodies.Select(Id)), "Copy lost metadata or retained original endpoint IDs.");
                CheckTube(copy.gameObject);
            }
        }

        private static GameObject CanvasObject() => GameObject.Find("Rope Routing");
        private static Button Button(string name) => CanvasObject().GetComponentsInChildren<Button>().Single(button => button.name == name);
        private static void Click(string name) => Press(Button(name));
        private static void ClickRow(string row, string name) => Press(CanvasObject().GetComponentsInChildren<Button>()
            .Single(button => button.name == name && button.transform.parent.name == row));
        private static void Press(Button button) => ExecuteEvents.Execute(button.gameObject,
            new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);

        private static Vector2 ScreenPoint(Vector3 world) => PivotCamera.Main.camera.WorldToScreenPoint(world);

        private static void AimPick(Vector3 world) {
            var pivot = PivotCamera.Main;
            var rotation = new Vector3(0, 180, 0);
            pivot.SetTransform(world, rotation, -8);
            var viewPoint = new Vector3(Screen.width * .31f, pivot.camera.pixelRect.center.y, 8);
            pivot.SetTransform(2 * world - pivot.camera.ScreenToWorldPoint(viewPoint), rotation, -8);
        }

        private static Task Pick(Vector3 world) {
            var point = ScreenPoint(world);
            Require(Hit(point) == CanvasObject().transform.Find("Input capture").gameObject, "The fixture's pick fell outside the exposed viewport.");
            return RouteClick(point);
        }

        private static void CheckPreview() => CheckTube(GameObject.Find("Rope route preview"));
        private static void CheckTube(GameObject body) {
            var mesh = body.GetComponent<MeshFilter>()?.sharedMesh;
            Require(mesh != null && body.GetComponent<MeshRenderer>().enabled && mesh.vertexCount > 20, "The rope tube is missing.");
            // Every ring has opposing vertices exactly one rope diameter apart.
            Near(Vector3.Distance(mesh.vertices[0], mesh.vertices[4]), .125f, "Rendered rope diameter");
        }

        private static string Id(GameObject obj) => obj.GetComponent<SavedObject>().ConnectionId;
        private static void SetKeys(params Key[] keys) {
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys));
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        }
        private static void Near(float actual, float expected, string label) =>
            Require(Mathf.Abs(actual - expected) < .001f, $"{label}: {actual:F5}, expected {expected:F5}.");
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
