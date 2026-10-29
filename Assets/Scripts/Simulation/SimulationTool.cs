using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using InputEvent = Protobot.InputEvents.InputEvent;

namespace Protobot.Simulation {
    /// <summary>Tool 5 owns transient poses. Only Commit creates an edit/undo state.</summary>
    public sealed class SimulationTool : MonoBehaviour {
        [SerializeField] private Toggle toolbarToggle;
        [SerializeField] private SelectionManager selectionManager;
        [SerializeField] private MouseCast mouseCast;
        [SerializeField] private Placement placement;
        [SerializeField] private Camera refCamera;
        [SerializeField] private InputEvent activateInput, animationInput, playInput, resetInput, fasterInput, slowerInput, commitInput, cancelInput, chainInput;
        [SerializeField] private Material chainMaterial;

        private static SimulationTool instance;
        private static int consumedFrame = -1;
        private readonly List<(InputEvent input, Action action)> subscriptions = new();
        private MechanismSimulation simulation;
        private SimulationPanel panel;
        private bool active, animation, playing;
        private int beganFrame;
        private Vector2 lastPointer;
        private float speed = 1;
        private string status;

        public static bool IsActive => instance != null && instance.active;
        public static bool HasPreview => instance != null && instance.simulation != null;
        public static bool CapturesSelection => IsActive || consumedFrame == Time.frameCount;
        public static bool BlocksCamera => IsActive && !instance.animation && instance.simulation != null;
        public static bool OverPanel => instance != null && instance.panel != null && instance.panel.ContainsPointer;
        public MechanismSimulation Current => simulation;
        public bool IsPlaying => playing;
        public bool AnimationVisible => animation;
        public float Speed => speed;

        private void Awake() {
            instance = this;
            toolbarToggle.onValueChanged.AddListener(SetActive);
            Bind(activateInput, Activate);
            Bind(animationInput, ToggleAnimation);
            Bind(playInput, TogglePlayback);
            Bind(resetInput, ResetMotion);
            Bind(fasterInput, () => SetSpeed(speed * 1.25f));
            Bind(slowerInput, () => SetSpeed(speed / 1.25f));
            Bind(commitInput, Commit);
            Bind(cancelInput, Cancel);
            Bind(chainInput, ChainSelected);
            ChainLoop.MaterialTemplate = chainMaterial;
            panel = new SimulationPanel(this);
        }

        private void Bind(InputEvent input, Action action) {
            if (input == null) return;
            input.performed += action;
            subscriptions.Add((input, action));
        }

        public void ChainSelected() {
            if (RebindAction.Rebinding || KeyboardTransformTool.IsCapturingInput || KeyboardTransformTool.IsEditingText() || placement.placing || InputEvent.IsNoclipMovementCaptured) return;
            var selection = selectionManager.current;
            var selected = selection is MultiSelection multi ? multi.objs : new List<GameObject> { selection is HoleSelection hole ? hole.holeData?.part : selection?.gameObject };
            var sprockets = selected.Where(b => b != null).SelectMany(b => b.GetComponentsInChildren<SimulationPart>())
                .Where(p => p.kind == DriveKind.Sprocket).Distinct().ToList();
            ExitForEdit();
            ChainLoop.Toggle(sprockets, out var message);
            Protobot.UI.InteractionFeedback.Notify(message);
        }

        public void Activate() {
            if (RebindAction.Rebinding || KeyboardTransformTool.IsCapturingInput || KeyboardTransformTool.IsEditingText() ||
                placement.placing || InputEvent.IsNoclipMovementCaptured) return;
            if (active) ToggleAnimation();
            else toolbarToggle.isOn = true;
        }

