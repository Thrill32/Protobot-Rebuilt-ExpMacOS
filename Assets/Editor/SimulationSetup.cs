using System;
using System.Linq;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.Tools;
using Protobot.UI;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class SimulationSetup {
        internal static readonly (string name, string path, string field)[] Bindings = {
            ("Enable Simulation Tool", "<Keyboard>/5", "activateInput"),
            ("Simulation Animation", null, "animationInput"),
            ("Simulation Play / Pause", null, "playInput"),
            ("Simulation Reset", null, "resetInput"),
            ("Simulation Faster", null, "fasterInput"),
            ("Simulation Slower", null, "slowerInput"),
            ("Simulation Commit", null, "commitInput"),
            ("Simulation Cancel", null, "cancelInput")
        };

        [MenuItem("Tools/Protobot/Configure Simulation")]
        public static void Configure() {
            const string keyPath = "Assets/Prefabs/Keybindings.prefab";
            var keys = PrefabUtility.LoadPrefabContents(keyPath);
            try {
                var group = keys.GetComponentsInChildren<Transform>(true).Single(t => t.name == "General");
                foreach (var binding in Bindings) {
                    // Binding display names can contain '/', which Transform.Find treats as a path.
                    var existing = group.Cast<Transform>().Where(t => t.name == binding.name).ToArray();
                    foreach (var duplicate in existing.Skip(1)) Object.DestroyImmediate(duplicate.gameObject);
                    if (existing.Length > 0) continue;
                    var obj = new GameObject(binding.name); obj.transform.SetParent(group, false);
                    obj.AddComponent<InputEvent>().defaultAction = new InputAction("Default", InputActionType.Button, binding.path);
                }
                PrefabUtility.SaveAsPrefabAsset(keys, keyPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(keys); }

            const string gearPath = "Assets/Resources/Part Prefabs/4Motion/Gears and Sprockets/Gear.prefab";
            var gears = PrefabUtility.LoadPrefabContents(gearPath);
            try {
                foreach (var data in gears.GetComponentsInChildren<PartData>(true)) {
                    var metadata = data.GetComponent<SimulationPart>() ?? data.gameObject.AddComponent<SimulationPart>();
                    metadata.Configure(data);
                }
                PrefabUtility.SaveAsPrefabAsset(gears, gearPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(gears); }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var placement = Object.FindFirstObjectByType<Placement>();
            var cast = Object.FindFirstObjectByType<MouseCast>();
            var connect = Object.FindFirstObjectByType<ConnectTool>();
            var tool = Object.FindFirstObjectByType<SimulationTool>();
            if (tool == null) {
                var obj = Object.Instantiate(connect.gameObject, connect.transform.parent);
                obj.name = "Simulation Tool";
                Object.DestroyImmediate(obj.GetComponent<ConnectTool>());
                tool = obj.AddComponent<SimulationTool>();
                var rect = (RectTransform)obj.transform;
                rect.anchoredPosition = ((RectTransform)connect.transform).anchoredPosition + new Vector2(0, -45);
                obj.GetComponent<Tooltip>().text = "Simulation (5) · press again for animation";
                foreach (var image in obj.GetComponentsInChildren<Image>(true))
                    if (image.name.Contains("Icon")) image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Sprites/Tools Icons/settings-gears.png");
                foreach (var text in obj.GetComponentsInChildren<Text>(true)) if (text.text == "4") text.text = "5";
                foreach (var text in obj.GetComponentsInChildren<TMP_Text>(true)) if (text.text == "4") text.text = "5";
                obj.GetComponent<Toggle>().SetIsOnWithoutNotify(false);
            }
            var serialized = new SerializedObject(tool);
            void Set(string field, Object value) => serialized.FindProperty(field).objectReferenceValue = value;
            Set("toolbarToggle", tool.GetComponent<Toggle>());
            Set("selectionManager", GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>());
            Set("mouseCast", cast); Set("placement", placement); Set("refCamera", cast.GetComponent<Camera>());
            foreach (var binding in Bindings) Set(binding.field, inputs.Single(i => i.name == binding.name));
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var rule = placement.GetComponent<GearPlaceDisplacement>() ?? placement.gameObject.AddComponent<GearPlaceDisplacement>();
            var ruleData = new SerializedObject(rule); ruleData.FindProperty("mouseCast").objectReferenceValue = cast;
            ruleData.ApplyModifiedPropertiesWithoutUndo();
            var placeData = new SerializedObject(placement);
            var property = placeData.FindProperty("placePositions");
            var rules = Enumerable.Range(0, property.arraySize).Select(i => property.GetArrayElementAtIndex(i).objectReferenceValue as PlaceDisplacement)
                .Where(r => r != null && r != rule).ToList();
            rules.Insert(rules.FindIndex(r => r is PlanePlaceDisplacement), rule);
            property.arraySize = rules.Count;
            for (int i = 0; i < rules.Count; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = rules[i];
            placeData.ApplyModifiedPropertiesWithoutUndo();
            ConfigurePreferences(inputs);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            Debug.Log("Simulation configured: tool 5, unbound playback actions, Preferences category, gear metadata and shared gear snapping.");
        }

        private static void ConfigurePreferences(InputEvent[] inputs) {
            var all = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var selection = all.Single(t => t.name == "Selection Prefs Menu");
            var menu = selection.parent.Find("Simulation Prefs Menu") as RectTransform;
            if (menu == null) {
                menu = new GameObject("Simulation Prefs Menu", typeof(RectTransform)).GetComponent<RectTransform>();
                menu.SetParent(selection.parent, false); menu.anchorMin = Vector2.zero; menu.anchorMax = Vector2.one;
                menu.offsetMin = menu.offsetMax = Vector2.zero;
                var template = all.Single(t => t.name == "Selection Toggle");
                var category = Object.Instantiate(template.gameObject, template.parent);
                category.name = "Simulation Toggle";
                ((RectTransform)category.transform).anchoredPosition = template.anchoredPosition + new Vector2(0, -35);
                var toggle = category.GetComponent<Toggle>();
                for (int i = toggle.onValueChanged.GetPersistentEventCount() - 1; i >= 0; i--)
                    UnityEventTools.RemovePersistentListener(toggle.onValueChanged, i);
                UnityEventTools.AddPersistentListener<bool>(toggle.onValueChanged, menu.gameObject.SetActive);
                toggle.SetIsOnWithoutNotify(false);
                foreach (var label in category.GetComponentsInChildren<Text>(true)) label.text = "Simulation";
                foreach (var label in category.GetComponentsInChildren<TMP_Text>(true)) label.text = "Simulation";
                var title = Object.Instantiate(selection.Find("Keybinds Title").gameObject, menu);
                foreach (var label in title.GetComponentsInChildren<Text>(true)) label.text = "Simulation controls";
            }
            var existingRows = menu.GetComponentsInChildren<RebindUI>(true);
            int index = 0;
            foreach (var binding in Bindings) {
                var input = inputs.Single(i => i.name == binding.name);
                var row = existingRows.FirstOrDefault(r => new SerializedObject(r).FindProperty("inputEvent").objectReferenceValue == input);
                if (row == null) {
                    var obj = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Shortcut Container.prefab"), menu);
                    obj.name = binding.name + " Shortcut";
                    row = obj.GetComponentInChildren<RebindUI>(true);
                    var data = new SerializedObject(row); data.FindProperty("inputEvent").objectReferenceValue = input; data.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(row);
                }
                var rect = (RectTransform)row.transform;
                rect.anchorMin = Vector2.up; rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f, 1);
                rect.sizeDelta = new Vector2(-20, 30); rect.anchoredPosition = new Vector2(0, -40 - index++ * 35);
                PrefabUtility.RecordPrefabInstancePropertyModifications(rect);
            }
            menu.gameObject.SetActive(false);
        }
    }
}
