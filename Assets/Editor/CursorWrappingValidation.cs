using System;
using System.Linq;
using System.Reflection;
using Protobot.InputEvents;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CursorWrappingValidation {
        private const string SmokeKey = "Protobot.CursorWrappingSmoke";
        private static int frames;
        [MenuItem("Tools/Protobot/Validate Cursor Wrapping")]
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(SmokeKey, false)) { frames = 0; EditorApplication.update += Tick; } }
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
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try { Check(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }
        private static void Check() {
            var cameraObject = new GameObject("Cursor continuity camera");
            var previousMouse = Mouse.current;
            var previousLock = Cursor.lockState;
            var previousBackground = InputSystem.settings.backgroundBehavior;
            var previousEditorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            var mouse = InputSystem.AddDevice<Mouse>();
            try {
                Cursor.lockState = CursorLockMode.None;
                var camera = cameraObject.AddComponent<Camera>();
                var viewport = new Rect(80, 40, 720, 520); camera.pixelRect = viewport;
                camera.orthographicSize = 9;
                var origin = new Vector3(1.13f, 2.27f, .33f);
                foreach (bool orthographic in new[] { false, true }) {
                    camera.orthographic = orthographic;
                    foreach (var view in new[] { new Vector3(8, 6, -20), new Vector3(-9, 13, -24), new Vector3(0, 0, -20) }) {
                        camera.transform.position = origin + view; camera.transform.LookAt(origin);
                        foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward }) {
                            var gesture = new TransformGesture();
                            var center = (Vector2)camera.WorldToScreenPoint(origin);
                            gesture.Begin(camera, origin, center);
                            var motion = new Vector2(7.25f, -5.5f);
                            var first = gesture.Position(center + motion, axis, false) - origin;
                            foreach (var edge in new[] {
                                new Vector2(viewport.xMax - 1, viewport.center.y), new Vector2(viewport.xMin + 1, viewport.center.y),
                                new Vector2(viewport.center.x, viewport.yMax - 1), new Vector2(viewport.center.x, viewport.yMin + 1),
                                new Vector2(viewport.xMax - 1, viewport.yMax - 1), new Vector2(viewport.xMin + 1, viewport.yMin + 1)
                            }) {
                                for (int repeat = 0; repeat < 4; repeat++) {
                                    var before = gesture.Position(edge, axis, false);
                                    Near(gesture.Position(edge + motion, axis, false) - before, first, "Sensitivity changed near an edge.");
                                    Require(TransformCursor.TryWrap(edge, viewport, out var wrapped), "Edge did not wrap.");
                                    var snapped = gesture.Position(edge, axis, true);
                                    gesture.RebasePointer(edge, wrapped);
                                    Near(gesture.Position(wrapped, axis, false), before, "Wrap changed unsnapped distance.");
                                    Near(gesture.Position(wrapped, axis, true), snapped, "Wrap changed snapping phase.");
                                    Near(gesture.Position(wrapped + motion, axis, false) - before, first, "Opposite edge changed units per pixel.");
                                }
                            }
                            Near(gesture.Origin, origin, "Wrapping changed the numeric/axis anchor.");
                        }
                    }
                }
                Require(!TransformCursor.TryWrap(new Vector2(viewport.center.x, viewport.yMax), viewport, out _, vertical: false),
                    "Horizontal scrub tried to warp at a vertical-only edge.");
                Require(TransformCursor.TryWrap(new Vector2(viewport.xMax, viewport.yMax), viewport, out var horizontal, vertical: false) && horizontal.y == viewport.yMax,
                    "Horizontal scrub changed the pointer's Y coordinate.");
                CheckNativeEvents(mouse, camera, origin);
                Debug.Log("Cursor wrapping validation passed: constant axis sensitivity across all edges/corners in perspective/orthographic views, snapping/numeric-anchor continuity, delayed/stale native events, coalesced and repeated warp echoes, accumulated real deltas, button/wheel preservation, partial delta events, repeated warps, device isolation and horizontal-only scrubbing.");
            }
            finally {
                typeof(CursorWarp).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                InputSystem.RemoveDevice(mouse); previousMouse?.MakeCurrent(); Cursor.lockState = previousLock;
                InputSystem.settings.backgroundBehavior = previousBackground;
                InputSystem.settings.editorInputBehaviorInPlayMode = previousEditorInput;
                Object.DestroyImmediate(cameraObject);
            }
        }

        private static void CheckNativeEvents(Mouse mouse, Camera camera, Vector3 origin) {
            camera.orthographic = false; camera.transform.position = origin + new Vector3(8, 6, -20); camera.transform.LookAt(origin);
            var viewport = camera.pixelRect;
            var gesture = new TransformGesture(); gesture.Begin(camera, origin, viewport.center);
            var edge = new Vector2(viewport.xMax - 1, viewport.center.y);
            TransformCursor.TryWrap(edge, viewport, out var wrapped);
            var real = new Vector2(5.25f, -2.5f);
            var expectedMotion = gesture.Position(viewport.center + real, Vector3.right, false) - gesture.Position(viewport.center, Vector3.right, false);
            for (int repeat = 0; repeat < 6; repeat++) {
                ResetWarp();
                Event(mouse, edge, Vector2.zero);
                Near(mouse.position.ReadValue(), edge, "Queued mouse event did not reach the device.");
                var before = gesture.Position(edge, Vector3.right, false);
                double queuedBefore = InputState.currentTime;
                Require(CursorWarp.Warp(mouse, wrapped), "Warp request failed."); gesture.RebasePointer(edge, wrapped);
                Near(mouse.position.ReadValue(), wrapped, "Software pointer did not move immediately.");
                Near(mouse.delta.ReadValue(), Vector2.zero, "Software warp left a delta.");
                Event(mouse, edge, Vector2.zero, time: queuedBefore, scroll: new Vector2(0, 120), buttons: 1);
                Near(mouse.position.ReadValue(), wrapped, "A queued old coordinate undid the warp.");
                Near(mouse.delta.ReadValue(), Vector2.zero, "A stale event injected measured motion.");
                Require(mouse.leftButton.isPressed && mouse.scroll.y.ReadValue() == 120,
                    $"Warp filtering swallowed a button or wheel event: button={mouse.leftButton.isPressed}, wheel={mouse.scroll.y.ReadValue()}, time={mouse.lastUpdateTime}/{queuedBefore}.");
                Event(mouse, edge + Vector2.right, Vector2.right);
                Near(mouse.position.ReadValue(), wrapped, "A pre-acknowledgement edge event bounced the pointer.");
                Event(mouse, wrapped + real, wrapped - edge + real);
                Near(mouse.position.ReadValue(), wrapped + real, "Coalesced acknowledgement lost real motion.");
                Near(mouse.delta.ReadValue(), real, "Warp distance leaked into relative motion.");
                Near(gesture.Position(mouse.position.ReadValue(), Vector3.right, false) - before, expectedMotion, "Native acknowledgement changed the measured distance.");
                Event(mouse, wrapped + real, wrapped - edge);
                Near(mouse.delta.ReadValue(), Vector2.zero, "A repeated warp echo injected camera motion.");
                Event(mouse, wrapped, wrapped - edge);
                Near(mouse.position.ReadValue(), wrapped + real, "A delayed destination echo rewound real movement.");
                Near(mouse.delta.ReadValue(), Vector2.zero, "A delayed destination echo changed measurements.");

                // Multiple real events in one input update must still accumulate.
                InputSystem.QueueStateEvent(mouse, new MouseState { position = wrapped + real + Vector2.right * 2, delta = Vector2.right * 2 });
                InputSystem.QueueStateEvent(mouse, new MouseState { position = wrapped + real + Vector2.right * 5, delta = Vector2.right * 3 });
                UpdateInput();
                Near(mouse.delta.ReadValue(), Vector2.right * 5, "Filtered real mouse events stopped accumulating.");
                var accepted = mouse.position.ReadValue();
                Event(mouse, edge, edge - wrapped, time: queuedBefore, scroll: new Vector2(0, -120));
                Near(mouse.position.ReadValue(), accepted, "A late stale event undid an acknowledged warp.");
                Event(mouse, accepted, Vector2.zero, scroll: new Vector2(0, -120));
                Require(!mouse.leftButton.isPressed && mouse.scroll.y.ReadValue() == -120, "Filtering lost release/scroll input.");
                InputSystem.QueueDeltaStateEvent(mouse.delta, wrapped - edge + real); UpdateInput();
                Near(mouse.delta.ReadValue(), real, "A delta-only warp event was not corrected.");
                var fastMotion = (wrapped - edge) * .75f;
                InputSystem.QueueDeltaStateEvent(mouse.delta, fastMotion); UpdateInput();
                Near(mouse.delta.ReadValue(), fastMotion, "A genuine fast relative movement was mistaken for a warp.");
            }
            CheckVerticalAndOverlappingWarps(mouse, viewport);
            Require(!CursorWarp.Warp(mouse, mouse.position.ReadValue()), "A zero-distance warp was issued.");
            var other = InputSystem.AddDevice<Mouse>();
            try {
                Event(other, new Vector2(100, 200), new Vector2(3, 4));
                Near(other.position.ReadValue(), new Vector2(100, 200), "Warp filtering affected another mouse.");
                Near(other.delta.ReadValue(), new Vector2(3, 4), "Warp filtering changed another device's delta.");
            }
            finally { InputSystem.RemoveDevice(other); mouse.MakeCurrent(); }
        }

        private static void CheckVerticalAndOverlappingWarps(Mouse mouse, Rect viewport) {
            var top = new Vector2(viewport.center.x, viewport.yMax - 1);
            Require(TransformCursor.TryWrap(top, viewport, out var bottom), "Vertical edge did not wrap.");
            Event(mouse, top, Vector2.zero);
            Require(CursorWarp.Warp(mouse, bottom), "Vertical warp request failed.");
            var verticalMotion = new Vector2(3f, 7f);
            Event(mouse, bottom + verticalMotion, bottom - top + verticalMotion);
            Near(mouse.position.ReadValue(), bottom + verticalMotion, "Vertical coalesced ack lost real motion.");
            Near(mouse.delta.ReadValue(), verticalMotion, "Vertical teleport leaked into accumulated motion.");
            Event(mouse, bottom, bottom - top);
            Near(mouse.position.ReadValue(), bottom + verticalMotion, "Delayed vertical ack rewound the pointer.");

            // A second physical traversal can return to the old edge within the
            // native echo guard. Matching position/delta is real motion on X or Y.
            foreach (var edge in new[] { top, new Vector2(viewport.xMax - 1, viewport.center.y) }) {
                ResetWarp(); Event(mouse,edge,Vector2.zero);
                TransformCursor.TryWrap(edge,viewport,out var wrapped);
                Require(CursorWarp.Warp(mouse,wrapped), "Repeat traversal warp failed.");
                Event(mouse,wrapped,wrapped-edge);
                var motion = edge-wrapped;
                Event(mouse,edge,motion);
                Near(mouse.position.ReadValue(),edge,"A real fast return to the previous edge was mistaken for a native echo.");
                Near(mouse.delta.ReadValue(),motion,"Fast repeated wrapping lost physical motion.");
            }

            // Windows can coalesce an acknowledgement after another edge is
            // reached. The older warp must not overwrite the newer logical
            // position, on either axis.
            ResetWarp();
            var right = new Vector2(viewport.xMax - 1, viewport.center.y);
            TransformCursor.TryWrap(right, viewport, out var left);
            Event(mouse, right, Vector2.zero);
            Require(CursorWarp.Warp(mouse, left), "First overlapping warp failed.");
            var upper = new Vector2(left.x, viewport.yMax - 1);
            Event(mouse, upper, upper - left);
            TransformCursor.TryWrap(upper, viewport, out var lower);
            Require(CursorWarp.Warp(mouse, lower), "Second overlapping warp failed.");
            Event(mouse, left, left - right);
            Near(mouse.position.ReadValue(), lower, "Older horizontal acknowledgement overwrote a newer vertical warp.");
            Event(mouse, lower + Vector2.one * 4f, lower - upper + Vector2.one * 4f);
            Near(mouse.position.ReadValue(), lower + Vector2.one * 4f, "Newer vertical acknowledgement lost post-warp motion.");
            Near(mouse.delta.ReadValue(), Vector2.one * 4f, "Overlapping warp motion was not accumulated.");
        }

        private static void Event(Mouse mouse, Vector2 position, Vector2 delta, double time = -1, Vector2 scroll = default, ushort buttons = 0) {
            InputSystem.QueueStateEvent(mouse, new MouseState { position = position, delta = delta, scroll = scroll, buttons = buttons }, time);
            UpdateInput();
        }
        private static void ResetWarp() => typeof(CursorWarp).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        private static void Near(Vector2 a, Vector2 b, string message) => Require(Vector2.Distance(a, b) < .002f, message + $" ({a} vs {b})");
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .002f, message + $" ({a} vs {b})");
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
