using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Protobot.Tools {
    /// <summary>Blender-style modal grab/rotation of the current rigid selection.</summary>
    public sealed class KeyboardTransformTool : MonoBehaviour {
        [SerializeField] private SelectionManager selectionManager;
        [SerializeField] private Placement placement;
        [SerializeField] private Camera refCamera;
        [SerializeField] private InputEvent grabInput;
        [SerializeField] private InputEvent rotateInput;
        [SerializeField] private InputEvent holeSnapInput; // Keep the saved Grab Hole Snap binding ID.
        [SerializeField] private ToolToggle moveTool;
        [SerializeField] private MoveToolManager moveToolManager;
        [SerializeField] private InputEvent orbitInput;
        [SerializeField] private InputEvent panInput;
        [SerializeField] private InputEvent noclipInput;
        [SerializeField] private ObjectDuplicator duplicator;

        [Tooltip("Show a world-axis guide while G is constrained to X, Y or Z.")]
        public bool showConstraintAxis = true;

        private static KeyboardTransformTool instance;
        private static int finishedFrame = -1;
        private readonly TransformGesture gesture = new();
        private readonly TransformNumericInput numericInput = new();
        private Quaternion initialOrientation;
        private float rotationDegrees;
        private readonly List<Transform> roots = new();
        private readonly List<Vector3> positions = new();
        private readonly List<Quaternion> rotations = new();
        private List<GameObject> bodies;
        private List<IElement> before;
        private bool duplicateForGesture;
        private readonly List<GameObject> duplicates = new();
        private ISelection selection;
        private Vector3 origin;
        private Vector3 axis;
        private bool active;
        private bool rotating;
        private bool settling;
        private Tween smoothingTween;
        private Vector3 displayedOffset;
        private Quaternion displayedRotation = Quaternion.identity;
        private Vector3 targetOffset;
        private Quaternion targetRotation = Quaternion.identity;
        private const float SmoothingDuration = 0.25f; // Same duration as standard axis dragging.

        public static bool IsCapturingInput => (instance != null && instance.isActiveAndEnabled && (instance.active || instance.placement?.IsTypingRotation == true)) ||
            finishedFrame == Time.frameCount;

        internal static void CaptureCurrentFrame() => finishedFrame = Time.frameCount;

        internal static bool CancelActive() {
            if (instance == null || !instance.active) return false;
            instance.Finish(false);
            return true;
        }

        public static bool BlocksUINavigation => IsCapturingInput ||
            (instance != null && instance.isActiveAndEnabled && instance.placement != null && instance.placement.rotating);

        public static bool BlocksCameraNavigation => IsCapturingInput;

        public bool IsCameraNavigationHeld => InputEvent.IsNoclipMovementCaptured ||
            CameraInputHeld(orbitInput) || CameraInputHeld(panInput) || CameraInputHeld(noclipInput);

        private static bool CameraInputHeld(InputEvent input) => input != null && input.IsCurrentlyPressed && AllowsInput(input);

        // Keep modifiers available, but consume selection, undo/redo, tool changes,
        // and camera clicks until the confirmation/cancellation frame has ended.
        public static bool AllowsInput(InputEvent input) {
            if (!IsCapturingInput || instance == null)
                return true;
            if (instance.numericInput.IsOpen || instance.placement?.IsTypingRotation == true)
                return false;
            // Raw modal axis keys win even over allowed actions rebound to X/Y/Z.
            var keys = Keyboard.current;
            if (keys != null && ((keys.xKey.isPressed && input.UsesControl(keys.xKey)) ||
                (keys.yKey.isPressed && input.UsesControl(keys.yKey)) ||
                (keys.zKey.isPressed && input.UsesControl(keys.zKey))))
                return false;
            if (input == instance.holeSnapInput)
                return instance.active && !instance.rotating && !instance.settling;
            if (!instance.rotating && instance.holeSnapInput != null && instance.holeSnapInput.IsCurrentlyPressed &&
                SharesPressedButton(input, instance.holeSnapInput))
                return false;
            if (input == instance.grabInput || input == instance.rotateInput ||
                input.name == "Snap Bypass" || input.name == "Grouping Bypass" || input.name == "Toggle Grid" || input.name == "Duplicate")
                return true;
            return false;
        }

        private static bool SharesPressedButton(InputEvent input, InputEvent priority) {
            return SharesPressedButton(input, priority.defaultAction) || SharesPressedButton(input, priority.rebindAction?.action);
        }

        private static bool SharesPressedButton(InputEvent input, InputAction priority) {
            if (priority == null || !priority.enabled) return false;
            foreach (var control in priority.controls) {
                if (control is not UnityEngine.InputSystem.Controls.ButtonControl button || !button.isPressed)
                    continue;
                if (control is UnityEngine.InputSystem.Controls.KeyControl key &&
                    (key.keyCode == Key.LeftCtrl || key.keyCode == Key.RightCtrl ||
                     key.keyCode == Key.LeftShift || key.keyCode == Key.RightShift ||
                     key.keyCode == Key.LeftAlt || key.keyCode == Key.RightAlt ||
                     key.keyCode == Key.LeftMeta || key.keyCode == Key.RightMeta))
                    continue;
                if (input.UsesControl(control)) return true;
            }
            return false;
        }

        private void Awake() {
            instance = this;
            grabInput.performed += Grab;
            rotateInput.performed += Rotate;
            if (holeSnapInput != null) holeSnapInput.performed += MoveFromGrab;
        }

        private void Grab() => Begin(false);
        private void Rotate() => Begin(true);

        private void Begin(bool rotate) {
            if (!isActiveAndEnabled || placement.placing || ConnectTool.IsCapturingPoints || CustomModelTool.IsCapturingInput ||
                RebindAction.Rebinding || InputEvent.IsNoclipMovementCaptured || !Application.isFocused ||
                Mouse.current == null || MouseInput.overUI || IsEditingText())
                return;

            BeginSelection(rotate);
        }

        private void BeginSelection(bool rotate) => BeginReservedSelection(rotate, false);

        private void BeginReservedSelection(bool rotate, bool reservedForCollaboration) {
            if (CollaborationSession.IsShared && !CollaborationSession.CanEdit && !reservedForCollaboration) return;
            if (!reservedForCollaboration) duplicateForGesture = duplicator != null && duplicator.IsDuplicateHeld;
            if (active) {
                if (settling || rotating == rotate)
                    return;
                Finish(false);
            }

            selection = selectionManager.current;
            if (selection?.gameObject == null)
                return;
            if (selection is HoleSelection holeSelection && holeSelection.holeFace != null)
                holeSelection.holeFace.Refresh();

            roots.Clear();
            positions.Clear();
            rotations.Clear();
            bodies = new List<GameObject>();
            origin = selection.gameObject.transform.position;
            initialOrientation = selection.gameObject.transform.rotation;

            if (selection is MultiSelection multi && multi.pivot != null) {
                roots.Add(multi.pivot.transform);
                bodies.AddRange(multi.pivot.GetComponentsInChildren<SavedObject>().Select(saved => saved.gameObject));
            }
            else {
                var body = selection is HoleSelection hole ? hole.holeData?.part : selection.gameObject;
                if (body == null)
                    return;
                AddBodies(body);
                foreach (var movingBody in bodies) {
                    var root = !selection.individual && movingBody.TryGetGroup(out Transform group) ? group : movingBody.transform;
                    if (!roots.Contains(root))
                        roots.Add(root);
                }
                var nestedRoots = roots.Where(candidate => roots.Any(other =>
                    candidate != other && candidate.IsChildOf(other))).ToList();
                roots.RemoveAll(nestedRoots.Contains);
            }

            if (bodies.Count == 0)
                return;

            if (CollaborationSession.IsShared) {
                if (!reservedForCollaboration) {
                    var requestedSelection = selection;
                    CollaborationSession.ReservePose(bodies, () => {
                        if (selectionManager.current == requestedSelection) BeginReservedSelection(rotate, true);
                        else CollaborationSession.CancelPose();
                    });
                    return;
                }
                if (!CollaborationSession.HasPoseReservation(bodies)) { CollaborationSession.CancelPose(); return; }
            }

            // A MultiPivot is already a movement root. Never parent it under a
            // temporary pivot or transform its children a second time.
            foreach (var root in roots) {
                foreach (var saved in root.GetComponentsInChildren<SavedObject>()) {
                    if (!bodies.Contains(saved.gameObject))
                        bodies.Add(saved.gameObject);
                }
                root.DOKill();
                positions.Add(root.position);
                rotations.Add(root.rotation);
            }
            foreach (var body in bodies)
                body.transform.DOKill();
            duplicates.Clear();
            before = CaptureState();
            BeginDuplication();
            rotating = rotate;
            numericInput.Close();
            rotationDegrees = 0f;
            active = true;
            settling = false;
            displayedOffset = targetOffset = Vector3.zero;
            displayedRotation = targetRotation = Quaternion.identity;
            gesture.Begin(refCamera, origin, MouseInput.Position);
            axis = rotate ? gesture.DefaultRotationAxis : Vector3.zero;
        }

        private void MoveFromGrab() {
            if (!active || rotating || settling || numericInput.IsOpen ||
                moveTool == null || moveToolManager == null) return;

            // Discard the uncommitted grab (including Alt copies) before using
            // the same selection, toolbar and placement path as tool 2.
            Finish(false);
            finishedFrame = -1;
            try {
                moveTool.Toggle(true);
                moveToolManager.StartMoving();
            }
            finally {
                // The shortcut still owns this frame: H must not toggle the grid,
                // and an input sharing its binding must not confirm placement.
                CaptureCurrentFrame();
            }
        }

        private void BeginDuplication() {
            if (duplicator == null || !(CollaborationSession.IsShared ? duplicateForGesture : duplicator.IsDuplicateHeld))
                return;
            duplicates.AddRange(duplicator.CreateCopies(bodies));
            foreach (var copy in duplicates)
                before.Add(new ObjectElement(copy) { existing = false });
        }

        private void AddBodies(GameObject obj) {
            if (obj == null)
                return;
            foreach (var connected in obj.GetConnectedObjects(true, true, !selection.individual)) {
                if (connected == null)
                    continue;
                foreach (var saved in connected.GetComponentsInChildren<SavedObject>()) {
                    if (!bodies.Contains(saved.gameObject))
                        bodies.Add(saved.gameObject);
                }
            }
        }

        internal static bool IsEditingText() {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && (selected.GetComponent<InputField>() != null ||
                                        selected.GetComponent<TMP_InputField>() != null);
        }

        private void LateUpdate() {
            if (!active)
                return;

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (RebindAction.Rebinding || !Application.isFocused || keyboard == null || mouse == null ||
                selectionManager.current != selection || roots.Any(root => root == null) || placement.placing) {
                Finish(false);
                return;
            }

            HandleInput(keyboard, mouse);
        }

        private void HandleInput(Keyboard keyboard, Mouse mouse) {
            if (keyboard.escapeKey.wasPressedThisFrame) {
                if (!EscapeDeselect.TryHandleEscape()) Finish(false);
                return;
            }
            if (mouse.rightButton.wasPressedThisFrame) {
                Finish(false);
                return;
            }

            if (settling)
                return;

            if (keyboard.slashKey.wasPressedThisFrame || keyboard.numpadDivideKey.wasPressedThisFrame) {
                numericInput.Cycle(!rotating && axis == Vector3.zero);
                if (!numericInput.IsOpen) ResumeMouse();
                return;
            }
            if (numericInput.IsOpen) {
                numericInput.Edit(keyboard);
                if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                    SubmitNumeric();
                return;
            }

            if (TransformGesture.TryReadAxis(out var pressedAxis)) {
                ChangeAxis(pressedAxis);
            }

            Apply(MouseInput.Position);
            if ((mouse.leftButton.wasPressedThisFrame && !MouseInput.overUI) ||
                keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) {
                Finish(true);
            }
            else WrapAxisCursor(mouse);
        }

        private void WrapAxisCursor(Mouse mouse) {
            if (!active || rotating || settling || numericInput.IsOpen || axis == Vector3.zero ||
                Cursor.lockState == CursorLockMode.Locked) return;
            var viewport = refCamera.pixelRect;
            viewport = Rect.MinMaxRect(Mathf.Max(0f, viewport.xMin), Mathf.Max(0f, viewport.yMin),
                Mathf.Min(Screen.width, viewport.xMax), Mathf.Min(Screen.height, viewport.yMax));
            var position = mouse.position.ReadValue();
            if (!TransformCursor.TryWrap(position, viewport, out var wrapped)) return;
            if (CursorWarp.Warp(mouse, wrapped)) gesture.RebasePointer(position, wrapped);
        }

        private void ChangeAxis(Vector3 pressedAxis) {
            axis = axis == pressedAxis ? (rotating ? gesture.DefaultRotationAxis : Vector3.zero) : pressedAxis;
        }

        private void Apply(Vector2 mouse) {
            var rotation = rotating
                ? gesture.Rotation(mouse, axis, Quaternion.identity, TransformSnapping.Active)
                : Quaternion.identity;
            var offset = rotating ? Vector3.zero : gesture.Position(mouse, axis, TransformSnapping.Active) - origin;
            rotationDegrees = rotating ? gesture.RotationDegrees : 0f;
            SetTarget(offset, rotation);
        }

        private void ResumeMouse() => gesture.Resume(MouseInput.Position, origin + targetOffset - gesture.Origin,
            rotating ? gesture.UnwrappedRotationDegrees : 0f, axis);

        private bool SubmitNumeric() {
            var entryOrigin = rotating ? origin : gesture.Origin;
            bool valid = TransformNumericInput.TryEvaluate(numericInput.Text,
                numericInput.Mode == TransformNumericInput.InputMode.Global, rotating, axis,
                entryOrigin, initialOrientation, out var offset, out var rotation, out float degrees);
            numericInput.Close();
            if (!valid) {
                ResumeMouse();
                return false;
            }
            rotationDegrees = degrees;
            if (!rotating) {
                offset += entryOrigin - origin;
                rotation = Quaternion.identity;
            }
            SetTarget(offset, rotation);
            Finish(true);
            return true;
        }

        private void SetTarget(Vector3 offset, Quaternion rotation) {
            if (Vector3.Distance(offset, targetOffset) < 0.00001f && Quaternion.Angle(rotation, targetRotation) < 0.0001f)
                return;

            smoothingTween?.Kill();
            targetOffset = offset;
            targetRotation = rotation;
            var startOffset = displayedOffset;
            var startRotation = displayedRotation;
            // Interpolate the shared rigid displacement, so independent roots
            // keep their offsets throughout the animation as well as at its end.
            smoothingTween = DOVirtual.Float(0f, 1f, SmoothingDuration, progress => {
                displayedOffset = Vector3.Lerp(startOffset, offset, progress);
                displayedRotation = Quaternion.Slerp(startRotation, rotation, progress);
                ApplyDisplayedTransform();
            }).SetEase(DOTween.defaultEaseType);
        }

        private void ApplyDisplayedTransform() {
            for (int i = 0; i < roots.Count; i++) {
                if (roots[i] != null)
                    roots[i].SetPositionAndRotation(origin + displayedOffset + displayedRotation * (positions[i] - origin), displayedRotation * rotations[i]);
            }
        }

        private void Finish(bool confirm) {
            numericInput.Close();
            if (!active)
                return;
            if (confirm) {
                // Let the same easing finish after confirmation, then snapshot
                // the exact snapped target rather than an in-between frame.
                settling = true;
                if (smoothingTween != null && smoothingTween.IsActive() && !smoothingTween.IsComplete()) {
                    smoothingTween.OnComplete(CompleteConfirmation);
                    return;
                }
                CompleteConfirmation();
                return;
            }
            else {
                smoothingTween?.Kill();
                smoothingTween = null;
                for (int i = 0; i < roots.Count; i++) {
                    if (roots[i] != null)
                        roots[i].SetPositionAndRotation(positions[i], rotations[i]);
                }
                ObjectDuplicator.DiscardCopies(duplicates);
                CollaborationSession.CancelPose();
            }
            active = false;
            settling = false;
            finishedFrame = Time.frameCount;
        }

        private void CompleteConfirmation() {
            smoothingTween?.Kill();
            smoothingTween = null;
            displayedOffset = targetOffset;
            displayedRotation = targetRotation;
            ApplyDisplayedTransform();
            bool changed = roots.Where((root, i) => root != null &&
                (Vector3.Distance(root.position, positions[i]) > 0.00001f ||
                 Quaternion.Angle(root.rotation, rotations[i]) > 0.0001f)).Any();
            if (changed) {
                if (CollaborationSession.IsShared) CollaborationSession.ConfirmPose(bodies, duplicates);
                else {
                    StateSystem.AddElements(before);
                    StateSystem.AddEmptyState();
                    StateSystem.AddElements(CaptureState());
                }
            }
            else {
                ObjectDuplicator.DiscardCopies(duplicates);
                CollaborationSession.CancelPose();
            }
            duplicates.Clear();
            smoothingTween = null;
            active = false;
            settling = false;
            finishedFrame = Time.frameCount;
        }

        private List<IElement> CaptureState() {
            var elements = roots.Where(root => root != null && root.GetComponent<SavedObject>() == null)
                .Select(root => (IElement)new RootElement(root)).ToList();
            elements.AddRange(bodies.Where(body => body != null).Select(body => (IElement)new ObjectElement(body)));
            elements.AddRange(duplicates.Where(copy => copy != null).Select(copy => (IElement)new ObjectElement(copy)));
            return elements;
        }

        // Restore a selection/group pivot without applying its delta to the
        // bodies again. Their world-space ObjectElements animate independently.
        private sealed class RootElement : IElement {
            private readonly Transform root;
            private readonly Vector3 position;
            private readonly Quaternion rotation;

            public RootElement(Transform root) {
                this.root = root;
                position = root.position;
                rotation = root.rotation;
            }

            public void Load() {
                if (root == null)
                    return;
                var children = root.Cast<Transform>().ToArray();
                var positions = children.Select(child => child.position).ToArray();
                var rotations = children.Select(child => child.rotation).ToArray();
                root.SetPositionAndRotation(position, rotation);
                for (int i = 0; i < children.Length; i++)
                    children[i].SetPositionAndRotation(positions[i], rotations[i]);
            }
        }

        private void OnDisable() => Finish(false);

        private string ConstraintLabel => axis == Vector3.right ? "X" :
            axis == Vector3.up ? "Y" : axis == Vector3.forward ? "Z" : "Free";

        public static string ControlsText => instance != null && instance.isActiveAndEnabled && instance.active
            ? instance.GetControlsText() : null;

        private string GetControlsText() {
            string snap = numericInput.IsOpen ? "Exact entry" : TransformSnapping.Active ? "Snap on" : "Snap off";
            string holeKey = holeSnapInput != null ? holeSnapInput.GetCurrentKeybind() : "H";
            string help = "X/Y/Z: axis    /: type value";
            if (!rotating) help += $"    {holeKey}: Move tool";
            return $"{(rotating ? "Rotate" : "Grab")} · {ConstraintLabel} · {snap}\n" +
                help + "\nClick / Enter: confirm    Esc / Right click: cancel";
        }

        private void OnGUI() {
            if (!active)
                return;
            if (showConstraintAxis && !rotating && axis != Vector3.zero)
                TransformFeedback.DrawAxis(refCamera, gesture.Origin, axis);
            string constraint = ConstraintLabel;
            if (numericInput.IsOpen) {
                bool global = numericInput.Mode == TransformNumericInput.InputMode.Global;
                string label = !rotating && axis == Vector3.zero ? "Global position: X, Y, Z (in)" :
                    $"{(global ? "Global" : "Relative")} {constraint} {(rotating ? "rotation (degrees)" : "movement (in)")}";
                string next = global ? "/: mouse control" : "/: global value";
                TransformFeedback.DrawNumeric(label, numericInput.DisplayText, $"Enter: apply exact value\n{next}    Invalid input: mouse control");
            }
            else TransformFeedback.Draw(rotating ? TransformFeedback.Angle(rotationDegrees) : TransformFeedback.Distance(targetOffset, axis));
        }

        private void OnDestroy() {
            grabInput.performed -= Grab;
            rotateInput.performed -= Rotate;
            if (holeSnapInput != null) holeSnapInput.performed -= MoveFromGrab;
            if (instance == this)
                instance = null;
        }
    }
}
