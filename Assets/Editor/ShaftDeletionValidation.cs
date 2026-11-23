using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.StateSystems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class ShaftDeletionValidation {
        const string Key = "Protobot.ShaftDeletionSmoke";
        static int ticks;
        static readonly List<string> problems = new();

        [InitializeOnLoadMethod]
        static void Resume() {
            if (!SessionState.GetBool(Key, false)) return;
            EditorApplication.update += Tick;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void HeadlessCameras() {
            if (!SessionState.GetBool(Key, false) || SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled = false;
        }

        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true);
            EditorApplication.EnterPlaymode();
        }

        static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++ticks < 20) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(Key, false);
            Application.logMessageReceived += Capture;
            try {
                Physics.simulationMode = SimulationMode.Script;
                await Run();
                Require(problems.Count == 0, "Unexpected logs:\n" + string.Join("\n", problems));
                Application.logMessageReceived -= Capture;
                Debug.Log("Shaft deletion lifecycle smoke passed: real shaft/collars, Delete/Undo/Redo, both collar orders, multi-selection and Group deletion, rapid repeats, inactive endpoints, stationary restore, re-entrant listeners, deferred notification cancellation, geometry/build replacement and destruction; zero unexpected warnings/errors.");
                EditorApplication.Exit(0);
            } catch (Exception e) {
                Application.logMessageReceived -= Capture;
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        static void Capture(string message, string stack, LogType type) {
            if (type == LogType.Warning || type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                problems.Add(message + "\n" + stack);
        }

        static async Task Frames(int count = 4, bool simulate = true) {
            for (int i = 0; i < count; i++) {
                if (simulate) {
                    Physics.SyncTransforms();
                    Physics.Simulate(.02f);
                }
                int frame = Time.frameCount;
                while (Time.frameCount == frame) await Task.Yield();
            }
        }

        static async Task Run() {
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            await Frames();
            StateSystem.instance.ResetStates();
            var parts = Assembly();
            await Frames(); Connected(parts);
            var detector = parts[0].GetComponent<Shaft>().holeDetector;
            int removals = 0;
            detector.OnRemoveHole += () => removals++;
            Delete(parts[0]);
            Released(parts); // Teardown must release synchronously, before a later frame.
            Require(removals == 2, "Shaft teardown did not release both collars exactly once.");
            await Frames(); Released(parts);
            await Undo(parts); await RedoReleased(parts); await Undo(parts);

            // Each collar order, including a second deletion before the first refresh.
            foreach (var order in new[] { new[] { 1, 2 }, new[] { 2, 1 } }) {
                Delete(parts[order[0]]);
                await Frames(); Ungrouped(parts);
                Require(detector.holes.Count == 1, "Deleting one collar retained its occupancy or lost the surviving collar.");
                StateSystem.instance.Undo(); await Frames(); Connected(parts);
                Delete(parts[order[0]]); Delete(parts[order[1]]);
                await Frames(); Released(parts);
                StateSystem.instance.Undo(); StateSystem.instance.Undo();
                await Frames(); Connected(parts);
                StateSystem.instance.Redo(); StateSystem.instance.Redo();
                await Frames(); Released(parts);
                StateSystem.instance.Undo(); StateSystem.instance.Undo();
                await Frames(); Connected(parts);
            }

            // Exercise both multi-selection and Group-tagged root deletion branches.
            foreach (var sample in new[] { (parts, false), (parts.Reverse().ToArray(), false), (parts, true) }) {
                var pivot = Pivot.Create("Shaft deletion multiselection");
                if (sample.Item2) pivot.tag = "Group";
                pivot.AddObjects(sample.Item1.ToList(), false);
                Delete(pivot.gameObject);
                await Frames(); Released(parts);
                Require(parts.All(p => !p.activeInHierarchy), "Multi-selection deletion left a body active.");
                await Undo(parts); await RedoReleased(parts); await Undo(parts);
                pivot.SetEmpty(); Object.Destroy(pivot.gameObject);
                await Frames(); Connected(parts);
            }

            // Pending add/remove notifications must not survive a disabled host.
            for (int i = 0; i < 12; i++) {
                Delete(parts[0]); Delete(parts[0]);
                StateSystem.instance.Undo();
                Delete(parts[1]); StateSystem.instance.Undo();
            }
            await Frames(); Connected(parts);
            Delete(parts[1]);
            Delete(parts[0]); await Frames(); Released(parts);
            StateSystem.instance.Undo(); await Frames(); Ungrouped(parts);
            Require(detector.holes.Count == 1, "An inactive collar was restored as an attachment.");
            StateSystem.instance.Undo(); await Frames(); Connected(parts);

            parts[0].SetActive(false);
            await Frames(simulate: false); Released(parts);
            parts[0].SetActive(true);
            await Frames(simulate: false); Connected(parts);

            // Disable only the detector/component, rather than the whole part.
            detector.enabled = false; await Frames(); Released(parts);
            detector.enabled = true; await Frames(); Connected(parts);
            var shaft = parts[0].GetComponent<Shaft>();
            shaft.enabled = false;
            parts[1].SetActive(false); await Frames(); Ungrouped(parts);
            parts[1].SetActive(true); shaft.enabled = true;
            await Frames(); Connected(parts);
            var clamp = Clamp(parts[1]);
            clamp.enabled = false; await Frames(); Ungrouped(parts);
            Require(!detector.holes.Contains(clamp) && clamp.detectors.Count == 0, "Disabled attachment retained occupancy.");
            clamp.enabled = true; await Frames(); Connected(parts);

            // Re-entrant attachment listeners can deactivate either endpoint.
            foreach (bool disableShaft in new[] { false, true }) {
                detector.RemoveHole(clamp);
                Action<HoleDetector> disable = _ => (disableShaft ? parts[0] : parts[1]).SetActive(false);
                clamp.OnSetDetector += disable;
                detector.AddHole(clamp);
                clamp.OnSetDetector -= disable;
                Require(!detector.holes.Contains(clamp) && clamp.detectors.Count == 0,
                    "Re-entrant disable retained half an attachment.");
                (disableShaft ? parts[0] : parts[1]).SetActive(true);
                await Frames(); Connected(parts);
            }

            int notifications = 0;
            detector.OnAddTargetHole += () => {
                Require(detector.CanDetect, "Deferred target notification ran on a disabled detector.");
                notifications++;
            };
            detector.RemoveAll(); detector.RefreshOverlaps();
            parts[0].SetActive(false);
            await Frames();
            Require(notifications == 0, "Cancelled target notification survived disable.");
            parts[0].SetActive(true); await Frames(); Connected(parts);
            Require(notifications == 1, "Target notification was lost or duplicated on restore.");

            // Same-count replacement, with old geometry destroyed while refresh is pending.
            var oldCollar = parts[1];
            var affected = HoleDetector.GetAffectedDetectors(oldCollar);
            var replacement = PartsManager.GeneratePart("CLMP-Normal-Normal", oldCollar.transform.position, oldCollar.transform.rotation);
            oldCollar.SetActive(false); Object.Destroy(oldCollar);
            parts[1] = replacement;
            HoleDetector.RefreshPart(replacement, affected);
            await Frames(); Connected(parts);
            Require(!GraphObjects().Contains(oldCollar), "Graph retained the destroyed collar.");

            // Exercise real build snapshots/replacement repeatedly at the same positions.
            var build = SceneBuild.ToBuildData(parts);
            for (int i = 0; i < 3; i++) {
                var previous = parts;
                SceneBuild.GenerateBuild(build);
                parts = LoadedAssembly();
                // Replace again before the newly generated detectors have refreshed.
                if (i == 1) {
                    var justGenerated = parts;
                    SceneBuild.GenerateBuild(build);
                    parts = LoadedAssembly();
                    await Frames();
                    Require(justGenerated.All(p => p == null), "Build replacement retained old objects.");
                }
                await Frames(); Connected(parts);
                Require(previous.All(p => p == null), "Build replacement retained the previous assembly.");
                Require(GraphObjects().All(p => p != null && p.activeInHierarchy), "Build replacement retained stale graph keys/edges.");
            }
            // Destroy an assembly with pending attachment and graph notifications.
            var destroyedDetector = parts[0].GetComponent<Shaft>().holeDetector;
            destroyedDetector.RemoveAll(); destroyedDetector.RefreshOverlaps();
            foreach (var part in parts.Reverse()) Object.Destroy(part);
            await Frames();
            Require(parts.All(p => p == null), "Destruction did not complete.");
            // A queued caller holding a Unity-destroyed reference must be harmless.
            destroyedDetector.RefreshOverlaps();
            destroyedDetector.AddHole(null);
            Require(GraphObjects().Count == 0, "Graph retained destroyed assembly references.");
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            await Frames();
        }

        static void Delete(GameObject target) {
            var probe = new GameObject("Shaft deletion command");
            var link = probe.AddComponent<SerializedObjectLink>();
            typeof(SerializedObjectLink).GetField("serializedObj", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(link, target);
            var action = probe.AddComponent<ObjectDeletor>();
            action.refObj = link;
            action.Execute();
            Object.Destroy(probe);
        }

        static async Task Undo(GameObject[] parts) {
            StateSystem.instance.Undo(); await Frames(); Connected(parts);
        }

        static async Task RedoReleased(GameObject[] parts) {
            StateSystem.instance.Redo(); await Frames(); Released(parts);
        }

        static HashSet<GameObject> GraphObjects() {
            var graph = (Dictionary<GameObject, HashSet<GameObject>>)typeof(GroupManager)
                .GetField("Graph", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return graph.Keys.Concat(graph.Values.SelectMany(v => v)).ToHashSet();
        }

        static HoleCollider Clamp(GameObject part) => part.GetComponentsInChildren<HoleCollider>(true)
            .First(h => h.holeType == HoleCollider.HoleType.Clamp);

        static GameObject[] LoadedAssembly() {
            var loaded = PartsManager.FindLoadedObjects();
            return new[] { loaded.Single(p => p.GetComponent<Shaft>() != null) }
                .Concat(loaded.Where(p => p.GetComponent<Shaft>() == null)).ToArray();
        }

        static GameObject[] Assembly() {
            var center = new Vector3(2000, 2000, 2000);
            var shaft = PartsManager.GeneratePart("SHFT-Normal-6", center, Quaternion.identity);
            var a = PartsManager.GeneratePart("CLMP-Normal-Normal", center - Vector3.forward, Quaternion.identity);
            var b = PartsManager.GeneratePart("CLMP-Clamping-Normal", center + Vector3.forward, Quaternion.identity);
            foreach (var pair in new[] { (a, -1f), (b, 1f) }) {
                var hole = pair.Item1.GetComponentsInChildren<HoleCollider>().First(h => h.holeType == HoleCollider.HoleType.Clamp);
                pair.Item1.transform.position += center + Vector3.forward * pair.Item2 - hole.transform.position;
            }
            Physics.SyncTransforms();
            return new[] { shaft, a, b };
        }

        static void Connected(GameObject[] parts) {
            Require(parts[0].TryGetGroup(out List<GameObject> group) && new HashSet<GameObject>(group).SetEquals(parts),
                "Shaft/collars did not reconnect.");
            var detector = parts[0].GetComponent<Shaft>().holeDetector;
            Require(detector.holes.Count == 2 && parts.Skip(1).All(p =>
                p.GetComponentsInChildren<HoleCollider>().Where(h => h.holeType == HoleCollider.HoleType.Clamp)
                .All(h => h.detectors.SequenceEqual(new[] { detector }))), "Incorrect detector ownership.");
        }

        static void Released(GameObject[] parts) {
            Ungrouped(parts);
            Require(parts[0].GetComponent<Shaft>().holeDetector.holes.Count == 0 && parts.Skip(1).All(p =>
                p.GetComponentsInChildren<HoleCollider>(true).All(h => h.detectors.Count == 0)), "Stale detector occupancy.");
        }

        static void Ungrouped(GameObject[] parts) {
            Require(parts.All(p => !p.TryGetGroup(out List<GameObject> _)), "Deleted assembly retained graph edges.");
            Require(!GraphObjects().Overlaps(parts), "Graph retained empty or stale assembly entries.");
        }

        static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
