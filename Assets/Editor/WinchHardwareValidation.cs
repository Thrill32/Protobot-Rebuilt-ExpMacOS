using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.Simulation;
using Protobot.Transformations;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class WinchHardwareValidation {
        private const string SmokeKey = "Protobot.WinchHardwareSmoke";
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static int frames;
        private static readonly (string id, string name, Vector3 size, int holes)[] Parts = {
            ("WBRK", "Winch Bracket", new Vector3(2.5f, 2, 2.51f), 105),
            ("WDRM", "Winch Drum", new Vector3(1.39f, 1.39f, 1.555f), 10),
            ("WRAT", "Ratchet Gear", new Vector3(1.716644f, 1.71455f, .32f), 5),
            ("WPAW", "Winch Pawl", new Vector3(.66147f, .623383f, .39f), 1),
            ("PULY", "VEX Pulley", new Vector3(1.125f, 1.125f, .375f), 1)
        };

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
                Debug.Log("Winch hardware passed: five Motion rows, CAD dimensions and open bores, rotated bracket and screw mounting, normal shaft snapping, repeated pulley placement, rope path measurements, and Main.pbb save/reopen.");
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
            var menu = Object.FindFirstObjectByType<AddPartsUI>(FindObjectsInactive.Include);
            menu.DisplayListGroup("Motion");
            var rows = Parts.ToDictionary(part => part.id, part => menu.partUIsContainer.GetComponentsInChildren<PartDisplayUI>(true)
                .Single(row => row.gameObject.activeSelf && row.partType?.id == part.id && row.nameText.text == part.name && row.mainIcon.sprite != null));
            var catalog = WireJson.Decode<CatalogManifest>(Resources.Load<TextAsset>("Collaboration/catalog").bytes);
            foreach (var part in Parts) {
                var entry = catalog.entries.Single(entry => entry.id == part.id);
                Require(!entry.first.used && !entry.second.used, part.id + " gained unexpected catalog parameters.");
            }
            menu.gameObject.SetActive(false);

            var placement = Object.FindFirstObjectByType<Placement>();
            var cast = new SerializedObject(placement.GetComponent<PartHolePlaceDisplacement>()).FindProperty("mouseCast").objectReferenceValue as MouseCast;
            var marker = new SerializedObject(placement.GetComponent<ScrewPlaceDisplacement>()).FindProperty("currentHoleFace").objectReferenceValue as HoleFace;
            var camera = cast.GetComponent<Camera>();
            var update = typeof(Placement).GetMethod("Update", PrivateInstance);
            var place = typeof(Placement).GetMethod("Place", PrivateInstance);
            var resolvedPose = typeof(Placement).GetField("currentDisplacement", PrivateInstance);
            var rotation = Quaternion.Euler(21, 32, 47);

            var plate = PartsManager.GeneratePart("PLTE-3-3", new Vector3(30, 40, 50), rotation);
            var plateHole = plate.GetComponentsInChildren<HoleCollider>().First();
            var surface = MoveTargeting.HoleFace(plateHole.holeData, plateHole.transform.forward);
            Select("WBRK");
            Require(Aim(plateHole.transform.position, plateHole.transform.forward, plateHole.transform.up) == plateHole.gameObject,
                "Pointer missed the plate mounting hole.");
            var bracket = Commit("WBRK");
            var mount = bracket.GetComponentsInChildren<HoleCollider>().Single(hole => hole.name == "Base mounting hole 3-3");
            Near(mount.transform.position - mount.transform.forward * mount.holeData.depth * .5f, surface.position, "Bracket mounting face");
            Near(mount.transform.forward, surface.rotation * Vector3.forward, "Bracket mounting axis");
            Near(mount.holeData.size.x, .182f, "Bracket square mounting width");
            Near(mount.holeData.depth, .056f, "Bracket steel thickness");

            var screwGenerator = PartsManager.GetPartType("SCRW").GetComponent<PartGenerator>();
            PartParameterEditing.Load(screwGenerator, "SCRW-1.00in");
            placement.StartPlacing(new PartPlacementData(screwGenerator, screwGenerator.GetComponent<PartType>(), placement.transform));
            marker.gameObject.SetActive(true);
            marker.Set(mount.holeData, mount.transform.forward);
            Aim(mount.transform.position, mount.transform.forward, mount.transform.up);
            var screw = Commit("SCRW-1.00in").GetComponent<Screw>();
            marker.direction = Vector3.zero;
            marker.gameObject.SetActive(false);
            Near(screw.transform.position, mount.transform.position + mount.transform.forward * .028f, "Screw seating face");
            var nut = PartsManager.GeneratePart("NUT-Hex", screw.GetLowestPoint(), screw.transform.rotation);
            screw.holeDetector.RefreshOverlaps();
            Require(screw.holeDetector.holes.Contains(mount) && screw.holeDetector.holes.Contains(plateHole) &&
                screw.GetAttachedParts()?.Contains(nut) == true, "Screw and nut did not attach both mounted parts.");

            // Mount the drum on an axle through matching holes on the bracket's two walls.
            var front = bracket.GetComponentsInChildren<HoleCollider>().Single(hole => hole.name == "Front mounting hole 3-3");
            var rear = bracket.GetComponentsInChildren<HoleCollider>().Single(hole => hole.name == "Rear mounting hole 3-3");
            var drumCenter = (front.transform.position + rear.transform.position) * .5f;
            var winchShaft = PartsManager.GeneratePart("SHFT-Normal-4", drumCenter, front.transform.rotation);
            Select("WDRM");
            var drum = MountOnShaft("WDRM", winchShaft, 0);
            var drumShaft = winchShaft.GetComponent<Shaft>();
            drumShaft.holeDetector.RefreshOverlaps();
            Require(drumShaft.holeDetector.holes.Contains(front) && drumShaft.holeDetector.holes.Contains(rear) &&
                drumShaft.holeDetector.GetObjects().Contains(drum), "The drum and bracket holes did not detect their shared shaft.");

            var shaft = PartsManager.GeneratePart("SHFT-Normal-6", new Vector3(50, 40, 50), rotation);
            Select("WRAT");
            var ratchet = MountOnShaft("WRAT", shaft, -2);
            Select("WPAW");
            var pawl = MountOnShaft("WPAW", shaft, 2);
            Select("PULY");
            var pulley = MountOnShaft("PULY", shaft, -1);
            var pulleyCopy = MountOnShaft("PULY", shaft, 1);
            Require(pulley != pulleyCopy && pulley.GetComponent<SavedObject>().ConnectionId != pulleyCopy.GetComponent<SavedObject>().ConnectionId,
                "One pulley row did not stamp two independent bodies.");
            placement.CancelPlacement();

            var shaftMesh = shaft.GetComponent<MeshFilter>().sharedMesh;
            Near(shaftMesh.bounds.size.x, .125f, "Nominal shaft width");
            foreach (var vertex in shaftMesh.vertices) {
                // Rounded square of the three CAD bores, in their keyed frame.
                var corner = new Vector2(Mathf.Max(0, Mathf.Abs(vertex.x) - .0537f), Mathf.Max(0, Mathf.Abs(vertex.y) - .0537f));
                Require(corner.magnitude < .0098f, "The normal shaft profile does not clear the keyed CAD bores.");
            }
            float shaftRadius = shaftMesh.vertices.Max(vertex => new Vector2(vertex.x, vertex.y).magnitude);
            Near(shaftRadius, .084236f, "Corner radius of the repository's normal shaft");
            Debug.Log($"Pulley CAD corner clearance against the stock shaft mesh: {.08f - shaftRadius:F6} inches.");

            var bodies = new[] { bracket, drum, ratchet, pawl, pulley, pulleyCopy };
            Physics.SyncTransforms();
            foreach (var body in bodies) CheckGeometry(body);
            var poses = bodies.ToDictionary(body => body.GetComponent<SavedObject>().ConnectionId,
                body => (id: body.GetComponent<SavedObject>().id, pose: new Pose(body.transform.position, body.transform.rotation)));

            var manager = Object.FindFirstObjectByType<BuildsManager>();
            string path = Path.Combine(Path.GetTempPath(), "Protobot winch " + Guid.NewGuid().ToString("N") + ".pbb");
            try {
                manager.buildPath = path;
                var save = (Task<bool>)typeof(BuildsManager).GetMethod("SaveAsync", PrivateInstance).Invoke(manager, new object[] { false });
                Require(await save && File.Exists(path) && manager.CurrentBuildIsSaved, "Main did not save the winch hardware.");
                manager.CreateNewBuild();
                Require(!PartsManager.FindLoadedObjects().Any(), "New Build left the original winch parts active.");
                Require(await manager.OpenPathAsync(path) && manager.buildPath == path && manager.CurrentBuildIsSaved, "Main did not reopen the saved winch build.");
                var reopened = PartsManager.FindLoadedObjects().Select(body => body.GetComponent<SavedObject>())
                    .Where(saved => poses.ContainsKey(saved.ConnectionId)).ToArray();
                Require(reopened.Length == bodies.Length && reopened.Count(saved => saved.id == "PULY") == 2, "Save/reopen lost catalog instances.");
                Physics.SyncTransforms();
                foreach (var saved in reopened) {
                    var expected = poses[saved.ConnectionId];
                    Require(expected.id == saved.id, "Save/reopen changed a catalog ID.");
                    Near(saved.transform.position, expected.pose.position, "Reopened position " + saved.id);
                    Require(Quaternion.Angle(saved.transform.rotation, expected.pose.rotation) < .05f, "Reopened rotation changed: " + saved.id);
                    CheckGeometry(saved.gameObject);
                }
            }
            finally {
                if (File.Exists(path)) File.Delete(path);
            }

            void Select(string id) {
                rows[id].ToggleCurrent(true);
                Require(placement.CurrentPartPlacement?.objectId == id, "Catalog row did not start placement: " + id);
            }

            GameObject Aim(Vector3 point, Vector3 normal, Vector3 up) {
                camera.transform.SetPositionAndRotation(point + normal * 5f, Quaternion.LookRotation(-normal, up));
                InputSystem.Update();
                InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(point));
                Physics.SyncTransforms();
                return cast.gameObject;
            }

            GameObject Commit(string id) {
                Require(!MouseInput.overUI, "The placement pointer is over UI: " + id);
                update.Invoke(placement, null);
                var preview = (Displacement)resolvedPose.GetValue(placement);
                var before = PartsManager.FindLoadedObjects().ToHashSet();
                place.Invoke(placement, null);
                var placed = PartsManager.FindLoadedObjects().Single(body => !before.Contains(body));
                Require(placed.GetComponent<SavedObject>().id == id, "Placement generated the wrong part: " + id);
                Near(placed.transform.position, preview.translation.Position, "Preview position " + id);
                Require(Quaternion.Angle(placed.transform.rotation, preview.rotation.Orientation) < .05f, "Placement changed preview rotation: " + id);
                return placed;
            }

            GameObject MountOnShaft(string id, GameObject target, float axial) {
                var point = target.transform.position + target.transform.forward * axial;
                Require(Aim(point, target.transform.right, target.transform.up) == target, "Pointer missed the shaft for " + id);
                var body = Commit(id);
                var bore = body.GetComponentsInChildren<HoleCollider>().Single(hole => hole.transform.localPosition == Vector3.zero);
                Near(bore.transform.position, point, "Shaft mounting center " + id);
                Near(bore.transform.forward, target.transform.forward, "Shaft mounting axis " + id);
                return body;
            }
        }

        private static void CheckGeometry(GameObject body) {
            var part = Parts.Single(part => part.id == body.GetComponent<SavedObject>().id);
            var mesh = body.GetComponent<MeshFilter>().sharedMesh;
            var collider = body.GetComponent<MeshCollider>();
            Require(mesh.isReadable && collider.sharedMesh == mesh && !collider.convex &&
                body.GetComponent<MeshRenderer>().sharedMaterial != null, "Missing native render or collision geometry for " + part.id);
            Near(mesh.bounds.size, part.size, "CAD bounds " + part.id);
            var holes = body.GetComponentsInChildren<HoleCollider>();
            Require(holes.Length == part.holes, "Missing authored holes for " + part.id);
            foreach (var hole in holes) {
                Require(hole.holeData.part == body && hole.gameObject.layer == HoleCollider.HOLE_COLLISIONS_LAYER &&
                    hole.CompareTag("HoleCollider"), "Incorrect hole ownership or collider layer for " + part.id);
                var start = hole.transform.position - hole.transform.forward * (hole.holeData.depth * .5f + .004f);
                Require(!collider.Raycast(new Ray(start, hole.transform.forward), out _, hole.holeData.depth + .008f),
                    $"Solid geometry blocks {part.id} / {hole.name}.");
            }
            if (part.id != "WBRK") {
                var bore = holes.Single(hole => hole.transform.localPosition == Vector3.zero);
                var radius = part.id == "PULY" ? .08f : .0635f;
                Near(bore.holeData.size.x * .5f, radius, "Axle bore radius or square half-width", .0001f);
                Require(collider.Raycast(new Ray(bore.transform.position, bore.transform.right), out var rim, .1f),
                    "Axle bore wall is missing: " + part.id);
                Near(rim.distance, radius, "Mesh axle bore radius or square half-width", .0006f);
                if (part.id != "PULY") {
                    var pivot = body.GetComponent<SimulationPart>();
                    Require(pivot.kind == DriveKind.Attachment, "Missing keyed attachment: " + part.id);
                    Near(pivot.Center, bore.transform.position, "Keyed pivot center");
                    Near(pivot.Axis, bore.transform.forward, "Keyed pivot axis");
                }
            }
            if (part.id == "WDRM" || part.id == "PULY") {
                var groove = body.GetComponent<RopeGroove>();
                var localZ = part.id == "WDRM" ? 0f : -.025f;
                var radius = part.id == "WDRM" ? .3585216f : .3f;
                Near(body.transform.InverseTransformPoint(groove.Center), Vector3.forward * localZ, "Local rope groove center");
                Near(groove.Axis, body.transform.forward, "Rope groove axis");
                Near(groove.SurfaceRadius, radius, "Rope groove surface radius", .0001f);
                Near(groove.EffectiveRadius(), radius + .0625f, "Rope centerline radius", .0001f);
                var radial = body.transform.right;
                Require(collider.Raycast(new Ray(groove.Center + radial, -radial), out var hit, 1f), "No CAD surface at the rope groove.");
                Near(Vector3.Distance(hit.point, groove.Center), groove.SurfaceRadius, "Mesh radius at the rope groove");
            }
        }

        private static void Near(Vector3 actual, Vector3 expected, string label) =>
            Require(Vector3.Distance(actual, expected) < .002f, $"{label}: {actual:F6}, expected {expected:F6}.");
        private static void Near(float actual, float expected, string label, float tolerance = .002f) =>
            Require(Mathf.Abs(actual - expected) < tolerance, $"{label}: {actual:F6}, expected {expected:F6}.");
        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
