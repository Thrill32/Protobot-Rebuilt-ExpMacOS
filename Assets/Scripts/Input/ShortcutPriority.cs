using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Protobot.InputEvents {
    /// <summary>Complete modified chords consume their trigger keys before single-key dispatch.</summary>
    internal static class ShortcutPriority {
        private static readonly HashSet<InputEvent> inputs = new();
        private static readonly Dictionary<InputControl, int> priorities = new();
        private static uint update;
        private static int frame = -1;
        internal static void Register(InputEvent input) { if (inputs.Add(input)) frame = -1; }
        internal static void Unregister(InputEvent input) { if (inputs.Remove(input)) frame = -1; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { inputs.Clear(); priorities.Clear(); frame = -1; }

        internal static bool Allows(InputEvent input) {
            if (frame != Time.frameCount || update != InputState.updateCount) {
                frame = Time.frameCount; update = InputState.updateCount;
                priorities.Clear();
                foreach (var candidate in inputs) {
                    if (candidate == null || !candidate.isActiveAndEnabled) continue;
                    Add(candidate.defaultAction); Add(candidate.rebindAction?.action);
                }
            }
            // Modifier-only actions (snap/grouping/duplicate) have no trigger
            // here and remain available alongside modified shortcuts.
            foreach (var entry in priorities) {
                int bound = Mathf.Max(Score(input.defaultAction, entry.Key, false), Score(input.rebindAction?.action, entry.Key, false));
                int pressed = Mathf.Max(Score(input.defaultAction, entry.Key, true), Score(input.rebindAction?.action, entry.Key, true));
                // An incomplete, more-specific alternative cannot lend its
                // modifier count to a currently pressed simpler binding.
                if (bound >= 0 && pressed < entry.Value) return false;
            }
            return true;
        }

        private static void Add(InputAction action) {
            if (action == null || !action.enabled || !action.AllControlsPressed()) return;
            foreach (var control in action.controls) {
                if (control is not ButtonControl button || !button.isPressed || Modifier(control) != 0) continue;
                int score = Score(action, control, true);
                if (score > 0 && (!priorities.TryGetValue(control, out int previous) || score > previous)) priorities[control] = score;
            }
        }

        private static int Score(InputAction action, InputControl trigger, bool requirePressed) {
            if (action == null || !action.enabled) return -1;
            if (requirePressed && !action.AllControlsPressed()) return -1;
            bool composite = false;
            foreach (var binding in action.bindings) if (binding.isComposite) { composite = true; break; }
            if (!composite) {
                int modifiers = 0; bool matches = false;
                foreach (var control in action.controls) { modifiers |= Modifier(control); matches |= control == trigger; }
                return matches ? Count(modifiers) : -1;
            }
            int best = -1;
            for (int i = 0; i < action.bindings.Count; i++) {
                var binding = action.bindings[i];
                if (binding.isPartOfComposite) continue;
                if (!binding.isComposite) {
                    if (InputControlPath.Matches(binding.effectivePath, trigger)) best = Mathf.Max(best, 0);
                    continue;
                }
                int modifiers = 0; bool matches = false, complete = true;
                while (i + 1 < action.bindings.Count && action.bindings[i + 1].isPartOfComposite) {
                    var part = action.bindings[++i]; bool pressed = false;
                    foreach (var control in action.controls) {
                        if (!InputControlPath.Matches(part.effectivePath, control)) continue;
                        modifiers |= Modifier(control); matches |= control == trigger;
                        pressed |= control is ButtonControl button && button.isPressed;
                    }
                    complete &= pressed;
                }
                if (matches && (!requirePressed || complete)) best = Mathf.Max(best, Count(modifiers));
            }
            return best;
        }

        private static int Modifier(InputControl control) {
            if (control.device is not Keyboard keyboard) return 0;
            if (control == keyboard.ctrlKey || control == keyboard.leftCtrlKey || control == keyboard.rightCtrlKey) return 1;
            if (control == keyboard.leftMetaKey || control == keyboard.rightMetaKey) return 2;
            if (control == keyboard.altKey || control == keyboard.leftAltKey || control == keyboard.rightAltKey) return 4;
            if (control == keyboard.shiftKey || control == keyboard.leftShiftKey || control == keyboard.rightShiftKey) return 8;
            return 0;
        }
        private static int Count(int mask) { int count = 0; for (; mask != 0; mask &= mask - 1) count++; return count; }
    }
}
