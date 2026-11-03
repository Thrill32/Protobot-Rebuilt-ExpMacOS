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
        [SerializeField] private InputEvent importInput, mountInput, orbitInput, panInput, noclipInput;
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
        private const string DirectoryKey = "customModelDirectory";
        private static readonly string[] UnitNames = { "Millimeters", "Centimeters", "Inches" };
        private static readonly float[] UnitScales = { 1f / 25.4f, 1f / 2.54f, 1f };

        public static bool IsCapturingInput => instance != null && instance.isActiveAndEnabled &&
            (instance.options || instance.busy || instance.placingMounts || finishedFrame == Time.frameCount);
        public static bool BlocksCamera => IsCapturingInput && (instance.options || instance.busy || KeyboardTransformTool.IsEditingText());
        public static string ControlsText => instance != null && instance.placingMounts
            ? $"Mounting points · click an imported model to add\n{instance.mountInput.GetCurrentKeybind()} / Esc: finish    Select a point + Delete: remove" : null;

        public static bool AllowsInput(InputEvent input) {
            if (!IsCapturingInput) return true;
            if (instance.options || instance.busy || KeyboardTransformTool.IsEditingText() || finishedFrame == Time.frameCount) return false;
            return input == instance.mountInput || input == instance.orbitInput || input == instance.panInput || input == instance.noclipInput;
        }

        private void Awake() => instance = this;
        private void Start() {
            importInput.performed += OpenImportSettings;
            mountInput.performed += ToggleMounts;
        }

        private bool CanStart => !busy && !NativeFileDialogs.IsOpen && !RebindAction.Rebinding &&
            !KeyboardTransformTool.IsCapturingInput && !ConnectTool.IsCapturingPoints && !placement.placing &&
            !KeyboardTransformTool.IsEditingText() && !InputEvent.IsNoclipMovementCaptured &&
            FindFirstObjectByType<BuildsManager>()?.IsFileOperationInProgress != true;

        public void OpenImportSettings() {
            if (!CanStart) return;
            CancelMode();
            Simulation.SimulationTool.ExitForEdit();
            options = true;
            var panel = CreatePanel("Import Custom Model", true, 340);
            AddLabel(panel, "OBJ and STL geometry is embedded in your build.\nChoose the source file's units before importing.", 50, 70);
            units = Mathf.Clamp(PlayerPrefs.GetInt("customModelUnits", 0), 0, 2);
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
            PlayerPrefs.SetInt("customModelUnits", units);
            int request = generation;
            busy = true;
            ClosePanel();
            try {
                var paths = await NativeFileDialogs.OpenModelAsync(PlayerPrefs.GetString(DirectoryKey, ""));
                if (this == null || request != generation || paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;
                await ReadAndCreate(paths[0], UnitScales[units], request);
            }
            catch (Exception exception) { ReportFailure(exception); }
            finally { if (this != null) { busy = false; finishedFrame = Time.frameCount; ClosePanel(); } }
        }

        // Also used by validation and integrations that already have a file path.
        public async Task<GameObject> ImportPathAsync(string path, float inchesPerUnit) {
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
            PlayerPrefs.SetString(DirectoryKey, Path.GetDirectoryName(path) ?? "");
            FindFirstObjectByType<PartListOutput>()?.CalculatePartsList();
            InteractionFeedback.Notify($"Imported {data.name}. G: move · {mountInput.GetCurrentKeybind()}: add mounting points.");
            return obj;
        }

        private static void ReportFailure(Exception exception) {
            Debug.LogWarning("Could not import model: " + exception.Message);
            InteractionFeedback.Notify("Could not import model: " + exception.Message);
        }

        public void ToggleMounts() {
            if (placingMounts) { CancelMode(); return; }
            if (!CanStart || options) return;
            Simulation.SimulationTool.ExitForEdit();
            selectionManager.ClearCurrent(false, true, true);
            placingMounts = true; startFrame = Time.frameCount;
            var panel = CreatePanel("Add Mounting Points", false, 340);
            AddLabel(panel, "Click model surfaces repeatedly. The surface normal sets the screw axis. Dimensions are in inches.", 45, 75);
            AddLabel(panel, "Diameter", 125, 30, 120);
            diameterField = AddField(panel, "0.169", 125);
            AddLabel(panel, "Depth", 170, 30, 120);
            depthField = AddField(panel, "0.1", 170);
            var typeButton = AddButton(panel, "", 215, () => { threaded = !threaded; UpdateTypeLabel(); });
            typeLabel = typeButton.GetComponentInChildren<Text>();
            UpdateTypeLabel();
            validationLabel = AddLabel(panel, "", 258, 35);
            AddButton(panel, "Done", 295, CancelMode);
            preview = new GameObject("Mounting point preview", typeof(MeshFilter), typeof(MeshRenderer));
            preview.layer = LayerMask.NameToLayer("Ignore Raycast");
            preview.GetComponent<MeshFilter>().sharedMesh = CustomMountPoint.RingMesh;
            preview.GetComponent<MeshRenderer>().sharedMaterial = Resources.Load<Material>("Custom Models/Mount");
            preview.SetActive(false);
        }

        private void UpdateTypeLabel() => typeLabel.text = threaded ? "Type: Threaded (screw anchor)" : "Type: Through hole";

        private void LateUpdate() {
            if (!options && !placingMounts && !busy) return;
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true && !NativeFileDialogs.IsOpen) { CancelMode(); return; }
            if (!placingMounts) return;
            preview.SetActive(false);
            if (RebindAction.Rebinding || Mouse.current == null || !Application.isFocused ||
                KeyboardTransformTool.IsEditingText() || MouseInput.overUI || InputEvent.IsNoclipMovementCaptured ||
                orbitInput.IsCurrentlyPressed || panInput.IsCurrentlyPressed || noclipInput.IsCurrentlyPressed || !MouseInput.withinScreen) return;
            UpdateMountPlacement();
        }

        private void UpdateMountPlacement() {
            if (!ReadDimension(diameterField.text, out float diameter) || !ReadDimension(depthField.text, out float depth)) {
                validationLabel.text = "Enter dimensions greater than 0, up to 100 in.";
                return;
            }
            validationLabel.text = "";
            // Cast against bodies so previous mounting points do not obscure the surface.
            if (!Physics.Raycast(refCamera.ScreenPointToRay(MouseInput.Position), out var hit, Mathf.Infinity, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore) ||
                !hit.collider.TryGetComponent<CustomModel>(out var model) || model.gameObject.IsDeleted()) return;
            preview.transform.SetPositionAndRotation(hit.point + hit.normal * .004f, Quaternion.LookRotation(hit.normal));
            preview.transform.localScale = Vector3.one * diameter;
            preview.SetActive(true);
            if (Time.frameCount == startFrame || !Mouse.current.leftButton.wasPressedThisFrame) return;
            var point = model.AddMount(hit.point, hit.normal, diameter, depth, threaded);
            if (point != null) point.RecordAdded();
            else InteractionFeedback.Notify("A mounting point already exists here.");
        }

        private static bool ReadDimension(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 100;

        public static void CancelActive() {
            if (instance != null && (instance.options || instance.busy || instance.placingMounts)) instance.CancelMode();
        }
        private void CancelMode() {
            generation++;
            options = placingMounts = false;
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
        private void OnDisable() => CancelMode();
        private void OnDestroy() {
            if (importInput != null) importInput.performed -= OpenImportSettings;
            if (mountInput != null) mountInput.performed -= ToggleMounts;
            if (instance == this) instance = null;
        }
    }
}
