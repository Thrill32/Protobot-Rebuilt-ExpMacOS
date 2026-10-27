using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.Events;

using Protobot.StateSystems;
using Protobot.InputEvents;
using Protobot.Tools;
using Protobot.Transformations;
using UnityEngine.InputSystem;

namespace Protobot {
    public class Placement : MonoBehaviour { 
        public static int PLACEMENT_LAYER => 14;

        [SerializeField] private MovementManager movementManager;

        public bool placing;
        public bool rotating;

        private PlacementData currentPlacementData;
        public PartPlacementData CurrentPartPlacement => placing ? currentPlacementData as PartPlacementData : null;

        public void RefreshPartPreview() {
            if (CurrentPartPlacement != null) {
                meshFilter.mesh = CurrentPartPlacement.GetDisplayMesh();
                CurrentPartPlacement.RefreshAttachment();
            }
        }

        [CacheComponent] private MeshRenderer meshRenderer;

        [CacheComponent] private MeshFilter meshFilter;

        [SerializeField] private List<PlaceDisplacement> placePositions;

        [Space(5)]

        [SerializeField] private InputEvent placeInput;
        [SerializeField] private InputEvent toggleRotateInput;
        [SerializeField] private InputEvent stopPlacingInput;
        [SerializeField] private InputEvent dupInput;

        [SerializeField] private UnityEvent OnStartPlacing;
        [SerializeField] private UnityEvent OnStopPlacing;
        [SerializeField] private UnityEvent OnStartRotate;
        [SerializeField] private UnityEvent OnStopRotate;

        private Displacement currentDisplacement;
        private Vector3 placementSnapOrigin;

        [SerializeField] private Camera refCamera = null; //used for dot product angle comparison

        private readonly TransformGesture rotationGesture = new();
        private Vector3 rotationAxis;
        private Quaternion initRot;
        
        private void Awake() {
            placing = false;
            rotating = false;
            
            placeInput.performed += () => {
                if (placing && !MouseInput.overUI)
                    Place();
            };

            // Deprecated: R rotates placed selections only. Retain the former
            // placement-rotation handler for reference, but do not subscribe it.
#if false
            toggleRotateInput.performed += () => {
                if (placing && !MouseInput.overUI) {
                    rotating = !rotating;

                    if (rotating) {
                        InitRotation();
                        OnStartRotate?.Invoke();
                    }
                    else OnStopRotate?.Invoke();
                }
            };
#endif

            /*dupInput.performed += () => {
                if (currentPlacementData != null && placing) {
                    /*if (currentPlacementData.TryParse(out GameObjectPlacementData objPlaceData)) {
                        SetPlacementLayer(objPlaceData.GetGameObject(), false);

                    }#1#
                }
            };

            dupInput.canceled += () => {
                if (currentPlacementData != null && placing) {
                    if (currentPlacementData.TryParse(out GameObjectPlacementData objPlaceData)) {
                        objPlaceData.GetGameObject().GetConnectedObjects(true).ForEach(x => x.SetActive(false));
                    }
                }
            };*/

            stopPlacingInput.performed += () => {
                if (placing)
                    StopPlacing();
            };

            currentDisplacement = new Displacement(transform);
        }

