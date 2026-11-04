using System;
using System.Reflection;
using Protobot;
using Protobot.InputEvents;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputEvent = Protobot.InputEvents.InputEvent;
using Object = UnityEngine.Object;

namespace ProtobotEditorTools {
    public static class ShortcutPriorityValidation {
        public static void ValidateRelated() {
            Validate();
            ModifierBindingsValidation.Validate();
            KeyboardTransformValidation.Validate();
        }
        [MenuItem("Tools/Protobot/Validate Shortcut Priority")]
        public static void Validate() {
            var manager = typeof(InputSystem).GetField("s_Manager", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var runInEdit = manager.GetType().GetProperty("runPlayerUpdatesInEditMode");
            var oldRunInEdit = runInEdit.GetValue(manager); runInEdit.SetValue(manager, true);
            var previousKeyboard = Keyboard.current;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var root = new GameObject("Shortcut priority validation");
            var plain = Create("Plain", keyboard.kKey.path);
            var modified = Create("Modified");
            var specific = Create("Specific");
            var modifierOnly = Create("Modifier only", keyboard.leftCtrlKey.path);
            int singles = 0, chords = 0;
            plain.performed += () => singles++; modified.performed += () => chords++;
            try {
                foreach (var modifier in new[] { Key.LeftCtrl, Key.RightCtrl, Key.LeftMeta, Key.RightMeta, Key.LeftAlt, Key.RightAlt }) {
                    foreach (bool chordFirst in new[] { true, false }) {
                        modified.rebindAction.action.Disable(); modified.rebindAction.action.Dispose();
                        modified.rebindAction.action = new InputAction("Rebound chord", InputActionType.Button);
                        modified.rebindAction.action.AddBinding(keyboard[modifier].path);
                        modified.rebindAction.action.AddBinding(keyboard.kKey.path);
                        modified.rebindAction.action.Enable();
                        Keys(); Poll(); singles = chords = 0;
                        Keys(modifier, Key.K); Poll(chordFirst);
                        Require(singles == 0 && chords == 1, $"{modifier}+K fired plain={singles}, chord={chords} (chordFirst={chordFirst}).");
                        Keys(Key.K); Poll(chordFirst);
                        Require(singles == 0 && !plain.IsCurrentlyPressed, "Releasing the modifier fired the held single key.");
                        Keys(); Poll(); Keys(Key.K); Poll();
                        Require(singles == 1, "A fresh single key stopped working.");
                    }
                }
                modified.rebindAction.action.Disable(); modified.defaultAction.Disable();
                modified.defaultAction.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", keyboard.leftCtrlKey.path).With("Button", keyboard.kKey.path);
                modified.defaultAction.Enable();
                specific.defaultAction.Disable();
                specific.defaultAction.AddBinding(keyboard.leftCtrlKey.path); specific.defaultAction.AddBinding(keyboard.leftShiftKey.path); specific.defaultAction.AddBinding(keyboard.kKey.path);
                specific.defaultAction.Enable();
                int precise = 0; specific.performed += () => precise++;
                Keys(); Poll(); specific.Update(); modifierOnly.Update(); singles = chords = 0;
                Keys(Key.LeftCtrl); Poll(); specific.Update(); modifierOnly.Update();
                Keys(Key.LeftCtrl, Key.K); Poll(); specific.Update(); modifierOnly.Update();
                Require(singles == 0 && chords == 1 && modifierOnly.IsCurrentlyPressed, "Composite chord failed or swallowed a modifier-only bind.");
                Keys(); Poll(); specific.Update(); singles = chords = precise = 0;
                Keys(Key.LeftCtrl, Key.LeftShift); Poll(); specific.Update();
                Keys(Key.LeftCtrl, Key.LeftShift, Key.K); Poll(); specific.Update();
                Require(singles == 0 && chords == 0 && precise == 1, "A more specific Ctrl+Shift chord did not override Ctrl and plain bindings.");
                Keys(); Poll(); specific.Update(); specific.defaultAction.Disable(); modified.defaultAction.Disable(); singles = 0;
                Keys(Key.LeftCtrl, Key.K); Poll();
                Require(singles == 1, "A disabled modified chord incorrectly blocked the single-key bind.");
                Keys(); Poll(); specific.Update();
                modified.defaultAction.Disable();
                modified.defaultAction.AddCompositeBinding("ButtonWithTwoModifiers")
                    .With("Modifier1", keyboard.leftCtrlKey.path).With("Modifier2", keyboard.leftAltKey.path).With("Button", keyboard.kKey.path);
                modified.defaultAction.Enable(); specific.defaultAction.Enable();
                singles = chords = precise = 0;
                Keys(Key.LeftCtrl, Key.LeftShift, Key.K); Poll(); specific.Update();
                Require(singles == 0 && chords == 0 && precise == 1,
                    "An incomplete Ctrl+Alt alternative let Ctrl+K compete with the complete Ctrl+Shift+K chord.");
                Keys(); Poll(); specific.Update();
                plain.defaultAction.Disable();
                plain.defaultAction.AddCompositeBinding("ButtonWithTwoModifiers")
                    .With("Modifier1", keyboard.leftAltKey.path).With("Modifier2", keyboard.leftMetaKey.path).With("Button", keyboard.kKey.path);
                plain.defaultAction.Enable(); singles = chords = precise = 0;
                Keys(Key.LeftCtrl, Key.LeftShift, Key.K); Poll(); specific.Update();
                Require(singles == 0 && chords == 0 && precise == 1,
                    "An incomplete Alt+Command alternative let plain K compete with Ctrl+Shift+K.");
                CheckFourModifierRebind();
                Debug.Log("Shortcut priority validation passed: Command/Control/Option on both sides, default composite and rebound chords, either polling order, modifier-first release, modifier-only coexistence, most-specific Ctrl+Shift precedence, incomplete alternatives, four-modifier rebinds with legacy override loading, disabled bindings and fresh single keys.");
            }
            finally {
                foreach (var input in new[] { plain, modified, specific, modifierOnly }) { input.defaultAction.Dispose(); input.rebindAction.action.Dispose(); }
                Object.DestroyImmediate(root); InputSystem.RemoveDevice(keyboard); previousKeyboard?.MakeCurrent(); runInEdit.SetValue(manager, oldRunInEdit);
            }
            InputEvent Create(string label, params string[] paths) {
                var go = new GameObject(label + " " + Guid.NewGuid()); go.transform.SetParent(root.transform);
                var input = go.AddComponent<InputEvent>(); input.defaultAction = new InputAction("Default", InputActionType.Button);
                foreach (var path in paths) input.defaultAction.AddBinding(path);
                input.Awake(); return input;
            }
            void Keys(params Key[] keys) {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
                typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null)
                    .Invoke(null, new object[] { InputUpdateType.Dynamic });
            }
            void Poll(bool chordFirst = false) { if (chordFirst) { modified.Update(); plain.Update(); } else { plain.Update(); modified.Update(); } }
            void CheckFourModifierRebind() {
                string id = "Four modifier validation " + Guid.NewGuid();
                var mouse = Mouse.current == null ? InputSystem.AddDevice<Mouse>() : null;
                var rebind = new RebindAction(id);
                try {
                    Keys(Key.LeftCtrl, Key.LeftShift, Key.LeftAlt, Key.LeftMeta);
                    rebind.AttemptRebind();
                    Keys(Key.LeftCtrl, Key.LeftShift, Key.LeftAlt, Key.LeftMeta, Key.K);
                    Require(!RebindAction.Rebinding && rebind.action.AllControlsPressed(), "Four-modifier rebinding failed.");
                    var loaded = new RebindAction(id);
                    try {
                        Keys(Key.LeftCtrl, Key.LeftShift, Key.LeftAlt, Key.LeftMeta, Key.K);
                        Require(loaded.action.AllControlsPressed(), "Saved four-modifier rebinding did not reload.");
                    }
                    finally { loaded.action.Dispose(); }
                    Keys(Key.LeftCtrl, Key.LeftShift, Key.LeftAlt, Key.K);
                    Require(!rebind.action.AllControlsPressed(), "Rebinding silently dropped Command from a four-modifier chord.");
                    // Older preferences contain four binding IDs, with the
                    // trigger in slot 3. Appending a modifier must preserve it.
                    using var legacy = new InputAction();
                    for (int i = 0; i < 4; i++) legacy.AddBinding(new InputBinding {
                        id = new Guid("00000000-0000-0000-0000-00000000000" + i), path = ""
                    });
                    legacy.ApplyBindingOverride(0, "<Keyboard>/ctrl");
                    legacy.ApplyBindingOverride(3, "<Keyboard>/k");
                    PlayerPrefs.SetString(id, legacy.SaveBindingOverridesAsJson());
                    loaded = new RebindAction(id);
                    try {
                        Keys(Key.LeftCtrl, Key.K);
                        Require(loaded.action.AllControlsPressed(), "Appending a modifier slot broke legacy shortcut preferences.");
                        Keys(Key.LeftCtrl);
                        Require(!loaded.action.AllControlsPressed(), "Legacy shortcut loading lost its trigger key.");
                    }
                    finally { loaded.action.Dispose(); }
                }
                finally {
                    Keys();
                    rebind.ResetRebinds(); rebind.action.Dispose();
                    if (mouse != null) InputSystem.RemoveDevice(mouse);
                }
            }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
