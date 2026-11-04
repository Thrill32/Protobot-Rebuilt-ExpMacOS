using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.InputEvents;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Protobot {
    /// <summary>Point-only edits inside M mode; the owning model stays stationary.</summary>
    public sealed class CustomMountEditor : IDisposable {
        private readonly Camera camera;
        private readonly InputEvent grab, rotate, delete;
        private readonly TransformGesture gesture = new();
        private readonly TransformNumericInput numeric = new();
        private Pose before;
        private Vector3 surface, planeStart, axis;
        private Quaternion orientation;
        private float degrees;
        private bool rotating, paused;
        private int startFrame;
        private HashSet<HoleDetector> affected;
        public bool Enabled { get; set; }
        public bool IsEditing { get; private set; }
        public bool IsTyping => numeric.IsOpen;
        public CustomMountPoint Selected { get; private set; }

        public CustomMountEditor(Camera camera) {
            this.camera = camera;
            var inputs = UnityEngine.Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None);
            grab = inputs.FirstOrDefault(input => input.name == "Grab Selection");
            rotate = inputs.FirstOrDefault(input => input.name == "Rotate Placement");
            delete = inputs.FirstOrDefault(input => input.name == "Delete Selection");
            if (grab != null) grab.performed += Grab;
            if (rotate != null) rotate.performed += Rotate;
            if (delete != null) delete.performed += Delete;
        }

        public bool AllowsInput(InputEvent input) => !IsTyping &&
            (input.name == "Snap Bypass" || !IsEditing && Selected != null && (input == grab || input == rotate || input == delete));
        public Pose? SavedPose(CustomMountPoint point) => IsEditing && point == Selected ? before : null;
        public string ControlsText => IsEditing
            ? $"Mounting point · {(rotating ? "Rotate" : "Move")} · {(axis == Vector3.zero ? "surface plane" : axis == Vector3.right ? "X" : axis == Vector3.up ? "Y" : "Z")}\nX/Y/Z: axis    /: exact value    Click / Enter: set    Esc: cancel"
            : Selected != null ? $"Mounting point selected · {grab?.GetCurrentKeybind() ?? "G"}: move · {rotate?.GetCurrentKeybind() ?? "R"}: rotate · Delete: remove\nClick another surface to add · Esc: deselect point"
            : "Mounting points · click a surface to add or an existing ring to select\nM / Esc: finish";

        public void Select(CustomMountPoint point) {
            if (Selected == point) return;
            if (IsEditing) Finish(false);
            if (Selected != null) Selected.SetSelected(false);
            Selected = point;
            if (Selected != null) Selected.SetSelected(true);
        }
        private void Grab() => Begin(false);
        private void Rotate() => Begin(true);
        public void Begin(bool rotatePoint) {
            if (!Enabled || Selected == null || IsEditing || KeyboardTransformTool.IsEditingText()) return;
            EventSystem.current?.SetSelectedGameObject(null);
            before = Selected.LocalPose;
            affected = HoleDetector.GetAffectedDetectors(Selected.Owner.gameObject);
            surface = Selected.Surface; orientation = Selected.transform.rotation;
            rotating = rotatePoint; degrees = 0;
            gesture.Begin(camera, surface, MouseInput.Position);
            axis = rotating ? gesture.DefaultRotationAxis : Vector3.zero;
            planeStart = PlanePoint(MouseInput.Position);
            paused = false; startFrame = Time.frameCount;
            IsEditing = true;
        }

        // Return true for the confirmation frame too, so one click cannot
        // both commit an edit and add a new point underneath it.
        public bool Tick(bool pause) {
            if (IsEditing && Selected == null || Selected != null && (!Selected.gameObject.activeInHierarchy || Selected.Owner == null)) {
                bool editing = IsEditing;
                if (editing) Finish(false);
                Select(null);
                return editing;
            }
            if (!IsEditing) return false;
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
            if (TransformGesture.TryReadAxis(out var chosen)) axis = axis == chosen ? (rotating ? gesture.DefaultRotationAxis : Vector3.zero) : chosen;
            if (rotating) {
                var rotation = gesture.Rotation(MouseInput.Position, axis, orientation, TransformSnapping.Active);
                degrees = gesture.RotationDegrees;
                Apply(surface, rotation);
            }
            else {
                Vector3 target;
                if (axis != Vector3.zero) target = gesture.Position(MouseInput.Position, axis, TransformSnapping.Active);
                else {
                    var offset = PlanePoint(MouseInput.Position) - planeStart;
                    var right = orientation * Vector3.right; var up = orientation * Vector3.up;
                    float x = Vector3.Dot(offset, right), y = Vector3.Dot(offset, up);
                    if (TransformSnapping.Active) { x = Mathf.Round(x / .5f) * .5f; y = Mathf.Round(y / .5f) * .5f; }
                    target = surface + right * x + up * y;
                }
                Apply(target, orientation);
            }
            if (Time.frameCount != startFrame && (Mouse.current.leftButton.wasPressedThisFrame || keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)) Finish(true);
            else if (!rotating && axis != Vector3.zero) {
                var viewport = camera.pixelRect;
                viewport = Rect.MinMaxRect(Mathf.Max(0, viewport.xMin), Mathf.Max(0, viewport.yMin), Mathf.Min(Screen.width, viewport.xMax), Mathf.Min(Screen.height, viewport.yMax));
                var pointer = (Vector2)MouseInput.Position;
                if (TransformCursor.TryWrap(pointer, viewport, out var wrapped) && CursorWarp.Warp(Mouse.current, wrapped)) {
                    gesture.RebasePointer(pointer, wrapped);
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
            gesture.Resume(MouseInput.Position, Selected.Surface - surface, rotating ? degrees : 0, axis);
            planeStart = PlanePoint(MouseInput.Position) - (Selected.Surface - surface);
        }
        public void Apply(Vector3 targetSurface, Quaternion rotation) {
            Selected.transform.SetPositionAndRotation(targetSurface - rotation * Vector3.forward * (Selected.Depth * .5f), rotation);
        }
        private void SubmitNumeric() {
            bool valid = TransformNumericInput.TryEvaluate(numeric.Text, numeric.Mode == TransformNumericInput.InputMode.Global,
                rotating, axis, surface, orientation, out var offset, out var delta, out _);
            numeric.Close();
            if (!valid) { ResumeMouse(); return; }
            Apply(rotating ? surface : surface + offset, rotating ? delta * orientation : orientation);
            Finish(true);
        }
        public void Finish(bool confirm) {
            if (!IsEditing) return;
            var point = Selected; var after = point != null && point.Owner != null ? point.LocalPose : before;
            IsEditing = false; numeric.Close();
            if (point == null || point.Owner == null) return;
            point.ApplyLocalPose(confirm ? after : before, affected);
            if (confirm) point.RecordPose(before, after);
        }
        private void Delete() {
            if (!Enabled || IsEditing || Selected == null || KeyboardTransformTool.IsEditingText()) return;
            var point = Selected; Select(null); point.Delete(null);
        }
        public bool CancelLevel() {
            if (IsEditing) { Finish(false); return true; }
            if (Selected != null) { Select(null); return true; }
            return false;
        }
        public void End() { Finish(false); Select(null); Enabled = false; }
        public void Draw() {
            if (!IsEditing) return;
            if (IsTyping) TransformFeedback.DrawNumeric($"Mounting point · {numeric.Mode} {(rotating ? "angle (degrees)" : "position / movement (in)")}", numeric.DisplayText, "Enter: set · /: value mode · Esc: cancel");
            else TransformFeedback.Draw(rotating ? TransformFeedback.Angle(degrees) : TransformFeedback.Distance(Selected.Surface - surface, axis));
        }
        public void Dispose() {
            End();
            if (grab != null) grab.performed -= Grab;
            if (rotate != null) rotate.performed -= Rotate;
            if (delete != null) delete.performed -= Delete;
        }
    }
}
