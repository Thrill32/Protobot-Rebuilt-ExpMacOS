using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using Protobot;
using Protobot.Builds;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class ColorPaintingValidation {
        private const string SmokeKey = "Protobot.ColorPaintingSmoke";
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
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }

        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick;
            SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Color painting validation passed: catalog families, per-slot defaults, default brush UI, default restoration after copy/regeneration/load, nested renderers/material slots, hole and box selection, exact undo/redo without repainting restored selections, no-op paint, material isolation, dirty detection, regeneration, clipboard and binary save/load.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        private static async Task Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            await NextFrame();
            var manager = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            var response = manager.GetComponent<ColorSelectionResponse>();
            var selector = manager.GetComponent<ClickSelector>();
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
            await CheckDefaults(response, duplicator);
            var color = new Color(.21f, .73f, .48f, 1);
            ColorToolActiveCheck.colorToolActive = true;
            ColorTool.ColorToSet = color;

            // Start with the previously supported plate so missing undo is
            // demonstrated independently of the broader material lookup bug.
            var plate = PartsManager.GeneratePart("PLTE-3-3", new Vector3(200, 200, 200), Quaternion.identity);
            var original = Materials(plate).Select(m => m.color).ToArray();
            var before = SceneBuild.ToBuildData(new[] { plate });
            manager.SetCurrent(new ObjectSelection { gameObject = plate, selector = selector, individual = true }, true);
            Require(Materials(plate).All(m => SameRGB(m.color, color)), "Click selection did not paint the plate.");
            int paintedState = StateSystem.curIndex;
            ColorTool.ColorToSet = Color.yellow;
            StateSystem.instance.Undo();
            Require(Materials(plate).Select(m => m.color).SequenceEqual(original), "Undo did not restore the exact pre-paint colors.");
            Require(before.CompareData(SceneBuild.ToBuildData(new[] { plate })), "Undo did not restore the saved appearance/dirty state.");
            StateSystem.instance.Redo();
            Require(StateSystem.curIndex == paintedState && Materials(plate).All(m => SameRGB(m.color, color)),
                "Redo or selection restoration repainted the part using the current brush color.");
            Require(!before.CompareData(SceneBuild.ToBuildData(new[] { plate })), "Painting a catalog part did not mark the build dirty.");
            ColorTool.ColorToSet = color;
            response.ChangeColor(plate);
            Require(StateSystem.curIndex == paintedState, "Painting an unchanged color created an empty undo operation.");

            var parts = new List<GameObject> { plate };
            foreach (var type in PartsManager.partTypes.Where(t => t.id != "Error")) {
                var generator = type.GetComponent<PartGenerator>();
                var part = generator.Generate(new Vector3(200 + parts.Count * 12, 200, 200), Quaternion.identity);
                parts.Add(part);
                var materials = Materials(part);
                Require(materials.Length > 0, "Missing body materials for " + type.name);
                var old = materials.Select(m => m.color).ToArray();
                var assets = materials.Select(m => (material: m, color: m.color, texture: m.mainTexture, shader: m.shader)).ToArray();
                int state = StateSystem.curIndex;
                response.ChangeColor(part);
                materials = Materials(part);
                Require(materials.All(m => SameRGB(m.color, color)), "Painting omitted body material slots on " + type.name);
                Require(materials.Select(m => m.color.a).SequenceEqual(old.Select(c => c.a)), "Painting changed transparency on " + type.name);
                Require(StateSystem.curIndex == state + 1, "Painting did not make exactly one undo operation for " + type.name);
                StateSystem.instance.Undo();
                Require(Materials(part).Select(m => m.color).SequenceEqual(old), "Undo flattened the original material colors on " + type.name);
                StateSystem.instance.Redo();
                Require(Materials(part).All(m => SameRGB(m.color, color)), "Redo missed body geometry on " + type.name);
                foreach (var asset in assets.Where(a => EditorUtility.IsPersistent(a.material)))
                    Require(asset.material.color == asset.color && asset.material.mainTexture == asset.texture && asset.material.shader == asset.shader,
                        "Painting changed shared catalog material/texture data on " + type.name);
            }

            var custom = CustomModel.Create(CustomModelImporter.Parse(Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3"), ".obj", "Paint model", 1), new Vector3(150, 200, 200), Quaternion.identity);
            var mount = custom.GetComponent<CustomModel>().AddMount(custom.transform.position, Vector3.forward, .169f, .1f, true);
            var rings = mount.GetComponentsInChildren<MeshRenderer>().Select(r => (renderer: r, material: r.sharedMaterial, color: r.sharedMaterial.color)).ToArray();
            response.ChangeColor(custom);
            Require(Materials(custom).All(m => SameRGB(m.color, color)), "Custom model body was not painted.");
            Require(rings.All(r => r.renderer.sharedMaterial == r.material && r.material.color == r.color), "Painting changed mounting marker materials.");
            parts.Add(custom);

            // A collider can be several levels below a renderer-less saved root.
            var nested = new GameObject("Nested paint test", typeof(SavedObject));
            nested.GetComponent<SavedObject>().id = "PLTE-3-3";
            var middle = new GameObject("Nested geometry"); middle.transform.SetParent(nested.transform, false);
            var child = GameObject.CreatePrimitive(PrimitiveType.Cube); child.transform.SetParent(middle.transform, false);
            var childRenderer = child.GetComponent<Renderer>();
            childRenderer.sharedMaterials = new[] {
                AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Black Plastic.mat"),
                AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Red Plastic.mat")
            };
            response.ChangeColor(child);
            Require(Materials(nested).All(m => SameRGB(m.color, color)), "Deep child hits or multiple material slots were not painted.");
            Require(PartColors.SampleMaterial(child) == childRenderer.sharedMaterial &&
                PartColors.SampleMaterial(mount.gameObject) == custom.GetComponent<Renderer>().sharedMaterial,
                "The eyedropper did not resolve nested surfaces or mounting-point bodies.");
            Object.Destroy(nested);
            await NextFrame();

            // Exercise the authored box selector, including its final response
            // dispatch, rather than issuing one paint call per enclosed object.
            var box = manager.GetComponent<BoxSelector>();
            var originalCamera = Get<Camera>(box, "cam");
            var boxCamera = new GameObject("Paint box camera", typeof(Camera)).GetComponent<Camera>();
            boxCamera.enabled = false; boxCamera.transform.position = new Vector3(0, 0, -10);
            var boxParts = new[] {
                PartsManager.GeneratePart("PLTE-3-3", new Vector3(0, 0, 5), Quaternion.identity),
                PartsManager.GeneratePart("CCHL-1x2-5", new Vector3(3, 0, 5), Quaternion.identity)
            };
            var boxColors = boxParts.Select(p => Materials(p).Select(m => m.color).ToArray()).ToArray();
            try {
                typeof(BoxSelector).GetField("cam", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(box, boxCamera);
                var start = (Vector2)boxCamera.WorldToScreenPoint(new Vector3(-3, -3, 5));
                var end = (Vector2)boxCamera.WorldToScreenPoint(new Vector3(7, 3, 5));
                typeof(BoxSelector).GetMethod("SetBoxSelection", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(box, new object[] { start, end });
                Require(manager.current is MultiSelection boxSelection && boxSelection.objs.Count == 2 &&
                    boxParts.All(p => Materials(p).All(m => SameRGB(m.color, color))), "Box selection did not paint every enclosed part.");
                StateSystem.instance.Undo();
                for (int i = 0; i < boxParts.Length; i++)
                    Require(Materials(boxParts[i]).Select(m => m.color).SequenceEqual(boxColors[i]), "Box painting did not undo as one operation.");
                StateSystem.instance.Redo();
                Require(boxParts.All(p => Materials(p).All(m => SameRGB(m.color, color))), "Box paint redo missed a member.");
            }
            finally {
                manager.ClearCurrent(false, true, true);
                typeof(BoxSelector).GetField("cam", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(box, originalCamera);
                foreach (var part in boxParts) Object.Destroy(part);
                Object.Destroy(boxCamera.gameObject);
            }

            // Hole routing must paint the body exactly once, after final selection.
            var hole = plate.GetComponentInChildren<HoleCollider>();
            var holeSelector = manager.GetComponent<HoleFaceResponseSelector>();
            Get<HoleFace>(holeSelector, "hoverHoleFace").Set(hole.holeData, hole.transform.forward);
            ColorTool.ColorToSet = Color.cyan;
            manager.SetCurrent(new ObjectSelection { gameObject = hole.gameObject, selector = selector, individual = true }, true);
            Require(manager.current is HoleSelection && Materials(plate).All(m => SameRGB(m.color, Color.cyan)), "Hole clicks did not paint the owning body.");
            StateSystem.instance.Undo();
            Require(Materials(plate).All(m => SameRGB(m.color, color)), "Hole painting required multiple undos or missed the body.");
            StateSystem.instance.Redo();

            ColorTool.ColorToSet = Color.magenta;
            var multi = new MultiSelection(selector, new List<GameObject> { plate, custom }) { individual = true };
            manager.SetCurrent(multi, true, true);
            int multiState = StateSystem.curIndex;
            StateSystem.instance.Undo();
            Require(Materials(plate).All(m => SameRGB(m.color, Color.cyan)) && Materials(custom).All(m => SameRGB(m.color, color)), "A single undo did not restore every painted selection member.");
            StateSystem.instance.Redo();
            Require(StateSystem.curIndex == multiState && new[] { plate, custom }.All(p => Materials(p).All(m => SameRGB(m.color, Color.magenta))), "Redo did not restore the painted set.");

            var wheel = parts.First(p => p.GetComponent<SavedObject>().id.StartsWith("OMNI"));
            var wheelGenerator = PartsManager.GetPartType(wheel.GetComponent<SavedObject>().id).GetComponent<PartGenerator>();
            var replacement = PartParameterEditing.Replace(wheel, wheelGenerator);
            Require(Materials(replacement).All(m => SameRGB(m.color, color)), "Parameter regeneration lost painted child colors.");
            Object.Destroy(replacement);
            var copies = duplicator.CreateCopies(new[] { wheel });
            ColorTool.ColorToSet = Color.blue; response.ChangeColor(copies[0]);
            Require(Materials(wheel).All(m => SameRGB(m.color, color)), "Painting a duplicate changed its source.");
            ObjectDuplicator.DiscardCopies(copies);
            await NextFrame();

            manager.ClearCurrent(false, true, true);
            var plateRenderer = plate.GetComponent<Renderer>();
            var visibleColor = SceneBuild.ToBuildData(new[] { plate }).parts[0].GetColor();
            plateRenderer.enabled = false;
            Require(SceneBuild.ToBuildData(new[] { plate }).parts[0].GetColor() == visibleColor,
                "Temporarily hiding a Move preview's source renderer changed the saved paint color.");
            plateRenderer.enabled = true;
            var snapshot = SceneBuild.ToBuildData(parts);
            var json = BuildTransfer.FromClipboard(BuildTransfer.ToClipboard(snapshot));
            using var stream = new MemoryStream();
            new BinaryFormatter().Serialize(stream, json); stream.Position = 0;
            var restored = (BuildData)new BinaryFormatter().Deserialize(stream);
            SceneBuild.GenerateBuild(restored);
            Require(snapshot.CompareData(SceneBuild.ToBuildData()), "Painted parts did not round-trip through JSON and binary build files.");
            foreach (var part in PartsManager.FindLoadedObjects()) {
                var saved = restored.parts.Single(p => p.connectionId == part.GetComponent<SavedObject>().ConnectionId);
                Require(Materials(part).All(m => SameRGB(m.color, saved.GetColor())), "Save/load lost painted material slots on " + saved.partId);
            }

            var legacy = SceneBuild.DefaultBuild;
            legacy.version = "1.0";
            legacy.parts = new[] { new ObjectData { partId = "PLTE-3-3" } };
            SceneBuild.GenerateBuild(legacy);
            Require(legacy.CompareData(SceneBuild.ToBuildData()), "A legacy build without RGB fields became dirty immediately after loading.");
            response.ChangeColor(PartsManager.FindLoadedObjects().Single());
            Require(!legacy.CompareData(SceneBuild.ToBuildData()), "Painting a legacy build did not mark it dirty.");
            StateSystem.instance.Undo();
            Require(legacy.CompareData(SceneBuild.ToBuildData()), "Undo did not restore the legacy build's clean state.");
        }

        private static async Task CheckDefaults(ColorSelectionResponse response, ObjectDuplicator duplicator) {
            var tool = Object.FindFirstObjectByType<ColorTool>(FindObjectsInactive.Include);
            var dropdown = Object.FindObjectsByType<UnityEngine.UI.Dropdown>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Single(d => d.options.Count > 0 && d.options[0].text == "Default");
            var panel = dropdown.transform.parent.gameObject;
            bool wasActive = panel.activeSelf;
            dropdown.SetValueWithoutNotify(0);
            tool.EnableUI(panel);
            Require(ColorTool.UseDefaultColor && !ColorTool.CustomColor, "Opening Default brush let slider callbacks replace it with a white tint.");
            panel.SetActive(wasActive);
            ColorToolActiveCheck.colorToolActive = true;

            var parts = new List<GameObject>();
            var defaults = new Dictionary<string, Color[]>();
            var tint = new Color(.31f, .67f, .19f, 1);
            foreach (var type in PartsManager.partTypes.Where(t => t.id != "Error"))
                parts.Add(type.GetComponent<PartGenerator>().Generate(new Vector3(parts.Count * 12, 200, 200), Quaternion.identity));
            var custom = CustomModel.Create(CustomModelImporter.Parse(Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3"), ".obj", "Default model", 1), new Vector3(-12, 200, 200), Quaternion.identity);
            var mount = custom.GetComponent<CustomModel>().AddMount(custom.transform.position, Vector3.forward, .169f, .1f, true);
            var markers = mount.GetComponentsInChildren<MeshRenderer>().Select(r => (material: r.sharedMaterial, color: r.sharedMaterial.color)).ToArray();
            parts.Add(custom);
            foreach (var part in parts) {
                var original = Materials(part).Select(m => m.color).ToArray();
                defaults.Add(part.GetComponent<SavedObject>().ConnectionId, original);
                var clean = SceneBuild.ToBuildData(new[] { part });
                ColorTool.ColorToSet = tint; response.ChangeColor(part);
                tool.HandleInput(0); response.ChangeColor(part);
                Require(Materials(part).Select(m => m.color).SequenceEqual(original) && !part.GetComponent<SavedObject>().hasPaint,
                    "Default painted white or flattened the original material colors on " + part.name);
                Require(clean.CompareData(SceneBuild.ToBuildData(new[] { part })), "Default did not restore the original saved appearance.");
                int state = StateSystem.curIndex;
                response.ChangeColor(part);
                Require(state == StateSystem.curIndex, "Default on an unchanged part created an empty undo operation.");
                StateSystem.instance.Undo();
                Require(Materials(part).All(m => SameRGB(m.color, tint)) && part.GetComponent<SavedObject>().hasPaint,
                    "Undo Default did not restore the paint and its metadata on " + part.name);
                StateSystem.instance.Redo();
                Require(Materials(part).Select(m => m.color).SequenceEqual(original), "Redo Default lost material colors on " + part.name);

                ColorTool.ColorToSet = tint; response.ChangeColor(part);
                var copies = duplicator.CreateCopies(new[] { part });
                tool.HandleInput(0); response.ChangeColor(copies[0]);
                Require(Materials(copies[0]).Select(m => m.color).SequenceEqual(original), "A painted copy forgot the catalog defaults on " + part.name);
                Require(Materials(part).All(m => SameRGB(m.color, tint)), "Resetting a copy changed its source.");
                ObjectDuplicator.DiscardCopies(copies);
            }
            Require(markers.All(m => m.material.color == m.color), "Default restoration changed custom mounting markers.");
            await NextFrame();

            // Loading paints freshly generated defaults. Reset must retain that
            // palette through JSON/binary transfers, rather than save a flat tint.
            var painted = BuildTransfer.FromClipboard(BuildTransfer.ToClipboard(SceneBuild.ToBuildData(parts)));
            using var memory = new MemoryStream();
            var formatter = new BinaryFormatter(); formatter.Serialize(memory, painted); memory.Position = 0;
            SceneBuild.GenerateBuild((BuildData)formatter.Deserialize(memory));
            foreach (var part in PartsManager.FindLoadedObjects()) {
                tool.HandleInput(0); response.ChangeColor(part);
                Require(Materials(part).Select(m => m.color).SequenceEqual(defaults[part.GetComponent<SavedObject>().ConnectionId]),
                    "A loaded painted part forgot its original defaults on " + part.name);
            }
            var reset = SceneBuild.ToBuildData();
            SceneBuild.GenerateBuild(BuildTransfer.FromClipboard(BuildTransfer.ToClipboard(reset)));
            foreach (var part in PartsManager.FindLoadedObjects())
                Require(Materials(part).Select(m => m.color).SequenceEqual(defaults[part.GetComponent<SavedObject>().ConnectionId]),
                    "Saving a reset part flattened its default material colors on " + part.name + ": actual=" +
                    string.Join(";", Materials(part).Select(m => m.color.ToString("F6"))) + " expected=" +
                    string.Join(";", defaults[part.GetComponent<SavedObject>().ConnectionId].Select(c => c.ToString("F6"))));

            var variant = PartsManager.partTypes.First(t => t.GetComponent<ChildPartGenerator>() != null &&
                t.GetComponent<PartGenerator>().UsesParams && t.GetComponent<PartGenerator>().GetParam1Options().Count > 1);
            var generator = variant.GetComponent<PartGenerator>();
            var originalPart = PartsManager.FindLoadedObjects().First(p => PartsManager.GetPartType(p.GetComponent<SavedObject>().id) == variant);
            PartParameterEditing.Load(generator, originalPart.GetComponent<SavedObject>().id);
            Require(PartParameterEditing.Step(generator, 0, 1), "Could not step the default-color variant fixture.");
            var expected = generator.Generate(new Vector3(0, 300, 200), Quaternion.identity);
            var replacement = PartParameterEditing.Replace(originalPart, generator);
            Require(Materials(replacement).Select(m => m.color).SequenceEqual(Materials(expected).Select(m => m.color)),
                "Changing parameters after Default retained the old variant's tint.");
            Object.Destroy(expected); Object.Destroy(replacement);

            // Older color-capable builds have RGB but no whole-body paint flag.
            var legacy = SceneBuild.DefaultBuild;
            legacy.parts = new[] { new ObjectData { partId = "SPKT-Normal-10T", rColor = tint.r, gColor = tint.g, bColor = tint.b } };
            SceneBuild.GenerateBuild(legacy);
            await NextFrame();
            var legacyPart = PartsManager.FindLoadedObjects().Single();
            Require(legacy.CompareData(SceneBuild.ToBuildData()), "Loading an older tinted build marked it dirty immediately.");
            tool.HandleInput(0); response.ChangeColor(legacyPart);
            Require(!legacy.CompareData(SceneBuild.ToBuildData()), "Default restoration on an older tinted part did not mark the build dirty.");
            StateSystem.instance.Undo();
            Require(legacy.CompareData(SceneBuild.ToBuildData()), "Undo Default did not restore an older build's clean state.");
            StateSystem.instance.Redo();
            Require(!legacy.CompareData(SceneBuild.ToBuildData()), "Redo Default did not restore an older build's dirty state.");
            SceneBuild.GenerateBuild(SceneBuild.DefaultBuild);
            await NextFrame();
            ColorToolActiveCheck.colorToolActive = true;
        }

        private static Material[] Materials(GameObject part) => MeshCombiner.GetRenderers(new[] { part })
            .SelectMany(r => r.sharedMaterials).Where(m => m != null && (m.HasProperty("_BaseColor") || m.HasProperty("_Color"))).ToArray();
        private static bool SameRGB(Color a, Color b) => Mathf.Abs(a.r - b.r) < .0001f && Mathf.Abs(a.g - b.g) < .0001f && Mathf.Abs(a.b - b.b) < .0001f;
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
