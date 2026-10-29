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
        private int placementStartFrame = -1;
        public PartPlacementData CurrentPartPlacement => placing ? currentPlacementData as PartPlacementData : null;

        public void RefreshPartPreview() {
            if (CurrentPartPlacement != null) {
                SetPreviewMesh(CurrentPartPlacement.GetDisplayMesh());
                CurrentPartPlacement.RefreshAttachment();
            }
        }

        [CacheComponent] private MeshRenderer meshRenderer;

        [CacheComponent] private MeshFilter meshFilter;
        private Mesh previewMesh;
        private MovePreviewVisibility previewVisibility;

        private void SetPreviewMesh(Mesh mesh) {
            ReleasePreviewMesh();
            previewMesh = mesh;
            meshFilter.sharedMesh = mesh;
        }

        private void OnDestroy() {
            moveRotation.End();
            ReleaseMovePreview();
            ReleasePreviewMesh();
        }

        private void ReleasePreviewMesh() {
            if (previewMesh == null) return;
            if (Application.isPlaying) Destroy(previewMesh);
            else DestroyImmediate(previewMesh);
            previewMesh = null;
        }

        private void ReleaseMovePreview() {
            if (currentPlacementData is GameObjectPlacementData moving && moving.GetGameObject() != null)
                SetPlacementLayer(moving.GetGameObject(), false, !moving.individual);
            previewVisibility?.Dispose();
            previewVisibility = null;
        }

        private void OnDisable() {
            ReleaseMovePreview();
            moveRotation.End();
            placing = rotating = false;
            if (meshRenderer != null) meshRenderer.enabled = false;
        }

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

        private readonly MoveRotation moveRotation = new();
        private Pose? rotationAnchor;
        private Quaternion freeRotation;
        private float attachmentTwist;
        private KeyboardTransformTool keyboardTool;
        private bool IsNavigatingCamera => keyboardTool != null && keyboardTool.IsCameraNavigationHeld;
        public bool IsTypingRotation => placing && rotating && moveRotation.NumericInput.IsOpen;

        public string ControlsText => !placing ? null : IsTypingRotation
            ? "Rotate · Exact entry\nEnter: place    /: value mode    Esc: cancel"
            : rotating
            ? $"Rotate · {(rotationAnchor.HasValue ? "Attachment axis" : "Camera axis")}\n/: type angle    {toggleRotateInput.GetCurrentKeybind()}: resume positioning    Click: place    Esc: cancel"
            : $"{toggleRotateInput.GetCurrentKeybind()}: rotate    Click: place    Esc: cancel";
        
        private void Awake() {
            placing = false;
            rotating = false;
            keyboardTool = GetComponent<KeyboardTransformTool>();
            
            placeInput.performed += () => {
                if (placing && Time.frameCount != placementStartFrame && !MouseInput.overUI && !IsNavigatingCamera && !IsTypingRotation)
                    Place();
            };

            toggleRotateInput.performed += ToggleRotation;

            stopPlacingInput.performed += () => {
                if (placing)
                    StopPlacing();
            };

            currentDisplacement = new Displacement(transform);
        }

        void Update() {
            if (placing) {
                if (rotating) Rotate();
                else UpdatePosition();
            }

            meshRenderer.enabled = placing;
        }

        private void LateUpdate() {
            if (!placing || !rotating || RebindAction.Rebinding || Keyboard.current == null) return;
            var keyboard = Keyboard.current;
            if (IsTypingRotation && keyboard.escapeKey.wasPressedThisFrame) {
                KeyboardTransformTool.CaptureCurrentFrame();
                StopPlacing();
                return;
            }
            if (moveRotation.HandleNumeric(keyboard, MouseInput.Position, out var confirmed)) {
                if (!IsTypingRotation) KeyboardTransformTool.CaptureCurrentFrame();
                if (confirmed.HasValue) {
                    ApplyPose(confirmed.Value);
                    Place();
                }
            }
        }

        private void UpdatePosition() {
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
                            freeRotation);
                    }
                    else if (placePosition is not PartHolePlaceDisplacement && placePosition is not Protobot.Simulation.GearPlaceDisplacement &&
                             currentPlacementData is PartPlacementData hardwarePart) {
                        newDisplacement = new Displacement(newDisplacement.translation.Position,
                            newDisplacement.rotation.Orientation * hardwarePart.initialRotation);
                    }

                    rotationAnchor = placePosition.ModifyRotation ? placePosition.GetRotationAnchor(displacement) : null;
                    var pose = new Pose(newDisplacement.translation.Position, newDisplacement.rotation.Orientation);
                    if (rotationAnchor.HasValue)
                        pose = MoveRotation.Twist(pose, rotationAnchor.Value, attachmentTwist);
                    ApplyPose(pose);
                    if (currentPlacementData is GameObjectPlacementData) freeRotation = pose.rotation;
                    break;
                }
            }
        }

        private void ApplyPose(Pose pose) {
            transform.DOKill();
            currentDisplacement = new Displacement(pose.position, pose.rotation);
            if (!rotating) currentDisplacement.Displace(gameObject);
            else {
                var start = new Pose(transform.position, transform.rotation);
                var pivot = moveRotation.Pivot;
                DOVirtual.Float(0, 1, .25f, progress => {
                    var displayed = MoveRotation.Interpolate(start, pose, pivot, progress);
                    transform.SetPositionAndRotation(displayed.position, displayed.rotation);
                }).SetTarget(transform).SetEase(DOTween.defaultEaseType);
            }
        }

        private void ToggleRotation() {
            if (!placing || IsTypingRotation || Mouse.current == null || MouseInput.overUI || RebindAction.Rebinding ||
                KeyboardTransformTool.IsEditingText() || IsNavigatingCamera) return;
            if (rotating) StopRotation();
            else {
                UpdatePosition();
                InitRotation();
            }
        }

        private void StopRotation() {
            if (!rotating) return;
            Rotate();
            transform.DOComplete();
            if (rotationAnchor.HasValue)
                attachmentTwist = TransformSnapping.WrapAngle(attachmentTwist + moveRotation.Degrees);
            freeRotation = currentDisplacement.rotation.Orientation;
            moveRotation.End();
            rotating = false;
            OnStopRotate?.Invoke();
        }

        public void StartPlacing(PlacementData newPlacementData) {
            Protobot.Simulation.SimulationTool.ExitForEdit();
            ReleaseMovePreview();
            currentPlacementData = newPlacementData;
            placementStartFrame = Time.frameCount;
            placing = true;
            rotating = false;
            moveRotation.End();
            rotationAnchor = null;
            attachmentTwist = 0;
            SetPreviewMesh(newPlacementData.GetDisplayMesh());
            placementSnapOrigin = Vector3.zero;

            if (newPlacementData is PartPlacementData partPlacement)
                transform.rotation = partPlacement.initialRotation;

            if (currentPlacementData.TryParse(out GameObjectPlacementData objPlaceData)) {
                var objTransform = objPlaceData.GetGameObject().transform;
                transform.rotation = objTransform.rotation;
                transform.position = objTransform.position;
                placementSnapOrigin = objTransform.position;
                currentDisplacement = new Displacement(transform);
                
                var bodies = objPlaceData.GetGameObject().GetConnectedObjects(true, false, !objPlaceData.individual);
                previewVisibility = new MovePreviewVisibility(bodies, MeshCombiner.GetRenderers(bodies));
                SetPlacementLayer(objPlaceData.GetGameObject(), true, !objPlaceData.individual);
            }

            currentDisplacement = new Displacement(transform);
            freeRotation = transform.rotation;

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
            ReleaseMovePreview();
            transform.DOKill();
            
            transform.rotation = Quaternion.identity;
            moveRotation.End();
            rotating = false;
            OnStopRotate?.Invoke();

            StartCoroutine(WaitStopPlacing());
        }

        private void Place() {
            StopRotation();
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
        public void InitRotation() {
            if (!placing || rotating) return;
            transform.DOComplete();
            moveRotation.Begin(refCamera, new Pose(currentDisplacement.translation.Position, currentDisplacement.rotation.Orientation),
                rotationAnchor, MouseInput.Position, attachmentTwist);
            rotating = true;
            OnStartRotate?.Invoke();
        }

        public void Rotate() {
            if (!placing || !rotating) return;
            if (IsTypingRotation || MoveRotation.SlashPressed(Keyboard.current)) return;
            if (IsNavigatingCamera || MouseInput.overUI) {
                moveRotation.Pause(MouseInput.Position);
                return;
            }
            ApplyPose(moveRotation.Evaluate(MouseInput.Position, TransformSnapping.Active));
        }

        private void OnGUI() {
            if (placing && rotating) moveRotation.DrawFeedback();
        }
    }
}
