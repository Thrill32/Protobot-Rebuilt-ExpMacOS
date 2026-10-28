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
        [SerializeField] private InputEvent holeSnapInput;
        [SerializeField] private InputEvent oppositeHoleInput;
        [SerializeField] private MouseCast mouseCast;
        [SerializeField] private HoleFace holePreview;
        [SerializeField] private Image moveSourceIcon;
        [SerializeField] private Material holeMoveMaterial;
        [SerializeField] private InputEvent orbitInput;
        [SerializeField] private InputEvent panInput;
        [SerializeField] private InputEvent noclipInput;
        [SerializeField] private ObjectDuplicator duplicator;

        [Tooltip("Show a world-axis guide while G is constrained to X, Y or Z.")]
        public bool showConstraintAxis = true;

        private static KeyboardTransformTool instance;
        private static int finishedFrame = -1;
        private readonly TransformGesture gesture = new();
        private readonly MoveRotation moveRotation = new();
        private MoveTarget currentMoveTarget;
        private float attachmentTwist;
        private readonly TransformNumericInput numericInput = new();
        private Quaternion initialOrientation;
        private float rotationDegrees;
        private readonly List<Transform> roots = new();
        private readonly List<Vector3> positions = new();
        private readonly List<Quaternion> rotations = new();
        private List<GameObject> bodies;
        private List<IElement> before;
        private readonly List<GameObject> duplicates = new();
        private ISelection selection;
        private Vector3 origin;
        private Vector3 grabOrigin;
        private Vector3 axis;
        private Quaternion grabRotation = Quaternion.identity;
        private bool holeSnapping;
        private bool hasSourceHole;
        private bool hasMoveTarget;
        private Vector3 sourceHoleOrigin;
        private Quaternion sourceHoleOrientation;
        private HoleMovePreview movePreview;
        private MoveTargetPreview targetPreview;
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

        public static bool BlocksUINavigation => IsCapturingInput ||
            (instance != null && instance.isActiveAndEnabled && instance.placement != null && instance.placement.rotating);

        public static bool AllowsCameraNavigation => instance != null && instance.isActiveAndEnabled &&
            instance.active && instance.holeSnapping && !instance.settling && !instance.numericInput.IsOpen && !instance.moveRotation.NumericInput.IsOpen;

        public static bool BlocksCameraNavigation => IsCapturingInput && !AllowsCameraNavigation;

        public bool IsCameraNavigationHeld => InputEvent.IsNoclipMovementCaptured ||
            CameraInputHeld(orbitInput) || CameraInputHeld(panInput) || CameraInputHeld(noclipInput);

        private bool IsNavigatingCamera => AllowsCameraNavigation && IsCameraNavigationHeld;

        private static bool CameraInputHeld(InputEvent input) => input != null && input.IsCurrentlyPressed && AllowsInput(input);

        // Keep modifiers available, but consume selection, undo/redo, tool changes,
        // and camera clicks until the confirmation/cancellation frame has ended.
        public static bool AllowsInput(InputEvent input) {
            if (!IsCapturingInput || instance == null)
                return true;
            if (instance.numericInput.IsOpen || instance.moveRotation.NumericInput.IsOpen || instance.placement?.IsTypingRotation == true)
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
            if (input == instance.oppositeHoleInput && instance.holeSnapping)
                return true;
            if (AllowsCameraNavigation && (input == instance.orbitInput || input == instance.panInput || input == instance.noclipInput))
                return true;
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
            if (holeSnapInput != null) holeSnapInput.performed += ToggleHoleSnap;
        }

        private void Grab() => Begin(false);
        private void Rotate() {
            if (active && !rotating && holeSnapping) ToggleMoveRotation();
            else Begin(true);
        }

        private void Begin(bool rotate) {
            if (!isActiveAndEnabled || placement.placing || ConnectTool.IsCapturingPoints ||
                RebindAction.Rebinding || InputEvent.IsNoclipMovementCaptured || !Application.isFocused ||
                Mouse.current == null || MouseInput.overUI || IsEditingText())
                return;

            BeginSelection(rotate);
        }

        private void BeginSelection(bool rotate) {
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
            grabOrigin = origin;
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
            holeSnapping = hasSourceHole = hasMoveTarget = false;
            moveRotation.End();
            attachmentTwist = 0;
            HideTargetPreview();
            grabRotation = Quaternion.identity;
            displayedOffset = targetOffset = Vector3.zero;
            displayedRotation = targetRotation = Quaternion.identity;
            gesture.Begin(refCamera, origin, MouseInput.Position);
            axis = rotate ? gesture.DefaultRotationAxis : Vector3.zero;
            if (selection is HoleSelection selectedHole && selectedHole.holeFace != null) {
                selectedHole.holeFace.Refresh();
                sourceHoleOrigin = selectedHole.holeFace.position;
                sourceHoleOrientation = selectedHole.holeFace.LookRotation;
                hasSourceHole = true;
            }
        }

        private void ToggleHoleSnap() {
            if (!active || rotating || settling || numericInput.IsOpen || moveRotation.NumericInput.IsOpen) return;
            if (holeSnapping) {
                LeaveHoleSnap();
                return;
            }
            FreezeDisplayedPose();
            holeSnapping = true;
            hasMoveTarget = false;
            axis = Vector3.zero;
            if (hasSourceHole) {
                RebaseOrigin(sourceHoleOrigin);
                ShowMovePreview();
            }
        }

        private void FreezeDisplayedPose() {
            smoothingTween?.Kill();
            smoothingTween = null;
            targetOffset = displayedOffset;
            targetRotation = displayedRotation;
        }

        private void LeaveHoleSnap() {
            StopMoveRotation();
            FreezeDisplayedPose();
            HideMovePreview();
            holeSnapping = hasMoveTarget = false;
            HideTargetPreview();
            axis = Vector3.zero;
            grabRotation = targetRotation;
            RebaseOrigin(grabOrigin);
            gesture.Begin(refCamera, origin + targetOffset, MouseInput.Position);
        }

        private void RebaseOrigin(Vector3 newOrigin) {
            displayedOffset = origin + displayedOffset + displayedRotation * (newOrigin - origin) - newOrigin;
            targetOffset = origin + targetOffset + targetRotation * (newOrigin - origin) - newOrigin;
            origin = newOrigin;
        }

        private bool IsMoving(Transform candidate) => roots.Any(root => root != null &&
            (candidate == root || candidate.IsChildOf(root)));

        private bool TryGetMoveTarget(bool source, out MoveTarget target) {
            target = default;
            if (MouseInput.overUI) return false;
            var ray = mouseCast != null ? mouseCast.ray : refCamera.ScreenPointToRay(MouseInput.Position);
            int mask = mouseCast != null ? mouseCast.layerMask : LayerMask.GetMask("Default", "HoleCollisions");
            bool opposite = oppositeHoleInput != null && oppositeHoleInput.IsCurrentlyPressed;
            return MoveTargeting.TryRaycast(ray, mask, opposite, !source, source ? null : IsMoving, out target) &&
                (!source || IsMoving(target.Body.transform));
        }

        private bool PickSourceHole() {
            if (!TryGetMoveTarget(true, out var target)) return false;
            sourceHoleOrigin = origin + Quaternion.Inverse(displayedRotation) * (target.face.position - origin - displayedOffset);
            sourceHoleOrientation = Quaternion.Inverse(displayedRotation) * target.face.rotation;
            hasSourceHole = true;
            HideTargetPreview();
            RebaseOrigin(sourceHoleOrigin);
            ShowMovePreview();
            return true;
        }

        private void ShowMovePreview() {
            HideMovePreview();
            movePreview = new HoleMovePreview(bodies, placement, holeMoveMaterial, moveSourceIcon, refCamera,
                origin + displayedOffset, displayedRotation);
        }

        private void HideMovePreview() {
            movePreview?.Dispose();
            movePreview = null;
        }

        private void UpdateMovePreview() => movePreview?.UpdatePose(refCamera, origin + displayedOffset, displayedRotation);

        private void PreviewSourceHole() {
            if (TryGetMoveTarget(true, out var target)) ShowTargetPreview(target);
            else HideTargetPreview();
        }

        private void ShowTargetPreview(MoveTarget target) {
            targetPreview ??= new MoveTargetPreview(holePreview, holeMoveMaterial);
            targetPreview.Show(target);
        }

        private void ApplyTarget(MoveTarget target) {
            ShowTargetPreview(target);
            var aligned = MoveTargeting.Align(new Pose(sourceHoleOrigin, sourceHoleOrientation), target.face,
                new Pose(origin, Quaternion.identity));
            aligned = MoveRotation.Twist(aligned, target.face, attachmentTwist);
            SetTarget(aligned.position - origin, aligned.rotation);
            currentMoveTarget = target;
        }

        private void ToggleMoveRotation() {
            if (!active || settling || numericInput.IsOpen || !hasSourceHole || IsNavigatingCamera) return;
            if (moveRotation.Active) {
                StopMoveRotation();
                return;
            }
            Apply(MouseInput.Position);
            moveRotation.Begin(refCamera, new Pose(origin + targetOffset, targetRotation),
                hasMoveTarget ? currentMoveTarget.face : null, MouseInput.Position, attachmentTwist,
                targetRotation * initialOrientation);
        }

        private void StopMoveRotation() {
            if (!moveRotation.Active) return;
            Apply(MouseInput.Position);
            if (moveRotation.Attachment.HasValue)
                attachmentTwist = TransformSnapping.WrapAngle(attachmentTwist + moveRotation.Degrees);
            grabRotation = targetRotation;
            smoothingTween?.Complete();
            moveRotation.End();
        }

        private void HideTargetPreview() {
            targetPreview?.Hide();
            if (holePreview != null) holePreview.gameObject.SetActive(false);
        }

        private void BeginDuplication() {
            if (duplicator == null || !duplicator.IsDuplicateHeld)
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

            HideTargetPreview();
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (RebindAction.Rebinding || !Application.isFocused || keyboard == null || mouse == null ||
                selectionManager.current != selection || roots.Any(root => root == null) || placement.placing) {
                Finish(false);
                return;
            }

            HandleInput(keyboard, mouse);
            UpdateMovePreview();
        }

        private void HandleInput(Keyboard keyboard, Mouse mouse) {
            HideTargetPreview();
            if (keyboard.escapeKey.wasPressedThisFrame || (!holeSnapping && mouse.rightButton.wasPressedThisFrame)) {
                Finish(false);
                return;
            }

            if (settling)
                return;

            if (moveRotation.Active && moveRotation.HandleNumeric(keyboard, MouseInput.Position, out var confirmed)) {
                if (confirmed.HasValue) {
                    rotationDegrees = moveRotation.Degrees;
                    SetTarget(confirmed.Value.position - origin, confirmed.Value.rotation);
                    Finish(true);
                }
                return;
            }

            if (!moveRotation.Active && (keyboard.slashKey.wasPressedThisFrame || keyboard.numpadDivideKey.wasPressedThisFrame)) {
                if (holeSnapping) LeaveHoleSnap();
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

            if (!moveRotation.Active && TransformGesture.TryReadAxis(out var pressedAxis)) {
                ChangeAxis(pressedAxis);
            }

            // Camera gestures must not pick/confirm a hole or move the rigid set.
            if (IsNavigatingCamera) {
                if (moveRotation.Active) moveRotation.Pause(MouseInput.Position);
                else hasMoveTarget = false;
                FreezeDisplayedPose();
                return;
            }

            if (holeSnapping && !hasSourceHole) {
                PreviewSourceHole();
                if (mouse.leftButton.wasPressedThisFrame && !MouseInput.overUI) PickSourceHole();
                return;
            }

            Apply(MouseInput.Position);
            if ((mouse.leftButton.wasPressedThisFrame && !MouseInput.overUI) ||
                keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) {
                if (!holeSnapping || hasMoveTarget || moveRotation.Active) Finish(true);
            }
            else WrapAxisCursor(mouse);
        }

        private void WrapAxisCursor(Mouse mouse) {
            if (!active || rotating || settling || holeSnapping || numericInput.IsOpen || axis == Vector3.zero ||
                Cursor.lockState == CursorLockMode.Locked) return;
            var viewport = refCamera.pixelRect;
            viewport = Rect.MinMaxRect(Mathf.Max(0f, viewport.xMin), Mathf.Max(0f, viewport.yMin),
                Mathf.Min(Screen.width, viewport.xMax), Mathf.Min(Screen.height, viewport.yMax));
            var position = mouse.position.ReadValue();
            if (!TransformCursor.TryWrap(position, viewport, out var wrapped)) return;
            gesture.RebasePointer(position, wrapped);
            mouse.WarpCursorPosition(wrapped);
            // Warping the OS cursor updates Input System on the next event.
            // Update its position now so this frame's info UI stays beside it.
            UnityEngine.InputSystem.LowLevel.InputState.Change(mouse.position, wrapped);
        }

        private void ChangeAxis(Vector3 pressedAxis) {
            if (holeSnapping) LeaveHoleSnap();
            if (!rotating && gesture.Origin != grabOrigin) {
                gesture.Begin(refCamera, grabOrigin, MouseInput.Position);
                gesture.Resume(MouseInput.Position, targetOffset, 0, Vector3.zero);
            }
            axis = axis == pressedAxis ? (rotating ? gesture.DefaultRotationAxis : Vector3.zero) : pressedAxis;
        }

        private void Apply(Vector2 mouse) {
            if (moveRotation.Active) {
                if (MouseInput.overUI) {
                    moveRotation.Pause(mouse);
                    return;
                }
                if (moveRotation.Attachment.HasValue) ShowTargetPreview(currentMoveTarget);
                var pose = moveRotation.Evaluate(mouse, TransformSnapping.Active);
                rotationDegrees = moveRotation.Degrees;
                SetTarget(pose.position - origin, pose.rotation);
                return;
            }
            if (holeSnapping) {
                hasMoveTarget = TryGetMoveTarget(false, out var target);
                if (hasMoveTarget) ApplyTarget(target);
                else HideTargetPreview();
                return;
            }
            var rotation = rotating
                ? gesture.Rotation(mouse, axis, Quaternion.identity, TransformSnapping.Active)
                : grabRotation;
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
                rotation = grabRotation;
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
            UpdateMovePreview();
        }

        private void Finish(bool confirm) {
            numericInput.Close();
            if (!active)
                return;
            HideTargetPreview();
            moveRotation.End();
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
                HideMovePreview();
                smoothingTween?.Kill();
                smoothingTween = null;
                for (int i = 0; i < roots.Count; i++) {
                    if (roots[i] != null)
                        roots[i].SetPositionAndRotation(positions[i], rotations[i]);
                }
                ObjectDuplicator.DiscardCopies(duplicates);
            }
            active = false;
            settling = false;
            holeSnapping = false;
            finishedFrame = Time.frameCount;
        }

        private void CompleteConfirmation() {
            displayedOffset = targetOffset;
            displayedRotation = targetRotation;
            ApplyDisplayedTransform();
            HideMovePreview();
            bool changed = roots.Where((root, i) => root != null &&
                (Vector3.Distance(root.position, positions[i]) > 0.00001f ||
                 Quaternion.Angle(root.rotation, rotations[i]) > 0.0001f)).Any();
            if (changed) {
                StateSystem.AddElements(before);
                StateSystem.AddEmptyState();
                StateSystem.AddElements(CaptureState());
            }
            else {
                ObjectDuplicator.DiscardCopies(duplicates);
            }
            duplicates.Clear();
            smoothingTween = null;
            active = false;
            settling = false;
            holeSnapping = false;
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

        private string ConstraintLabel => holeSnapping ? "Hole" : axis == Vector3.right ? "X" :
            axis == Vector3.up ? "Y" : axis == Vector3.forward ? "Z" : "Free";

        public static string ControlsText => instance != null && instance.isActiveAndEnabled && instance.active
            ? instance.GetControlsText() : null;

        private string GetControlsText() {
            if (moveRotation.Active && moveRotation.NumericInput.IsOpen)
                return "Rotate · Exact entry\nEnter: confirm    /: value mode    Esc: cancel";
            if (moveRotation.Active)
                return $"Rotate · {(moveRotation.Attachment.HasValue ? "Attachment axis" : "Camera axis")}\n" +
                    $"/: type angle    {rotateInput.GetCurrentKeybind()}: resume positioning    Click / Enter: confirm    Esc: cancel\nCamera controls available";
            string snap = numericInput.IsOpen ? "Exact entry" : holeSnapping ? "Exact alignment" : TransformSnapping.Active ? "Snap on" : "Snap off";
            string holeKey = holeSnapInput != null ? holeSnapInput.GetCurrentKeybind() : "H";
            string help = "X/Y/Z: axis    /: type value";
            if (!rotating) help += $"    {holeKey}: {(holeSnapping ? "free move" : "hole snap")}";
            string oppositeKey = oppositeHoleInput != null ? oppositeHoleInput.GetCurrentKeybind() : "Space";
            if (holeSnapping) help += (hasSourceHole ? "\nHover an outside hole or screw" : "\nClick a source hole on the selected parts") +
                $" · Hold {oppositeKey}: opposite hole face\nCamera controls available";
            if (holeSnapping && hasSourceHole) help += $"    {rotateInput.GetCurrentKeybind()}: rotate";
            return $"{(rotating ? "Rotate" : "Grab")} · {ConstraintLabel} · {snap}\n" +
                help + (holeSnapping ? "\nClick / Enter: confirm    Esc: cancel" : "\nClick / Enter: confirm    Esc / Right click: cancel");
        }

        private void OnGUI() {
            if (!active)
                return;
            if (moveRotation.Active) {
                moveRotation.DrawFeedback();
                return;
            }
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
            else TransformFeedback.Draw(rotating || moveRotation.Active ? TransformFeedback.Angle(rotationDegrees) : TransformFeedback.Distance(targetOffset, axis));
        }

        private void OnDestroy() {
            HideMovePreview();
            targetPreview?.Dispose();
            grabInput.performed -= Grab;
            rotateInput.performed -= Rotate;
            if (holeSnapInput != null) holeSnapInput.performed -= ToggleHoleSnap;
            if (instance == this)
                instance = null;
        }
    }
}
