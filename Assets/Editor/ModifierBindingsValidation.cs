using System;
using System.Linq;
using System.Reflection;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class ModifierBindingsValidation {
        [MenuItem("Tools/Protobot/Validate Separate Modifier Bindings")]
        public static void Validate() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var inputs = Object.FindObjectsByType<InputEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var group = inputs.Single(input => input.name == "Grouping Bypass");
            var snap = inputs.Single(input => input.name == "Snap Bypass");
            var multiple = inputs.Single(input => input.name == "Multi-Select Button");
            var expected = new[] {
                (group, new[] { "<Keyboard>/leftMeta", "<Keyboard>/rightMeta" }),
                (snap, new[] { "<Keyboard>/ctrl" }), (multiple, new[] { "<Keyboard>/ctrl" })
            };
            var rows = Object.FindObjectsByType<RebindUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var (input, paths) in expected) {
                Require(input.defaultAction.bindings.Select(binding => binding.path).SequenceEqual(paths),
                    "Incorrect default for " + input.name);
                Require(new SerializedObject(input).FindProperty("allowModifierOnly").boolValue,
                    "Modifier-only rebinding is disabled for " + input.name);
                Require(rows.Count(row => new SerializedObject(row).FindProperty("inputEvent").objectReferenceValue == input) == 1,
                    "Missing or duplicate Preferences row for " + input.name);
            }
            Require(group.GetComponent<GroupingBypassInput>() != null && snap.GetComponent<SnapBypassInput>() != null,
                "Modifier providers are missing from Main's bindings.");
            Require(new SerializedObject(Object.FindFirstObjectByType<MultiSelector>()).FindProperty("mutliSelectInput").objectReferenceValue == multiple,
                "Multi-selection does not use its own Preferences binding.");

            var manager = typeof(InputSystem).GetField("s_Manager", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var runInEdit = manager.GetType().GetProperty("runPlayerUpdatesInEditMode");
            var oldRunInEdit = runInEdit.GetValue(manager);
            var groupField = typeof(GroupingBypassInput).GetField("input", BindingFlags.NonPublic | BindingFlags.Static);
            var snapField = typeof(SnapBypassInput).GetField("input", BindingFlags.NonPublic | BindingFlags.Static);
            var instance = typeof(KeyboardTransformTool).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static);
            var frame = typeof(KeyboardTransformTool).GetField("finishedFrame", BindingFlags.NonPublic | BindingFlags.Static);
            var oldGroup = groupField.GetValue(null); var oldSnap = snapField.GetValue(null);
            var oldInstance = instance.GetValue(null); var oldFrame = frame.GetValue(null);
            bool oldSnapping = TransformSnapping.Enabled;
            var originals = expected.Select(item => (item.Item1, item.Item1.defaultAction, item.Item1.rebindAction)).ToArray();
            runInEdit.SetValue(manager, true);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try {
                instance.SetValue(null, null); frame.SetValue(null, -1);
                foreach (var (input, paths) in expected) {
                    input.defaultAction = new InputAction("Isolated modifier", InputActionType.Button);
                    foreach (var path in paths) input.defaultAction.AddBinding(path.Replace("<Keyboard>", keyboard.path));
                    input.defaultAction.Enable();
                    input.rebindAction = new RebindAction("Modifier validation " + Guid.NewGuid(), true);
                }
                groupField.SetValue(null, group); snapField.SetValue(null, snap);
                TransformSnapping.Enabled = true;
                Check(true, false, false, Key.LeftMeta);
                Check(true, false, false, Key.RightMeta);
                Check(false, true, true, Key.LeftCtrl);
                Check(false, true, true, Key.RightCtrl);
                Check(true, true, true, Key.LeftMeta, Key.LeftCtrl);
                Check(false, false, false);

                Rebind(snap, keyboard.bKey.path);
                Check(false, true, false, Key.B);
                Check(false, false, true, Key.LeftCtrl);
                Rebind(group, keyboard.vKey.path);
                Check(true, false, false, Key.V);
                Check(false, false, false, Key.LeftMeta);
                Rebind(multiple, keyboard.mKey.path);
                Check(false, false, true, Key.M);
                Check(false, false, false, Key.LeftCtrl);
                Check(true, true, true, Key.V, Key.B, Key.M);
                group.rebindAction.action.Disable();
                group.rebindAction.action.AddBinding(keyboard.leftShiftKey.path); group.rebindAction.action.Enable();
                snap.rebindAction.action.Disable();
                snap.rebindAction.action.AddBinding(keyboard.leftShiftKey.path); snap.rebindAction.action.Enable();
                Check(false, false, false, Key.V);
                Check(false, false, false, Key.B);
                Check(false, false, false, Key.LeftShift);
                Check(true, false, false, Key.LeftShift, Key.V);
                Check(false, true, false, Key.LeftShift, Key.B);
                Debug.Log("Separate modifier validation passed: default Command/Control behavior, distinct Preferences rows, modifier-only rebinding, independent grouping/snap/multi-select actions and complete rebound chords.");
            }
            finally {
                foreach (var (input, action, rebind) in originals) {
                    input.defaultAction.Dispose(); input.rebindAction.action.Dispose();
                    input.defaultAction = action; input.rebindAction = rebind;
                }
                groupField.SetValue(null, oldGroup); snapField.SetValue(null, oldSnap);
                instance.SetValue(null, oldInstance); frame.SetValue(null, oldFrame);
                TransformSnapping.Enabled = oldSnapping;
                InputSystem.RemoveDevice(keyboard); runInEdit.SetValue(manager, oldRunInEdit);
            }

            void Check(bool grouping, bool snapping, bool multi, params Key[] keys) {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
                typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
                foreach (var (input, _) in expected) input.Update();
                Require(GroupingBypassInput.IsHeld == grouping && !TransformSnapping.Active == snapping && multiple.IsPressed == multi,
                    $"Modifier actions are coupled for {string.Join(", ", keys)}: grouping={GroupingBypassInput.IsHeld}, snap={!TransformSnapping.Active}, multiple={multiple.IsPressed}");
            }
            void Rebind(InputEvent input, string path) {
                input.defaultAction.Disable(); input.rebindAction.action.AddBinding(path); input.rebindAction.action.Enable();
            }
        }

        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
