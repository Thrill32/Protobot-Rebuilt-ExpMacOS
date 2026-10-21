using UnityEngine;
using UnityEngine.EventSystems;

namespace Protobot.Tools {
    public abstract class ClickDragTool : MonoBehaviour, IEndDragHandler, IDragHandler, IPointerDownHandler, IPointerUpHandler {
        public MovementManager movementManager;
        public GameObject refObj => movementManager.MovingObj;

        public abstract void OnPointerDown();
        public void OnPointerDown(PointerEventData eventData) {
            if (KeyboardTransformTool.IsCapturingInput)
                return;
            OnPointerDown();
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
            OnPointerUp();
            movementManager.StopContinuous();
        }
    }
}
