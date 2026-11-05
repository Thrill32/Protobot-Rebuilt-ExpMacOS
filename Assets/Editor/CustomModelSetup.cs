using System.Linq;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class CustomModelSetup {
        internal static readonly (string name, string path, string field)[] Bindings = {
            ("Import Custom Model", "<Keyboard>/i", "importInput"),
            ("Add Custom Mounting Points", "<Keyboard>/m", "mountInput"),
            ("Change Mounting Depth", "<Keyboard>/b", "depthInput"),
            ("Recalibrate Mounting Grid", "<Keyboard>/n", "recalibrateInput")
        };

        [MenuItem("Tools/Protobot/Configure Custom Models")]
        public static void Configure() {
            const string keyPath = "Assets/Prefabs/Keybindings.prefab";
            var keys = PrefabUtility.LoadPrefabContents(keyPath);
            try {
                var group = keys.GetComponentsInChildren<Transform>(true).Single(t => t.name == "General");
                foreach (var binding in Bindings) {
                    if (group.Cast<Transform>().Any(t => t.name == binding.name)) continue;
                    var obj = new GameObject(binding.name); obj.transform.SetParent(group, false);
                    obj.AddComponent<InputEvent>().defaultAction = new InputAction("Default", InputActionType.Button, binding.path);
                }
                PrefabUtility.SaveAsPrefabAsset(keys, keyPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(keys); }

            if (!AssetDatabase.IsValidFolder("Assets/Resources/Custom Models")) AssetDatabase.CreateFolder("Assets/Resources", "Custom Models");
            CreateMaterial("Model", "Universal Render Pipeline/Lit", new Color(.65f, .68f, .72f));
            CreateMaterial("Mount", "Universal Render Pipeline/Unlit", new Color(.1f, .85f, 1));

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var placement = Object.FindFirstObjectByType<Placement>();
            var tool = placement.GetComponent<CustomModelTool>() ?? placement.gameObject.AddComponent<CustomModelTool>();
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var serialized = new SerializedObject(tool);
            void Set(string field, Object value) => serialized.FindProperty(field).objectReferenceValue = value;
            foreach (var binding in Bindings) Set(binding.field, inputs.Single(i => i.name == binding.name));
            Set("selectionManager", GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>());
            Set("placement", placement);
            Set("refCamera", Object.FindFirstObjectByType<MouseCast>().GetComponent<Camera>());
            Set("orbitInput", inputs.Single(i => i.name == "Orbit"));
            Set("panInput", inputs.Single(i => i.name == "Pan"));
            Set("noclipInput", inputs.Single(i => i.name == "Noclip Camera"));
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var template = rows.First(r => GetInput(r)?.name == "Pick Part");
            foreach (var binding in Bindings) {
                var input = inputs.Single(i => i.name == binding.name);
                if (rows.Any(r => GetInput(r) == input)) continue;
                var obj = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Shortcut Container.prefab"), template.transform.parent);
                obj.name = binding.name + " Shortcut";
                var row = obj.GetComponentInChildren<RebindUI>(true);
                var rowData = new SerializedObject(row); rowData.FindProperty("inputEvent").objectReferenceValue = input;
                rowData.ApplyModifiedPropertiesWithoutUndo(); PrefabUtility.RecordPrefabInstancePropertyModifications(row);
            }
            InteractionUpdatesSetup.LayoutSelectionShortcuts(template.transform.parent);

            var menu = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(t => t.name == "File Menu");
            if (menu.Find("Import Custom Model Button") == null) {
                var buttonTemplate = menu.GetComponentsInChildren<Button>(true).First();
                var obj = Object.Instantiate(buttonTemplate.gameObject, menu);
                obj.name = "Import Custom Model Button";
                var button = obj.GetComponent<Button>(); button.onClick = new Button.ButtonClickedEvent();
                UnityEventTools.AddPersistentListener(button.onClick, tool.OpenImportSettings);
                foreach (var label in obj.GetComponentsInChildren<Text>(true)) label.text = "Import Model… (I)";
                foreach (var label in obj.GetComponentsInChildren<TMPro.TMP_Text>(true)) label.text = "Import Model… (I)";
            }
            menu.sizeDelta = new Vector2(Mathf.Max(menu.sizeDelta.x, 210), Mathf.Max(menu.sizeDelta.y, 140));
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            Debug.Log("Custom model import, repeated mounting points, File menu and Preferences configured.");
        }

        private static void CreateMaterial(string name, string shader, Color color) {
            string path = "Assets/Resources/Custom Models/" + name + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            var material = new Material(Shader.Find(shader)) { name = name, color = color };
            if (name == "Model") { material.SetFloat("_Metallic", .754f); material.SetFloat("_Smoothness", .3f); }
            AssetDatabase.CreateAsset(material, path);
        }

        private static InputEvent GetInput(RebindUI row) => new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue as InputEvent;
    }
}
