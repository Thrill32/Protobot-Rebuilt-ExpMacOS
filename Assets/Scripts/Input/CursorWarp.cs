using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Protobot.InputEvents {
    /// <summary>Keeps asynchronous native cursor warps out of measured mouse motion.</summary>
    public static class CursorWarp {
        private static Mouse mouse;
        private static Vector2 from, to;
        private static double requestedAt;
        private static bool acknowledged, subscribed;
        private const double GuardSeconds = .25;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() {
            InputSystem.onEvent -= FilterEvent;
            subscribed = false;
            mouse = null;
        }

        public static bool Warp(Mouse device, Vector2 destination) {
            if (device == null || Cursor.lockState == CursorLockMode.Locked ||
                !Finite(destination) || (device.position.ReadValue() - destination).sqrMagnitude < .25f) return false;
            if (!subscribed) { InputSystem.onEvent += FilterEvent; subscribed = true; }
            mouse = device;
            from = device.position.ReadValue();
            to = destination;
            requestedAt = InputState.currentTime;
            acknowledged = false;
            device.WarpCursorPosition(destination);
            // All consumers see the visible destination now, rather than an
            // old edge coordinate while the OS command is still in flight.
            InputState.Change(device.position, destination);
            InputState.Change(device.delta, Vector2.zero);
            return true;
        }

        private static void FilterEvent(InputEventPtr input, InputDevice device) {
            if (mouse == null || device != mouse || (!input.IsA<StateEvent>() && !input.IsA<DeltaStateEvent>())) return;
            if (InputState.currentUpdateType == InputUpdateType.Editor) return;
            if (Cursor.lockState == CursorLockMode.Locked || InputState.currentTime - requestedAt > GuardSeconds) { mouse = null; return; }
            bool hasPosition = mouse.position.ReadValueFromEvent(input, out var position);
            bool hasDelta = mouse.delta.ReadValueFromEvent(input, out var delta);
            bool stale = input.time < requestedAt;
            if (hasPosition) {
                // Events queued before the warp can be delivered afterwards.
                // Until its acknowledgement, coordinates near the old edge
                // are also stale. Keep buttons and wheel data in these events.
                bool oldSide = !acknowledged && (position - from).sqrMagnitude < (position - to).sqrMagnitude;
                if (stale || oldSide || !Finite(position)) {
                    mouse.position.WriteValueIntoEvent(mouse.position.ReadValue(), input);
                    if (hasDelta) mouse.delta.WriteValueIntoEvent(Vector2.zero, input);
                    return;
                }
                var jump = to - from;
                if (acknowledged && hasDelta && (position - to).sqrMagnitude < 1f &&
                    (delta.sqrMagnitude < 1f || (delta - jump).sqrMagnitude < 1f)) {
                    // A delayed duplicate acknowledgement can arrive after
                    // real movement; do not rewind back to the warp origin.
                    mouse.position.WriteValueIntoEvent(mouse.position.ReadValue(), input);
                    mouse.delta.WriteValueIntoEvent(Vector2.zero, input);
                    return;
                }
                acknowledged = true;
                // Some backends report the entire teleport as delta; others
                // coalesce the warp with real motion. Position differences
                // retain only the latter, including multiple events/update.
                if (hasDelta) mouse.delta.WriteValueIntoEvent(position - mouse.position.ReadValue(), input);
            }
            else if (hasDelta) {
                var jump = to - from;
                float tolerance = Mathf.Min(32f, jump.magnitude * .1f);
                if (stale) mouse.delta.WriteValueIntoEvent(Vector2.zero, input);
                else if ((delta - jump).sqrMagnitude <= tolerance * tolerance)
                    mouse.delta.WriteValueIntoEvent(delta - jump, input);
            }
        }

        private static bool Finite(Vector2 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) &&
            !float.IsInfinity(value.x) && !float.IsInfinity(value.y);
    }
}
