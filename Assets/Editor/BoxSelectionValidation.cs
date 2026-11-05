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
                Debug.Log("Box selection validation passed: replacement, default and rebound additive input, complete chords, overlapping/repeated boxes, empty boxes, single/multi/hole selections and individual grouping scope.");
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
            var multiple = Get<InputEvent>(manager.GetComponent<MultiSelector>(), "mutliSelectInput");
            var oldAction = multiple.defaultAction;
            var oldRebind = multiple.rebindAction;
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
                Require(manager.current.individual, "Adding a box discarded individual selection scope.");

                multiple.defaultAction.Disable();
                multiple.rebindAction.action.AddBinding(keyboard.f8Key.path);
                multiple.rebindAction.action.AddBinding(keyboard.leftAltKey.path);
                multiple.rebindAction.action.Enable();
                Press(); Press(Key.LeftCtrl); Seed(); Drag(1); Expect(parts[1]);
                Press(); Press(Key.F8); Seed(); Drag(1); Expect(parts[1]);
                Press(); Press(Key.F8, Key.LeftAlt); Seed(); Drag(1); Expect(parts[0], parts[1]);
            }
            finally {
                Press();
                manager.ClearCurrent(false, true, true);
                multiple.defaultAction.Dispose(); multiple.rebindAction.action.Dispose();
                multiple.defaultAction = oldAction; multiple.rebindAction = oldRebind;
                if (oldRebind.IsEmpty) oldAction.Enable(); else oldRebind.action.Enable();
                typeof(BoxSelector).GetField("cam", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(box, oldCamera);
                InputSystem.RemoveDevice(keyboard);
                foreach (var part in parts) Object.Destroy(part);
                Object.Destroy(camera.gameObject);
            }

            void Press(params Key[] keys) {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
                InputSystem.Update();
                multiple.Update();
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

        private static T Get<T>(object target, string name) => (T)target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
