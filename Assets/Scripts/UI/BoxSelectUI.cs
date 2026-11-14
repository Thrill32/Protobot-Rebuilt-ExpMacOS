using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Protobot.InputEvents;

namespace Protobot.UI {
    public class BoxSelectUI : MonoBehaviour {

        [SerializeField] private InputEvent input;
        [CacheComponent] private Image image;
        [CacheComponent] private RectTransform rect;
        
        public Vector2 startPos;

        public Action<Vector2, Vector2> OnReleaseBox;

        private bool selecting;

        private void Awake() {
            input.performed += BeginBoxSelect;
            input.canceled += EndBoxSelect;
        }

        private void BeginBoxSelect() => TryBeginBoxSelect(MouseInput.overUI, MouseInput.Position);
        private void EndBoxSelect() => ResolveBoxSelect(MouseInput.overUI, MouseInput.Position);

        internal void TryBeginBoxSelect(bool overUI, Vector2 position) {
            if (overUI) return;
            selecting = true;
            InitializeBox(position);
        }

        internal void ResolveBoxSelect(bool overUI, Vector2 position) {
            if (!selecting) return;
            image.enabled = false;
            if (!overUI && Vector3.Distance(startPos, position) > 5)
                OnReleaseBox?.Invoke(startPos, position);

            rect.sizeDelta = Vector2.zero;
            selecting = false;
        }

        void Start() {
            image.enabled = false;
        }

        void Update() {
            if (selecting) {
                UpdateSelectionBox();
            }
        }

        void InitializeBox(Vector2 position) {
            image.enabled = true;
            startPos = position;
        }

        public bool CancelSelection() {
            if (!selecting) return false;
            image.enabled = false;
            rect.sizeDelta = Vector2.zero;
            selecting = false;
            return true;
        }

        void UpdateSelectionBox() {
            var parent = (RectTransform)rect.parent;
            var canvas = image.canvas.rootCanvas;
            var eventCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            // Pointer coordinates are screen pixels; the canvas can use a different
            // scale (and a camera). Convert both endpoints into the parent's frame.
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, startPos, eventCamera, out var start) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, MouseInput.Position, eventCamera, out var end)) return;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(Mathf.Abs(end.x - start.x), Mathf.Abs(end.y - start.y));
            rect.localPosition = Vector2.Min(start, end);
        }
    }
}
