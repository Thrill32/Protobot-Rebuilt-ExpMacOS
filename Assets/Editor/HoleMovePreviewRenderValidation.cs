using System;
using System.IO;
using System.Reflection;
using DG.Tweening;
using Protobot;
using Protobot.SelectionSystem;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    /// <summary>Graphics-enabled check of the Move presentation on real generated geometry.</summary>
    public static class HoleMovePreviewRenderValidation {
        private const string SmokeKey = "Protobot.HoleMoveRender";
        private static readonly string Output = Path.Combine(Path.GetTempPath(), "protobot-hole-move-preview.png");
        private static int frames;
        private static double deadline;
        private static EditorWindow gameView;
        private static bool capturing;
        private static KeyboardTransformTool tool;
        private static Screw screw;

        [InitializeOnLoadMethod]
        private static void Resume() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            frames = 0;
            capturing = false;
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            File.Delete(Output);
            SessionState.SetBool(SmokeKey, true);
            EditorApplication.EnterPlaymode();
        }

        private static void Tick() {
            try {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Move preview rendering timed out.");
                if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 20) return;
                if (gameView == null) {
                    gameView = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                    gameView.Show();
                    gameView.position = new Rect(60, 60, 1280, 840);
                    gameView.Focus();
                    Configure();
                    frames = 0;
                }
                else if (!capturing) {
                    Physics.SyncTransforms();
                    // Pointer behavior has its own Play Mode suite. Render a known target independently of editor cursor scaling.
                    Invoke(tool, "ApplyTarget", MoveTargeting.ForScrew(screw));
                    Get<Tween>(tool, "smoothingTween")?.Complete();
                    var tracker = Get<GameObject>(Get<object>(tool, "targetPreview"), "screwTracker");
                    if (!tracker.activeSelf) throw new InvalidOperationException("Screw endpoint tracker did not render.");
                    ScreenCapture.CaptureScreenshot(Output);
                    capturing = true;
                }
                else if (File.Exists(Output)) {
                    Debug.Log("Move preview render smoke passed: generated channels and screw, translucent proxy, Move fill, source icon and screw endpoint tracker. Screenshot: " + Output);
                    SessionState.SetBool(SmokeKey, false);
                    EditorApplication.update -= Tick;
                    EditorApplication.Exit(0);
                    return;
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

        private static void Configure() {
            tool = Object.FindFirstObjectByType<KeyboardTransformTool>();
            GameObject.Find("Splash Screen")?.SetActive(false);
            GameObject.Find("Add Parts Menu")?.SetActive(false);
            var camera = Get<Camera>(tool, "refCamera");
            var source = PartsManager.GeneratePart("CCHL-1x2-5", new Vector3(-1, 2, 0), Quaternion.Euler(15, 25, 20));
            PartsManager.GeneratePart("CCHL-1x3-5", new Vector3(1, 1.5f, 1), Quaternion.Euler(0, 15, 0));
            var hole = source.GetComponentInChildren<HoleCollider>();
            var center = source.GetComponent<Renderer>().bounds.center + Vector3.right;
            camera.transform.position = center + new Vector3(5, 4, -9);
            camera.transform.LookAt(center);
            var screwObject = PartsManager.GeneratePart("SCRW-1.00in", new Vector3(.5f, 3, -.7f),
                Quaternion.LookRotation(camera.transform.position - new Vector3(.5f, 3, -.7f)));
            screw = screwObject.GetComponent<Screw>();
            var marker = new GameObject("Render source face", typeof(MeshFilter), typeof(HoleFace));
            var face = marker.GetComponent<HoleFace>();
            var normal = Vector3.Dot(hole.transform.forward, camera.transform.forward) > 0 ? -hole.transform.forward : hole.transform.forward;
            face.Set(hole.holeData, normal);
            var manager = Get<SelectionManager>(tool, "selectionManager");
            manager.current = new HoleSelection { gameObject = marker, holeData = hole.holeData, holeFace = face, faceDirection = normal };
            Invoke(tool, "BeginSelection", false);
            Invoke(tool, "ToggleHoleSnap");
            if (Get<object>(tool, "movePreview") == null) throw new InvalidOperationException("Generated part did not get a Move preview.");
            // Keep the actual presentation on screen while batch mode has no OS focus.
            typeof(KeyboardTransformTool).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tool, false);
        }

        private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Invoke(object obj, string method, params object[] args) => obj.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
    }
}
