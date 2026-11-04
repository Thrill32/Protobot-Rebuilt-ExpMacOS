using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.SelectionSystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class PasteMaterialValidation {
        private const string SmokeKey = "Protobot.PasteMaterials";
        private static int frames;
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(SmokeKey, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Paste material validation passed: every wheel variant, shared catalog/source materials and textures survive paste commit/cancel, repeated paste, P templates and copy deletion; placed copies remain independent.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }
        private static async Task Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
            var preview = new GameObject("Material test preview");
            var sources = new List<GameObject>();
            foreach (var path in new[] { "Traction Wheel", "Omni Wheel", "Flex Wheel", "Mecanum Wheel" }) {
                var prefab = Resources.Load<GameObject>("Part Prefabs/4Motion/Wheels/" + path);
                var generator = prefab.GetComponent<PartGenerator>(); generator.InitParamValues();
                var first = generator.UsesParams ? generator.GetParam1Options() : new List<string> { "" };
                foreach (var p1 in first) {
                    generator.param1.value = p1;
                    var second = generator.UsesTwoParams ? generator.GetParam2Options() : new List<string> { "" };
                    foreach (var p2 in second) {
                        generator.param2.value = p2;
                        sources.Add(generator.Generate(new Vector3(sources.Count * 8, 100, 100), Quaternion.identity));
                    }
                }
            }
            var original = Renderers(sources).Select(r => (renderer: r, materials: r.sharedMaterials)).ToArray();
            var assetMaterials = original.SelectMany(entry => entry.materials).Where(m => m != null && EditorUtility.IsPersistent(m)).Distinct().ToArray();
            Require(assetMaterials.Length > 0, "Wheel fixture did not include shared catalog materials.");
            var assets = assetMaterials.Select(m => (material: m, shader: m.shader, texture: m.mainTexture)).ToArray();
            var snapshot = SceneBuild.ToBuildData(sources);
            snapshot.placementAnchor = PlacementAnchorData.From(Pose.identity, false);
            original = Renderers(sources).Select(r => (renderer: r, materials: r.sharedMaterials)).ToArray();
            for (int iteration = 0; iteration < 3; iteration++) {
                var clipboard = BuildTransfer.FromClipboard(BuildTransfer.ToClipboard(snapshot));
                var transfer = SelectionPlacementData.FromBuild(clipboard, preview.transform, duplicator, null);
                var templates = (List<GameObject>)typeof(SelectionPlacementData).GetField("templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(transfer);
                Require(Renderers(templates).SelectMany(r => r.sharedMaterials).All(m => m == null || !EditorUtility.IsPersistent(m)),
                    "Paste templates retained shared catalog materials that cleanup would destroy.");
                var placed = transfer.Generate(new Vector3(0, 200 + iteration * 10, 100), Quaternion.identity);
                var placedMaterials = Renderers(placed).SelectMany(r => r.sharedMaterials).Where(m => m != null).ToArray();
                transfer.Dispose(); await NextFrame();
                CheckOriginals(); CheckValid(placed, "Placed wheel lost a material after its paste templates were disposed.");
                Require(placedMaterials.All(m => m != null), "Template cleanup destroyed a placed copy's material.");
                var rootMaterial = placed[0].GetComponent<Renderer>().material;
                var oldColor = sources[0].GetComponent<Renderer>().sharedMaterial.color;
                rootMaterial.color = Color.cyan;
                Require(sources[0].GetComponent<Renderer>().sharedMaterial.color == oldColor, "Recoloring a pasted wheel recolored its source.");
                // Clone a clone, then dispose the first copy before the second.
                var clones = duplicator.CreateCopies(placed);
                ObjectDuplicator.DiscardCopies(placed); await NextFrame();
                CheckOriginals(); CheckValid(clones, "Discarding an earlier copy destroyed a later copy's material.");
                Require(rootMaterial == null, "Recoloring left an untracked material on a discarded copy.");
                ObjectDuplicator.DiscardCopies(clones); await NextFrame();
                Require(placedMaterials.All(m => m == null), "Discarded copies leaked their private materials.");
                var canceled = SelectionPlacementData.FromBuild(clipboard, preview.transform, duplicator, null);
                canceled.Dispose(); await NextFrame(); CheckOriginals();
            }
            var manager = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            manager.SetCurrent(new MultiSelection(null, sources) { individual = true }, true);
            var repeat = SelectionPlacementData.Capture(manager.current, preview.transform, duplicator);
            var stamp = repeat.Generate(new Vector3(0, 300, 100), Quaternion.identity);
            repeat.Dispose(); await NextFrame(); CheckOriginals(); CheckValid(stamp, "P template cleanup damaged its stamp.");
            ObjectDuplicator.DiscardCopies(stamp); await NextFrame(); CheckOriginals();
            foreach (var generator in Resources.LoadAll<GameObject>("Part Prefabs/4Motion/Wheels").Select(p => p.GetComponent<PartGenerator>()).Where(g => g != null)) {
                generator.InitParamValues();
                var fresh = generator.Generate(new Vector3(0, 400, 100), Quaternion.identity);
                CheckValid(new[] { fresh }, "Pasting damaged newly generated catalog wheels."); Object.Destroy(fresh);
            }
            manager.ClearCurrent(false, true, true); Object.Destroy(preview);

            void CheckOriginals() {
                foreach (var asset in assets)
                    Require(asset.material != null && asset.material.shader == asset.shader && asset.material.mainTexture == asset.texture,
                        "Paste cleanup destroyed or altered a shared catalog material/texture.");
                foreach (var entry in original)
                    Require(entry.materials.All(m => m != null) && entry.renderer.sharedMaterials.SequenceEqual(entry.materials),
                        "Paste cleanup destroyed or replaced the source wheel's material.");
            }
        }
        private static IEnumerable<Renderer> Renderers(IEnumerable<GameObject> objects) => objects.SelectMany(o => o.GetComponentsInChildren<Renderer>(true));
        private static void CheckValid(IEnumerable<GameObject> objects, string message) {
            foreach (var renderer in Renderers(objects))
                Require(renderer.sharedMaterials.All(m => m != null && m.shader != null && !m.shader.name.Contains("InternalError")), message);
        }
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
