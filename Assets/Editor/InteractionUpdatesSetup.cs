using System;
using System.Linq;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProtobotEditorTools {
    public static class InteractionUpdatesSetup {
        private static readonly (string name, string path, string field)[] Bindings = {
            ("Pick Part", "<Keyboard>/p", "pickPartInput"),
            ("Increase First Property", "<Keyboard>/upArrow", "increaseFirstInput"),
            ("Decrease First Property", "<Keyboard>/downArrow", "decreaseFirstInput"),
            ("Increase Second Property", "<Keyboard>/rightArrow", "increaseSecondInput"),
            ("Decrease Second Property", "<Keyboard>/leftArrow", "decreaseSecondInput")
        };

        public static void ConfigureAndValidate() {
            Configure();
            InteractionUpdatesValidation.Validate();
            KeyboardTransformValidation.Validate();
        }

        [MenuItem("Tools/Protobot/Configure Interaction Updates")]
        public static void Configure() {
            const string prefabPath = "Assets/Prefabs/Keybindings.prefab";
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try {
                var group = root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "General");
                foreach (var binding in Bindings) {
                    var child = group.Find(binding.name);
                    if (child != null) continue;
                    var obj = new GameObject(binding.name);
                    obj.transform.SetParent(group, false);
                    var input = obj.AddComponent<InputEvent>();
                    input.defaultAction = new InputAction("Default", InputActionType.Button, binding.path);
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var properties = UnityEngine.Object.FindFirstObjectByType<AddPropertiesUI>(FindObjectsInactive.Include);
            // Keep shortcut handling alive when the add menu hides its view.
            var placement = UnityEngine.Object.FindFirstObjectByType<Placement>();
            if (properties.gameObject != placement.gameObject) {
                var previous = properties;
                UnityEditorInternal.ComponentUtility.CopyComponent(previous);
                UnityEditorInternal.ComponentUtility.PasteComponentAsNew(placement.gameObject);
                properties = placement.GetComponent<AddPropertiesUI>();
                foreach (var display in UnityEngine.Object.FindObjectsByType<ParamDisplay>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    var data = new SerializedObject(display);
                    var property = data.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue == previous)
                            property.objectReferenceValue = properties;
                    data.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(display);
                }
                UnityEngine.Object.DestroyImmediate(previous);
            }
            var serialized = new SerializedObject(properties);
            serialized.FindProperty("selectionManager").objectReferenceValue = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            var inputs = UnityEngine.Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var rows = UnityEngine.Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var template = rows.First(ui => GetInput(ui)?.name == "Duplicate");
            foreach (var binding in Bindings) {
                var input = inputs.Single(i => i.name == binding.name);
                serialized.FindProperty(binding.field).objectReferenceValue = input;
                if (rows.Any(ui => GetInput(ui) == input)) continue;
                var shortcut = (GameObject)PrefabUtility.InstantiatePrefab(
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Shortcut Container.prefab"), template.transform.parent);
                shortcut.name = binding.name + " Shortcut";
                var row = shortcut.GetComponentInChildren<RebindUI>(true);
                var data = new SerializedObject(row);
                data.FindProperty("inputEvent").objectReferenceValue = input;
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(row);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            LayoutSelectionShortcuts(template.transform.parent);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("Interaction updates configured.");
        }

        public static void RepairShortcutLayout() {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var row = UnityEngine.Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .First(ui => GetInput(ui)?.name == "Increase First Property");
            LayoutSelectionShortcuts(row.transform.parent);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        internal static void LayoutSelectionShortcuts(Transform container) {
            int index = 0;
            foreach (Transform child in container) {
                if (child.GetComponentInChildren<RebindUI>(true) == null) continue;
                var rect = (RectTransform)child;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(.5f, 1f);
                rect.sizeDelta = new Vector2(-20f, 30f);
                rect.anchoredPosition = new Vector2(0f, -40f - 35f * index++);
                PrefabUtility.RecordPrefabInstancePropertyModifications(rect);
            }
            var preferences = (RectTransform)container.parent.parent;
            preferences.sizeDelta = new Vector2(preferences.sizeDelta.x,
                Mathf.Max(preferences.sizeDelta.y, 40f + 35f * index + 20f));
        }

        private static InputEvent GetInput(RebindUI ui) =>
            new SerializedObject(ui).FindProperty("inputEvent").objectReferenceValue as InputEvent;
    }
}
