using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Utilities;

namespace Protobot.InputEvents {
    public class RebindAction {
        public InputAction action;
        private readonly string id;
        private readonly bool allowModifierOnly;
        private string SavedRebinds => PlayerPrefs.GetString(id);
        public bool IsEmpty => string.IsNullOrEmpty(SavedRebinds);

        public Action OnSaveRebinds;
        public Action<bool> OnLoadRebinds;
        public Action OnResetRebinds;

        public Action OnEndRebind;
        public Action OnCompleteRebind;
        public Action OnCancelRebind;
        
        public static bool Rebinding { get; private set; }
        
        private List<InputControl> IgnoredInputs {
            get {
                var inputs = new List<InputControl> { Mouse.current.leftButton };

                if (!allowModifierOnly) {
                    inputs.Add(Keyboard.current.leftCtrlKey);
                    inputs.Add(Keyboard.current.rightCtrlKey);
                    inputs.Add(Keyboard.current.leftShiftKey);
                    inputs.Add(Keyboard.current.rightShiftKey);
                    inputs.Add(Keyboard.current.leftAltKey);
                    inputs.Add(Keyboard.current.rightAltKey);
                }

                return inputs;
            }
        }
        
        private InputControl CancelInput => Keyboard.current.escapeKey;
        
        public RebindAction(string _id, bool allowModifierOnly = false) {
            id = _id;
            this.allowModifierOnly = allowModifierOnly;

            action = new InputAction();

            action.Disable();
            
            //add four empty bindings to the rebindAction
            for (int i = 0; i <= 3; i++) {
                InputBinding binding = new InputBinding {
                    id = new Guid("00000000-0000-0000-0000-00000000000" + i.ToString()),
                    path = ""
                };
                action.AddBinding(binding);
            }

            LoadRebinds();
            
            action.Enable();

            OnEndRebind += () => {
                Rebinding = false;
            };
        }

        public void AttemptRebind() {
            Rebinding = true;
            
            InputSystem.onAnyButtonPress.CallOnce(input => {
                if (input == CancelInput) {
                    OnCancelRebind?.Invoke();
                    OnEndRebind?.Invoke();
                }
                else if (IgnoredInputs.Contains(input)) {
                    AttemptRebind();
                }
                else {
                    if (allowModifierOnly && IsModifier(input)) {
                        for (int i = 0; i <= 2; i++) {
                            action.RemoveBindingOverride(i);
                        }

                        action.ApplyBindingOverride(3, ConvertToBindingPath(input.path));
                    }
                    else {
                        var keyPaths = GetPressedModifiers();

                        for (int i = 0; i <= 2; i++) {
                            if (i < keyPaths.Count) {
                                action.ApplyBindingOverride(i, keyPaths[i]);
                            }
                            else {
                                action.RemoveBindingOverride(i);
                            }
                        }

                        action.ApplyBindingOverride(3, ConvertToBindingPath(input.path));
                    }
                    
                    SaveRebinds();
                    OnCompleteRebind?.Invoke();
                    OnEndRebind?.Invoke();
                }
            });
        }

        private static bool IsModifier(InputControl input) {
            var keyboard = Keyboard.current;
            return input == keyboard.leftCtrlKey || input == keyboard.rightCtrlKey ||
                   input == keyboard.leftShiftKey || input == keyboard.rightShiftKey ||
                   input == keyboard.leftAltKey || input == keyboard.rightAltKey;
        }

        public string ConvertToBindingPath(string path) {
            var bindingPath = "<" + path[1..];
            return bindingPath.Replace("/", ">/");
        }

        public List<string> GetPressedModifiers() {
            Keyboard curKeyboard = Keyboard.current;

            var modifiers = new List<ButtonControl> {
                curKeyboard.ctrlKey,
                curKeyboard.shiftKey,
                curKeyboard.altKey,
            };

            var keyPaths = new List<string>();

            foreach (ButtonControl modifier in modifiers) {
                if (modifier.isPressed) {
                    string path = modifier.path;
                    path = path.Replace("/Keyboard", "<Keyboard>");
                    keyPaths.Add(path);
                }
            }

            return keyPaths;
        }

        public void SaveRebinds() {
            var rebinds = action.SaveBindingOverridesAsJson();
            PlayerPrefs.SetString(id, rebinds);

            action.Enable();

            OnSaveRebinds?.Invoke();
        }

        public bool LoadRebinds() {
            if (!IsEmpty) {
                action.Enable();

                action.LoadBindingOverridesFromJson(SavedRebinds);
            }

            OnLoadRebinds?.Invoke(IsEmpty);

            return IsEmpty;
        }

        public void ResetRebinds() {
            action.RemoveAllBindingOverrides();
            action.Disable();

            PlayerPrefs.DeleteKey(id);

            OnResetRebinds?.Invoke();
        }

        public void ManuelRebind(string binding)
        {
            action.ApplyBindingOverride(0, binding);
            SaveRebinds();

            Debug.Log($"Manually rebound action to {binding}");
            OnCompleteRebind?.Invoke();
        }
    }
}
