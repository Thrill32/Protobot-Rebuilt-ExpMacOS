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
                Debug.Log("Rope routing smoke passed: Main route controls, attachment picking, signed wraps, ordered pulleys, 48-inch/variable budgets, undo/redo, endpoint restoration, 1/8-inch tube, complete P/clipboard/Alt copies, and .pbb save/reopen.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        private static async Task Check() {
            GameObject.Find("Launch Canvas")?.SetActive(false);
            GameObject.Find("Splash Screen")?.SetActive(false);
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
            Click("Pick Drum"); Pick(origin + Vector3.left * .50f);
            Click("Pick End"); Pick(hole.transform.position);
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
            Click("Add Pulley"); Pick(second.transform.position + Vector3.down * .3f);
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
            Click("Apply");
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
            Click("Cancel");
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

        private static void Pick(Vector3 point) {
            var camera = PivotCamera.Main.camera;
            camera.transform.SetPositionAndRotation(point + Vector3.forward * 8, Quaternion.LookRotation(Vector3.back, Vector3.up));
            InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point));
            Click("Input capture");
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
