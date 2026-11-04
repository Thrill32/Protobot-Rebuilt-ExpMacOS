using System;
using System.Threading.Tasks;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Tools;
using Protobot.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Protobot.Builds {
    public sealed class BuildTransferTool : MonoBehaviour {
        [SerializeField] private BuildsManager buildsManager;
        [SerializeField] private SelectionManager selectionManager;
        [SerializeField] private Placement placement;
        [SerializeField] private ObjectDuplicator duplicator;
        [SerializeField] private InputEvent copyInput, pasteInput, exportInput, importInput;
        private static BuildTransferTool instance;
        private bool busy;
        private int generation;
        public static bool IsBusy => instance != null && instance.isActiveAndEnabled && instance.busy;

        private void Awake() => instance = this;
        private void Start() {
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.OSXPlayer) {
                foreach (var input in new[] { copyInput, pasteInput }) {
                    bool enabled = input.defaultAction.enabled;
                    input.defaultAction.Disable();
                    for (int i = 0; i < input.defaultAction.bindings.Count; i++) {
                        var binding = input.defaultAction.bindings[i];
                        if (binding.name == "Modifier")
                            input.defaultAction.ChangeBinding(i).WithPath(binding.path.Replace("Meta", "Ctrl"));
                    }
                    if (enabled) input.defaultAction.Enable();
                }
                foreach (var button in FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    if (button.name != "Copy Selection Button" && button.name != "Paste Selection Button") continue;
                    foreach (var text in button.GetComponentsInChildren<UnityEngine.UI.Text>(true)) text.text = text.text.Replace("Cmd+", "Ctrl+");
                    foreach (var text in button.GetComponentsInChildren<TMPro.TMP_Text>(true)) text.text = text.text.Replace("Cmd+", "Ctrl+");
                }
            }
            copyInput.performed += CopySelected;
            pasteInput.performed += Paste;
            exportInput.performed += SaveSelectedAs;
            importInput.performed += ImportBuild;
        }

        private bool CanStart => isActiveAndEnabled && placement.isActiveAndEnabled && !busy && !buildsManager.IsFileOperationInProgress && !NativeFileDialogs.IsOpen &&
            !placement.placing && !RebindAction.Rebinding && !KeyboardTransformTool.IsEditingText() && !KeyboardTransformTool.IsCapturingInput &&
            !ConnectTool.IsCapturingPoints && !Simulation.SimulationTool.CapturesSelection && !CustomModelTool.IsCapturingInput &&
            !InputEvent.IsNoclipMovementCaptured;

        public async void CopySelected() => await CopySelectedAsync();
        public async void Paste() => await PasteAsync();
        public async void SaveSelectedAs() => await SaveSelectedAsAsync();
        public async void ImportBuild() => await ImportBuildAsync();

        public async Task<bool> CopySelectedAsync() {
            if (!CanStart) return false;
            var snapshot = BuildTransfer.Capture(selectionManager.current);
            if (snapshot == null) { InteractionFeedback.Notify("Select parts to copy."); return false; }
            busy = true;
            try {
                var text = await Task.Run(() => BuildTransfer.ToClipboard(snapshot));
                if (this == null) return false;
                GUIUtility.systemCopyBuffer = text;
                InteractionFeedback.Notify($"Copied {snapshot.parts.Length} parts. Paste in this or another project.");
                return true;
            }
            catch (Exception exception) { Report("copy selection", exception); return false; }
            finally { if (this != null) busy = false; }
        }

        public async Task<bool> PasteAsync() {
            if (!CanStart) return false;
            var text = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(BuildTransfer.ClipboardPrefix, StringComparison.Ordinal)) {
                InteractionFeedback.Notify("The clipboard does not contain Protobot parts."); return false;
            }
            busy = true;
            int request = generation;
            try {
                var data = await Task.Run(() => BuildTransfer.FromClipboard(text));
                return this != null && request == generation && BeginPlacement(data);
            }
            catch (Exception exception) { Report("paste selection", exception); return false; }
            finally { if (this != null) busy = false; }
        }

        public async Task<bool> SaveSelectedAsAsync() {
            if (!CanStart) return false;
            var snapshot = BuildTransfer.Capture(selectionManager.current);
            if (snapshot == null) { InteractionFeedback.Notify("Select parts to save."); return false; }
            busy = true;
            try { return await buildsManager.SaveSelectionAsAsync(snapshot); }
            finally { if (this != null) busy = false; }
        }

        public async Task<bool> ImportBuildAsync(string path = null) {
            if (!CanStart) return false;
            busy = true;
            int request = generation;
            try {
                var data = await buildsManager.ReadImportAsync(path);
                return this != null && request == generation && data != null && BeginPlacement(data);
            }
            catch (Exception exception) { Report("import build", exception); return false; }
            finally { if (this != null) busy = false; }
        }

        private bool BeginPlacement(BuildData data) {
            if (placement.placing || !isActiveAndEnabled) return false;
            var copied = SelectionPlacementData.FromBuild(data, placement.transform, duplicator, selectionManager);
            placement.StartPlacing(copied);
            return true;
        }

        // A complete clipboard chord wins over C's chain action and any rebound action
        // using its letter, independent of InputEvent Update order. Modifiers remain available.
        public static bool AllowsInput(InputEvent input) {
            if (instance == null || !instance.isActiveAndEnabled) return true;
            if (instance.busy) return false;
            var priority = instance.copyInput != null && instance.copyInput.IsCurrentlyPressed ? instance.copyInput :
                instance.pasteInput != null && instance.pasteInput.IsCurrentlyPressed ? instance.pasteInput : null;
            return priority == null || priority == input || (!SharesLetter(input, priority.defaultAction) && !SharesLetter(input, priority.rebindAction?.action));
        }

        private static bool SharesLetter(InputEvent input, InputAction action) {
            if (action == null || !action.enabled) return false;
            foreach (var control in action.controls) {
                if (control is not ButtonControl button || !button.isPressed) continue;
                if (control is KeyControl key && (key.keyCode == Key.LeftMeta || key.keyCode == Key.RightMeta ||
                    key.keyCode == Key.LeftCtrl || key.keyCode == Key.RightCtrl || key.keyCode == Key.LeftAlt ||
                    key.keyCode == Key.RightAlt || key.keyCode == Key.LeftShift || key.keyCode == Key.RightShift)) continue;
                if (control == Keyboard.current?.ctrlKey || control == Keyboard.current?.shiftKey || control == Keyboard.current?.altKey) continue;
                if (input.UsesControl(control)) return true;
            }
            return false;
        }

        public static void CancelPending() { if (instance != null) instance.generation++; }
        private static void Report(string action, Exception exception) {
            Debug.LogWarning("Could not " + action + ": " + exception.Message);
            InteractionFeedback.Notify("Could not " + action + ": " + exception.Message);
        }
        private void OnDisable() { generation++; }
        private void OnDestroy() {
            if (copyInput != null) copyInput.performed -= CopySelected;
            if (pasteInput != null) pasteInput.performed -= Paste;
            if (exportInput != null) exportInput.performed -= SaveSelectedAs;
            if (importInput != null) importInput.performed -= ImportBuild;
            if (instance == this) instance = null;
        }
    }
}
