using System;
using System.Collections.Generic;
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
using Protobot.Transformations;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class SelectionPlacementValidation {
        private const string SmokeKey = "Protobot.SelectionPlacementSmoke";
        private static int frames;

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0; EditorApplication.update += Tick;
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
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Selection placement validation passed: frozen multi-part P copies, grid-only body/hardware placement, selected/opposite hole and screw anchoring, Ctrl-hole multi-selection, rigid rotation, repeated clicks, cancellation/disable cleanup, one-step undo/redo, custom geometry/colors, and embedded save/load.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static async Task Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            GameObject.Find("Add Parts Menu")?.SetActive(false);
            // Geometry checks use a batch GameView; remove UI ray targets from its tiny viewport.
            foreach (var raycaster in Object.FindObjectsByType<BaseRaycaster>(FindObjectsSortMode.None)) raycaster.enabled = false;
            var placement = Object.FindFirstObjectByType<Placement>();
            var ui = placement.GetComponent<AddPropertiesUI>();
            var manager = Get<SelectionManager>(ui, "selectionManager");
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
            var cast = Object.FindFirstObjectByType<MouseCast>();
            var camera = cast.GetComponent<Camera>();
            var face = new SerializedObject(manager.GetComponent<HoleFaceResponseSelector>()).FindProperty("selectHoleFace").objectReferenceValue as HoleFace;
            var sources = new[] {
                PartsManager.GeneratePart("CCHL-1x2-5", new Vector3(300, 300, 300), Quaternion.Euler(13, 27, 41)),
                PartsManager.GeneratePart("PLTE-3-3", new Vector3(303, 302, 301), Quaternion.Euler(27, 11, 59))
            };
            sources[0].GetComponent<Renderer>().material.color = Color.red;
            sources[1].GetComponent<Renderer>().material.color = Color.blue;
            var multi = new MultiSelection(null, sources.ToList()) { individual = true };
            manager.SetCurrent(multi, true);
            var sourcePoses = sources.Select(s => new Pose(s.transform.position, s.transform.rotation)).ToArray();
            var parents = sources.Select(s => s.transform.parent).ToArray();
            var pivotPose = new Pose(multi.gameObject.transform.position, multi.gameObject.transform.rotation);
            var existing = PartsManager.FindLoadedObjects();
            Invoke(ui, "PlacePickedPart");
            var data = placement.CurrentSelectionPlacement;
            Require(data?.PartCount == 2 && !data.HasHole, "P did not capture the selected two-part set.");
            foreach (var toggle in Object.FindObjectsByType<ToolToggle>(FindObjectsSortMode.None))
                if (new SerializedObject(toggle).FindProperty("hideDuringKeyboardTransform").boolValue)
                    Require(!toggle.active, "P left transform/move handles active on the original selection.");
            Require(PartsManager.FindLoadedObjects().Count == existing.Count, "The copy preview entered the build before a click.");
            AssertSources(sources, sourcePoses, parents);
            var firstPose = new Pose(new Vector3(310, 314, 308), Quaternion.Euler(31, 42, 53));
            Invoke(placement, "ApplyPose", firstPose);
            int state = StateSystem.curIndex;
            Invoke(placement, "Place");
            var first = PartsManager.FindLoadedObjects().Except(existing).ToArray();
            Require(first.Length == 2 && placement.placing, "A click did not stamp the complete set and retain repeated placement.");
            CheckCopies(sources, sourcePoses, pivotPose, first, firstPose);
            Require(StateSystem.curIndex == state + 1, "A multi-part stamp created more than one undo step.");
            existing.AddRange(first);
            var nextPose = new Pose(new Vector3(320, 316, 312), Quaternion.Euler(15, 65, 19));
            Invoke(placement, "ApplyPose", nextPose); Invoke(placement, "Place");
            var second = PartsManager.FindLoadedObjects().Except(existing).ToArray();
            CheckCopies(sources, sourcePoses, pivotPose, second, nextPose);
            Require(StateSystem.curIndex == state + 2, "Repeated stamping did not create one undo step per set.");
            StateSystem.instance.Undo();
            Require(second.All(copy => !copy.activeSelf) && first.All(copy => copy.activeSelf), "Undo did not remove only the last stamped set.");
            StateSystem.instance.Redo();
            Require(second.All(copy => copy.activeSelf), "Redo did not restore every part in the set.");
            placement.StopPlacing();
            Require(!placement.placing && placement.CurrentSelectionPlacement == null, "Cancel did not release the copy preview.");
            try { data.Generate(Vector3.zero, Quaternion.identity); throw new Exception("Canceled templates remained usable."); }
            catch (ObjectDisposedException) { }
            AssertSources(sources, sourcePoses, parents);
            Require(PartsManager.FindLoadedObjects().Select(p => p.GetComponent<SavedObject>().ConnectionId).Distinct().Count() == PartsManager.FindLoadedObjects().Count,
                "Stamped parts reused source connection identities.");

            await NextFrame();
            var targetBody = PartsManager.GeneratePart("PLTE-3-3", new Vector3(400.17f, 400.23f, 400.31f), Quaternion.Euler(22, 37, 11));
            var target = targetBody.GetComponentsInChildren<HoleCollider>().First();
            foreach (var body in new[] { sources[0], PartsManager.GeneratePart("SCRW-1.00in", new Vector3(305, 305, 305), Quaternion.Euler(13, 29, 51)),
                PartsManager.GeneratePart("GEAR-Normal-36T", new Vector3(306, 305, 305), Quaternion.Euler(17, 43, 23)) }) {
                Require(body != null, "Missing grid-only test part.");
                manager.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true);
                Invoke(ui, "PlacePickedPart");
                Require(!placement.CurrentSelectionPlacement.HasHole, "A body pick invented a source hole.");
                Aim(camera, target.transform.position, target.transform.forward);
                var hover = Get<HoleFace>(placement.GetComponent<HolePlaceDisplacement>(), "hoverHoleFace");
                hover.Set(target.holeData, target.transform.forward); hover.gameObject.SetActive(true);
                Require(!MouseInput.overUI && !placement.GetComponent<KeyboardTransformTool>().IsCameraNavigationHeld,
                    $"Grid test pointer was captured: UI={MouseInput.overUI}, camera={placement.GetComponent<KeyboardTransformTool>().IsCameraNavigationHeld}.");
                Invoke(placement, "UpdatePosition");
                var plane = placement.GetComponent<PlanePlaceDisplacement>();
                plane.TryGetDisplacement(placement.CurrentSelectionPlacement, out var grid);
                Near(Current(placement).position, TransformSnapping.SnapPosition(grid.translation.Position, Vector3.zero), "Body P snapped to an object instead of the grid.");
                Near(Current(placement).rotation, body.transform.rotation, "Body P inferred a mounting orientation.");
                var screwTarget = PartsManager.GeneratePart("SCRW-1.00in", new Vector3(410, 410, 410), Quaternion.identity);
                Aim(camera, screwTarget.transform.position, screwTarget.transform.forward);
                Invoke(placement, "UpdatePosition");
                plane.TryGetDisplacement(placement.CurrentSelectionPlacement, out grid);
                Near(Current(placement).position, TransformSnapping.SnapPosition(grid.translation.Position, Vector3.zero), "Body P snapped to a screw.");
                placement.StopPlacing(); await NextFrame();
            }

            var customData = CustomModelImporter.Parse(Encoding.UTF8.GetBytes("v -1 -1 0\nv 1 -1 0\nv 0 1 0\nf 1 2 3"), ".obj", "P custom model", 1);
            var custom = CustomModel.Create(customData, new Vector3(500, 500, 500), Quaternion.Euler(17, 29, 8));
            custom.GetComponent<Renderer>().material.color = Color.green;
            var point = custom.GetComponent<CustomModel>().AddMount(custom.transform.position, custom.transform.forward, .169f, .1f, true);
            var sourceHole = point.GetComponent<HoleCollider>();
            var sourceScrew = PartsManager.GeneratePart("SCRW-1.00in", point.Surface, Quaternion.LookRotation(custom.transform.forward));
            var screw = sourceScrew.GetComponent<Screw>();
            Physics.SyncTransforms(); screw.holeDetector.RefreshOverlaps(); screw.FormConnection();
            Require(custom.TryGetGroup(out List<GameObject> group) && group.Contains(sourceScrew), "Source assembly was not connected.");
            var selectedHole = new HoleSelection { gameObject = face.gameObject, holeFace = face, holeData = sourceHole.holeData,
                faceDirection = custom.transform.forward };
            manager.SetCurrent(selectedHole, true);
            Invoke(ui, "PlacePickedPart");
            Require(placement.CurrentSelectionPlacement?.PartCount == 2 && placement.CurrentSelectionPlacement.HasHole, "Hole P did not capture its connected assembly.");
            var initialSourceFace = MoveTargeting.HoleFace(sourceHole.holeData, custom.transform.forward);
            Aim(camera, target.transform.position, target.transform.forward);
            Invoke(placement, "UpdatePosition");
            var expectedFace = MoveTargeting.HoleFace(target.holeData, cast.hit.normal);
            Near(Current(placement).position, expectedFace.position, "The chosen source face did not reach the target face.");
            existing = PartsManager.FindLoadedObjects();
            Invoke(placement, "Place");
            var assemblyCopy = PartsManager.FindLoadedObjects().Except(existing).ToArray();
            Require(assemblyCopy.Length == 2, "Hole P omitted part of the connected assembly.");
            var clonedModel = assemblyCopy.Single(copy => copy.GetComponent<CustomModel>() != null);
            var clonedPoint = clonedModel.GetComponentInChildren<CustomMountPoint>();
            Near(clonedPoint.Surface, expectedFace.position, "The duplicate of the selected hole was not the mating anchor.");
            Require(Vector3.Dot(clonedPoint.transform.forward, expectedFace.rotation * Vector3.forward) < -.999f, "The duplicate hole normal does not oppose its target.");
            Require(clonedModel.GetComponent<Renderer>().material.color == Color.green, "P lost the custom-model color.");
            var clonedScrew = assemblyCopy.Single(copy => copy.GetComponent<Screw>() != null);
            var expectedScrew = MoveTargeting.Align(initialSourceFace, expectedFace, new Pose(sourceScrew.transform.position, sourceScrew.transform.rotation));
            Near(clonedScrew.transform.position, expectedScrew.position, "Screw spacing changed inside the cloned assembly.");
            var twist = MoveRotation.Twist(Current(placement), expectedFace, 37.5f);
            Invoke(placement, "ApplyPose", twist);
            Near(Current(placement).position, expectedFace.position, "Attachment rotation moved the selected hole anchor.");

            // Space selects the far face of the target without changing the frozen source face.
            target = PartsManager.GeneratePart("PLTE-3-3", new Vector3(430.17f, 430.23f, 430.31f), Quaternion.Euler(22, 37, 11))
                .GetComponentsInChildren<HoleCollider>().First();
            Aim(camera, target.transform.position, target.transform.forward);
            InputState.Change(Keyboard.current, new KeyboardState(Key.Space));
            Require(Get<InputEvent>(placement.GetComponent<HolePlaceDisplacement>(), "oppositeHoleInput").IsCurrentlyPressed,
                "The test did not press the opposite-hole binding.");
            Invoke(placement, "UpdatePosition");
            var oppositeTarget = MoveTargeting.HoleFace(target.holeData, -cast.hit.normal);
            Near(Current(placement).position, oppositeTarget.position, "Space did not choose the opposite target face.");
            InputState.Change(Keyboard.current, new KeyboardState());
            var looseScrew = PartsManager.GeneratePart("SCRW-1.00in", new Vector3(440, 440, 440), Quaternion.Euler(12, 35, 19)).GetComponent<Screw>();
            Aim(camera, looseScrew.transform.position, looseScrew.transform.forward);
            Invoke(placement, "UpdatePosition");
            Near(Current(placement).position, looseScrew.GetLowestPoint(), "Selected-hole P did not snap to a screw endpoint.");
            placement.StopPlacing(); await NextFrame();

            // A Ctrl-hole click adds its body to the existing multi-selection and retains that exact face.
            var multiSelector = manager.GetComponent<MultiSelector>();
            Get<InputEvent>(multiSelector, "mutliSelectInput").IsPressed = false;
            manager.SetCurrent(new ObjectSelection { gameObject = sources[0], individual = true }, true);
            Get<InputEvent>(multiSelector, "mutliSelectInput").IsPressed = true;
            var anchorHole = new HoleSelection { gameObject = face.gameObject, holeFace = face, holeData = sourceHole.holeData,
                faceDirection = -custom.transform.forward, individual = true };
            face.Set(sourceHole.holeData, -custom.transform.forward);
            manager.SetCurrent(anchorHole, true);
            var anchoredMulti = manager.current as MultiSelection;
            Require(anchoredMulti?.placementHole != null && anchoredMulti.placementHole.Hole == sourceHole.holeData, "Ctrl-hole did not retain the source anchor in a multi-selection.");
            Require(anchoredMulti.objs.All(obj => obj.GetComponent<Pivot>() == null), "Multi-selection nested an old selection pivot.");
            Get<InputEvent>(multiSelector, "mutliSelectInput").IsPressed = false;
            Invoke(ui, "PlacePickedPart");
            Require(placement.CurrentSelectionPlacement.HasHole && placement.CurrentSelectionPlacement.PartCount == 2, "P lost the multi-selection hole anchor or individual scope.");
            using (var frozen = SelectionPlacementData.Capture(manager.current, placement.transform, duplicator)) {
                // Removing the source after capture must not destroy a custom template's material or geometry.
                manager.ClearCurrent(false, true, true);
                custom.GetComponent<Renderer>().material.color = Color.magenta;
                Object.DestroyImmediate(custom);
                var copies = frozen.Generate(new Vector3(550, 550, 550), Quaternion.identity);
                var preserved = copies.Single(copy => copy.GetComponent<CustomModel>() != null);
                Require(preserved.GetComponent<Renderer>().material.color == Color.green && preserved.GetComponentInChildren<CustomMountPoint>() != null,
                    "Frozen custom geometry/materials depended on the original instance.");
                var chosenFace = MoveTargeting.HoleFace(preserved.GetComponentInChildren<HoleCollider>().holeData, -preserved.GetComponentInChildren<CustomMountPoint>().transform.forward);
                Near(chosenFace.position, new Vector3(550, 550, 550), "P used the other source face instead of the selected opposite face.");
            }
            placement.StopPlacing(); await NextFrame();

            // Cancellation before a first click and component disable must never emit saved parts.
            manager.SetCurrent(new ObjectSelection { gameObject = sources[0], individual = true }, true);
            int count = PartsManager.FindLoadedObjects().Count;
            Invoke(ui, "PlacePickedPart"); placement.StopPlacing();
            Require(PartsManager.FindLoadedObjects().Count == count, "Cancel before placement created parts.");
            Invoke(ui, "PlacePickedPart");
            var disabledData = placement.CurrentSelectionPlacement;
            placement.enabled = false;
            Require(!placement.placing && PartsManager.FindLoadedObjects().Count == count, "Disabling placement leaked live copies.");
            try { disabledData.Generate(Vector3.zero, Quaternion.identity); throw new Exception("Disabled templates remained usable."); }
            catch (ObjectDisposedException) { }
            placement.enabled = true;
            var saved = SceneBuild.ToBuildData();
            using var stream = new MemoryStream(); new BinaryFormatter().Serialize(stream, saved); stream.Position = 0;
            var restored = (BuildData)new BinaryFormatter().Deserialize(stream);
            SceneBuild.GenerateBuild(restored);
            Require(SceneBuild.ToBuildData().CompareData(restored), "Stamped copies lost their geometry, holes or transforms on save/load.");
            Require(Object.FindObjectsByType<CustomModel>(FindObjectsSortMode.None).Length >= 2, "Stamped custom models did not reload.");
        }

        private static void CheckCopies(GameObject[] sources, Pose[] sourcePoses, Pose frame, GameObject[] copies, Pose target) {
            Require(copies.Length == sources.Length, "Wrong number of stamped parts.");
            for (int i = 0; i < sources.Length; i++) {
                var copy = copies.Single(obj => obj.GetComponent<SavedObject>().id == sources[i].GetComponent<SavedObject>().id);
                Near(copy.transform.position, target.position + target.rotation * Quaternion.Inverse(frame.rotation) * (sourcePoses[i].position - frame.position), "P changed relative positions.");
                Near(copy.transform.rotation, target.rotation * Quaternion.Inverse(frame.rotation) * sourcePoses[i].rotation, "P changed relative orientations.");
                Require(copy.GetComponent<Renderer>().material.color == sources[i].GetComponent<Renderer>().material.color, "P lost a part's color.");
                Require(copy.GetComponent<Renderer>().sharedMaterial != sources[i].GetComponent<Renderer>().sharedMaterial, "P shares mutable materials with its source.");
            }
        }

        private static void AssertSources(GameObject[] sources, Pose[] poses, Transform[] parents) {
            for (int i = 0; i < sources.Length; i++) {
                Near(sources[i].transform.position, poses[i].position, "P moved an original part.");
                Near(sources[i].transform.rotation, poses[i].rotation, "P rotated an original part.");
                Require(sources[i].transform.parent == parents[i], "P reparented an original part.");
            }
        }
        private static void Aim(Camera camera, Vector3 position, Vector3 outward) {
            camera.transform.SetPositionAndRotation(position + outward * 5, Quaternion.LookRotation(-outward));
            EventSystem.current.SetSelectedGameObject(null);
            InputState.Change(Mouse.current.position, (Vector2)camera.WorldToScreenPoint(position));
            EventSystem.current.currentInputModule.Process();
            Physics.SyncTransforms();
        }
        private static Pose Current(Placement placement) {
            var current = Get<Displacement>(placement, "currentDisplacement");
            return new Pose(current.translation.Position, current.rotation.Orientation);
        }
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .001f, message + $" ({a} != {b})");
        private static void Near(Quaternion a, Quaternion b, string message) => Require(Quaternion.Angle(a, b) < .1f, message);
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
