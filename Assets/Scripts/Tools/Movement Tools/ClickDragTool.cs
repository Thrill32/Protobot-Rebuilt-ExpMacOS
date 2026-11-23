using UnityEngine;
using UnityEngine.EventSystems;

namespace Protobot.Tools {
    public abstract class ClickDragTool : MonoBehaviour, IEndDragHandler, IDragHandler, IPointerDownHandler, IPointerUpHandler {
        public MovementManager movementManager;
        public GameObject refObj => movementManager.MovingObj;

        private bool dragging;
        private bool waiting;
        private int requestGeneration;
        protected Vector3 startPosition;
        protected Vector3 feedbackOffset;
        protected float feedbackDegrees;

        private void OnGUI() {
            if (!dragging) return;
            if (this is RotationTool)
                TransformFeedback.Draw(TransformFeedback.Angle(feedbackDegrees));
            else if (this is PositionTool)
                TransformFeedback.Draw(TransformFeedback.Distance(feedbackOffset, this is PositionAxis axis ? axis.normal.Vector : Vector3.zero));
        }

        protected virtual void OnDisable() {
            if (waiting) { requestGeneration++; waiting = false; CollaborationSession.CancelPose(); }
            if (dragging) movementManager.StopContinuous();
            dragging = false;
        }

        public bool CancelDrag() {
            if (waiting) { requestGeneration++; waiting = false; CollaborationSession.CancelPose(); return true; }
            if (!dragging) return false;
            dragging = false;
            OnEndDrag();
            movementManager.CancelMovement();
            return true;
        }

        public abstract void OnPointerDown();
        public void OnPointerDown(PointerEventData eventData) {
            if (Protobot.InputEvents.EscapeDeselect.CapturesFrame || KeyboardTransformTool.IsCapturingInput || Placement.IsCopyingSelection)
                return;
            if (CollaborationSession.IsShared) {
                if (!CollaborationSession.CanEdit) return;
                waiting = true;
                int request = ++requestGeneration;
                var selected = refObj;
                bool duplicate = FindFirstObjectByType<ObjectDuplicator>()?.IsDuplicateHeld == true;
                CollaborationSession.ReservePose(movementManager.GetMovingConnectedObjs(), () => {
                    if (this == null || !isActiveAndEnabled || !waiting || request != requestGeneration || selected != refObj) { CollaborationSession.CancelPose(); return; }
                    waiting = false; movementManager.PrepareSharedDuplicate(duplicate); BeginDrag();
                }, () => { if (this != null && request == requestGeneration) waiting = false; });
                return;
            }
            BeginDrag();
        }
        private void BeginDrag() {
            startPosition = refObj.transform.position;
            feedbackDegrees = 0f;
            feedbackOffset = Vector3.zero;
            OnPointerDown();
            dragging = true;
            movementManager.StartContinuous();
        }

        public abstract void OnDrag();
        public void OnDrag(PointerEventData eventData) {
            if (dragging && !KeyboardTransformTool.IsCapturingInput && !Placement.IsCopyingSelection)
                OnDrag();
        }

        public abstract void OnEndDrag();
        public void OnEndDrag(PointerEventData eventData) => OnEndDrag();

        public abstract void OnPointerUp();
        public void OnPointerUp(PointerEventData eventData) {
            if (waiting) { requestGeneration++; waiting = false; CollaborationSession.CancelPose(); return; }
            if (!dragging || KeyboardTransformTool.IsCapturingInput || Placement.IsCopyingSelection)
                return;
            dragging = false;
            OnPointerUp();
            movementManager.StopContinuous();
        }
    }
}
