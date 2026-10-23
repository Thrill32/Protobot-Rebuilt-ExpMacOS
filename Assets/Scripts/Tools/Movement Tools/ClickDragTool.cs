using UnityEngine;
using UnityEngine.EventSystems;

namespace Protobot.Tools {
    public abstract class ClickDragTool : MonoBehaviour, IEndDragHandler, IDragHandler, IPointerDownHandler, IPointerUpHandler {
        public MovementManager movementManager;
        public GameObject refObj => movementManager.MovingObj;

        private bool dragging;
        protected Vector3 startPosition;
        protected Vector3 feedbackOffset;
        protected float feedbackDegrees;

        private void OnGUI() {
            if (!dragging) return;
            if (this is RotationTool)
                TransformFeedback.Draw($"{feedbackDegrees:0.##}°");
            else if (this is PositionTool)
                TransformFeedback.Draw(TransformFeedback.Distance(feedbackOffset, this is PositionAxis axis ? axis.normal.Vector : Vector3.zero));
        }

        protected virtual void OnDisable() {
            if (dragging) movementManager.StopContinuous();
            dragging = false;
        }

        public abstract void OnPointerDown();
        public void OnPointerDown(PointerEventData eventData) {
            if (KeyboardTransformTool.IsCapturingInput)
                return;
            startPosition = refObj.transform.position;
            feedbackDegrees = 0f;
            feedbackOffset = Vector3.zero;
            OnPointerDown();
            dragging = true;
            movementManager.StartContinuous();
        }

        public abstract void OnDrag();
        public void OnDrag(PointerEventData eventData) {
            if (!KeyboardTransformTool.IsCapturingInput)
                OnDrag();
        }

        public abstract void OnEndDrag();
        public void OnEndDrag(PointerEventData eventData) => OnEndDrag();

        public abstract void OnPointerUp();
        public void OnPointerUp(PointerEventData eventData) {
            if (KeyboardTransformTool.IsCapturingInput)
                return;
            dragging = false;
            OnPointerUp();
            movementManager.StopContinuous();
        }
    }
}
