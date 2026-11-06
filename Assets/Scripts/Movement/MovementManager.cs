using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Protobot.Transformations;
using DG.Tweening;
using System;
using Protobot.StateSystems;
using System.Linq;

namespace Protobot {
    public class MovementManager : MonoBehaviour {
        [SerializeField] private ObjectLink objectLink;
        public GameObject MovingObj => objectLink.obj;

        private Pivot movementPivot;

        private Tween movementTween;

        private bool displaceContinous = false;

        public Action OnStartMoving;
        public Action OnFinishMoving;
        public Action OnCancelMoving;
        private Pose startingPose;
        private State startingState;
        private List<IElement> startingElements;

        List<GameObject> movingObjs;


        private void Start() { 
            OnStartMoving += () => {
                ObjectElement.AddObjectElements(GetMovingConnectedObjs());
            };

            OnFinishMoving += () => {
                StateSystem.AddEmptyState();
                ObjectElement.AddObjectElements(GetMovingConnectedObjs());
            };

            movementPivot = Pivot.Create("Movement Pivot");
        }

        private void Update() {
            if (!movementPivot.IsEmpty && movementTween != null && !movementTween.active && !displaceContinous) {
                OnFinishMoving?.Invoke();
                movementPivot.SetEmpty();
            }
        }

        public List<GameObject> GetMovingConnectedObjs() => MovingObj.GetConnectedObjects(true, false, !objectLink.individual);

        public void FormPivot(List<GameObject> objs) {
            movementPivot.SetEmpty();
            movementPivot.transform.position = MovingObj.transform.position;
            movementPivot.transform.rotation = MovingObj.transform.rotation;
            
            movementPivot.AddObjects(objs, !objectLink.individual);
        }

        public void StopContinuous() {
            displaceContinous = false;
        }

        public bool CancelMovement() {
            if (movementPivot == null || movementPivot.IsEmpty || startingState == null) return false;
            movementPivot.transform.DOKill();
            movementTween = null;
            movementPivot.transform.SetPositionAndRotation(startingPose.position, startingPose.rotation);
            movementPivot.SetEmpty();
            displaceContinous = false;
            foreach (var element in startingElements) startingState.elements.Remove(element);
            startingState = null;
            OnCancelMoving?.Invoke();
            return true;
        }

        private void BeginMovement() {
            startingState = StateSystem.curState;
            int count = startingState.elements.Count;
            OnStartMoving?.Invoke();
            startingElements = startingState.elements.Skip(count).ToList();
        }

        /// <summary>
        /// Prepares movementmanager by setting up all pivots and actions for the displacements to be running every frame
        /// </summary>
        /// <param name="value"></param>
        public void StartContinuous() {
            if (CollaborationSession.BlocksLegacyMutation) return;
            displaceContinous = true;

            if (displaceContinous) {
                movingObjs = GetMovingConnectedObjs();

                FormPivot(movingObjs);
                startingPose = new Pose(movementPivot.transform.position, movementPivot.transform.rotation);
                BeginMovement();
            }
        }

        public void DisplaceTo(Displacement displacement, float duration = 0.25f) {
            if (CollaborationSession.BlocksLegacyMutation) return;
            if (!displaceContinous) {
                movingObjs = GetMovingConnectedObjs();
                BeginMovement();
            }

            if (!displaceContinous) {
                FormPivot(movingObjs);
                startingPose = new Pose(movementPivot.transform.position, movementPivot.transform.rotation);
            }
            movementTween = displacement.Displace(movementPivot.gameObject, duration);
        }

        public void MoveTo(Vector3 position) {
            var displacement = new Displacement(position, MovingObj.transform.rotation);
            DisplaceTo(displacement);
        }

        public void RotateTo(Quaternion rotation) {
            var displacement = new Displacement(MovingObj.transform.position, rotation);
            DisplaceTo(displacement);
        }
    }
}