        public void SetActive(bool value) {
            if (value && (placement.placing || KeyboardTransformTool.IsCapturingInput)) {
                toolbarToggle.SetIsOnWithoutNotify(false);
                return;
            }
            if (active == value) return;
            active = value;
            if (value) {
                animation = playing = false;
                status = "Select a motor, gear or shaft";
                var selection = selectionManager.current;
                var selected = selection is HoleSelection hole ? hole.holeData?.part : selection?.gameObject;
                if (selection is MultiSelection multi)
                    selected = multi.objs.FirstOrDefault(MechanismSimulation.IsDriver);
                Begin(selected);
            }
            else {
                RestorePreview();
                animation = false;
            }
            panel?.Refresh();
        }

        public bool Begin(GameObject driver) {
            if (!MechanismSimulation.IsDriver(driver)) return false;
            RestorePreview();
            var candidate = MechanismSimulation.Create(driver);
            if (candidate == null) return false;
            if (candidate.HasConflict) { status = "This gear loop has conflicting ratios"; return false; }
            if (!candidate.HasMotion) { status = "Attach a shaft to this motor to preview its output"; return false; }
            simulation = candidate;
            playing = false;
            beganFrame = Time.frameCount;
            lastPointer = Mouse.current != null ? Mouse.current.position.ReadValue() : default;
            status = MechanismSimulation.PartLabel(driver);
            panel?.RebuildTracks();
            return true;
        }

        public void ToggleAnimation() {
            if (!active) return;
            animation = !animation;
            playing = false;
            lastPointer = Mouse.current != null ? Mouse.current.position.ReadValue() : default;
            beganFrame = Time.frameCount;
            panel.Refresh();
        }

        public void TogglePlayback() {
            if (!active || simulation == null) return;
            animation = true;
            playing = !playing;
            panel.Refresh();
        }

        public void SetSpeed(float value) { speed = Mathf.Clamp(value, .1f, 4); panel?.Refresh(); }
        public void Seek(double seconds) { if (simulation == null) return; playing = false; simulation.Evaluate(seconds); panel?.Refresh(); }
        public void ResetMotion() { if (!active) return; Seek(0); }

        public void Commit() {
            if (simulation == null) return;
            playing = false;
            var changed = simulation.tracks.Where(t => t.rotates && t.body != null &&
                (Vector3.Distance(t.body.transform.position, t.start.position) > .00001f || Quaternion.Angle(t.body.transform.rotation, t.start.rotation) > .0001f)).ToList();
            if (changed.Count > 0) {
                foreach (var track in changed)
                    StateSystem.AddElement(new ObjectElement(track.body) { position = track.start.position, rotation = track.start.rotation });
                StateSystem.AddEmptyState();
                ObjectElement.AddObjectElements(changed.Select(t => t.body).ToList());
            }
            simulation = null;
            RefreshConnections();
            consumedFrame = Time.frameCount;
            status = "Pose set · select another motor, gear or shaft";
            panel?.RebuildTracks();
        }

        public void Cancel() {
            RestorePreview();
            consumedFrame = Time.frameCount;
            status = "Preview reverted · select a motor, gear or shaft";
            panel?.RebuildTracks();
        }

        private void RestorePreview() {
            playing = false;
            if (simulation == null) return;
            simulation.Restore();
            simulation = null;
            RefreshConnections();
            panel?.RebuildTracks();
        }

        private static void RefreshConnections() {
            Physics.SyncTransforms();
            foreach (var detector in FindObjectsByType<HoleDetector>(FindObjectsSortMode.None)) detector.RefreshOverlaps();
        }

        public static void ExitForEdit() {
            if (instance == null || !instance.active) return;
            instance.toolbarToggle.isOn = false;
        }

        public static Pose SavedPose(GameObject body) => instance != null && instance.simulation != null &&
            instance.simulation.TryGetOriginal(body, out var pose) ? pose : new Pose(body.transform.position, body.transform.rotation);

