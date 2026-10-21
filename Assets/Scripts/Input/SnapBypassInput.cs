using UnityEngine;

namespace Protobot.InputEvents {
    [RequireComponent(typeof(InputEvent))]
    public sealed class SnapBypassInput : MonoBehaviour {
        private static InputEvent input;

        public static bool IsHeld => input != null && input.IsAnyBindingPressed;

        private void Awake() {
            input = GetComponent<InputEvent>();
        }

        private void OnDestroy() {
            if (input == GetComponent<InputEvent>())
                input = null;
        }
    }
}
