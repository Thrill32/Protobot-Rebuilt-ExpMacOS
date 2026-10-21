using UnityEngine;
using UnityEngine.Events;
using System;
using UnityEngine.EventSystems;
using Protobot.StateSystems;

namespace Protobot.Tools {
    public abstract class PositionTool : ClickDragTool {
        public abstract Vector3 FinalPosition {get;}

        private Vector3 snapOrigin;

        /// <summary> Executes right when mouse is down before dragging </summary>
        abstract public void Initialize();
        abstract public void Move();

        //Input events
        public override void OnPointerDown() {
            snapOrigin = movementManager.MovingObj != null
                ? movementManager.MovingObj.transform.position
                : Vector3.zero;
            Initialize();
        }

        public override void OnPointerUp() {
        }
        public Vector3 MoveToPos(Vector3 pos)
        {
            if (TransformSnapping.Active)
                pos = TransformSnapping.SnapPosition(pos, snapOrigin);

            movementManager.MoveTo(pos);
            return pos;
        }
    }
}
