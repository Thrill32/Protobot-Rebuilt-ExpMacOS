#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Protobot.InputEvents;
using Protobot.Tools;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    /// <summary>Graphics-enabled smoke test of the real Main-scene OnGUI paths.
    /// Synthesizes display state only; modal transform behavior has its own suite.</summary>
    public sealed class InteractionFeedbackRenderProbe : MonoBehaviour {
        private const string SmokeKey = "Protobot.FeedbackRenderSmoke";
        private static readonly string[] Modes = { "modifiers", "grab", "rotate" };
        private static int startupFrames;
        private static double deadline;
        private static InteractionFeedbackRenderProbe probe;
        private static EditorWindow gameView;
        private InteractionFeedback feedback;
        private KeyboardTransformTool tool;
        private InputEvent[] inputs;
        private int mode, repaints;
        private bool capturing;
        private Exception failure;

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
        }

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            foreach (var mode in Modes) File.Delete(PathFor(mode));
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }

        private static string PathFor(string mode) => Path.Combine(Path.GetTempPath(), "protobot-feedback-" + mode + ".png");

        private static void Tick() {
            try {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Feedback rendering did not complete.");
                if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++startupFrames < 15) return;
                if (probe == null) {
                    gameView = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                    gameView.Show(); gameView.Focus();
                    probe = new GameObject("Feedback render probe").AddComponent<InteractionFeedbackRenderProbe>();
                    probe.feedback = Object.FindFirstObjectByType<InteractionFeedback>();
                    probe.tool = Object.FindFirstObjectByType<KeyboardTransformTool>();
                    if (probe.feedback == null) throw new InvalidOperationException("Main did not install InteractionFeedback.");
                    // Invoke each real renderer once per event from the probe,
                    // keeping display state stable without running a transform.
                    probe.feedback.enabled = false; probe.tool.enabled = false;
                    probe.inputs = Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None)
                        .Where(input => input.name == "Duplicate" || input.name == "Snap Bypass" || input.name == "Multi-Select Button").ToArray();
                    if (probe.inputs.Length != 3) throw new InvalidOperationException("Missing modifier bindings.");
                    foreach (var input in probe.inputs) {
                        input.rebindAction.action.Disable();
                        input.defaultAction.Enable();
                    }
                }
                if (probe.failure != null) throw new InvalidOperationException("Real feedback renderer failed.", probe.failure);
                if (probe.capturing && File.Exists(PathFor(Modes[probe.mode]))) {
                    Debug.Log("Feedback render captured: " + PathFor(Modes[probe.mode]));
                    if (++probe.mode == Modes.Length) {
                        SessionState.SetBool(SmokeKey, false);
                        EditorApplication.update -= Tick;
                        Debug.Log("Feedback render smoke passed: real canvas registry traversal, modifier stacking, G/R help, compact measurements and translucent panels.");
                        EditorApplication.Exit(0);
                        return;
                    }
                    probe.capturing = false; probe.repaints = 0;
                }
                gameView.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception exception) {
                SessionState.SetBool(SmokeKey, false);
                EditorApplication.update -= Tick;
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private void OnGUI() {
            if (feedback == null || failure != null || mode >= Modes.Length) return;
            try {
                tool.enabled = true;
                Set("active", mode != 0); Set("rotating", mode == 2);
                Set("axis", Vector3.zero); Set("targetOffset", Vector3.right * 1.5f); Set("rotationDegrees", 37.5f);
                InputState.Change(Keyboard.current, new KeyboardState(Key.LeftAlt, Key.LeftMeta, Key.LeftCtrl), InputUpdateType.Dynamic);
                InputState.Change(Mouse.current.position, new Vector2(Screen.width * .6f, Screen.height * .6f), InputUpdateType.Dynamic);
                foreach (var input in inputs) input.Update();
                Invoke(feedback, "OnGUI");
                Invoke(tool, "OnGUI");
                if (Event.current.type == EventType.Repaint && ++repaints >= 5 && !capturing) {
                    var messages = (System.Collections.Generic.List<string>)typeof(InteractionFeedback)
                        .GetField("messages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(feedback);
                    if (!messages.Contains("Duplicating") || !messages.Any(text => text.StartsWith("Bypassing grouping")) ||
                        (mode == 0 && !messages.Contains("Selecting multiple")) ||
                        (mode > 0 && !messages.Any(text => text.StartsWith(mode == 1 ? "Grab ·" : "Rotate ·"))))
                        throw new InvalidOperationException("Modifier/modal text did not reach the renderer: " + string.Join(" | ", messages) +
                            "; bindings: " + string.Join(" | ", inputs.Select(input =>
                                $"{input.name}={input.defaultAction.GetBindingDisplayString()} held:{input.IsCurrentlyPressed} pressed:{input.IsPressed}")));
                    ScreenCapture.CaptureScreenshot(PathFor(Modes[mode]));
                    capturing = true;
                }
            }
            catch (Exception exception) { failure = exception; }
            finally {
                Set("active", false);
                tool.enabled = false;
            }
        }

        private void Set(string field, object value) => typeof(KeyboardTransformTool).GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tool, value);
        private static void Invoke(object target, string method) => target.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
    }
}
#endif
