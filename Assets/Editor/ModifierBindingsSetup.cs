using System;
using System.Linq;
using Protobot.InputEvents;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class ModifierBindingsSetup {
        private static readonly (string name, string[] paths, string label)[] Bindings = {
            ("Grouping Bypass", new[] { "<Keyboard>/leftMeta", "<Keyboard>/rightMeta" }, "Bypass Grouping"),
            ("Snap Bypass", new[] { "<Keyboard>/ctrl" }, "Bypass Snap"),
            ("Multi-Select Button", new[] { "<Keyboard>/ctrl" }, "Select Multiple")
        };

        [MenuItem("Tools/Protobot/Configure Separate Modifier Bindings")]
        public static void Configure() {
            const string path = "Assets/Prefabs/Keybindings.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try {
                var general = root.GetComponentsInChildren<Transform>(true).Single(child => child.name == "General");
                foreach (var binding in Bindings) {
                    var input = root.GetComponentsInChildren<InputEvent>(true).SingleOrDefault(candidate => candidate.name == binding.name);
                    if (input == null) {
                        var obj = new GameObject(binding.name);
                        obj.transform.SetParent(general, false);
                        input = obj.AddComponent<InputEvent>();
                    }
                    if (input.defaultAction == null || input.defaultAction.type != InputActionType.Button ||
                        !input.defaultAction.bindings.Select(item => item.path).SequenceEqual(binding.paths)) {
                        input.defaultAction = new InputAction("Default", InputActionType.Button);
                        foreach (var bindingPath in binding.paths) input.defaultAction.AddBinding(bindingPath);
                    }
                    var data = new SerializedObject(input);
                    data.FindProperty("allowModifierOnly").boolValue = true;
                    data.ApplyModifiedPropertiesWithoutUndo();
                    if (binding.name == "Grouping Bypass" && input.GetComponent<GroupingBypassInput>() == null)
                        input.gameObject.AddComponent<GroupingBypassInput>();
                }
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None).ToList();
            var duplicateRow = rows.Single(row => Input(row)?.name == "Duplicate");
            var modifierRows = new System.Collections.Generic.List<Transform>();
            foreach (var binding in Bindings) {
                var input = inputs.Single(candidate => candidate.name == binding.name);
                var row = rows.SingleOrDefault(candidate => Input(candidate) == input);
                if (row == null) {
                    var obj = (GameObject)PrefabUtility.InstantiatePrefab(
                        AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Shortcut Container.prefab"), duplicateRow.transform.parent);
                    obj.name = binding.name + " Shortcut";
                    row = obj.GetComponentInChildren<RebindUI>(true);
                    var data = new SerializedObject(row);
                    data.FindProperty("inputEvent").objectReferenceValue = input;
                    data.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(row);
                }
                var rowData = new SerializedObject(row);
                var label = (UnityEngine.UI.Text)rowData.FindProperty("actionNameText").objectReferenceValue;
                label.text = binding.label;
                row.rebindText.text = binding.name == "Grouping Bypass" ? "Cmd" : "Ctrl";
                PrefabUtility.RecordPrefabInstancePropertyModifications(label);
                PrefabUtility.RecordPrefabInstancePropertyModifications(row.rebindText);
                modifierRows.Add(row.transform);
            }
            var container = duplicateRow.transform.parent;
            var children = container.Cast<Transform>().Where(child => !modifierRows.Contains(child)).ToList();
            children.InsertRange(children.IndexOf(duplicateRow.transform) + 1, modifierRows);
            for (int i = 0; i < children.Count; i++) children[i].SetSiblingIndex(i);
            InteractionUpdatesSetup.LayoutSelectionShortcuts(container);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("Separate modifier bindings configured: Grouping=Command, Snap=Control, Multi-select=Control.");
        }

        public static void ConfigureAndValidate() {
            Configure();
            ModifierBindingsValidation.Validate();
            TransformEntryValidation.Validate();
        }

        private static InputEvent Input(RebindUI row) =>
            new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue as InputEvent;
    }
}
