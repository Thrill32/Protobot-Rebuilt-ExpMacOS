using System.Collections.Generic;
using Protobot.Simulation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Protobot {
    public static class MouseInput {
        public static Mouse CurrentMouse => Mouse.current;

        public static ButtonControl LeftButton => CurrentMouse.leftButton;
        public static ButtonControl MiddleButton => CurrentMouse.middleButton;
        public static ButtonControl RightButton => CurrentMouse.rightButton;

        public static Vector3 Position => CurrentMouse.position.ReadValue();
        public static float xAxis => CurrentMouse.delta.x.ReadValue();
        public static float yAxis => CurrentMouse.delta.y.ReadValue();
        public static float scrollAxis => CurrentMouse.scroll.y.ReadValue();
        private static readonly List<RaycastResult> uiHits = new();
        private static EventSystem uiEvents;
        private static PointerEventData uiPointer;
        private static Mouse ownedMouse;
        private static PointerCapture sceneCapture;
        private static PointerCapture cameraCapture;

        private struct PointerCapture {
            private int buttons;
            private int? releaseFrame;

            public bool BlocksPointer(bool hit, int pressed, int held) {
                if ((buttons & ~held) != 0) releaseFrame = Time.frameCount;
                buttons &= held;
                if (hit) buttons |= pressed & held;
                return hit || buttons != 0 || releaseFrame == Time.frameCount;
            }
        }

        /// <summary>Query the current hit, even before EventSystem.Update, and
        /// retain UI gestures through release when the pointer leaves the UI.</summary>
        internal static bool UIBlocksPointer(bool forCamera = false) {
            var mouse = CurrentMouse;
            var events = EventSystem.current;
            if (mouse == null || events == null) return false;
            if (ownedMouse != mouse || uiEvents != events) {
                ownedMouse = mouse; uiEvents = events;
                uiPointer = new PointerEventData(events);
                sceneCapture = default;
                cameraCapture = default;
            }
            uiPointer.position = mouse.position.ReadValue();
            uiHits.Clear();
            events.RaycastAll(uiPointer, uiHits);
            bool hit = uiHits.Count > 0;
            int held = (mouse.leftButton.isPressed ? 1 : 0) | (mouse.rightButton.isPressed ? 2 : 0) | (mouse.middleButton.isPressed ? 4 : 0);
            int pressed = (mouse.leftButton.wasPressedThisFrame ? 1 : 0) | (mouse.rightButton.wasPressedThisFrame ? 2 : 0) | (mouse.middleButton.wasPressedThisFrame ? 4 : 0);
            // The routing overlay still shields the scene and the UI behind it.
            // Its backdrop is the viewport surface for camera navigation.
            bool cameraHit = hit && !SimulationTool.IsRouteBackdrop(uiHits[0].gameObject);
            bool blocksScene = sceneCapture.BlocksPointer(hit, pressed, held);
            bool blocksCamera = cameraCapture.BlocksPointer(cameraHit, pressed, held);
            return forCamera ? blocksCamera : blocksScene;
        }

        public static bool overUI => ColorPickerPointerOwnership.BlocksScenePointer || CollaborationSession.ContainsPointer ||
            UIBlocksPointer();
        internal static bool BlocksCameraPointer => ColorPickerPointerOwnership.BlocksScenePointer || CollaborationSession.ContainsPointer ||
            UIBlocksPointer(forCamera: true);
        public static bool withinScreen => new Rect(0,0, Screen.width, Screen.height).Contains(Position);
    }
}
