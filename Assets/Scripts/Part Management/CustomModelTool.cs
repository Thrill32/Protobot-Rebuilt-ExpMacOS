using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.Tools;
using Protobot.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Protobot {
    public sealed class CustomModelTool : MonoBehaviour {
        [SerializeField] private InputEvent importInput, mountInput, depthInput, recalibrateInput, orbitInput, panInput, noclipInput;
        [SerializeField] private SelectionManager selectionManager;
        [SerializeField] private Placement placement;
        [SerializeField] private Camera refCamera;
        private static CustomModelTool instance;
        private static int finishedFrame = -1;
        private bool options, busy, placingMounts;
        private int generation, startFrame;
        private int units;
        private InputField diameterField, depthField;
        private Text typeLabel, validationLabel;
        private bool threaded = true;
        private GameObject panelRoot, preview;
        private CustomMountEditor mountEditor;
        private Button movePointButton;
        public CustomMountEditor MountEditor => mountEditor;
        private const string DirectoryKey = "customModelDirectory";
        private static readonly string[] UnitNames = { "Millimeters", "Centimeters", "Inches" };
        private static readonly float[] UnitScales = { 1f / 25.4f, 1f / 2.54f, 1f };

        public static bool IsCapturingInput => instance != null && instance.isActiveAndEnabled &&
            (instance.options || instance.busy || instance.placingMounts || finishedFrame == Time.frameCount);
        public static bool IsEditingMounts => instance != null && instance.isActiveAndEnabled && instance.placingMounts;
        public static bool BlocksCamera => IsCapturingInput && (instance.options || instance.busy || instance.mountEditor?.IsTyping == true || KeyboardTransformTool.IsEditingText());
        public static string ControlsText => instance != null && instance.placingMounts
            ? instance.mountEditor?.ControlsText : null;
        internal static Pose? SavedMountPose(CustomMountPoint point) => instance?.mountEditor?.SavedPose(point);
        internal static float? SavedMountDepth(CustomMountPoint point) => instance?.mountEditor?.SavedDepth(point);

        public static bool AllowsInput(InputEvent input) {
            if (!IsCapturingInput) return true;
            if (instance.options || instance.busy || KeyboardTransformTool.IsEditingText() || finishedFrame == Time.frameCount) return false;
            if (instance.mountEditor?.IsTyping == true) return false;
            return input == instance.mountInput || input == instance.orbitInput || input == instance.panInput || input == instance.noclipInput || instance.mountEditor?.AllowsInput(input) == true;
        }

        private void Awake() => instance = this;
        private void Start() {
            mountEditor = new CustomMountEditor(refCamera);
            mountEditor.DepthCalibrated += CopyMountDimensions;
            importInput.performed += OpenImportSettings;
            mountInput.performed += ToggleMounts;
        }

        private bool CanStart => !busy && !NativeFileDialogs.IsOpen && !RebindAction.Rebinding &&
            !KeyboardTransformTool.IsCapturingInput && !ConnectTool.IsCapturingPoints && !placement.placing &&
            !KeyboardTransformTool.IsEditingText() && !InputEvent.IsNoclipMovementCaptured &&
            FindFirstObjectByType<BuildsManager>()?.IsFileOperationInProgress != true;

        public void OpenImportSettings() {
            if (CollaborationSession.BlocksLegacyMutation) return;
            if (!CanStart) return;
            CancelMode();
            Simulation.SimulationTool.ExitForEdit();
            options = true;
            var panel = CreatePanel("Import Custom Model", true, 340);
            AddLabel(panel, "OBJ and STL geometry is embedded in your build.\nChoose the source file's units before importing.", 50, 70);
            units = Mathf.Clamp(LocalPreferences.GetInt("customModelUnits", 0), 0, 2);
            Button unitButton = null;
            unitButton = AddButton(panel, "Source units: " + UnitNames[units], 140, () => {
                units = (units + 1) % UnitNames.Length;
                UpdateUnitLabel();
            });
            void UpdateUnitLabel() => unitButton.GetComponentInChildren<Text>().text = "Source units: " + UnitNames[units];
            AddLabel(panel, "Models use one editable color. OBJ material and texture files are not imported.", 185, 55);
            AddButton(panel, "Choose OBJ / STL file…", 250, ChooseFile);
            AddButton(panel, "Cancel", 292, CancelMode);
        }

        private async void ChooseFile() {
            if (!options || busy || NativeFileDialogs.IsOpen) return;
            options = false;
            LocalPreferences.SetInt("customModelUnits", units);
            int request = generation;
            busy = true;
            ClosePanel();
            try {
                var paths = await NativeFileDialogs.OpenModelAsync(LocalPreferences.GetString(DirectoryKey, ""));
                if (this == null || request != generation || paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;
                await ReadAndCreate(paths[0], UnitScales[units], request);
            }
            catch (Exception exception) { ReportFailure(exception); }
            finally { if (this != null) { busy = false; finishedFrame = Time.frameCount; ClosePanel(); } }
        }

        // Also used by validation and integrations that already have a file path.
        public async Task<GameObject> ImportPathAsync(string path, float inchesPerUnit) {
            if (CollaborationSession.BlocksLegacyMutation) return null;
            if (!CanStart) return null;
            Simulation.SimulationTool.ExitForEdit();
            CancelMode();
            int request = generation;
            busy = true;
            try { return await ReadAndCreate(path, inchesPerUnit, request); }
            catch (Exception exception) { ReportFailure(exception); return null; }
            finally { if (this != null) { busy = false; finishedFrame = Time.frameCount; ClosePanel(); } }
        }

        private async Task<GameObject> ReadAndCreate(string path, float inchesPerUnit, int request) {
            var panel = CreatePanel("Importing model…", true, 175);
            AddLabel(panel, "Reading " + Path.GetFileName(path), 55, 50);
            AddButton(panel, "Cancel", 120, CancelMode);
            var data = await Task.Run(() => CustomModelImporter.Read(path, inchesPerUnit));
            if (this == null || request != generation || !isActiveAndEnabled) return null;
            var obj = CustomModel.Create(data, PivotCamera.Main.focusPosition, Quaternion.identity);
            StateSystem.AddElement(new ObjectElement(obj) { existing = false });
            selectionManager.ClearCurrent(false, true, true);
            selectionManager.SetCurrent(new ObjectSelection { gameObject = obj, individual = true }, true);
            StateSystem.AddState(new ObjectElement(obj));
            LocalPreferences.SetString(DirectoryKey, Path.GetDirectoryName(path) ?? "");
            FindFirstObjectByType<PartListOutput>()?.CalculatePartsList();
            InteractionFeedback.Notify($"Imported {data.name}. G: move · {mountInput.GetCurrentKeybind()}: add mounting points.");
            return obj;
        }

        private static void ReportFailure(Exception exception) {
            Debug.LogWarning("Could not import model: " + exception.Message);
            InteractionFeedback.Notify("Could not import model: " + exception.Message);
        }

        public void ToggleMounts() {
            if (CollaborationSession.BlocksLegacyMutation) return;
            if (placingMounts) { CancelMode(); return; }
            if (!CanStart || options) return;
            Simulation.SimulationTool.ExitForEdit();
            selectionManager.ClearCurrent(false, true, true);
            placingMounts = true; startFrame = Time.frameCount;
            CustomMountPoint.RefreshAllVisuals();
            mountEditor.Enabled = true;
            var panel = CreatePanel("Add / Edit Mounting Points", false, 385);
            AddLabel(panel, $"Click a surface to add on its {CustomMountGrid.Spacing:0.##} in layout. Select rings to move/copy points, including over pre-made holes. Dimensions are in inches.", 45, 75);
            AddLabel(panel, "New diameter", 125, 30, 140);
            diameterField = AddField(panel, "0.169", 125);
            AddLabel(panel, "New depth", 170, 30, 140);
            depthField = AddField(panel, "0.046", 170);
            var typeButton = AddButton(panel, "", 215, () => { threaded = !threaded; UpdateTypeLabel(); });
            typeLabel = typeButton.GetComponentInChildren<Text>();
            UpdateTypeLabel();
            validationLabel = AddLabel(panel, "", 258, 35);
            movePointButton = AddButton(panel, "Move selected point", 295, () => mountEditor.Begin(false));
            movePointButton.interactable = false;
            AddButton(panel, "Done", 340, CancelMode);
            preview = new GameObject("Mounting point preview", typeof(MeshFilter), typeof(MeshRenderer));
            preview.layer = LayerMask.NameToLayer("Ignore Raycast");
            preview.GetComponent<MeshFilter>().sharedMesh = CustomMountPoint.RingMesh;
            preview.GetComponent<MeshRenderer>().sharedMaterial = Resources.Load<Material>("Custom Models/Mount");
            preview.SetActive(false);
        }

        private void UpdateTypeLabel() => typeLabel.text = threaded ? "New point: Threaded (screw anchor)" : "New point: Through hole";

        private void CopyMountDimensions(CustomMountPoint point) {
            diameterField.text = point.Diameter.ToString("G9", CultureInfo.InvariantCulture);
            depthField.text = point.Depth.ToString("G9", CultureInfo.InvariantCulture);
            InteractionFeedback.Notify("New-hole depth and diameter copied from the last-selected hole.");
        }

        private void LateUpdate() {
            if (!options && !placingMounts && !busy) return;
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true && !NativeFileDialogs.IsOpen) {
                if (!EscapeDeselect.TryHandleEscape()) CancelMode();
                return;
            }
            if (placingMounts) UpdateMountEditing(Application.isFocused);
        }

        private void UpdateMountEditing(bool focused) {
            preview.SetActive(false);
            // A modal edit owns pointer motion across UI and screen edges,
            // just like G/axis movement; otherwise the top toolbar stops wrapping.
            bool pointerPaused = !mountEditor.IsEditing && Mouse.current != null && (MouseInput.overUI || !MouseInput.withinScreen);
            bool pause = RebindAction.Rebinding || Mouse.current == null || !focused ||
                KeyboardTransformTool.IsEditingText() || pointerPaused || InputEvent.IsNoclipMovementCaptured ||
                orbitInput.IsCurrentlyPressed || panInput.IsCurrentlyPressed || noclipInput.IsCurrentlyPressed;
            movePointButton.interactable = mountEditor.Selected != null && !mountEditor.IsEditing;
            movePointButton.GetComponentInChildren<Text>().text = mountEditor.SelectedPoints.Count > 1 ? "Move selected points" : "Move selected point";
            if (mountEditor.Tick(pause) || pause) return;
            UpdateMountPlacement();
        }

        private void UpdateMountPlacement() {
            var existing = CustomMountPoint.Pick(refCamera.ScreenPointToRay(MouseInput.Position));
            if (existing != null) {
                validationLabel.text = $"Click ring to select · G: move · R: rotate · {depthInput.GetCurrentKeybind()}: depth";
                if (Time.frameCount != startFrame && Mouse.current.leftButton.wasPressedThisFrame) mountEditor.Select(existing, mountEditor.MultiSelectHeld);
                return;
            }
            if (!ReadDimension(diameterField.text, out float diameter) || !ReadDimension(depthField.text, out float depth)) {
                validationLabel.text = "Enter dimensions greater than 0, up to 100 in.";
                return;
            }
            validationLabel.text = mountEditor.Selected != null ? $"{mountEditor.SelectedPoints.Count} selected · G: move · R: rotate · {depthInput.GetCurrentKeybind()}: depth" : "";
            // Cast against bodies so previous mounting points do not obscure the surface.
            if (!Physics.Raycast(refCamera.ScreenPointToRay(MouseInput.Position), out var hit, Mathf.Infinity, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore) ||
                !hit.collider.TryGetComponent<CustomModel>(out var model) || model.gameObject.IsDeleted()) return;
            var face = CustomMountGrid.PlacementPose(model, hit.point, hit.normal, TransformSnapping.Active, out bool onGrid);
            validationLabel.text = onGrid ? $"{CustomMountGrid.Spacing:0.##} in layout · {mountEditor.SnapBypassBinding}: bypass" :
                TransformSnapping.Active ? "First point anchors this plane's layout" : "Snap bypass · free placement";
            preview.transform.SetPositionAndRotation(face.position + hit.normal * .004f, face.rotation);
            preview.transform.localScale = Vector3.one * diameter;
            preview.SetActive(true);
            var bounds = model.GetComponent<MeshFilter>().sharedMesh.bounds;
            bounds.Expand(.002f);
            if (onGrid && !bounds.Contains(model.transform.InverseTransformPoint(face.position))) {
                validationLabel.text = $"Grid point is outside model · {mountEditor.SnapBypassBinding}: bypass";
                return;
            }
            if (Time.frameCount == startFrame || !Mouse.current.leftButton.wasPressedThisFrame) return;
            var point = model.AddMount(face, diameter, depth, threaded);
            if (point != null) { point.RecordAdded(); mountEditor.Select(point); }
            else InteractionFeedback.Notify("A mounting point already exists here.");
        }

        private static bool ReadDimension(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 100;

        public static void CancelActive() {
            if (instance != null && (instance.options || instance.busy || instance.placingMounts)) instance.CancelMode();
        }
        internal static bool TryCancelActive() {
            if (instance == null || !(instance.options || instance.busy || instance.placingMounts)) return false;
            if (instance.mountEditor?.CancelLevel() == true) return true;
            instance.CancelMode();
            return true;
        }
        private void CancelMode() {
            mountEditor?.End();
            generation++;
            options = placingMounts = false;
            CustomMountPoint.RefreshAllVisuals();
            finishedFrame = Time.frameCount;
            if (preview != null) { preview.SetActive(false); Destroy(preview); preview = null; }
            ClosePanel();
        }

        private void ClosePanel() {
            if (panelRoot == null) return;
            if (EventSystem.current?.currentSelectedGameObject != null && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(panelRoot.transform))
                EventSystem.current.SetSelectedGameObject(null);
            panelRoot.SetActive(false); Destroy(panelRoot); panelRoot = null;
        }

        private RectTransform CreatePanel(string title, bool modal, float height) {
            ClosePanel();
            panelRoot = new GameObject("Custom Model Controls", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = panelRoot.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 200;
            var scaler = panelRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1200, 800); scaler.matchWidthOrHeight = .5f;
            if (modal) {
                var shade = new GameObject("Backdrop", typeof(RectTransform), typeof(Image)); shade.transform.SetParent(panelRoot.transform, false);
                var rect = (RectTransform)shade.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
                shade.GetComponent<Image>().color = new Color(0, 0, 0, .55f);
            }
            var obj = new GameObject(title, typeof(RectTransform), typeof(Image)); obj.transform.SetParent(panelRoot.transform, false);
            var panel = (RectTransform)obj.transform;
            panel.anchorMin = panel.anchorMax = modal ? new Vector2(.5f, .5f) : Vector2.one;
            panel.pivot = modal ? new Vector2(.5f, .5f) : Vector2.one;
            panel.anchoredPosition = modal ? Vector2.zero : new Vector2(-16, -50);
            panel.sizeDelta = new Vector2(360, height);
            obj.GetComponent<Image>().color = new Color(.12f, .14f, .17f, .98f);
            AddLabel(panel, title, 12, 30).fontSize = 20;
            return panel;
        }

        private static RectTransform Widget(Transform parent, string name, float top, float height, float width = 328, float left = 16) {
            var obj = new GameObject(name, typeof(RectTransform)); obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform; rect.anchorMin = rect.anchorMax = Vector2.up; rect.pivot = Vector2.up;
            rect.anchoredPosition = new Vector2(left, -top); rect.sizeDelta = new Vector2(width, height); return rect;
        }
        private static Text AddLabel(Transform panel, string text, float top, float height, float width = 328) {
            var rect = Widget(panel, "Label", top, height, width); var label = rect.gameObject.AddComponent<Text>();
            label.font = TransformFeedback.FeedbackFont; label.fontSize = 16; label.text = text; label.color = Color.white;
            label.raycastTarget = false; label.supportRichText = false; return label;
        }
        private static Button AddButton(Transform panel, string text, float top, UnityEngine.Events.UnityAction action) {
            var rect = Widget(panel, text, top, 34); var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.22f, .27f, .33f);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; button.onClick.AddListener(action);
            var label = AddLabel(rect, text, 0, 34, 296); label.alignment = TextAnchor.MiddleCenter;
            button.navigation = new Navigation { mode = Navigation.Mode.None }; return button;
        }
        private static InputField AddField(Transform panel, string text, float top) {
            var rect = Widget(panel, "Dimension", top, 34, 180, 164);
            rect.gameObject.AddComponent<Image>().color = new Color(.24f, .27f, .3f);
            var field = rect.gameObject.AddComponent<InputField>();
            var label = AddLabel(rect, text, 0, 34, 148); label.alignment = TextAnchor.MiddleLeft; label.color = Color.white;
            field.textComponent = label; field.text = text; field.characterLimit = 12;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            return field;
        }

        private void OnApplicationFocus(bool focus) { if (!focus && placingMounts) CancelMode(); }
        private void OnGUI() => mountEditor?.Draw();
        private void OnDisable() => CancelMode();
        private void OnDestroy() {
            mountEditor?.Dispose();
            if (importInput != null) importInput.performed -= OpenImportSettings;
            if (mountInput != null) mountInput.performed -= ToggleMounts;
            if (instance == this) instance = null;
        }
    }
}
