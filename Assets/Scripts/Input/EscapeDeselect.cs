using System.Linq;
using Protobot.Builds;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.Tools;
using Protobot.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Protobot.InputEvents {
    /// <summary>One Escape press unwinds exactly one level of editor interaction.</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class EscapeDeselect : MonoBehaviour {
        private static EscapeDeselect instance;
        private int handledFrame = -1;
        private bool awaitingRelease;
        private Placement placement;
        public static bool CapturesFrame => instance != null && instance.handledFrame == Time.frameCount;
        public static bool BlocksUINavigation => CapturesFrame || instance != null && Keyboard.current?.escapeKey.isPressed == true;

        public static bool AllowsInput(InputEvent input) => !CapturesFrame &&
            (instance == null || Keyboard.current?.escapeKey.isPressed != true || !input.UsesControl(Keyboard.current.escapeKey));

        private void Awake() { instance = this; placement = GetComponent<Placement>(); }
        private void OnEnable() => InputSystem.onAfterUpdate += ObserveRelease;
        private void OnDisable() => InputSystem.onAfterUpdate -= ObserveRelease;
        private void ObserveRelease() {
            if (InputState.currentUpdateType == InputUpdateType.Dynamic && Keyboard.current?.escapeKey.isPressed != true)
                awaitingRelease = false;
        }
        private void Update() => TryHandleEscape();

        // Also called by the old modal input paths, so direct input dispatch and
        // Update/LateUpdate ordering cannot process a second tier on the same press.
        public static bool TryHandleEscape() {
            if (instance == null || !instance.isActiveAndEnabled || Keyboard.current?.escapeKey.wasPressedThisFrame != true) return false;
            if (CapturesFrame || instance.awaitingRelease) return true;
            instance.awaitingRelease = true;
            instance.handledFrame = Time.frameCount;
            instance.DeselectOneLevel();
            return true;
        }

        private void DeselectOneLevel() {
            // Native panels and the rebind UI own their own Escape handling.
            if (NativeFileDialogs.IsOpen || RebindAction.Rebinding || RebindAction.CanceledThisFrame) return;
            if (CancelFocusedUI()) return;
            if (CollaborationSession.CancelWaitingReservation()) return;
            if (KeyboardTransformTool.CancelActive()) return;
            if (placement != null && placement.placing) { placement.CancelPlacement(); return; }
            foreach (var drag in FindObjectsByType<ClickDragTool>(FindObjectsSortMode.None))
                if (drag.CancelDrag()) return;
            foreach (var movement in FindObjectsByType<MovementManager>(FindObjectsSortMode.None))
                if (movement.CancelMovement()) return;
            foreach (var box in FindObjectsByType<BoxSelectUI>(FindObjectsSortMode.None))
                if (box.CancelSelection()) return;
            if (CustomModelTool.TryCancelActive()) return;
            if (BuildTransferTool.IsBusy || FindFirstObjectByType<BuildsManager>()?.IsFileOperationInProgress == true) return;
            var simulation = FindFirstObjectByType<SimulationTool>();
            if (simulation != null && simulation.Current != null) { simulation.Cancel(); return; }
            var connect = FindFirstObjectByType<ConnectTool>();
            if (connect != null && connect.CancelPending()) return;

            var selection = GameObject.Find("Primary Selection Manager")?.GetComponent<SelectionManager>();
            if (selection?.current?.gameObject != null) {
                selection.ClearCurrent(false, true, true);
                connect?.RefreshSelection();
                return;
            }
            if (connect != null && connect.DeactivateTool()) return;
            if (SimulationTool.IsActive) { SimulationTool.ExitForEdit(); return; }
            foreach (var tool in FindObjectsByType<ToolToggle>(FindObjectsSortMode.None))
                if (tool.DeselectTool()) return;
        }

        private static bool CancelFocusedUI() {
            if (CollaborationSession.CancelPanel()) return true;
            var events = EventSystem.current;
            var focused = events != null ? events.currentSelectedGameObject : null;
            if (focused != null) {
                var rename = FindFirstObjectByType<CurrentBuildUI>();
                if (rename != null && rename.CancelRename()) { events.SetSelectedGameObject(null); return true; }
                var escape = new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape };
                if (focused.TryGetComponent<UnityEngine.UI.InputField>(out var field)) {
                    field.ProcessEvent(escape);
                    field.DeactivateInputField();
                    events.SetSelectedGameObject(null);
                    return true;
                }
                if (focused.TryGetComponent<TMP_InputField>(out var tmp)) {
                    tmp.ProcessEvent(escape);
                    tmp.DeactivateInputField();
                    events.SetSelectedGameObject(null);
                    return true;
                }
                if (ExecuteEvents.ExecuteHierarchy(focused, new BaseEventData(events), ExecuteEvents.cancelHandler) != null) {
                    events.SetSelectedGameObject(null);
                    return true;
                }
            }
            var menus = FindObjectsByType<MenuBarToggle>(FindObjectsSortMode.None);
            if (menus.Any(menu => menu.GetComponent<UnityEngine.UI.Toggle>().isOn)) {
                foreach (var menu in menus) menu.GetComponent<UnityEngine.UI.Toggle>().isOn = false;
                events?.SetSelectedGameObject(null);
                return true;
            }
            var preferences = GameObject.Find("Preferences Canvas");
            if (preferences != null && preferences.TryGetComponent<Canvas>(out var canvas) && canvas.enabled) {
                preferences.SetActive(false);
                events?.SetSelectedGameObject(null);
                return true;
            }
            foreach (var context in FindObjectsByType<ContextMenuUI>(FindObjectsSortMode.None)) {
                context.DisableMenu();
                events?.SetSelectedGameObject(null);
                return true;
            }
            return false;
        }

        private void OnDestroy() { if (instance == this) instance = null; }
    }
}
