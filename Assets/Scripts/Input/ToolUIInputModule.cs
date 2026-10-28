using Protobot.Tools;
using Protobot.UI;
using UnityEngine.InputSystem.UI;

namespace Protobot {
    /// <summary>Keeps UI submit/cancel from competing with part picking and modal tools.</summary>
    public sealed class ToolUIInputModule : InputSystemUIInputModule {
        public override void Process() {
            var focused = eventSystem.currentSelectedGameObject;
            bool captured = KeyboardTransformTool.BlocksUINavigation ||
                (focused != null && focused.GetComponentInParent<PartDisplayUI>() != null);
            if (!captured) {
                base.Process();
                return;
            }

            // InputEvent capture does not cover Unity's independent UI actions.
            // Gate navigation at dispatch so Update/LateUpdate ordering cannot
            // let Enter toggle a focused part row before numeric submission.
            // Pointer events and text-field updates still run in base.Process.
            bool navigation = eventSystem.sendNavigationEvents;
            try {
                eventSystem.sendNavigationEvents = false;
                base.Process();
            }
            finally { eventSystem.sendNavigationEvents = navigation; }
        }
    }
}
