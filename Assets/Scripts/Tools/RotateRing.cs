using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Protobot.StateSystems;

namespace Protobot.Tools {
    public class RotateRing : RotationTool {
        [SerializeField] private Camera refCamera = null; //used for dot product angle comparison

        [SerializeField] private VectorLink rotVectorLink;
        
        private Vector3 initRotVector; //the initial Vector to rotate about

        private Vector2 previousMouseVector;
        private float accumulatedAngle;
        private Vector2 MouseVector => ((Vector2)MouseInput.Position - (Vector2)refCamera.WorldToScreenPoint(transform.position)).normalized;
        private Quaternion initRot; //the intiial rotation of the selection

        private Quaternion finalRotation = Quaternion.identity;
        public override Quaternion FinalRotation => finalRotation;

        //Main functions
        public override void Rotate() {
            Vector2 curMouseVector = MouseVector;
            if (curMouseVector != Vector2.zero) {
                accumulatedAngle += Vector2.SignedAngle(curMouseVector, previousMouseVector);
                previousMouseVector = curMouseVector;
            }
            float angle = accumulatedAngle;
            
            if (TransformSnapping.Active)
                angle = TransformSnapping.SnapAngle(angle);
            angle = TransformSnapping.WrapAngle(angle);

            float camDot = Vector3.Dot(refCamera.transform.forward, initRotVector); //finds the dot product between the camera direction vs. the look direction of this rotate ring
            feedbackDegrees = TransformSnapping.WrapAngle(angle * -Mathf.Sign(camDot));
            Quaternion newRot = Quaternion.AngleAxis(angle, -initRotVector * Mathf.Sign(camDot)) * initRot;
            
            movementManager.RotateTo(newRot);

            finalRotation = newRot;
        }

        public override void Initialize() {
            previousMouseVector = MouseVector;
            accumulatedAngle = 0f;

            initRot = refObj.transform.rotation;
            initRotVector = rotVectorLink.Vector;
        }

        //Input Events
        public override void OnDrag() {
            if (MouseInput.LeftButton.isPressed)
                Rotate();
        }

        public override void OnEndDrag() {

        }
    }
}
