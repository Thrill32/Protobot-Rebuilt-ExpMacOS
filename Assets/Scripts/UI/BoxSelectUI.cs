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
                ///* this can be removed before release PLEASE DO NOT FORGET THIS IS AWFUL
                if (Mouse.current.leftButton.isPressed)
                {
                    input.rebindAction.ResetRebinds(); //I hate this ill remove it before full release I think only one person had this issue I hope shouldn't happen ever again
                }//*/
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
            if (startPos == null) {
                startPos = MouseInput.Position;
            }

            Vector2 curPos = MouseInput.Position;

            float width = curPos.x - startPos.x;
            float height = curPos.y - startPos.y;

            rect.sizeDelta = new Vector2(Mathf.Abs(width), Mathf.Abs(height));

            float xOffset = (width < 0) ? width : 0;
            float yOffset = (height < 0) ? height : 0;
            rect.position = startPos + new Vector2(xOffset, yOffset);
        }
    }
}
