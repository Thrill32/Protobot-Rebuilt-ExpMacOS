using System.Linq;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class BuildTransferSetup {
        internal static readonly (string name, string key, string field)[] Bindings = {
            ("Copy Selection", "c", "copyInput"), ("Paste Selection", "v", "pasteInput"),
            ("Save Selected Parts As", null, "exportInput"), ("Import Build", null, "importInput")
        };

        [MenuItem("Tools/Protobot/Configure Build Transfers")]
        public static void Configure() {
            const string path = "Assets/Prefabs/Keybindings.prefab";
            var keys = PrefabUtility.LoadPrefabContents(path);
            try {
                var group = keys.GetComponentsInChildren<Transform>(true).Single(t => t.name == "General");
                foreach (var binding in Bindings) {
                    if (group.Cast<Transform>().Any(t => t.name == binding.name)) continue;
                    var obj = new GameObject(binding.name); obj.transform.SetParent(group, false);
                    var action = new InputAction("Default", InputActionType.Button);
                    if (binding.key != null)
                        foreach (string side in new[] { "leftMeta", "rightMeta" })
                            action.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/" + side).With("Button", "<Keyboard>/" + binding.key);
                    obj.AddComponent<InputEvent>().defaultAction = action;
                }
                PrefabUtility.SaveAsPrefabAsset(keys, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(keys); }

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var placement = Object.FindFirstObjectByType<Placement>();
            var tool = placement.GetComponent<BuildTransferTool>() ?? placement.gameObject.AddComponent<BuildTransferTool>();
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var data = new SerializedObject(tool);
            void Set(string field, Object value) => data.FindProperty(field).objectReferenceValue = value;
            Set("buildsManager", Object.FindFirstObjectByType<BuildsManager>(FindObjectsInactive.Include));
            Set("selectionManager", GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>());
            Set("placement", placement); Set("duplicator", Object.FindFirstObjectByType<ObjectDuplicator>());
            foreach (var binding in Bindings) Set(binding.field, inputs.Single(i => i.name == binding.name));
            data.ApplyModifiedPropertiesWithoutUndo();

            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var template = rows.First(row => GetInput(row)?.name == "Pick Part");
            var menu = ConfigurePreferencesMenu(template.transform.parent);
            int index = 0;
            foreach (var binding in Bindings) {
                var input = inputs.Single(i => i.name == binding.name);
                var row = rows.FirstOrDefault(row => GetInput(row) == input);
                if (row == null) {
                    var obj = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Shortcut Container.prefab"), menu);
                    obj.name = binding.name + " Shortcut";
                    row = obj.GetComponentInChildren<RebindUI>(true);
                    var serialized = new SerializedObject(row); serialized.FindProperty("inputEvent").objectReferenceValue = input;
                    serialized.ApplyModifiedPropertiesWithoutUndo(); PrefabUtility.RecordPrefabInstancePropertyModifications(row);
                }
                row.transform.SetParent(menu, false);
                var rect = (RectTransform)row.transform;
                rect.anchorMin = Vector2.up; rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f, 1);
                rect.sizeDelta = new Vector2(-20, 30); rect.anchoredPosition = new Vector2(0, -40 - index++ * 35);
                PrefabUtility.RecordPrefabInstancePropertyModifications(rect);
            }
            InteractionUpdatesSetup.LayoutSelectionShortcuts(template.transform.parent);
            var preferences = (RectTransform)template.transform.parent.parent.parent;
            int selectionRows = template.transform.parent.GetComponentsInChildren<RebindUI>(true).Length;
            if (preferences.sizeDelta.y <= 60 + 35 * (selectionRows + Bindings.Length))
                preferences.sizeDelta = new Vector2(preferences.sizeDelta.x, Mathf.Max(620, 60 + 35 * selectionRows));
            var rects = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var file = rects.Single(t => t.name == "File Menu");
            var edit = rects.Single(t => t.name == "Edit Menu");
            var buttonTemplate = file.GetComponentsInChildren<Button>(true).First();
            AddMenuButton(file, buttonTemplate, "Save Selected Parts As Button", "Save Selected Parts As…", tool.SaveSelectedAs);
            AddMenuButton(file, buttonTemplate, "Import Build Button", "Import Build…", tool.ImportBuild);
            AddMenuButton(edit, buttonTemplate, "Copy Selection Button", "Copy Selected Parts (Cmd+C)", tool.CopySelected);
            AddMenuButton(edit, buttonTemplate, "Paste Selection Button", "Paste Parts (Cmd+V)", tool.Paste);
            file.sizeDelta = new Vector2(250, 180);
            edit.sizeDelta = new Vector2(250, 60);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            Debug.Log("Build transfers configured: selection Save As, placeable build import, cross-project clipboard, menus and Preferences.");
        }

        private static Transform ConfigurePreferencesMenu(Transform selection) {
            var menu = selection.parent.Find("Files Prefs Menu");
            if (menu == null) {
                var rect = new GameObject("Files Prefs Menu", typeof(RectTransform)).GetComponent<RectTransform>();
                rect.SetParent(selection.parent, false); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero; menu = rect;
                var all = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var categoryTemplate = all.Single(t => t.name == "Selection Toggle");
                var category = Object.Instantiate(categoryTemplate.gameObject, categoryTemplate.parent); category.name = "Files Toggle";
                var last = all.Single(t => t.name == "Simulation Toggle");
                ((RectTransform)category.transform).anchoredPosition = last.anchoredPosition + Vector2.down * 35;
                var toggle = category.GetComponent<Toggle>();
                for (int i = toggle.onValueChanged.GetPersistentEventCount() - 1; i >= 0; i--) UnityEventTools.RemovePersistentListener(toggle.onValueChanged, i);
                UnityEventTools.AddPersistentListener<bool>(toggle.onValueChanged, menu.gameObject.SetActive);
                toggle.SetIsOnWithoutNotify(false);
                foreach (var text in category.GetComponentsInChildren<Text>(true)) text.text = "Files / Clipboard";
                foreach (var text in category.GetComponentsInChildren<TMP_Text>(true)) text.text = "Files / Clipboard";
                var title = Object.Instantiate(selection.Find("Keybinds Title").gameObject, menu);
                foreach (var text in title.GetComponentsInChildren<Text>(true)) text.text = "Files and clipboard";
                foreach (var text in title.GetComponentsInChildren<TMP_Text>(true)) text.text = "Files and clipboard";
            }
            menu.gameObject.SetActive(false);
            return menu;
        }

        private static void AddMenuButton(Transform menu, Button template, string name, string label, UnityEngine.Events.UnityAction action) {
            var existing = menu.Find(name);
            var obj = existing != null ? existing.gameObject : Object.Instantiate(template.gameObject, menu); obj.name = name;
            var button = obj.GetComponent<Button>(); button.onClick = new Button.ButtonClickedEvent();
            UnityEventTools.AddPersistentListener(button.onClick, action);
            foreach (var text in obj.GetComponentsInChildren<Text>(true)) text.text = label;
            foreach (var text in obj.GetComponentsInChildren<TMP_Text>(true)) text.text = label;
        }
        private static InputEvent GetInput(RebindUI row) => new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue as InputEvent;
    }
}
