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

        public static bool IsCapturingInput => (instance != null && instance.isActiveAndEnabled && instance.active) ||
            finishedFrame == Time.frameCount;

        // Keep modifiers available, but consume selection, undo/redo, tool changes,
        // and camera clicks until the confirmation/cancellation frame has ended.
        public static bool AllowsInput(InputEvent input) {
            if (!IsCapturingInput || instance == null)
                return true;
            if (instance.numericInput.IsOpen)
                return false;
            if (input == instance.grabInput || input == instance.rotateInput ||
                input.name == "Snap Bypass" || input.name == "Toggle Grid" || input.name == "Duplicate")
                return true;
            return false;
        }

        private void Awake() {
            instance = this;
            grabInput.performed += Grab;
            rotateInput.performed += Rotate;
        }

        private void Grab() => Begin(false);
        private void Rotate() => Begin(true);

        private void Begin(bool rotate) {
            if (!isActiveAndEnabled || placement.placing || ConnectTool.IsCapturingPoints ||
                RebindAction.Rebinding || InputEvent.IsNoclipMovementCaptured || !Application.isFocused ||
                Mouse.current == null || MouseInput.overUI || IsEditingText())
                return;

            if (active) {
                if (settling || rotating == rotate)
                    return;
                Finish(false);
            }

            selection = selectionManager.current;
            if (selection?.gameObject == null)
                return;

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
                    var root = movingBody.TryGetGroup(out Transform group) ? group : movingBody.transform;
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
            displayedOffset = targetOffset = Vector3.zero;
            displayedRotation = targetRotation = Quaternion.identity;
            gesture.Begin(refCamera, origin, MouseInput.Position);
            axis = rotate ? gesture.DefaultRotationAxis : Vector3.zero;
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
            foreach (var connected in obj.GetConnectedObjects(true, true)) {
                if (connected == null)
                    continue;
                foreach (var saved in connected.GetComponentsInChildren<SavedObject>()) {
                    if (!bodies.Contains(saved.gameObject))
                        bodies.Add(saved.gameObject);
                }
            }
        }

        private static bool IsEditingText() {
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
                selectionManager.current != selection || roots.Any(root => root == null) || placement.placing ||
                keyboard.escapeKey.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame) {
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

            if (TransformGesture.TryReadAxis(out var pressedAxis))
                axis = axis == pressedAxis ? (rotating ? gesture.DefaultRotationAxis : Vector3.zero) : pressedAxis;

            Apply(MouseInput.Position);
            if ((mouse.leftButton.wasPressedThisFrame && !MouseInput.overUI) ||
                keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                Finish(true);
        }

        private void Apply(Vector2 mouse) {
            var rotation = rotating
                ? gesture.Rotation(mouse, axis, Quaternion.identity, TransformSnapping.Active)
                : Quaternion.identity;
            var offset = rotating ? Vector3.zero : gesture.Position(mouse, axis, TransformSnapping.Active) - origin;
            rotationDegrees = rotating ? gesture.RotationDegrees : 0f;
            SetTarget(offset, rotation);
        }

        private void ResumeMouse() => gesture.Resume(MouseInput.Position, targetOffset, rotationDegrees, axis);

        private bool SubmitNumeric() {
            bool valid = TransformNumericInput.TryEvaluate(numericInput.Text,
                numericInput.Mode == TransformNumericInput.InputMode.Global, rotating, axis,
                origin, initialOrientation, out var offset, out var rotation, out float degrees);
            numericInput.Close();
            if (!valid) {
                ResumeMouse();
                return false;
            }
            rotationDegrees = degrees;
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
            }
            active = false;
            settling = false;
            finishedFrame = Time.frameCount;
        }

        private void CompleteConfirmation() {
            displayedOffset = targetOffset;
            displayedRotation = targetRotation;
            ApplyDisplayedTransform();
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

        private void OnGUI() {
            if (!active)
                return;
            if (showConstraintAxis && !rotating && axis != Vector3.zero)
                TransformFeedback.DrawAxis(refCamera, origin, axis);
            string constraint = axis == Vector3.right ? "X" : axis == Vector3.up ? "Y" : axis == Vector3.forward ? "Z" : "Free";
            string snap = numericInput.IsOpen ? "Exact entry" : TransformSnapping.Active ? "Snap on" : "Snap off";
            TransformFeedback.DrawControls($"{(rotating ? "Rotate" : "Grab")} · {constraint} · {snap}\n" +
                "X/Y/Z: axis    /: type value\nClick / Enter: confirm    Esc / Right click: cancel");
            if (numericInput.IsOpen) {
                bool global = numericInput.Mode == TransformNumericInput.InputMode.Global;
                string label = !rotating && axis == Vector3.zero ? "Global position: X, Y, Z (in)" :
                    $"{(global ? "Global" : "Relative")} {constraint} {(rotating ? "rotation (degrees)" : "movement (in)")}";
                string next = global ? "/: mouse control" : "/: global value";
                TransformFeedback.DrawNumeric(label, numericInput.DisplayText, $"Enter: apply exact value\n{next}    Invalid input: mouse control");
            }
            else TransformFeedback.Draw(rotating ? $"{rotationDegrees:0.##}°" : TransformFeedback.Distance(targetOffset, axis));
        }

        private void OnDestroy() {
            grabInput.performed -= Grab;
            rotateInput.performed -= Rotate;
            if (instance == this)
                instance = null;
        }
    }
}
