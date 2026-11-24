using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.Transformations;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class ScrewHardwareValidation {
        private const string SmokeKey = "Protobot.ScrewHardwareSmoke";
        private const string SetScrewId = "SCRW-Set Screw";
        private const string RivetId = "SCRW-Bearing Rivet";
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly string[] Sizes = { "1/4in", "3/8in", "1/2in", "5/8in", "3/4in", "7/8in",
            "1.00in", "1.25in", "1.50in", "1.75in", "2.00in", "2.25in", "2.50in" };
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
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Screw hardware passed: catalog and legacy IDs, Type and Size controls, rotated CAD seats and grouping, placement and editing history, clipboard, and Main.pbb save/reopen.");
                EditorApplication.Exit(0);
            }
            catch (Exception error) {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static async Task Check() {
            GameObject.Find("Launch Canvas")?.SetActive(false);
            GameObject.Find("Splash Screen")?.SetActive(false);
            CheckCatalog();
            var menu = Object.FindFirstObjectByType<AddPartsUI>(FindObjectsInactive.Include);
            menu.DisplayListGroup("Structure");
            var row = menu.partUIsContainer.GetComponentsInChildren<PartDisplayUI>(true)
                .Single(candidate => candidate.gameObject.activeSelf && candidate.partType?.id == "SCRW");
            var placement = Object.FindFirstObjectByType<Placement>();
            var ui = Object.FindFirstObjectByType<AddPropertiesUI>();
            var selection = Reference<SelectionManager>(ui, "selectionManager");
            var typeRow = Reference<ParamDisplay>(ui, "param1Display");
            var sizeRow = Reference<ParamDisplay>(ui, "param2Display");
            var type = Reference<Dropdown>(typeRow, "dropdown");
            var size = Reference<Dropdown>(sizeRow, "dropdown");
            var generator = row.partType.GetComponent<ScrewPartGenerator>();
            var screwRule = placement.GetComponent<ScrewPlaceDisplacement>();
            var cast = Reference<MouseCast>(placement.GetComponent<PartHolePlaceDisplacement>(), "mouseCast");
            var marker = Reference<HoleFace>(screwRule, "currentHoleFace");
            var camera = cast.GetComponent<Camera>();
            var right = Reference<InputEvent>(ui, "increaseSecondInput");
            var left = Reference<InputEvent>(ui, "decreaseSecondInput");
            foreach (var input in new[] { left, right }) {
                input.rebindAction.action.Disable();
                input.defaultAction.Enable();
            }

            var rotation = Quaternion.Euler(21, 34, 43);
            var collar = Generate("CLMP-Normal-Normal", new Vector3(40, 41, 42), rotation);
            var threaded = collar.GetComponentsInChildren<HoleCollider>().Single(hole => hole.holeType == HoleCollider.HoleType.Threaded);
            var bore = collar.GetComponentsInChildren<HoleCollider>().Single(hole => hole.holeType == HoleCollider.HoleType.Clamp);
            var shaft = Generate("SHFT-Normal-2", bore.transform.position, bore.transform.rotation);

            var bearing = Generate("BRNG-Normal", new Vector3(43, 41, 42), rotation);
            var bearingHole = bearing.GetComponentsInChildren<HoleCollider>().OrderBy(hole => hole.transform.localPosition.x).First();
            var plate = Generate("PLTE-3-3", bearingHole.transform.position - bearingHole.transform.forward * .125f, rotation);
            var plateHole = plate.GetComponentsInChildren<HoleCollider>().OrderBy(hole => (hole.transform.position - bearingHole.transform.position).sqrMagnitude).First();
            plate.transform.position += bearingHole.transform.position - bearingHole.transform.forward *
                ((bearingHole.holeData.depth + plateHole.holeData.depth) * .5f) - plateHole.transform.position;
            var otherHole = plate.GetComponentsInChildren<HoleCollider>().OrderByDescending(hole => (hole.transform.position - plateHole.transform.position).sqrMagnitude).First();

            row.ToggleCurrent(true);
            await NextFrame();
            Require(placement.CurrentPartPlacement?.partGenerator == generator, "The existing Screw row did not start placement.");
            Require(type.options.Select(option => option.text).SequenceEqual(new[] { "Standard Screw", "Set Screw", "Bearing Rivet" }) &&
                type.value == 0 && sizeRow.gameObject.activeInHierarchy && size.options.Select(option => option.text).SequenceEqual(Sizes),
                "Screw type order, default, or available sizes changed.");
            Pick(size, "7/8in");
            PressSize(right, Key.RightArrow);
            Require(generator.GetId() == "SCRW-1.00in", "The preview's right arrow did not change the standard size.");
            PressSize(left, Key.LeftArrow);
            var standard = Mount(otherHole, otherHole.transform.forward, "SCRW-7/8in");
            Require(standard.GetComponent<Screw>() != null, "Standard Screw lost its threaded/nut fastener behavior.");

            Pick(type, "Set Screw");
            CheckFixedShortcuts(SetScrewId);
            SetMarker(bearingHole, bearingHole.transform.forward);
            Require(!screwRule.TryGetDisplacement(placement.CurrentPartPlacement, out _), "Set screw accepted an unthreaded bearing bore.");
            var setScrew = Mount(threaded, -threaded.transform.forward, SetScrewId);
            var setBounds = CheckMesh(setScrew, .164f, .25f);
            var tip = setScrew.transform.TransformPoint(Vector3.forward * setBounds.min.z);
            Require(shaft.GetComponent<Collider>().Raycast(new Ray(setScrew.transform.position, -setScrew.transform.forward), out var contact, .3f),
                "The set screw axis missed the shaft.");
            Near(tip, contact.point, "Set screw cup stopped at the shaft face");
            Require(setBounds.max.z > .104f && setBounds.max.z < .1042f, "The socket end has an incorrect offset from the collar.");
            CheckGroup(setScrew, collar);

            Pick(type, "Bearing Rivet");
            CheckFixedShortcuts(RivetId);
            SetMarker(threaded, -threaded.transform.forward);
            Require(!screwRule.TryGetDisplacement(placement.CurrentPartPlacement, out _), "Bearing rivet accepted a threaded hole.");
            var rivet = Mount(bearingHole, bearingHole.transform.forward, RivetId);
            var rivetBounds = CheckMesh(rivet, .3f, .525370f);
            var rimRay = new Ray(rivet.transform.position + rivet.transform.right * .134f - rivet.transform.forward * .025f, rivet.transform.forward);
            Require(rivet.GetComponent<MeshCollider>().Raycast(rimRay, out var rim, .033f), "The rivet cap has no underside at the mounting face.");
            Near(Vector3.Dot(rim.point - rivet.transform.position, rivet.transform.forward), 0, "Rivet rim seat");
            Require(-rivetBounds.min.z > bearingHole.holeData.depth + plateHole.holeData.depth, "The rivet does not reach through the plate.");
            CheckGroup(rivet, bearing, plate);
            plate.transform.position -= bearingHole.transform.forward * .02f;
            CheckGroup(rivet, bearing);
            plate.transform.position += bearingHole.transform.forward * .02f;
            CheckGroup(rivet, bearing, plate);
            var nutData = PartsManager.GetPartType("NUT").GetComponent<PartGenerator>();
            PartParameterEditing.Load(nutData, "NUT-Hex");
            var nut = new PartPlacementData(nutData, nutData.GetComponent<PartType>(), placement.transform);
            foreach (var (fastener, top) in new[] { (setScrew, setBounds.max.z), (rivet, rivetBounds.max.z) }) {
                Aim(fastener.transform.position + fastener.transform.forward * top, fastener.transform.forward);
                Require(cast.gameObject == fastener && !placement.GetComponent<NutPlaceDisplacement>().TryGetDisplacement(nut, out _),
                    "The fixed fastener appeared as a nut endpoint.");
            }

            Pick(type, "Standard Screw");
            Require(sizeRow.gameObject.activeInHierarchy && generator.GetId() == "SCRW-7/8in", "Returning to Standard Screw lost the preview size.");
            placement.CancelPlacement();

            selection.SetCurrent(new ObjectSelection { gameObject = rivet, individual = true }, true);
            Invoke(ui, "ObserveSelection");
            Require(type.value == 2, "Editing a placed rivet selected the wrong type.");
            CheckFixedShortcuts(RivetId);
            var instanceId = rivet.GetComponent<SavedObject>().ConnectionId;
            Pick(type, "Set Screw");
            var editedSet = CheckEdited(SetScrewId, rivet, instanceId);
            CheckFixedShortcuts(SetScrewId);
            Pick(type, "Standard Screw");
            var editedStandard = CheckEdited("SCRW-1/4in", rivet, instanceId);
            Pick(size, "1.25in");
            CheckEdited("SCRW-1.25in", rivet, instanceId);
            StateSystem.instance.Undo();
            Require(selection.current.gameObject == editedStandard, "Undo did not restore the standard size.");
            StateSystem.instance.Undo();
            Require(selection.current.gameObject == editedSet, "Undo did not restore the set-screw type.");
            StateSystem.instance.Undo();
            Require(selection.current.gameObject == rivet, "Undo did not restore the bearing rivet.");
            CheckGroup(rivet, bearing, plate);
            StateSystem.instance.Redo();
            Require(selection.current.gameObject == editedSet, "Redo did not restore the edited set screw.");
            StateSystem.instance.Redo();
            StateSystem.instance.Redo();
            CheckEdited("SCRW-1.25in", rivet, instanceId);
            StateSystem.instance.Undo();
            StateSystem.instance.Undo();
            StateSystem.instance.Undo();

            CheckClipboard(standard, placement);
            await CheckReopen(new[] { standard, setScrew, rivet });

            void CheckFixedShortcuts(string id) {
                string hiddenSize = generator.param2.value;
                int history = StateSystem.curIndex;
                Require(!sizeRow.gameObject.activeInHierarchy, "A fixed type displayed Size: " + id);
                PressSize(right, Key.RightArrow);
                PressSize(left, Key.LeftArrow);
                Require(generator.GetId() == id && generator.param2.value == hiddenSize && StateSystem.curIndex == history,
                    "A size shortcut changed a fixed type: " + id);
            }

            void PressSize(InputEvent input, Key key) {
                SetKeyboard(key);
                Require(input.IsPressed, "The size arrow binding did not receive input.");
                // Headless Unity has no foreground Game View.
                Invoke(ui, "UpdateHeldPropertyShortcut");
                SetKeyboard();
                Invoke(ui, "UpdateHeldPropertyShortcut");
            }

            void SetKeyboard(params Key[] keys) {
                InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys));
                typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
                left.Update();
                right.Update();
            }

            void SetMarker(HoleCollider hole, Vector3 axis) {
                marker.gameObject.SetActive(true);
                marker.Set(hole.holeData, axis);
            }

            void Aim(Vector3 point, Vector3 axis) {
                var up = rotation * Vector3.up;
                if (Mathf.Abs(Vector3.Dot(axis, up)) > .999f) up = rotation * Vector3.right;
                camera.transform.SetPositionAndRotation(point + axis * 5f, Quaternion.LookRotation(-axis, up));
                InputSystem.Update();
                InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point));
                Physics.SyncTransforms();
            }

            GameObject Mount(HoleCollider hole, Vector3 axis, string id) {
                Aim(hole.transform.position, axis);
                Require(cast.gameObject == hole.gameObject, "Pointer missed the mounting hole for " + id);
                SetMarker(hole, axis);
                Require(!MouseInput.overUI && screwRule.TryGetDisplacement(placement.CurrentPartPlacement, out _), "No mounting preview for " + id);
                Invoke(placement, "Update");
                var preview = (Displacement)typeof(Placement).GetField("currentDisplacement", PrivateInstance).GetValue(placement);
                var before = PartsManager.FindLoadedObjects().ToHashSet();
                Invoke(placement, "Place");
                var body = PartsManager.FindLoadedObjects().Single(part => !before.Contains(part));
                Require(body.GetComponent<SavedObject>().id == id, "Placed the wrong type/size for " + id);
                Near(body.transform.position, hole.transform.position + axis * (hole.holeData.depth * .5f), "Mounting face " + id);
                Near(body.transform.forward, axis, "Mounting axis " + id);
                Near(body.transform.position, preview.translation.Position, "Preview position " + id);
                Require(Quaternion.Angle(body.transform.rotation, preview.rotation.Orientation) < .05f, "Placement changed the preview rotation.");
                marker.gameObject.SetActive(false);
                StateSystem.instance.Undo();
                Require(body.IsDeleted(), "Undo did not remove " + id);
                StateSystem.instance.Redo();
                Require(!body.IsDeleted(), "Redo did not restore " + id);
                return body;
            }

            GameObject CheckEdited(string id, GameObject original, string connection) {
                var body = selection.current.gameObject;
                var saved = body.GetComponent<SavedObject>();
                Require(saved.id == id && saved.ConnectionId == connection && original.IsDeleted(), "Wrong selected replacement for " + id);
                Near(body.transform.position, original.transform.position, "Edited mounting frame " + id);
                Require(Quaternion.Angle(body.transform.rotation, original.transform.rotation) < .05f, "Editing rotated the part.");
                return body;
            }
        }

        private static void CheckCatalog() {
            var catalog = WireJson.Decode<CatalogManifest>(Resources.Load<TextAsset>("Collaboration/catalog").bytes);
            var entry = catalog.entries.Single(candidate => candidate.id == "SCRW");
            Require(entry.first.used && !entry.second.used && entry.first.options.SequenceEqual(Sizes.Concat(new[] { "Set Screw", "Bearing Rivet" })),
                "The collaboration options do not preserve the standard and fixed save IDs.");
            foreach (var value in entry.first.options) {
                var id = "SCRW-" + value;
                DocumentModel.ValidatePart(new Part { id = Guid.NewGuid().ToString("N"), catalogId = id }, catalog);
                var body = Generate(id, Vector3.one * 200, Quaternion.identity);
                Require(body.GetComponent<SavedObject>().id == id &&
                    (body.GetComponent<Screw>() != null) == Sizes.Contains(value), "Failed to generate catalog option " + id);
                body.SetActive(false);
                Object.Destroy(body);
            }
        }

        private static Bounds CheckMesh(GameObject body, float diameter, float length) {
            var mesh = body.GetComponent<MeshFilter>().sharedMesh;
            Require(mesh.isReadable && body.GetComponent<MeshCollider>().sharedMesh == mesh && !body.CompareTag("Screw") &&
                body.GetComponent<FixedFastener>() != null, "Fixed fastener lost native mesh, collider or hardware metadata.");
            Near(mesh.bounds.size.x, diameter, "CAD diameter");
            Near(mesh.bounds.size.y, diameter, "CAD diameter");
            Near(mesh.bounds.size.z, length, "CAD length");
            return mesh.bounds;
        }

        private static void CheckGroup(GameObject body, params GameObject[] expected) {
            var fastener = body.GetComponent<FixedFastener>();
            fastener.holeDetector.RefreshOverlaps();
            Require(fastener.GetAttachedParts().ToHashSet().SetEquals(expected) && GroupManager.TryGetGroup(body, out var group) &&
                group.ToHashSet().SetEquals(expected.Append(body)), "Incorrect fastener group for " + body.name + ": " +
                string.Join(", ", fastener.GetAttachedParts().Select(part => part.name)));
        }

        private static void CheckClipboard(GameObject standard, Placement placement) {
            var data = BuildTransfer.Capture(new ObjectSelection { gameObject = standard, individual = true });
            foreach (var legacyId in new[] { "SCRW-7/8in", "SCRW-Star-0.875" }) {
                data.parts.Single().partId = legacyId;
                var pasted = BuildTransfer.FromClipboard(BuildTransfer.ClipboardPrefix + JsonUtility.ToJson(data));
                using var preview = SelectionPlacementData.FromBuild(pasted, placement.transform, Object.FindFirstObjectByType<ObjectDuplicator>(), null);
                var copies = preview.Generate(Vector3.one * 90, Quaternion.identity);
                var copy = copies.Single();
                Require(copy.GetComponent<SavedObject>().id == "SCRW-7/8in" &&
                    copy.GetComponent<MeshFilter>().sharedMesh == standard.GetComponent<MeshFilter>().sharedMesh,
                    "Legacy clipboard selection lost the standard screw's original size: " + legacyId);
                ObjectDuplicator.DiscardCopies(copies);
            }
        }

        private static async Task CheckReopen(GameObject[] parts) {
            var expected = parts.ToDictionary(body => body.GetComponent<SavedObject>().ConnectionId,
                body => (id: body.GetComponent<SavedObject>().id, pose: new Pose(body.transform.position, body.transform.rotation)));
            var manager = Object.FindFirstObjectByType<BuildsManager>();
            string path = Path.Combine(Path.GetTempPath(), "Protobot screw hardware " + Guid.NewGuid().ToString("N") + ".pbb");
            try {
                manager.buildPath = path;
                var save = (Task<bool>)typeof(BuildsManager).GetMethod("SaveAsync", PrivateInstance).Invoke(manager, new object[] { false });
                Require(await save && File.Exists(path) && manager.CurrentBuildIsSaved, "Main did not save the three screw types.");
                manager.CreateNewBuild();
                Require(await manager.OpenPathAsync(path) && manager.CurrentBuildIsSaved, "Main did not reopen the saved screw types.");
                var reopened = PartsManager.FindLoadedObjects().Select(body => body.GetComponent<SavedObject>()).ToArray();
                foreach (var saved in reopened.Where(saved => expected.ContainsKey(saved.ConnectionId))) {
                    var prior = expected[saved.ConnectionId];
                    Require(prior.id == saved.id && Quaternion.Angle(saved.transform.rotation, prior.pose.rotation) < .05f,
                        "Reopen changed the screw ID or rotation.");
                    Near(saved.transform.position, prior.pose.position, "Reopened mounting position");
                    expected.Remove(saved.ConnectionId);
                    if (saved.id == SetScrewId) CheckGroup(saved.gameObject, reopened.Single(body => body.nameId == "CLMP").gameObject);
                    if (saved.id == RivetId) CheckGroup(saved.gameObject, reopened.Single(body => body.nameId == "BRNG").gameObject,
                        reopened.Single(body => body.nameId == "PLTE").gameObject);
                }
                Require(expected.Count == 0, "Reopen lost a screw type.");
            }
            finally {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static GameObject Generate(string id, Vector3 p, Quaternion r) => PartsManager.GeneratePart(id, p, r);
        private static T Reference<T>(Object target, string field) where T : Object =>
            (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private static void Invoke(object target, string method) => target.GetType().GetMethod(method, PrivateInstance).Invoke(target, null);
        private static void Pick(Dropdown dropdown, string option) => dropdown.value = dropdown.options.FindIndex(value => value.text == option);
        private static async Task NextFrame() {
            int frame = Time.frameCount;
            while (Time.frameCount == frame) await Task.Yield();
        }
        private static void Near(Vector3 actual, Vector3 expected, string label) =>
            Require(Vector3.Distance(actual, expected) < .002f, $"{label}: {actual:F6}, expected {expected:F6}.");
        private static void Near(float actual, float expected, string label) =>
            Require(Mathf.Abs(actual - expected) < .002f, $"{label}: {actual:F6}, expected {expected:F6}.");
        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
