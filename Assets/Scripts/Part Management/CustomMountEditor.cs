using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.InputEvents;
using Protobot.StateSystems;
using Protobot.Tools;
using Protobot.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Protobot {
    /// <summary>Point-only edits inside M mode; the owning model stays stationary.</summary>
    public sealed class CustomMountEditor : IDisposable {
        private readonly Camera camera;
        private readonly InputEvent grab, rotate, depth, delete, multiple, duplicate, bypass, recalibrate, undo, redo;
        private readonly ObjectDuplicator duplicator;
        private readonly TransformGesture gesture = new();
        private readonly TransformNumericInput numeric = new();
        private readonly List<CustomMountPoint> selection = new();
        private readonly List<EditPoint> edits = new();
        private CustomMountPoint[] originals;
        private bool duplicating;
        private struct EditPoint {
            public CustomMountPoint point;
            public Pose before;
            public Vector3 surface;
            public Quaternion rotation;
            public float depth;
        }
        private Vector3 surface, planeStart, axis;
        private Quaternion orientation;
        private float degrees;
        private bool rotating, changingDepth, paused;
        private int startFrame, finishedFrame = -1;
        private const float DepthIncrement = .01f;
        private HashSet<HoleDetector> affected;
        public bool Enabled { get; set; }
        public bool IsEditing { get; private set; }
        public bool IsTyping => numeric.IsOpen;
        public event Action<CustomMountPoint> DepthCalibrated;
        public CustomMountPoint Selected => selection.LastOrDefault();
        public IReadOnlyList<CustomMountPoint> SelectedPoints => selection;
        public bool MultiSelectHeld => multiple?.IsCurrentlyPressed == true;
        public string SnapBypassBinding => bypass?.GetCurrentKeybind() ?? "Ctrl";
        private string SelectionName => selection.Count == 1 ? "Mounting point" : $"{selection.Count} mounting points";

        public CustomMountEditor(Camera camera) {
            this.camera = camera;
            var inputs = UnityEngine.Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None);
            grab = inputs.FirstOrDefault(input => input.name == "Grab Selection");
            rotate = inputs.FirstOrDefault(input => input.name == "Rotate Placement");
            depth = inputs.FirstOrDefault(input => input.name == "Change Mounting Depth");
            delete = inputs.FirstOrDefault(input => input.name == "Delete Selection");
            multiple = inputs.FirstOrDefault(input => input.name == "Multi-Select Button");
            duplicate = inputs.FirstOrDefault(input => input.name == "Duplicate");
            bypass = inputs.FirstOrDefault(input => input.name == "Snap Bypass");
            recalibrate = inputs.FirstOrDefault(input => input.name == "Recalibrate Mounting Grid");
            undo = inputs.FirstOrDefault(input => input.name == "Undo");
            redo = inputs.FirstOrDefault(input => input.name == "Redo");
            duplicator = UnityEngine.Object.FindFirstObjectByType<ObjectDuplicator>();
            if (grab != null) grab.performed += Grab;
            if (rotate != null) rotate.performed += Rotate;
            if (depth != null) depth.performed += BeginDepth;
            if (delete != null) delete.performed += Delete;
            if (recalibrate != null) recalibrate.performed += RecalibrateGrid;
        }

        public bool AllowsInput(InputEvent input) => !IsTyping &&
            (input == bypass || input == multiple || input == duplicate ||
                !IsEditing && (input == undo || input == redo) ||
                !IsEditing && Selected != null && (input == grab || input == rotate || input == depth || input == delete || input == recalibrate) ||
                IsEditing && changingDepth && input == recalibrate);
        public Pose? SavedPose(CustomMountPoint point) {
            if (IsEditing)
                foreach (var edit in edits) if (edit.point == point) return edit.before;
            return null;
        }
        public float? SavedDepth(CustomMountPoint point) {
            if (IsEditing && changingDepth)
                foreach (var edit in edits) if (edit.point == point) return edit.depth;
            return null;
        }
        public string ControlsText => IsEditing
            ? changingDepth
                ? $"{SelectionName} · Depth · mouse: relative change · {DepthIncrement:0.##} in steps · {SnapBypassBinding}: bypass\n/: relative / absolute / mouse · {recalibrate?.GetCurrentKeybind() ?? "N"}: copy depth + diameter for new holes and set · Click / Enter: set · Esc: cancel"
                : $"{SelectionName} · {(duplicating ? "Copy / " : "")}{(rotating ? "Rotate" : "Move")} · {(axis == Vector3.zero ? "surface plane" : axis == Vector3.right ? "X" : axis == Vector3.up ? "Y" : "Z")}\nX/Y/Z: axis    /: exact value    Click / Enter: set    Esc: cancel"
            : Selected != null ? $"{SelectionName} selected · {grab?.GetCurrentKeybind() ?? "G"}: move · {rotate?.GetCurrentKeybind() ?? "R"}: rotate · {depth?.GetCurrentKeybind() ?? "B"}: depth · Delete: remove\n{recalibrate?.GetCurrentKeybind() ?? "N"}: grid from last selected · {multiple?.GetCurrentKeybind() ?? "Ctrl"}+click: select multiple · Hold {duplicate?.GetCurrentKeybind() ?? "Alt"} with G/R: copy · Esc: deselect"
            : $"Mounting points · click a surface to add or a ring to select\n{CustomMountGrid.Spacing:0.##} in layout on each plane · {SnapBypassBinding}: bypass · M / Esc: finish";

        public void Select(CustomMountPoint point, bool additive = false) {
            if (!additive && selection.Count == 1 && Selected == point) return;
            if (IsEditing) Finish(false);
            if (!additive) SetSelection(Array.Empty<CustomMountPoint>());
            if (point == null) return;
            if (additive && selection.Remove(point)) point.SetSelected(false);
            else { selection.Add(point); point.SetSelected(true); }
        }
        private void SetSelection(IEnumerable<CustomMountPoint> points) {
            foreach (var old in selection) if (old != null) old.SetSelected(false);
            selection.Clear();
            selection.AddRange(points.Where(p => p != null && p.Owner != null && p.gameObject.activeInHierarchy && !p.gameObject.IsDeleted()).Distinct());
            foreach (var point in selection) point.SetSelected(true);
        }
        private void Grab() => Begin(false);
        private void Rotate() => Begin(true);
        private void RecalibrateGrid() {
            if (!Enabled || Selected == null || Selected.IsPreview || !Selected.gameObject.activeInHierarchy ||
                Selected.gameObject.IsDeleted() || KeyboardTransformTool.IsEditingText()) return;
            if (IsEditing) {
                if (changingDepth && !IsTyping) { Finish(true); DepthCalibrated?.Invoke(Selected); }
                return;
            }
            bool changed = CustomMountGrid.Recalibrate(Selected);
            InteractionFeedback.Notify(changed ? "Grid recalibrated from the last-selected hole; existing holes stay in place."
                : "This hole already anchors its plane's grid.");
        }
        public void Begin(bool rotatePoint) => Begin(rotatePoint, false);
        public void BeginDepth() => Begin(false, true);
        private void Begin(bool rotatePoint, bool editDepth) {
            if (!Enabled || Selected == null || IsEditing || KeyboardTransformTool.IsEditingText()) return;
            EventSystem.current?.SetSelectedGameObject(null);
            originals = selection.ToArray();
            affected = HoleDetector.GetAffectedDetectors(originals.Select(p => p.Owner.gameObject).Distinct().ToArray());
            duplicating = !editDepth && duplicator != null && duplicator.IsDuplicateHeld;
            if (duplicating) SetSelection(originals.Select(p => p.Owner.CopyMount(p)).ToArray());
            edits.Clear();
            edits.AddRange(selection.Select(point => new EditPoint {
                point = point, before = point.LocalPose, surface = point.Surface, rotation = point.transform.rotation, depth = point.Depth
            }));
            rotating = rotatePoint; changingDepth = editDepth; degrees = 0;
            surface = rotating && edits.Count > 1
                ? new Vector3(edits.Average(e => e.surface.x), edits.Average(e => e.surface.y), edits.Average(e => e.surface.z))
                : Selected.Surface;
            orientation = Selected.transform.rotation;
            gesture.Begin(camera, surface, MouseInput.Position);
            axis = changingDepth ? -(orientation * Vector3.forward) : rotating ? gesture.DefaultRotationAxis : Vector3.zero;
            planeStart = PlanePoint(MouseInput.Position);
            paused = false; startFrame = Time.frameCount;
            IsEditing = true;
        }

        // Return true for the confirmation frame too, so one click cannot
        // both commit an edit and add a new point underneath it.
        public bool Tick(bool pause) {
            if (selection.Any(p => p == null || !p.gameObject.activeInHierarchy || p.Owner == null || p.gameObject.IsDeleted())) {
                bool editing = IsEditing;
                if (editing) Finish(false);
                SetSelection(selection.ToArray());
                return editing;
            }
            if (!IsEditing) return finishedFrame == Time.frameCount;
            var keys = Keyboard.current;
            if (keys == null) return true;
            if (MoveRotation.SlashPressed(keys)) {
                numeric.Cycle(!rotating && axis == Vector3.zero);
                if (!IsTyping) ResumeMouse();
                return true;
            }
            if (IsTyping) {
                numeric.Edit(keys);
                if (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame) SubmitNumeric();
                return true;
            }
            if (pause) { paused = true; return true; }
            if (paused) { ResumeMouse(); paused = false; }
            if (!changingDepth && TransformGesture.TryReadAxis(out var chosen)) axis = axis == chosen ? (rotating ? gesture.DefaultRotationAxis : Vector3.zero) : chosen;
            if (changingDepth) {
                float offset = Vector3.Dot(gesture.Position(MouseInput.Position, axis, false) - surface, axis);
                if (TransformSnapping.Active) offset = Mathf.Round(offset / DepthIncrement) * DepthIncrement;
                // Clamp the shared delta, keeping differences between selected depths intact.
                float minimum = Mathf.Min(.001f, edits.Min(e => e.depth));
                offset = Mathf.Clamp(offset, edits.Max(e => minimum - e.depth), edits.Min(e => 100f - e.depth));
                ApplyDepth(offset);
            }
            else if (rotating) {
                var rotation = gesture.Rotation(MouseInput.Position, axis, orientation, TransformSnapping.Active);
                degrees = gesture.RotationDegrees;
                Apply(surface, rotation);
            }
            else {
                Vector3 target;
                if (axis != Vector3.zero) {
                    target = gesture.Position(MouseInput.Position, axis, false);
                    if (TransformSnapping.Active) target = surface + (target - surface).Round(CustomMountGrid.Spacing);
                }
                else {
                    var offset = PlanePoint(MouseInput.Position) - planeStart;
                    var right = orientation * Vector3.right; var up = orientation * Vector3.up;
                    float x = Vector3.Dot(offset, right), y = Vector3.Dot(offset, up);
                    if (TransformSnapping.Active) {
                        float step = CustomMountGrid.Spacing;
                        x = Mathf.Round(x / step) * step; y = Mathf.Round(y / step) * step;
                    }
                    target = surface + right * x + up * y;
                }
                Apply(target, orientation);
            }
            if (Time.frameCount != startFrame && ((Mouse.current.leftButton.wasPressedThisFrame && !MouseInput.overUI) || keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)) Finish(true);
            else if (!rotating && axis != Vector3.zero) {
                var viewport = camera.pixelRect;
                viewport = Rect.MinMaxRect(Mathf.Max(0, viewport.xMin), Mathf.Max(0, viewport.yMin), Mathf.Min(Screen.width, viewport.xMax), Mathf.Min(Screen.height, viewport.yMax));
                var pointer = (Vector2)MouseInput.Position;
                if (TransformCursor.TryWrap(pointer, viewport, out var wrapped) && CursorWarp.Warp(Mouse.current, wrapped)) {
                    gesture.RebasePointer(pointer, wrapped, changingDepth ? axis : Vector3.zero);
                    // Returning to the mounting plane must retain movement
                    // measured before the native cursor crossed the screen.
                    planeStart += PlanePoint(wrapped) - PlanePoint(pointer);
                }
            }
            return true;
        }

        private Vector3 PlanePoint(Vector2 pointer) {
            var plane = new Plane(orientation * Vector3.forward, surface);
            var ray = camera.ScreenPointToRay(pointer);
            return plane.Raycast(ray, out float distance) ? ray.GetPoint(distance) : surface;
        }
        private void ResumeMouse() {
            var offset = changingDepth ? axis * (Selected.Depth - edits.Last().depth) : Selected.Surface - surface;
            gesture.Resume(MouseInput.Position, offset, rotating ? degrees : 0, axis);
            planeStart = PlanePoint(MouseInput.Position) - (Selected.Surface - surface);
        }
        public bool ApplyDepth(float value, bool absolute = false) {
            if (!IsEditing || !changingDepth || float.IsNaN(value) || float.IsInfinity(value)) return false;
            var targets = edits.Select(e => absolute ? value : e.depth + value).ToArray();
            if (targets.Any(target => target <= 0 || target > 100 || float.IsNaN(target) || float.IsInfinity(target))) return false;
            for (int i = 0; i < edits.Count; i++) edits[i].point.SetDepth(targets[i], edits[i].surface);
            return true;
        }
        public void Apply(Vector3 targetSurface, Quaternion rotation) {
            var delta = rotation * Quaternion.Inverse(orientation);
            foreach (var edit in edits) {
                if (edit.point == null || edit.point.Owner == null) continue;
                var face = targetSurface + delta * (edit.surface - surface);
                var pointRotation = delta * edit.rotation;
                edit.point.transform.SetPositionAndRotation(face - pointRotation * Vector3.forward * (edit.point.Depth * .5f), pointRotation);
            }
        }
        private void SubmitNumeric() {
            if (changingDepth) {
                bool applied = TransformNumericInput.TryReadNumber(numeric.Text, out float value) &&
                    ApplyDepth(value, numeric.Mode == TransformNumericInput.InputMode.Global);
                numeric.Close();
                if (applied) Finish(true);
                else { InteractionFeedback.Notify("Depth must be greater than 0 and at most 100 in for every selected hole."); ResumeMouse(); }
                return;
            }
            bool valid = TransformNumericInput.TryEvaluate(numeric.Text, numeric.Mode == TransformNumericInput.InputMode.Global,
                rotating, axis, surface, orientation, out var offset, out var delta, out var numericDegrees);
            numeric.Close();
            if (!valid) { ResumeMouse(); return; }
            if (rotating) degrees = numericDegrees;
            Apply(rotating ? surface : surface + offset, rotating ? delta * orientation : orientation);
            Finish(true);
        }
        public void Finish(bool confirm) {
            if (!IsEditing) return;
            var valid = edits.Where(e => e.point != null && e.point.Owner != null).ToArray();
            if (changingDepth) {
                IsEditing = false; numeric.Close(); finishedFrame = Time.frameCount;
                var after = new List<IElement>();
                bool changed = confirm && valid.Any(e => e.depth != e.point.Depth);
                foreach (var edit in valid) {
                    if (changed) {
                        StateSystem.AddElement(edit.point.DepthState(edit.before, edit.depth));
                        after.Add(edit.point.DepthState(edit.point.LocalPose, edit.point.Depth));
                    }
                    if (!confirm) edit.point.SetDepth(edit.depth);
                    edit.point.ApplyLocalPose(confirm && edit.point.Depth != edit.depth ? edit.point.LocalPose : edit.before, affected);
                }
                if (changed) StateSystem.AddState(new State(after));
                changingDepth = false; edits.Clear(); originals = null;
                return;
            }
            var existingCopies = new List<CustomMountPoint>();
            if (confirm && duplicating) {
                foreach (var edit in valid) {
                    var existing = edit.point.Owner.GetComponentsInChildren<CustomMountPoint>().FirstOrDefault(other =>
                        !other.IsPreview && other.Owner == edit.point.Owner && !other.gameObject.IsDeleted() &&
                        (other.Surface - edit.point.Surface).sqrMagnitude < .0001f &&
                        Vector3.Dot(other.transform.forward, edit.point.transform.forward) > .99f);
                    if (existing == null) continue;
                    if (Mathf.Abs(existing.Diameter - edit.point.Diameter) > .00001f ||
                        Mathf.Abs(existing.Depth - edit.point.Depth) > .00001f || existing.Threaded != edit.point.Threaded) {
                        InteractionFeedback.Notify("An existing mounting point has different settings. Move the copies before placing.");
                        ResumeMouse(); return;
                    }
                    existingCopies.Add(edit.point);
                }
                if (valid.Length > 0 && existingCopies.Count == valid.Length) {
                    InteractionFeedback.Notify("These mounting points already exist. Move or rotate the copies before placing.");
                    ResumeMouse(); return;
                }
            }
            IsEditing = false; numeric.Close();
            if (duplicating && !confirm) {
                Discard(valid.Select(e => e.point));
                SetSelection(originals);
            }
            else {
                if (existingCopies.Count > 0) {
                    valid = valid.Where(e => !existingCopies.Contains(e.point)).ToArray();
                    Discard(existingCopies);
                    SetSelection(valid.Select(e => e.point));
                    InteractionFeedback.Notify($"Added {valid.Length} mounting point(s); {existingCopies.Count} already existed.");
                }
                var after = valid.Select(e => e.point.LocalPose).ToArray();
                for (int i = 0; i < valid.Length; i++) {
                    if (duplicating) valid[i].point.SetPreview(false);
                    valid[i].point.ApplyLocalPose(confirm ? after[i] : valid[i].before, affected);
                }
                if (confirm && valid.Length > 0 && (duplicating || valid.Where((e, i) => Vector3.Distance(e.before.position, after[i].position) > .000001f ||
                    Quaternion.Angle(e.before.rotation, after[i].rotation) > .0001f).Any())) {
                    var result = new List<IElement>();
                    for (int i = 0; i < valid.Length; i++) {
                        StateSystem.AddElement(duplicating ? valid[i].point.ExistenceState(false) : valid[i].point.PoseState(valid[i].before));
                        result.Add(duplicating ? valid[i].point.ExistenceState(true) : valid[i].point.PoseState(after[i]));
                    }
                    StateSystem.AddState(new State(result));
                }
            }
            duplicating = false; edits.Clear(); originals = null;
        }
        private static void Discard(IEnumerable<CustomMountPoint> points) {
            foreach (var point in points) {
                point.gameObject.SetActive(false);
                if (Application.isPlaying) UnityEngine.Object.Destroy(point.gameObject);
                else UnityEngine.Object.DestroyImmediate(point.gameObject);
            }
        }
        private void Delete() {
            if (!Enabled || IsEditing || Selected == null || KeyboardTransformTool.IsEditingText()) return;
            var points = selection.ToArray(); Select(null);
            var after = new List<IElement>();
            foreach (var point in points) {
                StateSystem.AddElement(point.ExistenceState(true)); point.SetExists(false);
                after.Add(point.ExistenceState(false));
            }
            StateSystem.AddState(new State(after));
        }
        public bool CancelLevel() {
            if (IsEditing) { Finish(false); return true; }
            if (Selected != null) { Select(null); return true; }
            return false;
        }
        public void End() { Finish(false); Select(null); Enabled = false; }
        public void Draw() {
            if (!IsEditing) return;
            if (IsTyping) TransformFeedback.DrawNumeric(changingDepth
                ? $"{SelectionName} · {(numeric.Mode == TransformNumericInput.InputMode.Relative ? "Relative depth change" : "Absolute depth")} (in)"
                : $"{SelectionName} · {numeric.Mode} {(rotating ? "angle (degrees)" : "position / movement (in)")}", numeric.DisplayText, "Enter: set · /: value mode · Esc: cancel");
            else if (changingDepth) TransformFeedback.Draw($"Depth {Selected.Depth:0.###} in · change {Selected.Depth - edits.Last().depth:+0.###;-0.###;0} in");
            else TransformFeedback.Draw(rotating ? TransformFeedback.Angle(degrees) : TransformFeedback.Distance(Selected.Surface - surface, axis));
        }
        public void Dispose() {
            End();
            if (grab != null) grab.performed -= Grab;
            if (rotate != null) rotate.performed -= Rotate;
            if (depth != null) depth.performed -= BeginDepth;
            if (delete != null) delete.performed -= Delete;
            if (recalibrate != null) recalibrate.performed -= RecalibrateGrid;
        }
    }
}