        void Update() {
            if (placing) {
                // Deprecated placement rotation: preview movement always runs.
#if false
                if (rotating) {
                    if (TransformGesture.TryReadAxis(out var axis))
                        rotationAxis = rotationAxis == axis ? Vector3.zero : axis;
                    Rotate();
                }
                else {
#endif
                {
                    foreach (PlaceDisplacement placePosition in placePositions) {
                        if (placePosition.TryGetDisplacement(currentPlacementData, out Displacement displacement)) {
                            var newDisplacement = displacement;

                            // Hole and shaft placement already provide exact mechanical alignment.
                            // Only quantize free plane placement to the structural hole pitch.
                            if (TransformSnapping.Active && placePosition is PlanePlaceDisplacement) {
                                var snappedPosition = TransformSnapping.SnapPosition(
                                    displacement.translation.Position,
                                    placementSnapOrigin);
                                newDisplacement = new Displacement(
                                    snappedPosition,
                                    displacement.rotation.Orientation);
                            }

                            if (!placePosition.ModifyRotation) {
                                newDisplacement = new Displacement(newDisplacement.translation.Position,
                                    currentPlacementData is PartPlacementData part
                                        ? part.initialRotation : currentDisplacement.rotation.Orientation);
                            }
                            else if (placePosition is not PartHolePlaceDisplacement &&
                                     currentPlacementData is PartPlacementData hardwarePart) {
                                newDisplacement = new Displacement(newDisplacement.translation.Position,
                                    newDisplacement.rotation.Orientation * hardwarePart.initialRotation);
                            }

                            currentDisplacement = newDisplacement;
                            newDisplacement.Displace(gameObject);
                            break;
                        }
                    }
                }
            }

            meshRenderer.enabled = placing;
        }

        public void StartPlacing(PlacementData newPlacementData) {
            currentPlacementData = newPlacementData;
            placing = true;
            rotating = false;
            meshFilter.mesh = newPlacementData.GetDisplayMesh();
            placementSnapOrigin = Vector3.zero;

            if (newPlacementData is PartPlacementData partPlacement)
                transform.rotation = partPlacement.initialRotation;

            if (currentPlacementData.TryParse(out GameObjectPlacementData objPlaceData)) {
                var objTransform = objPlaceData.GetGameObject().transform;
                transform.rotation = objTransform.rotation;
                transform.position = objTransform.position;
                placementSnapOrigin = objTransform.position;
                currentDisplacement = new Displacement(transform);
                
                SetPlacementLayer(objPlaceData.GetGameObject(), true, !objPlaceData.individual);
            }

            currentDisplacement = new Displacement(transform);

            OnStartPlacing?.Invoke();
        }

        public void SetPlacementLayer(GameObject obj, bool value, bool includeConnections = true) {
            obj.GetConnectedObjects(true, false, includeConnections).ForEach(x => {
                if (value && x.layer == 0)
                    x.layer = Placement.PLACEMENT_LAYER;
                
                if (!value && x.layer == PLACEMENT_LAYER)
                    x.layer = 0;
            });
        }

        IEnumerator WaitStopPlacing() {
            yield return new WaitForEndOfFrame();
            OnStopPlacing?.Invoke();
            placing = false;
        }

        public void StopPlacing() {
            if (currentPlacementData.TryParse(out GameObjectPlacementData objPlaceData))
                SetPlacementLayer(objPlaceData.GetGameObject(), false, !objPlaceData.individual);
            
            transform.rotation = Quaternion.identity;
            rotating = false;
            OnStopRotate?.Invoke();

            StartCoroutine(WaitStopPlacing());
        }

        private void Place() {
#if false // Deprecated: placing must never invoke the legacy rotation path.
            if (rotating)
                Rotate();
#endif
            bool isPartPlacement = currentPlacementData.GetType() == typeof(PartPlacementData);
            bool isGameObjectPlacement = currentPlacementData.GetType() == typeof(GameObjectPlacementData);

            var displaceRot = currentDisplacement.rotation.Orientation;
            var displacePos = currentDisplacement.translation.Position;
            
            if (isPartPlacement) {
                var generator = ((PartPlacementData)currentPlacementData).partGenerator;
                GameObject placedObj = generator.Generate(displacePos, displaceRot);

                // Call OutputPartsList to update the weight display after placing a part
                PartListOutput partListOutput = FindObjectOfType<PartListOutput>();
                if (partListOutput != null) {
                    partListOutput.CalculatePartsList(); // Recalculate and notify weight update
                }

                ObjectElement prevElement = new ObjectElement(placedObj);
                prevElement.existing = false;
                StateSystem.AddElement(prevElement);

                ObjectElement objElement = new ObjectElement(placedObj);
                StateSystem.AddState(objElement);
            }
            else if (isGameObjectPlacement) {
                movementManager.DisplaceTo(currentDisplacement, 0);
                StopPlacing();
            }

            rotating = false;
            OnStopRotate?.Invoke();
        }
        [Obsolete("Placement rotation is disabled. Use R on placed selections instead.", true)]
        public void InitRotation() {
#if false // Retained temporarily for reference; also inert if invoked by a UnityEvent.
            initRot = transform.rotation;
            rotationAxis = Vector3.zero;
            rotationGesture.Begin(refCamera, transform.position, MouseInput.Position);
#endif
        }

        [Obsolete("Placement rotation is disabled. Use R on placed selections instead.", true)]
        public void Rotate() {
#if false // Retained temporarily for reference; also inert if invoked by a UnityEvent.
            Quaternion newRot = rotationGesture.Rotation(MouseInput.Position, rotationAxis, initRot, TransformSnapping.Active);

            Rotation rot = new Rotation(newRot);
            currentDisplacement = new Displacement(currentDisplacement.translation.Position, newRot);
            rot.Rotate(gameObject);
#endif
        }
    }
}
