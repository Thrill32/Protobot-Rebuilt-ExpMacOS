using System.Linq;
using Protobot;
using Protobot.InputEvents;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProtobotEditorTools {
    public static class GrabHoleSnappingSetup {
        [MenuItem("Tools/Protobot/Configure Grab Hole Snapping")]
        public static void Configure() {
            const string prefabPath = "Assets/Prefabs/Keybindings.prefab";
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try {
                var general = root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "General");
                if (general.Find("Grab Hole Snap") == null) {
                    var obj = new GameObject("Grab Hole Snap");
                    obj.transform.SetParent(general, false);
                    obj.AddComponent<InputEvent>().defaultAction = new InputAction("Default", InputActionType.Button, "<Keyboard>/h");
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var controller = Object.FindFirstObjectByType<KeyboardTransformTool>();
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var holeSnap = inputs.Single(input => input.name == "Grab Hole Snap");
            var data = new SerializedObject(controller);
            data.FindProperty("holeSnapInput").objectReferenceValue = holeSnap;
            data.FindProperty("oppositeHoleInput").objectReferenceValue = inputs.Single(input => input.name == "Select Opposite Hole");
            data.FindProperty("mouseCast").objectReferenceValue = Object.FindFirstObjectByType<MouseCast>();
            data.FindProperty("holePreview").objectReferenceValue = new SerializedObject(
                Object.FindFirstObjectByType<Protobot.SelectionSystem.HoleFaceResponseSelector>())
                .FindProperty("hoverHoleFace").objectReferenceValue;
            data.FindProperty("moveSourceIcon").objectReferenceValue = Object.FindObjectsByType<UnityEngine.UI.Image>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Single(image => image.name == "Move Button");
            data.FindProperty("holeMoveMaterial").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Hole Move Preview.mat");
            data.FindProperty("orbitInput").objectReferenceValue = inputs.Single(input => input.name == "Orbit");
            data.FindProperty("panInput").objectReferenceValue = inputs.Single(input => input.name == "Pan");
            data.FindProperty("noclipInput").objectReferenceValue = inputs.Single(input => input.name == "Noclip Camera");
            data.ApplyModifiedPropertiesWithoutUndo();
            var move = new SerializedObject(controller.GetComponent<HolePlaceDisplacement>());
            move.FindProperty("oppositeHoleInput").objectReferenceValue = inputs.Single(input => input.name == "Select Opposite Hole");
            move.FindProperty("targetMarkerMaterial").objectReferenceValue = data.FindProperty("holeMoveMaterial").objectReferenceValue;
            move.ApplyModifiedPropertiesWithoutUndo();

            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var template = rows.First(row => Input(row)?.name == "Duplicate");
            if (!rows.Any(row => Input(row) == holeSnap)) {
                var shortcut = (GameObject)PrefabUtility.InstantiatePrefab(
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Shortcut Container.prefab"), template.transform.parent);
                shortcut.name = "Grab Hole Snap Shortcut";
                var row = shortcut.GetComponentInChildren<RebindUI>(true);
                var serialized = new SerializedObject(row);
                serialized.FindProperty("inputEvent").objectReferenceValue = holeSnap;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(row);
            }
            InteractionUpdatesSetup.LayoutSelectionShortcuts(template.transform.parent);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("Grab hole snapping configured: H binding, Preferences row, target casting and opposite-face input.");
        }

        private static InputEvent Input(RebindUI row) =>
            new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue as InputEvent;
    }
}
