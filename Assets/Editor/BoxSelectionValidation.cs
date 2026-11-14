using System;
using System.Collections.Generic;
using System.Reflection;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class BoxSelectionValidation {
        private const string SmokeKey = "Protobot.BoxSelectionSmoke";
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

        private static void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(SmokeKey, false);
            try {
                Check();
                Debug.Log("Box selection validation passed: release-time default/rebound grouping bypass, additive selection, overlapping groups, repeated/empty boxes, hole scope, cancellation, UI boundaries and bounded tiny/large sliced rendering.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static void Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            foreach (var raycaster in Object.FindObjectsByType<BaseRaycaster>(FindObjectsSortMode.None)) raycaster.enabled = false;
            var manager = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            var box = manager.GetComponent<BoxSelector>();
            var boxUI = Get<BoxSelectUI>(box, "boxSelectUI");
            CheckSelectionBoxRendering(boxUI);
            var multiple = Get<InputEvent>(manager.GetComponent<MultiSelector>(), "mutliSelectInput");
            var grouping = (InputEvent)typeof(GroupingBypassInput).GetField("input", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var oldAction = multiple.defaultAction;
            var oldRebind = multiple.rebindAction;
            var oldGroupingAction = grouping.defaultAction;
            var oldGroupingRebind = grouping.rebindAction;
            var oldCamera = Get<Camera>(box, "cam");
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var camera = new GameObject("Box validation camera", typeof(Camera)).GetComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 10;
            camera.pixelRect = new Rect(0, 0, 1000, 1000);
            camera.transform.position = new Vector3(200, 200, 190);
            var parts = new[] {
                PartsManager.GeneratePart("PLTE-3-3", new Vector3(194, 200, 200), Quaternion.identity),
                PartsManager.GeneratePart("PLTE-3-3", new Vector3(200, 200, 200), Quaternion.identity),
                PartsManager.GeneratePart("PLTE-3-3", new Vector3(206, 200, 200), Quaternion.identity)
            };
            try {
                typeof(BoxSelector).GetField("cam", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(box, camera);
                oldAction.Disable(); oldRebind.action.Disable();
                multiple.defaultAction = new InputAction("Box validation multiple", InputActionType.Button, keyboard.leftCtrlKey.path);
                multiple.defaultAction.Enable();
                multiple.rebindAction = new RebindAction("Box validation " + Guid.NewGuid(), true);
                grouping.rebindAction.action.Disable();
                grouping.rebindAction = new RebindAction("Box validation grouping " + Guid.NewGuid(), true);

                Press(); Seed(); Drag(1); Expect(parts[1]);
                Press(Key.LeftCtrl); Seed(); Drag(1); Expect(parts[0], parts[1]);
                Drag(2, true); Expect(parts);
                Drag(1); Expect(parts);
                var retained = manager.current;
                DragEmpty(); Require(manager.current == retained, "An empty additive box replaced the selection.");
                boxUI.OnReleaseBox.Invoke(new Vector2(100, 100), new Vector2(102, 102));
                Require(manager.current == retained, "A short box gesture changed selection.");
                Press(); DragEmpty(); Require(manager.current == null, "An empty replacement box did not clear selection.");
                Press(Key.LeftCtrl); DragEmpty(); Require(manager.current == null, "An empty additive box created a selection.");
                Drag(1); Expect(parts[1]);

                // A hole selected individually must remain individually scoped
                // when a box is added, even after its shared face is hidden.
                var collider = parts[0].GetComponentInChildren<HoleCollider>();
                var face = Get<HoleFace>(manager.GetComponent<HoleFaceResponseSelector>(), "selectHoleFace");
                manager.SetCurrent(new HoleSelection {
                    gameObject = face.gameObject, holeData = collider.holeData,
                    holeFace = face, faceDirection = collider.transform.forward, individual = true
                }, true, true);
                Drag(1); Expect(parts[0], parts[1]);
                Require(!manager.current.individual, "A box inherited grouping bypass from a prior hole selection.");

                multiple.defaultAction.Disable();
                multiple.rebindAction.action.AddBinding(keyboard.f8Key.path);
                multiple.rebindAction.action.AddBinding(keyboard.leftAltKey.path);
                multiple.rebindAction.action.Enable();
                Press(); Press(Key.LeftCtrl); Seed(); Drag(1); Expect(parts[1]);
                Press(); Press(Key.F8); Seed(); Drag(1); Expect(parts[1]);
                Press(); Press(Key.F8, Key.LeftAlt); Seed(); Drag(1); Expect(parts[0], parts[1]);

                // Connect the first and third parts while the third remains
                // outside a box around the first.
                var graph = (Dictionary<GameObject, HashSet<GameObject>>)typeof(GroupManager)
                    .GetField("Graph", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                graph[parts[0]] = new HashSet<GameObject> { parts[2] };
                graph[parts[2]] = new HashSet<GameObject> { parts[0] };

                multiple.rebindAction.action.Disable(); multiple.defaultAction.Enable();
                grouping.rebindAction.action.Disable(); grouping.defaultAction.Enable();
                Press(); Drag(0); Expect(parts[0], parts[2]);
                Require(!manager.current.individual, "An ordinary box unexpectedly bypassed grouping.");
                Press(Key.LeftMeta); Require(GroupingBypassInput.IsHeld, "The default grouping modifier was not physically held.");
                Drag(0); Expect(parts[0]);
                Require(manager.current.individual, "The default grouping modifier did not bypass the overlapping group.");
                Press(); Drag(0); Expect(parts[0], parts[2]);
                Require(!manager.current.individual, "Grouping bypass latched after the default modifier was released.");

                grouping.defaultAction.Disable();
                grouping.rebindAction.action.AddBinding(keyboard.leftAltKey.path);
                grouping.rebindAction.action.Enable();
                Press(Key.LeftAlt); Require(GroupingBypassInput.IsHeld, "The rebound grouping modifier was not physically held.");
                Drag(0); Expect(parts[0]);
                Press(); Drag(0); Expect(parts[0], parts[2]);
                Require(!manager.current.individual, "Grouping bypass latched after the rebound modifier was released.");

                // A prior individually scoped selection cannot lend that scope
                // to an additive box resolved after the modifier is released.
                manager.SetCurrent(new ObjectSelection { gameObject = parts[1], individual = true }, true, true);
                Press(Key.LeftCtrl); Drag(0); Expect(parts);
                Require(!manager.current.individual, "Additive box inherited grouping bypass from its previous selection.");

                // Exercise the real BoxSelectUI performed/canceled boundary
                // against the active EventSystem, including UI release and a
                // canceled drag that later receives the input's canceled event.
                int releases = 0;
                boxUI.OnReleaseBox += (_, _) => releases++;
                Invoke(boxUI, "TryBeginBoxSelect", true, new Vector2(50, 50));
                Require(!Get<bool>(boxUI, "selecting"), "A box selection started over UI.");
                Invoke(boxUI, "TryBeginBoxSelect", false, new Vector2(500, 500));
                Require(Get<bool>(boxUI, "selecting"), "A scene box selection did not start.");
                Invoke(boxUI, "ResolveBoxSelect", true, new Vector2(50, 50));
                Require(releases == 0 && !Get<bool>(boxUI, "selecting"), "Releasing a box over UI resolved a scene selection.");
                Invoke(boxUI, "TryBeginBoxSelect", false, new Vector2(500, 500));
                Require(boxUI.CancelSelection(), "An active box could not be canceled.");
                Invoke(boxUI, "ResolveBoxSelect", false, new Vector2(50, 50));
                Require(releases == 0, "A canceled box resolved on the later input release.");
            }
            finally {
                Press();
                manager.ClearCurrent(false, true, true);
                multiple.defaultAction.Dispose(); multiple.rebindAction.action.Dispose();
                multiple.defaultAction = oldAction; multiple.rebindAction = oldRebind;
                if (oldRebind.IsEmpty) oldAction.Enable(); else oldRebind.action.Enable();
                grouping.defaultAction = oldGroupingAction; grouping.rebindAction.action.Dispose(); grouping.rebindAction = oldGroupingRebind;
                if (oldGroupingRebind.IsEmpty) oldGroupingAction.Enable(); else oldGroupingRebind.action.Enable();
                typeof(BoxSelector).GetField("cam", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(box, oldCamera);
                InputSystem.RemoveDevice(keyboard);
                foreach (var part in parts) Object.Destroy(part);
                Object.Destroy(camera.gameObject);
            }

            void Press(params Key[] keys) {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
                InputSystem.Update();
                multiple.Update();
                grouping.Update();
            }
            void Seed() => manager.SetCurrent(new ObjectSelection { gameObject = parts[0] }, true, true);
            void Drag(int index, bool reverse = false) {
                var center = (Vector2)camera.WorldToScreenPoint(parts[index].transform.position);
                var offset = new Vector2(60, 60);
                boxUI.OnReleaseBox.Invoke(center + (reverse ? offset : -offset), center + (reverse ? -offset : offset));
            }
            void DragEmpty() => boxUI.OnReleaseBox.Invoke(new Vector2(50, 50), new Vector2(100, 100));
            void Expect(params GameObject[] expected) {
                Require(manager.current is MultiSelection selection && selection.objs.Count == expected.Length &&
                    new HashSet<GameObject>(selection.objs).SetEquals(expected),
                    $"Box selection has incorrect or duplicate members: expected {expected.Length}, got {(manager.current as MultiSelection)?.objs.Count}; additive={multiple.IsPressed}, raw={multiple.defaultAction.IsPressed()}, key={keyboard.leftCtrlKey.isPressed}.");
            }
        }

        private static void CheckSelectionBoxRendering(BoxSelectUI boxUI) {
            var image = boxUI.GetComponent<Image>();
            var rect = (RectTransform)image.transform;
            Require(!image.raycastTarget, "The selection visual intercepts its own drag/release.");
            Require(image.type == Image.Type.Sliced && image.sprite != null && image.sprite.border.sqrMagnitude > 0f,
                "The selection rectangle is not using a bounded sliced border.");
            Require(image.sprite.border.x / (image.pixelsPerUnit * image.pixelsPerUnitMultiplier) <= 2f,
                "The selection rectangle border exceeds two canvas units.");

            string tileWarning = null;
            Application.LogCallback callback = (message, _, _) => {
                if (message.Contains("Too many sprite tiles") && message.Contains("Selecting Box")) tileWarning = message;
            };
            Application.logMessageReceived += callback;
            try {
                image.enabled = true;
                rect.sizeDelta = new Vector2(2f, 2f);
                Canvas.ForceUpdateCanvases();
                rect.sizeDelta = new Vector2(20000f, 12000f);
                Canvas.ForceUpdateCanvases();
                Require(tileWarning == null, "Large selection rectangle generated tiled-sprite geometry: " + tileWarning);
            }
            finally {
                Application.logMessageReceived -= callback;
                image.enabled = false;
                rect.sizeDelta = Vector2.zero;
            }
        }

        private static T Get<T>(object target, string name) => (T)target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static object Invoke(object target, string name, params object[] args) => target.GetType()
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args);
        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
