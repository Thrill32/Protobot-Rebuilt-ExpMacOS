using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using System;

namespace Protobot.InputEvents {
    public class InputEvent : MonoBehaviour {
        public InputAction defaultAction;
        public RebindAction rebindAction;

        [SerializeField] private bool allowModifierOnly;

        public Action performed;
        public Action canceled;
        public bool IsPressed = false;

        public static bool IsNoclipMovementCaptured { get; private set; }

        private bool prevPressStatus = false;
        private bool suppressedUntilRelease;

        public void Awake() {
            //performed += () => Debug.Log("Performed " + name + " input event!");

            rebindAction = new RebindAction(name, allowModifierOnly);

            rebindAction.OnCompleteRebind += () => defaultAction.Disable();
            rebindAction.OnSaveRebinds += () => defaultAction.Disable();

            rebindAction.OnResetRebinds += () => defaultAction.Enable();

            rebindAction.OnLoadRebinds += hasRebinds => {
                if (hasRebinds) {
                    defaultAction.Disable();
                }
            };

            if (rebindAction.IsEmpty) {
                defaultAction.Enable();
            }
        }

        public void Update() {
            if (RebindAction.Rebinding) return;

            IsPressed = IsCurrentlyPressed;

            if (!IsPressed)
                suppressedUntilRelease = false;
            else if (!Protobot.Tools.KeyboardTransformTool.AllowsInput(this))
                suppressedUntilRelease = true;

            if (suppressedUntilRelease)
                IsPressed = false;

            if (IsNoclipMovementCaptured && UsesNoclipMovementBinding())
                IsPressed = false;

            if (IsPressed != prevPressStatus) {
                if (IsPressed)
                    performed?.Invoke();
                else
                    canceled?.Invoke();
            }

            prevPressStatus = IsPressed;
        }

        public bool IsCurrentlyPressed => !RebindAction.Rebinding &&
            ((defaultAction != null && defaultAction.AllControlsPressed()) ||
             (rebindAction?.action != null && rebindAction.action.AllControlsPressed()));

        /// <summary>
        /// Accept any alternative default binding (e.g. left/right Command),
        /// while requiring every control of a user-rebound chord.
        /// </summary>
        public bool IsAnyBindingPressed => !RebindAction.Rebinding &&
            ((defaultAction != null && defaultAction.IsPressed()) ||
             (rebindAction?.action != null && rebindAction.action.AllControlsPressed()));

        public bool UsesControl(InputControl control) => control != null &&
            (ActionUsesControl(defaultAction, control) || ActionUsesControl(rebindAction?.action, control));

        private static bool ActionUsesControl(InputAction action, InputControl control) {
            if (action == null || !action.enabled) return false;
            foreach (var candidate in action.controls)
                if (candidate == control) return true;
            return false;
        }

        public static void SetNoclipMovementCapture(bool active) {
            IsNoclipMovementCaptured = active;
        }

        private bool UsesNoclipMovementBinding() {
            var activeAction = rebindAction != null && !rebindAction.IsEmpty
                ? rebindAction.action
                : defaultAction;

            foreach (var binding in activeAction.bindings) {
                var path = binding.effectivePath;
                if (string.IsNullOrEmpty(path))
                    continue;

                path = path.ToLowerInvariant();
                if (path == "<keyboard>/w" || path == "<keyboard>/a" ||
                    path == "<keyboard>/s" || path == "<keyboard>/d" ||
                    path == "<keyboard>/e" || path == "<keyboard>/q")
                    return true;
            }

            return false;
        }

        public void OnDisable() {
            defaultAction.Disable();
        }
        
        public string GetCurrentKeybind()
        {
            return rebindAction != null && !rebindAction.IsEmpty
                ? rebindAction.action.GetBindingDisplayString() : defaultAction.GetBindingDisplayString();
        }
        
        public bool IsKeyPressed(string keyName)
        {
            var key = Keyboard.current.FindKeyOnCurrentKeyboardLayout(keyName);
            if (key == null) print("Key not found: " + keyName);
            return key != null && key.isPressed;
        }
        //i have no idea what im doing
        public void Rebind(string newBinding)
        {
            if (defaultAction != null)
            {
                defaultAction.ApplyBindingOverride(newBinding);
                Debug.Log($"Rebound to {newBinding}");
            }
            else
            {
                Debug.LogError("inputAction is null! Cannot rebind.");
            }
        }
    }
}
