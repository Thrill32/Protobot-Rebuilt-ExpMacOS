using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Protobot.InputEvents;

namespace Protobot.Tools {
    public class ToolToggle : MonoBehaviour {
        public bool toggled; //refers to when a tool is toggled
        [SerializeField] private InputEvent toggleOnInput;
        [SerializeField] private bool hideDuringKeyboardTransform;

        public bool active {
            get {
                if (hideDuringKeyboardTransform && KeyboardTransformTool.IsCapturingInput)
                    return false;
                if (toggled) {
                    foreach (IToolRequirement req in requirements) {
                        if (!req.isValid) {
                            return false;
                        }
                    }
                    return true;
                }
                return false;
            }
        }

        private bool prevActivity;

        private IToolRequirement[] requirements;

        [Space(10)]

        public UnityEvent OnToggle;
        public UnityEvent OnActivate;
        public UnityEvent OnDeactivate;

        void Awake() {
            requirements = GetComponents<IToolRequirement>();
            OnDeactivate?.Invoke();

            if (toggleOnInput != null) {
                toggleOnInput.performed += () => Toggle(true);
            }
        }

        void Update() {
            if (!hideDuringKeyboardTransform)
                UpdateActivity();
        }

        // Keyboard shortcuts start during Update. Refresh transform handles
        // afterward so they are hidden before this frame is rendered.
        void LateUpdate() {
            if (hideDuringKeyboardTransform)
                UpdateActivity();
        }

        private void UpdateActivity() {
            bool acivity = active;

            if (toggled && prevActivity != acivity) {
                if (acivity)
                    OnActivate?.Invoke();
                else
                    OnDeactivate?.Invoke();
            }

            prevActivity = acivity;
        }

        public void Toggle(bool value) {
            if (value) {
                toggled = true;
                OnToggle?.Invoke();
            }
            else {
                toggled = false;
                OnDeactivate?.Invoke();
            }
        }
    }
}