        // Resolve ownership before the existing InputEvent dispatch. Tool changes and edits restore first.
        public static bool AllowsInput(InputEvent input) {
            if (!IsActive) return consumedFrame != Time.frameCount;
            if (input.name.StartsWith("Simulation ", StringComparison.Ordinal) || input == instance.activateInput) return true;
            if (input.name == "Grouping Bypass" || input.name == "Snap Bypass") return true;
            if (KeyboardTransformTool.IsEditingText()) return false;
            if (input.name == "Select Object" || input.name == "Select" || input.name.Contains("Select") || input.name == "Place") return false;
            if (input.name == "Orbit" || input.name == "Pan" || input.name == "Noclip Camera") return !BlocksCamera;
            // Selection mouse clicks belong to this tool, regardless of the user's binding names.
            if (Mouse.current != null && (input.UsesControl(Mouse.current.leftButton) || input.UsesControl(Mouse.current.rightButton))) return false;
            ExitForEdit();
            return true;
        }

        private void LateUpdate() {
            if (!active) return;
            if (simulation != null && !simulation.IsValid) Cancel();
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (RebindAction.Rebinding || KeyboardTransformTool.IsEditingText() || !Application.isFocused || mouse == null) return;
            ReadInput(mouse, keyboard);
        }

        private void ReadInput(Mouse mouse, Keyboard keyboard) {
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) { Cancel(); return; }
            if (!animation && mouse.rightButton.wasPressedThisFrame && !OverPanel) { Cancel(); return; }
            if (playing && simulation != null) simulation.Evaluate(simulation.Time + UnityEngine.Time.unscaledDeltaTime * speed);
            bool overUI = OverPanel || (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject());
            var pointer = mouse.position.ReadValue();
            if (simulation != null && !animation && Time.frameCount != beganFrame) {
                if (!OverPanel) {
                    simulation.Evaluate(simulation.Time + (pointer.x - lastPointer.x) / 90.0);
                    var viewport = refCamera.pixelRect;
                    viewport = Rect.MinMaxRect(Mathf.Max(0, viewport.xMin), Mathf.Max(0, viewport.yMin),
                        Mathf.Min(Screen.width, viewport.xMax), Mathf.Min(Screen.height, viewport.yMax));
                    // Horizontal wrapping only; vertical movement does not drive the simulation.
                    if (TransformCursor.TryWrap(pointer, viewport, out var wrapped)) {
                        wrapped.y = pointer.y;
                        mouse.WarpCursorPosition(wrapped);
                        InputState.Change(mouse.position, wrapped);
                        pointer = wrapped;
                    }
                    if (mouse.leftButton.wasPressedThisFrame && !overUI) Commit();
                }
            }
            else if (mouse.leftButton.wasPressedThisFrame && !overUI && !InputEvent.IsNoclipMovementCaptured) {
                var hit = mouseCast.hit;
                var body = hit.collider != null ? hit.collider.GetComponentInParent<SavedObject>()?.gameObject : null;
                if (body != null && Begin(body)) {
                    selectionManager.SetCurrent(new ObjectSelection { gameObject = body, individual = GroupingBypassInput.IsHeld }, true);
                }
            }
            lastPointer = pointer;
            panel.Refresh();
        }

        internal string Status => status;
        internal string ControlsText => animation
            ? $"Timeline: scrub · Set pose: keep changes · Esc: revert · {activateInput.GetCurrentKeybind()}: manual"
            : $"Drag left/right · Click: set pose · Esc: revert · {activateInput.GetCurrentKeybind()}: animation";
        private void OnApplicationFocus(bool focused) { if (!focused && active) Cancel(); }
        private void OnApplicationPause(bool paused) { if (paused && active) Cancel(); }
        private void OnDisable() { if (active) SetActive(false); }
        private void OnDestroy() {
            RestorePreview();
            foreach (var subscription in subscriptions) subscription.input.performed -= subscription.action;
            if (toolbarToggle != null) toolbarToggle.onValueChanged.RemoveListener(SetActive);
            panel?.Dispose();
            if (instance == this) instance = null;
        }
    }
}
