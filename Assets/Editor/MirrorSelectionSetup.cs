using System.Linq;
using Protobot.InputEvents;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProtobotEditorTools {
    public static class MirrorSelectionSetup {
        [MenuItem("Tools/Protobot/Configure Mirror Selection")]
        public static void Configure() {
            const string prefabPath = "Assets/Prefabs/Keybindings.prefab";
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try {
                var general = root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "General");
                if (general.Find("Mirror Selection") == null) {
                    var binding = new GameObject("Mirror Selection");
                    binding.transform.SetParent(general, false);
                    binding.AddComponent<InputEvent>().defaultAction = new InputAction("Default", InputActionType.Button, "<Keyboard>/k");
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var tool = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var input = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Single(binding => binding.name == "Mirror Selection");
            SetReference(tool, "mirrorInput", input);

            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var container = rows.First(row => Binding(row)?.name == "Duplicate").transform.parent;
            if (!rows.Any(row => Binding(row) == input)) {
                var shortcut = (GameObject)PrefabUtility.InstantiatePrefab(
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Shortcut Container.prefab"), container);
                shortcut.name = "Mirror Selection Shortcut";
                SetReference(shortcut.GetComponentInChildren<RebindUI>(true), "inputEvent", input);
            }
            InteractionUpdatesSetup.LayoutSelectionShortcuts(container);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static Object Binding(RebindUI row) => new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue;

        private static void SetReference(Object component, string property, Object value) {
            var serialized = new SerializedObject(component);
            serialized.FindProperty(property).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            if (PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }
    }
}
