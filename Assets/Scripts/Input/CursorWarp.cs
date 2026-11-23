using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Protobot.InputEvents {
    /// <summary>Keeps asynchronous native cursor warps out of measured mouse motion.</summary>
    public static class CursorWarp {
        private struct Request {
            public Vector2 from, to;
            public double time;
            public double realtime;
            public bool acknowledged;
            public bool valid;
            public Vector2 Jump => to - from;
        }

        private static Mouse mouse;
        private static Request current, previous;
        private static bool subscribed;
        private const double GuardSeconds = .25;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() {
            InputSystem.onEvent -= FilterEvent;
            subscribed = false;
            mouse = null;
            current = previous = default;
        }

        public static bool Warp(Mouse device, Vector2 destination) {
            if (device == null || Cursor.lockState == CursorLockMode.Locked ||
                !Finite(destination) || (device.position.ReadValue() - destination).sqrMagnitude < .25f) return false;
            if (!subscribed) { InputSystem.onEvent += FilterEvent; subscribed = true; }
            mouse = device;
            // Retain the immediately preceding request even when the backend's
            // event clock advances in coarse steps (notably windowed Windows).
            // The current request still bounds the overall guard lifetime.
            if (current.valid) previous = current;
            current = new Request {
                from = device.position.ReadValue(),
                to = destination,
                time = InputState.currentTime,
                realtime = Time.realtimeSinceStartupAsDouble,
                valid = true
            };
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
            if (Cursor.lockState == CursorLockMode.Locked || !current.valid || Time.realtimeSinceStartupAsDouble - current.realtime > GuardSeconds) {
                mouse = null; current = previous = default; return;
            }
            bool hasPosition = mouse.position.ReadValueFromEvent(input, out var position);
            bool hasDelta = mouse.delta.ReadValueFromEvent(input, out var delta);
            bool stale = input.time < current.time;
            var logical = mouse.position.ReadValue();
            if (hasPosition) {
                if (stale || !Finite(position)) {
                    Rewrite(input, logical, Vector2.zero, hasPosition, hasDelta);
                    return;
                }
                if (!current.acknowledged && previous.valid && Echo(previous, position, delta, hasDelta, out var previousMotion)) {
                    Rewrite(input, logical + previousMotion, previousMotion, true, hasDelta);
                    return;
                }
                if (!current.acknowledged && CloserTo(position, current.from, current.to)) {
                    Rewrite(input, logical, Vector2.zero, hasPosition, hasDelta);
                    return;
                }
                if (!current.acknowledged) {
                    // The first destination-side coordinate is the native ack.
                    // Apply only post-warp motion relative to the logical cursor,
                    // independently on X/Y so windowed Windows vertical warps
                    // cannot inject or lose their teleport distance.
                    var motion = position - current.to;
                    current.acknowledged = true;
                    Rewrite(input, logical + motion, motion, true, hasDelta);
                    return;
                }
                if (Echo(current, position, delta, hasDelta, out var currentMotion) ||
                    previous.valid && Echo(previous, position, delta, hasDelta, out currentMotion)) {
                    Rewrite(input, logical + currentMotion, currentMotion, true, hasDelta);
                    return;
                }
            }
            else if (hasDelta) {
                if (stale) mouse.delta.WriteValueIntoEvent(Vector2.zero, input);
                else if (RemoveJump(delta, current.Jump, out var motion) ||
                         previous.valid && RemoveJump(delta, previous.Jump, out motion))
                    mouse.delta.WriteValueIntoEvent(motion, input);
            }
        }

        private static bool Echo(Request request, Vector2 position, Vector2 delta, bool hasDelta, out Vector2 motion) {
            motion = Vector2.zero;
            const float positionTolerance = 2f;
            bool atDestination = (position - request.to).sqrMagnitude <= positionTolerance * positionTolerance;
            bool atOrigin = (position - request.from).sqrMagnitude <= positionTolerance * positionTolerance;
            if (atOrigin) {
                // A fast second traversal can reach the same edge during the
                // native-echo guard. A real event has continuous position/delta;
                // an old-origin echo has zero/stale delta or the teleport jump.
                var continuous = position - mouse.position.ReadValue();
                if (hasDelta && continuous.sqrMagnitude > 4f && (delta - continuous).sqrMagnitude < 1f)
                    return false;
                return true;
            }
            if (atDestination && (!hasDelta || delta.sqrMagnitude < 1f || RemoveJump(delta, request.Jump, out motion)))
                return true;
            if (hasDelta && RemoveJump(delta, request.Jump, out motion)) return true;
            return false;
        }

        private static bool RemoveJump(Vector2 delta, Vector2 jump, out Vector2 motion) {
            motion = delta;
            bool removed = false;
            float tolerance = Mathf.Min(32f, Mathf.Max(2f, jump.magnitude * .1f));
            if (Mathf.Abs(jump.x) > 1f && Mathf.Abs(delta.x - jump.x) <= tolerance) { motion.x -= jump.x; removed = true; }
            if (Mathf.Abs(jump.y) > 1f && Mathf.Abs(delta.y - jump.y) <= tolerance) { motion.y -= jump.y; removed = true; }
            return removed;
        }

        private static bool CloserTo(Vector2 value, Vector2 first, Vector2 second) =>
            (value - first).sqrMagnitude < (value - second).sqrMagnitude;

        private static void Rewrite(InputEventPtr input, Vector2 position, Vector2 delta, bool hasPosition, bool hasDelta) {
            if (hasPosition) mouse.position.WriteValueIntoEvent(position, input);
            if (hasDelta) mouse.delta.WriteValueIntoEvent(delta, input);
        }

        private static bool Finite(Vector2 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) &&
            !float.IsInfinity(value.x) && !float.IsInfinity(value.y);
    }
}
