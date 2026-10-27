using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Protobot.Tools {
    /// <summary>Slash-mode entry and exact transform targets, independent of snapping.</summary>
    public sealed class TransformNumericInput {
        public enum InputMode { Mouse, Relative, Global }
        public InputMode Mode { get; private set; }
        public bool IsOpen => Mode != InputMode.Mouse;
        public string Text { get; private set; } = "";
        private Keyboard keyboard;
        private int cursor;
        private bool selectAll;

        public void Cycle(bool positionWithoutAxis) {
            var next = Mode == InputMode.Mouse ? (positionWithoutAxis ? InputMode.Global : InputMode.Relative) :
                Mode == InputMode.Relative ? InputMode.Global : InputMode.Mouse;
            Close();
            Mode = next;
            if (!IsOpen) return;
            keyboard = Keyboard.current;
            if (keyboard != null) keyboard.onTextInput += OnTextInput;
        }

        public void Close() {
            if (keyboard != null) keyboard.onTextInput -= OnTextInput;
            keyboard = null;
            Mode = InputMode.Mouse;
            Text = "";
            cursor = 0;
            selectAll = false;
        }

        private void OnTextInput(char character) {
            if (char.IsControl(character) || character == '/') return;
            Insert(character.ToString());
        }

        private void Insert(string value) {
            if (selectAll) { Text = ""; cursor = 0; selectAll = false; }
            value = value.Replace("\r", "").Replace("\n", "");
            int remaining = 96 - Text.Length;
            if (value.Length > remaining) value = value.Substring(0, remaining);
            Text = Text.Insert(cursor, value);
            cursor += value.Length;
        }

        public void Edit(Keyboard keys) {
            bool modifier = keys.ctrlKey.isPressed || keys.leftMetaKey.isPressed || keys.rightMetaKey.isPressed;
            if (modifier && keys.aKey.wasPressedThisFrame) { selectAll = true; return; }
            if (modifier && keys.vKey.wasPressedThisFrame) { Insert(GUIUtility.systemCopyBuffer); return; }
            if (modifier && keys.cKey.wasPressedThisFrame && selectAll) GUIUtility.systemCopyBuffer = Text;
            if (keys.backspaceKey.wasPressedThisFrame || keys.deleteKey.wasPressedThisFrame) {
                if (selectAll) { Text = ""; cursor = 0; }
                else if (keys.backspaceKey.wasPressedThisFrame && cursor > 0) Text = Text.Remove(--cursor, 1);
                else if (keys.deleteKey.wasPressedThisFrame && cursor < Text.Length) Text = Text.Remove(cursor, 1);
                selectAll = false;
            }
            if (keys.leftArrowKey.wasPressedThisFrame) { cursor = Mathf.Max(0, cursor - 1); selectAll = false; }
            if (keys.rightArrowKey.wasPressedThisFrame) { cursor = Mathf.Min(Text.Length, cursor + 1); selectAll = false; }
            if (keys.homeKey.wasPressedThisFrame) { cursor = 0; selectAll = false; }
            if (keys.endKey.wasPressedThisFrame) { cursor = Text.Length; selectAll = false; }
        }

        public string DisplayText => selectAll ? "[ " + Text + " ]" :
            Text.Insert(cursor, Time.unscaledTime % 1f < .5f ? "│" : " ");

        public static bool TryEvaluate(string text, bool global, bool rotating, Vector3 axis,
            Vector3 origin, Quaternion initialRotation, out Vector3 offset, out Quaternion rotation, out float degrees) {
            offset = Vector3.zero;
            rotation = Quaternion.identity;
            degrees = 0f;
            if (!rotating && axis == Vector3.zero) {
                var components = text.Split(',');
                if (!global || components.Length != 3 || !Number(components[0], out float x) ||
                    !Number(components[1], out float y) || !Number(components[2], out float z)) return false;
                offset = new Vector3(x, y, z) - origin;
                return Finite(offset);
            }
            if (axis == Vector3.zero || !Number(text, out float value)) return false;
            if (!rotating) {
                offset = axis * (global ? value - Vector3.Dot(origin, axis) : value);
                return Finite(offset);
            }
            value = TransformSnapping.WrapAngle(value);
            degrees = value;
            if (global) {
                var euler = initialRotation.eulerAngles;
                int component = axis == Vector3.right ? 0 : axis == Vector3.up ? 1 : 2;
                degrees = Mathf.DeltaAngle(euler[component], value);
                euler[component] = value;
                rotation = Quaternion.Euler(euler) * Quaternion.Inverse(initialRotation);
            }
            else rotation = Quaternion.AngleAxis(value, axis);
            return true;
        }

        private static bool Number(string text, out float value) =>
            float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
