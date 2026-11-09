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
using Protobot.SelectionSystem;
using Protobot.Collaboration;
using System.Linq;

namespace Protobot {
    public class Placement : MonoBehaviour { 
        public static int PLACEMENT_LAYER => 14;
        private static Placement instance;
        public static bool IsCopyingSelection => instance != null && instance.placing && instance.currentPlacementData is SelectionPlacementData;

        [SerializeField] private MovementManager movementManager;

        public bool placing;
        public bool rotating;

        private PlacementData currentPlacementData;
        private int placementStartFrame = -1;
        private bool awaitingPlaceInputRelease;
        public PartPlacementData CurrentPartPlacement => placing ? currentPlacementData as PartPlacementData : null;
        public SelectionPlacementData CurrentSelectionPlacement => placing ? currentPlacementData as SelectionPlacementData : null;
        private Coroutine stopping;

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
            ReleaseSelectionPreview();
            ReleasePreviewMesh();
            if (instance == this) instance = null;
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
            ReleaseSelectionPreview();
            moveRotation.End();
            placing = rotating = false;
            if (meshRenderer != null) meshRenderer.enabled = false;
        }

        private void ReleaseSelectionPreview() {
            if (currentPlacementData is not SelectionPlacementData selection) return;
            selection.Dispose();
            currentPlacementData = null;
            ReleasePreviewMesh();
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
        private VersionRef[] targetDependencies = System.Array.Empty<VersionRef>();
        public Part[] SharedMovePreview() {
            if (!placing || currentPlacementData is not GameObjectPlacementData moving) return null;
            var source = moving.GetGameObject();
            var bodies = source.GetConnectedObjects(true, false, !moving.individual);
            var delta = currentDisplacement.rotation.Orientation * Quaternion.Inverse(source.transform.rotation);
            var origin = source.transform.position;
            return bodies.Select(body => {
                var part = CollaborationScene.Capture(body);
                var position = currentDisplacement.translation.Position + delta * (body.transform.position - origin);
                var rotation = delta * body.transform.rotation;
                part.position = new double[] { position.x, position.y, position.z };
                part.rotation = new double[] { rotation.x, rotation.y, rotation.z, rotation.w }; return part;
            }).ToArray();
        }
        private Vector3 placementSnapOrigin;

        [SerializeField] private Camera refCamera = null; //used for dot product angle comparison

        private readonly MoveRotation moveRotation = new();
        private Pose? rotationAnchor;
        private Quaternion freeRotation;
        private float attachmentTwist;
        private KeyboardTransformTool keyboardTool;
        private bool IsNavigatingCamera => keyboardTool != null && keyboardTool.IsCameraNavigationHeld;
        public bool IsTypingRotation => placing && rotating && moveRotation.NumericInput.IsOpen;

        public string ControlsText {
            get {
                if (!placing) return null;
                var controls = IsTypingRotation
                    ? "Rotate · Exact entry\nEnter: place    /: value mode    Esc: cancel"
                    : rotating
                    ? $"Rotate · {(rotationAnchor.HasValue ? "Attachment axis" : "Camera axis")}\n/: type angle    {toggleRotateInput.GetCurrentKeybind()}: resume positioning    Click: place    Esc: cancel"
                    : $"{toggleRotateInput.GetCurrentKeybind()}: rotate    Click: place    Esc: cancel";
                // Placement status follows the preview lifetime, rather than
                // lingering as a timed notification after a quick click/cancel.
                return currentPlacementData is SelectionPlacementData selection
                    ? $"Placing {selection.PartCount} parts · {(selection.RepeatPlacement ? "repeat placement" : "place and select")}\n{controls}"
                    : controls;
            }
        }
        
        private void Awake() {
            instance = this;
            if (GetComponent<EscapeDeselect>() == null) gameObject.AddComponent<EscapeDeselect>();
            placing = false;
            rotating = false;
            keyboardTool = GetComponent<KeyboardTransformTool>();
            
            placeInput.performed += () => {
                if (placing && !awaitingPlaceInputRelease && Time.frameCount != placementStartFrame && !MouseInput.overUI && !IsNavigatingCamera && !IsTypingRotation)
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
                // A native file picker's closing click can reach Unity after the
                // preview starts. Require a released input on a later frame first.
                if (Time.frameCount != placementStartFrame && !placeInput.IsCurrentlyPressed)
                    awaitingPlaceInputRelease = false;
                if (rotating) Rotate();
                else UpdatePosition();
            }

            meshRenderer.enabled = placing;
        }

        private void OnApplicationFocus(bool focused) {
            if (!placing) return;
            awaitingPlaceInputRelease = true;
            placementStartFrame = Time.frameCount;
        }

        private void LateUpdate() {
            if (!placing || !rotating || RebindAction.Rebinding || Keyboard.current == null) return;
            var keyboard = Keyboard.current;
            if (IsTypingRotation && keyboard.escapeKey.wasPressedThisFrame) {
                if (!EscapeDeselect.TryHandleEscape()) StopPlacing();
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
            if (currentPlacementData is SelectionPlacementData && (IsNavigatingCamera || MouseInput.overUI)) return;
            foreach (PlaceDisplacement placePosition in placePositions) {
                // P uses only its explicitly selected source face, or the grid.
                // Hardware and gear rules must never infer an anchor for this rigid copy.
                if (currentPlacementData is SelectionPlacementData selection && placePosition is not PlanePlaceDisplacement &&
                    (!selection.HasHole || placePosition is not HolePlaceDisplacement)) continue;
                CollaborationScene.BeginReads();
                bool found;
                Displacement displacement;
                try { found = placePosition.TryGetDisplacement(currentPlacementData, out displacement); }
                finally { targetDependencies = CollaborationScene.EndReads(); }
                if (found) {
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
            if (CollaborationSession.IsShared && !CollaborationSession.CanEdit) {
                if (newPlacementData is SelectionPlacementData unsupported) unsupported.Dispose();
                return;
            }
            CustomModelTool.CancelActive();
            Protobot.Simulation.SimulationTool.ExitForEdit();
            if (stopping != null) { StopCoroutine(stopping); stopping = null; }
            ReleaseMovePreview();
            ReleaseSelectionPreview();
            currentPlacementData = newPlacementData;
            targetDependencies = System.Array.Empty<VersionRef>();
            placementStartFrame = Time.frameCount;
            awaitingPlaceInputRelease = true;
            placing = true;
            rotating = false;
            moveRotation.End();
            rotationAnchor = null;
            attachmentTwist = 0;
            SetPreviewMesh(newPlacementData.GetDisplayMesh());
            placementSnapOrigin = Vector3.zero;

            if (newPlacementData is PartPlacementData partPlacement)
                transform.rotation = partPlacement.initialRotation;
            else if (newPlacementData is SelectionPlacementData selectionPlacement)
                transform.rotation = selectionPlacement.InitialRotation;

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
            ReleaseSelectionPreview();
            stopping = null;
        }

        public void StopPlacing() {
            if (!placing || stopping != null) return;
            ReleaseMovePreview();
            if (CollaborationSession.IsShared && currentPlacementData is GameObjectPlacementData) CollaborationSession.CancelPose();
            transform.DOKill();
            
            transform.rotation = Quaternion.identity;
            moveRotation.End();
            rotating = false;
            OnStopRotate?.Invoke();

            if (currentPlacementData is SelectionPlacementData) {
                placing = false;
                meshRenderer.enabled = false;
                ReleaseSelectionPreview();
                KeyboardTransformTool.CaptureCurrentFrame();
                OnStopPlacing?.Invoke();
                return;
            }

            stopping = StartCoroutine(WaitStopPlacing());
        }

        public void CancelPlacement() {
            if (!placing) return;
            StopPlacing();
            if (stopping == null) return; // Selection templates already stop synchronously.
            StopCoroutine(stopping);
            stopping = null;
            placing = false;
            meshRenderer.enabled = false;
            OnStopPlacing?.Invoke();
        }

        private void Place() {
            if (CollaborationSession.IsShared && !CollaborationSession.CanEdit) return;
            StopRotation();
            bool isPartPlacement = currentPlacementData.GetType() == typeof(PartPlacementData);
            bool isGameObjectPlacement = currentPlacementData.GetType() == typeof(GameObjectPlacementData);

            var displaceRot = currentDisplacement.rotation.Orientation;
            var displacePos = currentDisplacement.translation.Position;
            
            if (isPartPlacement) {
                var generator = ((PartPlacementData)currentPlacementData).partGenerator;
                GameObject placedObj = generator.Generate(displacePos, displaceRot);
                if (CollaborationSession.IsShared) {
                    CollaborationSession.PlaceCatalog(placedObj, targetDependencies);
                    return;
                }

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
            else if (currentPlacementData is SelectionPlacementData selection) {
                if (CollaborationSession.IsShared) {
                    var selected = selection.ResultSelection;
                    var sharedCopies = selection.Generate(displacePos, displaceRot);
                    if (!selection.RepeatPlacement) StopPlacing();
                    CollaborationSession.PlaceCopies(sharedCopies, targetDependencies, selected);
                    return;
                }
                var resultSelection = selection.ResultSelection;
                var previousSelection = resultSelection != null ? resultSelection.current : null;
                if (resultSelection != null) StateSystem.AddElement(new PlacedSelectionElement(resultSelection, previousSelection));
                var copies = selection.Generate(displacePos, displaceRot);
                foreach (var copy in copies) StateSystem.AddElement(new ObjectElement(copy) { existing = false });
                var elements = new List<IElement>();
                foreach (var copy in copies) elements.Add(new ObjectElement(copy));
                foreach (var chain in selection.GeneratedChains) {
                    StateSystem.AddElement(new ActiveElement(chain, false));
                    elements.Add(new ActiveElement(chain, true));
                }
                if (!selection.RepeatPlacement) StopPlacing();
                if (resultSelection != null) {
                    ISelection placed = copies.Count == 1 ? new ObjectSelection { gameObject = copies[0], individual = true }
                        : new MultiSelection(null, copies) { individual = true };
                    var selected = new PlacedSelectionElement(resultSelection, placed);
                    selected.Load();
                    elements.Add(selected);
                }
                StateSystem.AddState(new State(elements));
                FindFirstObjectByType<PartListOutput>()?.CalculatePartsList();
            }
            else if (isGameObjectPlacement) {
                if (CollaborationSession.IsShared) {
                    var moving = (GameObjectPlacementData)currentPlacementData;
                    var source = moving.GetGameObject();
                    var bodies = source.GetConnectedObjects(true, false, !moving.individual);
                    var delta = displaceRot * Quaternion.Inverse(source.transform.rotation);
                    var origin = source.transform.position;
                    var poses = bodies.Select(b => new Pose(displacePos + delta * (b.transform.position - origin), delta * b.transform.rotation)).ToArray();
                    for (int i = 0; i < bodies.Count; i++) bodies[i].transform.SetPositionAndRotation(poses[i].position, poses[i].rotation);
                    CollaborationSession.ConfirmPose(bodies, dependencies: targetDependencies);
                }
                else movementManager.DisplaceTo(currentDisplacement, 0);
                StopPlacing();
            }

            rotating = false;
            OnStopRotate?.Invoke();
        }

        private sealed class ActiveElement : IElement {
            private readonly GameObject obj;
            private readonly bool active;
            public ActiveElement(GameObject obj, bool active) { this.obj = obj; this.active = active; }
            public void Load() { if (obj != null) obj.SetActive(active); }
        }

        private sealed class PlacedSelectionElement : IElement {
            private readonly SelectionManager manager;
            private readonly ISelection selection;
            public PlacedSelectionElement(SelectionManager manager, ISelection selection) { this.manager = manager; this.selection = selection; }
            public void Load() {
                if (manager == null) return;
                if (manager.current != null) manager.current.selector = null;
                manager.ClearCurrent(false, true, true);
                if (selection?.gameObject != null && selection.gameObject.activeInHierarchy) {
                    selection.selector = null;
                    manager.SetCurrent(selection, true, bypassResponseSelectors: true);
                }
            }
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
