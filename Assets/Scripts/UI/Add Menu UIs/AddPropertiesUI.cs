using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.Tools;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Protobot.UI {
    public class AddPropertiesUI : MonoBehaviour {
        [SerializeField] private Placement placement;
        [SerializeField] private SelectionManager selectionManager;
        [SerializeField] private InputEvent pickPartInput;
        [SerializeField] private InputEvent increaseFirstInput;
        [SerializeField] private InputEvent decreaseFirstInput;
        [SerializeField] private InputEvent increaseSecondInput;
        [SerializeField] private InputEvent decreaseSecondInput;
        [SerializeField] private CanvasGroup upperAddMenu;
        [SerializeField] private Text titleText;
        [SerializeField] private float singleParamSize = 335;
        [SerializeField] private float doubleParamSize = 515;
        [SerializeField] private ParamDisplay param1Display;
        [SerializeField] private ParamDisplay param2Display;

        private PartType partType;
        private PartGenerator generator;
        private ISelection observedSelection;
        private GameObject editingObject;
        private InputEvent repeatInput;
        private float nextRepeat;
        private bool wasPlacing;

        private void Start() {
            PartDisplayUI.OnChangeSelected += UpdateDisplay;
            pickPartInput.performed += PickPart;
            Show(false);
        }

        private void OnDestroy() {
            PartDisplayUI.OnChangeSelected -= UpdateDisplay;
            if (pickPartInput != null) pickPartInput.performed -= PickPart;
        }

        private static bool EditingText() {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && (selected.GetComponent<InputField>() != null || selected.GetComponent<TMP_InputField>() != null);
        }

        private bool CanUseShortcut => Application.isFocused && !EditingText() && !RebindAction.Rebinding &&
            !placement.rotating && !InputEvent.IsNoclipMovementCaptured && !KeyboardTransformTool.IsCapturingInput && !ConnectTool.IsCapturingPoints &&
            UnityEngine.InputSystem.Mouse.current != null && !UnityEngine.InputSystem.Mouse.current.leftButton.isPressed;

        private void LateUpdate() {
            if (!placement.placing && (wasPlacing || observedSelection != selectionManager.current))
                ObserveSelection();
            wasPlacing = placement.placing;
            if (!CanUseShortcut || generator == null || (!placement.placing && editingObject == null) ||
                (placement.placing && placement.CurrentPartPlacement == null)) {
                repeatInput = null;
                return;
            }
            UpdateHeldPropertyShortcut();
        }

        private void UpdateHeldPropertyShortcut() {
            InputEvent pressed = null;
            int index = 0, direction = 1;
            if (increaseFirstInput.IsPressed) pressed = increaseFirstInput;
            else if (decreaseFirstInput.IsPressed) { pressed = decreaseFirstInput; direction = -1; }
            else if (increaseSecondInput.IsPressed) { pressed = increaseSecondInput; index = 1; }
            else if (decreaseSecondInput.IsPressed) { pressed = decreaseSecondInput; index = 1; direction = -1; }
            if (pressed == null) { repeatInput = null; return; }
            if (pressed != repeatInput || Time.unscaledTime >= nextRepeat) {
                bool first = pressed != repeatInput;
                repeatInput = pressed;
                nextRepeat = Time.unscaledTime + (first ? .4f : .12f);
                if (PartParameterEditing.Step(generator, index, direction)) {
                    ApplyParameters();
                    RefreshDisplay();
                }
            }
        }

        private void ObserveSelection() {
            observedSelection = selectionManager.current;
            editingObject = SelectedPart();
            if (editingObject == null || !ConfigureFrom(editingObject)) {
                generator = null;
                Show(false);
                return;
            }
            RefreshDisplay();
        }

        private GameObject SelectedPart() {
            var selection = selectionManager.current;
            if (selection is MultiSelection) return null;
            var obj = selection is HoleSelection hole ? hole.holeData.part : selection?.gameObject;
            return obj != null && obj.TryGetComponent<SavedObject>(out _) && !obj.IsDeleted() ? obj : null;
        }

        private bool ConfigureFrom(GameObject obj) {
            var saved = obj.GetComponent<SavedObject>();
            partType = PartsManager.GetPartType(saved.id);
            generator = partType != null ? partType.GetComponent<PartGenerator>() : null;
            return PartParameterEditing.Load(generator, saved.id);
        }

        public void PickPart() {
            if (!CanUseShortcut || MouseInput.overUI || placement.placing) return;
            PlacePickedPart();
        }

        private void PlacePickedPart() {
            var source = SelectedPart();
            if (source == null || !ConfigureFrom(source)) return;
            editingObject = null;
            RefreshDisplay();
            placement.StartPlacing(new PartPlacementData(generator, partType, placement.transform));
        }

        public void UpdateDisplay(PartDisplayUI display) {
            editingObject = null;
            partType = display.partType;
            generator = partType.GetComponent<PartGenerator>();
            PartParameterEditing.Normalize(generator);
            RefreshDisplay();
            placement.StartPlacing(new PartPlacementData(generator, partType, placement.transform));
        }

        private void Show(bool visible) {
            if (visible) upperAddMenu.gameObject.SetActive(true);
            upperAddMenu.alpha = visible ? 1 : 0;
            upperAddMenu.interactable = upperAddMenu.blocksRaycasts = visible;
        }

        private void RefreshDisplay() {
            Show(generator != null && generator.UsesParams);
            if (generator == null || !generator.UsesParams) return;
            titleText.text = (editingObject != null ? "Edit " : "Add ") + partType.name;
            UpdateDisplayedDropdowns(generator.UsesTwoParams);
        }

        public void UpdateDisplayedDropdowns(bool usesTwoParams) {
            var rect = upperAddMenu.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, usesTwoParams ? doubleParamSize : singleParamSize);
            param1Display.gameObject.SetActive(true);
            param2Display.gameObject.SetActive(usesTwoParams);
            SetDropdown1();
        }

        public void SetDropdown1() {
            param1Display.SetDisplay(generator.param1, generator.GetParam1Options());
            SetDropdown2();
        }

        public void SetDropdown2() {
            if (generator.UsesTwoParams)
                param2Display.SetDisplay(generator.param2, generator.GetParam2Options());
        }

        public void UpdateParam1(string value) => UpdateParameter(0, value);
        public void UpdateParam2(string value) => UpdateParameter(1, value);

        private void UpdateParameter(int index, string value) {
            if (generator == null) return;
            string before = generator.GetId();
            (index == 0 ? generator.param1 : generator.param2).value = value;
            PartParameterEditing.Normalize(generator);
            if (before != generator.GetId()) ApplyParameters();
            // Keep text fields editable without resetting the caret on each keystroke.
            if (index == 0) SetDropdown2();
        }

        private void ApplyParameters() {
            if (editingObject == null) {
                placement.RefreshPartPreview();
                return;
            }
            var old = editingObject;
            var previousSelection = selectionManager.current;
            var hole = previousSelection as HoleSelection;
            Vector3? anchor = hole?.holeData.position;
            Vector3? localHole = anchor.HasValue ? old.transform.InverseTransformPoint(anchor.Value) : null;
            var replacement = PartParameterEditing.Replace(old, generator, anchor, localHole);
            if (replacement == null) return;
            ISelection nextSelection = new ObjectSelection { gameObject = replacement, individual = previousSelection?.individual ?? false };
            if (hole != null) {
                HoleCollider nearest = null;
                float distance = float.PositiveInfinity;
                foreach (var candidate in replacement.GetComponentsInChildren<HoleCollider>()) {
                    float d = (candidate.transform.position - anchor.Value).sqrMagnitude;
                    if (d < distance) { nearest = candidate; distance = d; }
                }
                if (nearest != null) nextSelection = new HoleSelection {
                    individual = previousSelection?.individual ?? false,
                    gameObject = hole.gameObject, holeFace = hole.holeFace, holeData = nearest.holeData,
                    faceDirection = hole.holeFace.direction
                };
            }
            StateSystem.AddElement(new ReplacementElement(selectionManager, old, replacement, previousSelection, false));
            var after = new ReplacementElement(selectionManager, old, replacement, nextSelection, true);
            after.Load();
            StateSystem.AddState(after);
            editingObject = replacement;
            observedSelection = selectionManager.current;
            FindFirstObjectByType<PartListOutput>()?.CalculatePartsList();
        }

        private sealed class ReplacementElement : IElement {
            private readonly SelectionManager manager;
            private readonly GameObject oldPart, newPart;
            private readonly ISelection selection;
            private readonly bool replaced;

            public ReplacementElement(SelectionManager manager, GameObject oldPart, GameObject newPart, ISelection selection, bool replaced) {
                this.manager = manager; this.oldPart = oldPart; this.newPart = newPart;
                this.selection = selection; this.replaced = replaced;
            }

            public void Load() {
                // This is a programmatic restoration, including edits made over UI.
                manager.ClearCurrent(false, true, true);
                var affected = HoleDetector.GetAffectedDetectors(oldPart, newPart);
                if (oldPart != null) new ObjectElement(oldPart).ApplyExistence(!replaced);
                if (newPart != null) new ObjectElement(newPart).ApplyExistence(replaced);
                var visiblePart = replaced ? newPart : oldPart;
                if (visiblePart != null) HoleDetector.RefreshPart(visiblePart, affected);
                manager.SetCurrent(selection, true);
            }
        }
    }
}
