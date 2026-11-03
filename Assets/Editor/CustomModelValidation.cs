using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using DG.Tweening;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using InputEvent = Protobot.InputEvents.InputEvent;

namespace ProtobotEditorTools {
    public static class CustomModelValidation {
        private const string SmokeKey = "Protobot.CustomModelSmoke";
        private static int frames;
        private const string CubeObj = "v -1 -1 -1\nv 1 -1 -1\nv 1 1 -1\nv -1 1 -1\nv -1 -1 1\nv 1 -1 1\nv 1 1 1\nv -1 1 1\nf 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 4 8 7 3\nf 1 5 8 4\nf 2 3 7 6\n";

        [MenuItem("Tools/Protobot/Validate Custom Models")]
        public static void Validate() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var tool = Object.FindFirstObjectByType<CustomModelTool>();
            Require(tool != null, "Main scene is missing the custom model controller.");
            var data = new SerializedObject(tool);
            foreach (var field in new[] { "importInput", "mountInput", "orbitInput", "panInput", "noclipInput", "selectionManager", "placement", "refCamera" })
                Require(data.FindProperty(field).objectReferenceValue != null, "Missing custom-model reference: " + field);
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var binding in CustomModelSetup.Bindings) {
                var input = inputs.Single(i => i.name == binding.name);
                Require(input.defaultAction.bindings[0].path == binding.path, "Wrong custom-model default key.");
                Require(rows.Any(row => new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue == input), "Missing custom-model Preferences row.");
            }
            Require(Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(b => b.name == "Import Custom Model Button"), "Missing File menu import button.");
            Require(Resources.Load<Material>("Custom Models/Model") != null && Resources.Load<Material>("Custom Models/Mount") != null, "Missing player materials.");
            CheckParsers();
            Debug.Log("Custom model static validation passed: scene, menu, rebinds, materials, OBJ polygons/negative indices, ASCII/binary STL, units, locale and invalid-input rejection.");
        }

        private static void CheckParsers() {
            var oldCulture = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var cube = Parse(CubeObj, ".OBJ", 1 / 25.4f);
                Require(cube.triangles.Length == 36 && cube.vertices.Length == 24, "OBJ quads did not triangulate.");
                Require(Mathf.Abs(cube.vertices.Max() - 1 / 25.4f) < .00001f, "Model units/centering are wrong.");
                var negative = Parse("v 0 0 0\nv 2.5 0 0\nv 0 1 0\nf -3/1/1 -2/2/1 -1/3/1 # triangle", ".obj");
                Require(negative.triangles.SequenceEqual(new[] { 0, 1, 2 }), "OBJ relative/slash indices failed.");
                var concave = Parse("v 0 0 0\nv 2 0 0\nv 2 2 0\nv 1 1 0\nv 0 2 0\nf 1 2 3 4 5", ".obj");
                Require(concave.triangles.Length == 9, "Concave OBJ polygon failed.");
                var ascii = Parse("solid sample\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid sample", ".stl");
                Require(ascii.vertices.Length == 9 && ascii.triangles.Length == 3, "ASCII STL failed.");
                using var binary = new MemoryStream();
                using (var writer = new BinaryWriter(binary, Encoding.UTF8, true)) {
                    var header = new byte[80]; Encoding.ASCII.GetBytes("solid binary header").CopyTo(header, 0); writer.Write(header); writer.Write(1u);
                    writer.Write(0f); writer.Write(0f); writer.Write(1f);
                    foreach (float value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }) writer.Write(value);
                    writer.Write((ushort)0);
                }
                var stl = CustomModelImporter.Parse(binary.ToArray(), ".STL", "Binary", 1);
                Require(stl.vertices.SequenceEqual(ascii.vertices), "Binary STL with solid header was mistaken for ASCII.");
                foreach (var invalid in new[] { "", "v NaN 0 0\nf 1 1 1", "v 0 0 0\nf 0 1 2", "v 0 0 0\nf 1 2 3", "v 0 0 0" }) Reject(() => Parse(invalid, ".obj"));
                Reject(() => Parse("solid broken\nfacet normal 0 0 1\nvertex 0 0 0\nendfacet", ".stl"));
                Reject(() => Parse(CubeObj, ".fbx"));
                Reject(() => Parse(CubeObj, ".obj", 0));
            }
            finally { CultureInfo.CurrentCulture = oldCulture; }
        }

        private static CustomModelData Parse(string text, string extension, float scale = 1) => CustomModelImporter.Parse(Encoding.UTF8.GetBytes(text), extension, "Custom cube", scale);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        public static void Smoke() {
            Validate();
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0; EditorApplication.update += Tick;
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                await CheckRuntime();
                Debug.Log("Custom model runtime validation passed: asynchronous import, modal input, repeated mounts, exact face geometry, screw snapping/grouping, selected-point deletion, undo/redo, independent duplicates, embedded binary/JSON save/load and old builds.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static async Task CheckRuntime() {
            var tool = Object.FindFirstObjectByType<CustomModelTool>();
            var selection = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            var states = StateSystem.instance;
            var path = Path.Combine(Path.GetTempPath(), "Prötobot custom " + Guid.NewGuid().ToString("N") + ".OBJ");
            File.WriteAllText(path, CubeObj);
            try {
                int initialCount = PartsManager.FindLoadedObjects().Count;
                var canceledImport = tool.ImportPathAsync(path, 1);
                CustomModelTool.CancelActive();
                Require(await canceledImport == null && PartsManager.FindLoadedObjects().Count == initialCount, "Canceled import added geometry to the build.");
                Require(await tool.ImportPathAsync(path + ".missing", 1) == null && PartsManager.FindLoadedObjects().Count == initialCount, "Failed import changed the build.");
                var importTask = tool.ImportPathAsync(path, 1);
                Require(CustomModelTool.IsCapturingInput && CustomModelTool.BlocksCamera, "Import does not capture input.");
                Require(await tool.ImportPathAsync(path, 1) == null, "Overlapping import was allowed.");
                var obj = await importTask;
                Require(obj != null && selection.current?.gameObject == obj, "Import did not create/select a model.");
                File.Delete(path); // Everything below must work without the original model file.
                var model = obj.GetComponent<CustomModel>();
                obj.transform.SetPositionAndRotation(new Vector3(1000, 1000, 1000), Quaternion.Euler(17, 29, 8));
                states.Undo(); Require(!obj.activeSelf, "Undo import failed.");
                states.Redo(); Require(obj.activeSelf, "Redo import failed.");
                obj.transform.DOKill();
                obj.transform.SetPositionAndRotation(new Vector3(1000, 1000, 1000), Quaternion.Euler(17, 29, 8));

                var surface = obj.transform.TransformPoint(Vector3.forward);
                var normal = obj.transform.forward;
                var point = model.AddMount(surface, normal, .169f, .1f, true);
                point.RecordAdded();
                Require(Vector3.Distance(point.Surface, surface) < .0001f, "Mounting face is offset from the surface.");
                Require(point.GetComponent<HoleCollider>().holeData.part == obj && Mathf.Abs(point.GetComponent<HoleCollider>().holeData.depth - .1f) < .00001f, "Mount lost its ownership/depth.");
                var ringRay = new Ray(surface + point.transform.right * (.169f * .45f) + normal * 3, -normal);
                Physics.SyncTransforms();
                Require(MoveTargeting.TryRaycast(ringRay, LayerMask.GetMask("Default", "HoleCollisions"), false, false, null, out var ringTarget) &&
                    ringTarget.hole == point.GetComponent<HoleCollider>().holeData, "The visible mounting ring is not selectable over the model.");
                Require(model.AddMount(surface, normal, .169f, .1f, true) == null, "Repeated click created an overlapping mounting point.");
                var point2 = model.AddMount(obj.transform.TransformPoint(new Vector3(.5f, 0, 1)), normal, .2f, .25f, false);
                point2.RecordAdded();
                states.Undo(); Require(!point2.gameObject.activeSelf && point.gameObject.activeSelf, "Undo mount addition changed another point.");
                states.Redo(); Require(point2.gameObject.activeSelf, "Redo mount addition failed.");
                var beforeDelete = SceneBuild.ToBuildData();

                var face = new SerializedObject(selection.GetComponent<HoleFaceResponseSelector>()).FindProperty("selectHoleFace").objectReferenceValue as HoleFace;
                selection.SetCurrent(new HoleSelection { gameObject = face.gameObject, holeFace = face, holeData = point.GetComponent<HoleCollider>().holeData,
                    faceDirection = normal, individual = true }, true);
                var deletor = Object.FindFirstObjectByType<ObjectDeletor>();
                deletor.Execute();
                Require(!point.gameObject.activeSelf && obj.activeSelf && point2.gameObject.activeSelf, "Selected-point Delete deleted the model or another point.");
                Require(!SceneBuild.ToBuildData().CompareData(beforeDelete), "Mount deletion was not detected as an unsaved edit.");
                states.Undo(); Require(point.gameObject.activeSelf, "Undo mount deletion failed.");
                states.Redo(); Require(!point.gameObject.activeSelf, "Redo mount deletion failed.");
                states.Undo();
                selection.ClearCurrent(false, true, true);

                var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
                var copy = duplicator.CreateCopies(new[] { obj }).Single();
                copy.transform.position += Vector3.right * 4;
                Require(copy.GetComponent<MeshFilter>().sharedMesh != obj.GetComponent<MeshFilter>().sharedMesh, "Cloned models share owned mesh lifetime.");
                var copyPoint = copy.GetComponentsInChildren<CustomMountPoint>().First();
                Require(Mathf.Abs(copyPoint.GetComponent<HoleCollider>().holeData.depth - .1f) < .00001f, "Cloned point lost its physical depth.");
                copyPoint.Delete(null);
                Require(obj.GetComponentsInChildren<CustomMountPoint>().Length == 2, "Deleting a cloned point changed the source model.");

                // Actual hardware must see the same custom hole as pointer placement and graph assembly.
                var screwGen = PartsManager.GetPartType("SCRW").GetComponent<PartGenerator>();
                PartParameterEditing.Load(screwGen, "SCRW-1.00in");
                var screwObj = screwGen.Generate(surface, Quaternion.LookRotation(normal));
                var screw = screwObj.GetComponent<Screw>();
                Physics.SyncTransforms(); screw.holeDetector.RefreshOverlaps(); screw.FormConnection();
                Require(screw.holeDetector.holes.Contains(point.GetComponent<HoleCollider>()), "Screw detector cannot use a custom threaded point.");
                Require(obj.TryGetGroup(out System.Collections.Generic.List<GameObject> group) && group.Contains(screwObj), "Custom point did not connect the model and screw.");
                var ray = new Ray(surface + normal * 3, -normal);
                Require(MoveTargeting.TryRaycast(ray, LayerMask.GetMask("Default", "HoleCollisions"), false, false,
                    t => t.IsChildOf(screwObj.transform), out var target) && target.hole == point.GetComponent<HoleCollider>().holeData,
                    "Custom mounting point is hidden by the model surface during snapping.");
                point.Delete(null);
                Require(!obj.TryGetGroup(out System.Collections.Generic.List<GameObject> _), "Deleting the point left a screw connection.");
                states.Undo();
                Require(screw.holeDetector.holes.Contains(point.GetComponent<HoleCollider>()), "Undo point deletion did not restore screw occupancy.");

                var snapshot = SceneBuild.ToBuildData();
                using var stream = new MemoryStream(); new BinaryFormatter().Serialize(stream, snapshot); stream.Position = 0;
                var restored = (BuildData)new BinaryFormatter().Deserialize(stream);
                Require(restored.CompareData(snapshot), "Embedded binary serialization changed geometry/mounts.");
                var json = JsonUtility.FromJson<BuildData>(JsonUtility.ToJson(snapshot));
                Require(json.CompareData(snapshot), "Embedded JSON serialization changed geometry/mounts.");
                Require(restored.parts.Count(p => p.customModel != null) == 2, "Saved custom-model instance count is wrong.");
                SceneBuild.GenerateBuild(restored);
                var loaded = Object.FindObjectsByType<CustomModel>(FindObjectsSortMode.None);
                Require(loaded.Length == 2 && loaded.Sum(m => m.GetComponentsInChildren<CustomMountPoint>().Length) == 3, "Embedded load lost custom models or mounting points.");
                Require(SceneBuild.ToBuildData().CompareData(restored), "Loaded models do not match their saved geometry/pose/color.");

                await NextFrame();
                tool.ToggleMounts();
                Require(CustomModelTool.IsCapturingInput && !CustomModelTool.BlocksCamera, "Repeated-point mode did not retain camera navigation.");
                var grab = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(i => i.name == "Grab Selection");
                Require(!CustomModelTool.AllowsInput(grab), "Mounting mode permits a conflicting grab.");
                // Exercise repeated pointer clicks through the real tool, rather than only its model API.
                var testCamera = new GameObject("Custom model pointer test camera", typeof(Camera));
                var camera = testCamera.GetComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 2;
                camera.pixelRect = new Rect(0, 0, Screen.width, Screen.height);
                var body = loaded.OrderBy(m => m.GetComponentsInChildren<CustomMountPoint>().Length).First();
                var pointerSurface = body.transform.TransformPoint(new Vector3(-.5f, .4f, 1));
                camera.transform.SetPositionAndRotation(pointerSurface + body.transform.forward * 5, Quaternion.LookRotation(-body.transform.forward));
                Set(tool, "refCamera", camera);
                int countBefore = body.GetComponentsInChildren<CustomMountPoint>().Length;
                await NextFrame();
                // Batch-mode GameViews are unfocused; call the pointer phase after its focus guard.
                var updateMount = typeof(CustomModelTool).GetMethod("UpdateMountPlacement", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 2; i++) {
                    var position = (Vector2)camera.WorldToScreenPoint(pointerSurface + body.transform.right * (i * .4f));
                    InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position }); InputSystem.Update();
                    await NextFrame();
                    InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = position, buttons = 1 }); InputSystem.Update();
                    updateMount.Invoke(tool, null);
                    await NextFrame();
                }
                InputSystem.QueueStateEvent(Mouse.current, new MouseState()); InputSystem.Update();
                Require(body.GetComponentsInChildren<CustomMountPoint>().Length == countBefore + 2, "Repeated surface clicks did not add mounting points.");
                Set(tool, "refCamera", Object.FindFirstObjectByType<MouseCast>().GetComponent<Camera>());
                Object.Destroy(testCamera);
                tool.ToggleMounts();
                await NextFrame();
                tool.OpenImportSettings();
                Require(CustomModelTool.BlocksCamera, "Import options did not capture the camera.");
                if (Environment.GetCommandLineArgs().Contains("-customModelRenderProbe")) {
                    await Capture("/tmp/protobot-custom-import.png");
                    CustomModelTool.CancelActive(); await NextFrame(); tool.ToggleMounts();
                    PivotCamera.Main.SetTransform(body.transform.position, new Vector3(15, 215, 0), -8);
                    for (int i = 0; i < 3; i++) await NextFrame();
                    await Capture("/tmp/protobot-custom-mounts.png");
                }
                CustomModelTool.CancelActive();
                SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
                Require(PartsManager.FindLoadedObjects().Count == 0, "A legacy/default build failed to clear custom models.");
            }
            finally { if (File.Exists(path)) File.Delete(path); CustomModelTool.CancelActive(); }
        }

        private static async Task NextFrame() { int frame = Time.frameCount; while (Time.frameCount == frame) await Task.Yield(); }
        private static async Task Capture(string path) {
            var view = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            view.Show(); view.position = new Rect(60, 60, 1280, 840); view.Focus();
            for (int i = 0; i < 5; i++) { view.Repaint(); EditorApplication.QueuePlayerLoopUpdate(); await NextFrame(); }
            File.Delete(path); ScreenCapture.CaptureScreenshot(path);
            double deadline = EditorApplication.timeSinceStartup + 15;
            while (!File.Exists(path)) {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Custom model UI did not render.");
                view.Repaint(); EditorApplication.QueuePlayerLoopUpdate(); await NextFrame();
            }
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid model was accepted."); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
