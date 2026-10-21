using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Protobot.SelectionSystem;
using Protobot.StateSystems;

namespace Protobot.Tools {
    /// <summary>
    /// Translates a selected rigid set so one picked hole/body center meets
    /// another. Rotation is deliberately left unchanged.
    /// </summary>
    public sealed class ConnectTool : MonoBehaviour {
        [SerializeField] private MouseCast mouseCast;
        [SerializeField] private SelectionManager selectionManager;
        [SerializeField] private Toggle toolbarToggle;

        public static bool IsCapturingPoints { get; private set; }

        private readonly List<GameObject> movingBodies = new();
        private readonly List<GameObject> movingRoots = new();
        private bool toolActive;
        private bool hadSelectionOnActivation;
        private bool hasSource;
        private Vector3 sourcePosition;
        private GameObject sourceMarker;
        private GameObject statusRoot;
        private TextMeshProUGUI statusText;

        private void Awake() {
            if (mouseCast == null)
                mouseCast = FindFirstObjectByType<MouseCast>();

            if (selectionManager == null) {
                var selectionObject = GameObject.Find("Primary Selection Manager");
                if (selectionObject != null)
                    selectionManager = selectionObject.GetComponent<SelectionManager>();
            }

            if (toolbarToggle == null)
                toolbarToggle = GetComponent<Toggle>();

            if (toolbarToggle != null)
                toolbarToggle.onValueChanged.AddListener(SetActive);

            CreateStatusHud();
        }

        private void Update() {
            if (!toolActive)
                return;

            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) {
                if (toolbarToggle != null)
                    toolbarToggle.isOn = false;
                else
                    SetActive(false);
                return;
            }

            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
                return;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            TryPickPoint();
        }

        public void SetActive(bool value) {
            toolActive = value;
            IsCapturingPoints = value;

            if (value) {
                CaptureCurrentSelection();
                ResetSource();
                SetStatus(hadSelectionOnActivation
                    ? $"Connect · {movingBodies.Count} selected · pick source"
                    : "Connect · pick source");
            }
            else {
                ResetSource();
                if (statusRoot != null)
                    statusRoot.SetActive(false);
            }
        }

        private void TryPickPoint() {
            if (mouseCast == null)
                return;

            var hit = mouseCast.hit;
            if (hit.collider == null) {
                SetStatus("Connect · pick a hole or body");
                return;
            }

            if (!TryResolveEndpoint(hit, out var endpointPosition, out var endpointBody, out var endpointName)) {
                SetStatus("Connect · pick a hole or body");
                return;
            }

            if (!hasSource) {
                if (!hadSelectionOnActivation)
                    CaptureBodySet(endpointBody);

                if (movingBodies.Count == 0) {
                    SetStatus("Connect · source has no movable body");
                    return;
                }

                if (!movingBodies.Contains(endpointBody)) {
                    SetStatus("Connect · source must be on the selected bodies");
                    return;
                }

                sourcePosition = endpointPosition;
                hasSource = true;
                ShowSourceMarker(sourcePosition);
                SetStatus($"Connect · {endpointName} selected · pick target");
                return;
            }

            if (movingBodies.Contains(endpointBody)) {
                SetStatus("Connect · target must be outside the moving selection");
                return;
            }

            MoveSelection(endpointPosition);
            ResetSource();

            if (!hadSelectionOnActivation) {
                movingBodies.Clear();
                movingRoots.Clear();
            }

            SetStatus("Connected · pick another source");
        }

        private bool TryResolveEndpoint(
            RaycastHit hit,
            out Vector3 position,
            out GameObject body,
            out string endpointName) {
            var hole = hit.collider.GetComponent<HoleCollider>();
            if (hole != null && hole.holeData?.part != null) {
                position = hole.holeData.position;
                body = hole.holeData.part;
                endpointName = "hole center";
                return true;
            }

            var savedObject = hit.collider.GetComponentInParent<SavedObject>();
            if (savedObject != null) {
                body = savedObject.gameObject;
                position = body.transform.position;
                endpointName = "body pivot";
                return true;
            }

            position = default;
            body = null;
            endpointName = null;
            return false;
        }

        private void CaptureCurrentSelection() {
            movingBodies.Clear();
            movingRoots.Clear();
            hadSelectionOnActivation = false;

            var selection = selectionManager != null ? selectionManager.current : null;
            if (selection == null)
                return;

            switch (selection) {
                case MultiSelection multiSelection:
                    AddBodies(multiSelection.objs);
                    if (multiSelection.pivot != null)
                        movingRoots.Add(multiSelection.pivot.gameObject);
                    break;
                case HoleSelection holeSelection when holeSelection.holeData?.part != null:
                    CaptureBodySet(holeSelection.holeData.part);
                    break;
                default:
                    var selectedSavedObject = selection.gameObject != null
                        ? selection.gameObject.GetComponentInParent<SavedObject>()
                        : null;
                    if (selectedSavedObject != null)
                        CaptureBodySet(selectedSavedObject.gameObject);
                    else if (selection.gameObject != null &&
                             selection.gameObject.TryGetComponent(out Pivot pivot)) {
                        AddBodies(pivot.objects);
                        movingRoots.Add(pivot.gameObject);
                    }
                    break;
            }

            hadSelectionOnActivation = movingBodies.Count > 0;
        }

        private void CaptureBodySet(GameObject body) {
            movingBodies.Clear();
            movingRoots.Clear();

            if (body == null)
                return;

            if (body.TryGetGroup(out List<GameObject> groupObjects))
                AddBodies(groupObjects);
            else
                AddBodies(body.GetConnectedObjects(true, true));

            movingRoots.AddRange(movingBodies);
        }

        private void AddBodies(IEnumerable<GameObject> objects) {
            foreach (var candidate in objects) {
                if (candidate == null)
                    continue;

                var savedObject = candidate.GetComponent<SavedObject>() ??
                                  candidate.GetComponentInParent<SavedObject>();
                if (savedObject != null && !movingBodies.Contains(savedObject.gameObject))
                    movingBodies.Add(savedObject.gameObject);
            }
        }

        private void MoveSelection(Vector3 targetPosition) {
            if (movingBodies.Count == 0 || movingRoots.Count == 0)
                return;

            var stateBodies = movingBodies.Where(body => body != null).Distinct().ToList();
            var roots = movingRoots.Where(root => root != null).Distinct().ToList();

            ObjectElement.AddObjectElements(stateBodies);

            var movePivot = Pivot.Create("Connect Tool Pivot");
            movePivot.transform.position = sourcePosition;
            movePivot.AddObjects(roots);

            movePivot.transform.DOMove(targetPosition, 0.25f).OnComplete(() => {
                movePivot.Destroy();
                StateSystem.AddEmptyState();
                ObjectElement.AddObjectElements(stateBodies);
            });
        }

        private void ResetSource() {
            hasSource = false;
            if (sourceMarker != null)
                Destroy(sourceMarker);
        }

        private void ShowSourceMarker(Vector3 position) {
            if (sourceMarker != null)
                Destroy(sourceMarker);

            sourceMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sourceMarker.name = "Connect Source Marker";
            sourceMarker.layer = LayerMask.NameToLayer("Ignore Raycast");
            sourceMarker.transform.position = position;
            sourceMarker.transform.localScale = Vector3.one * 0.16f;

            var collider = sourceMarker.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var renderer = sourceMarker.GetComponent<Renderer>();
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (renderer != null && shader != null) {
                renderer.material = new Material(shader);
                renderer.material.color = new Color(0f, 0.6f, 1f, 1f);
            }
        }

        private void CreateStatusHud() {
            var canvasObject = new GameObject(
                "Connect Tool HUD",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 99;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            statusRoot = new GameObject(
                "Connect Status",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            statusRoot.transform.SetParent(canvasObject.transform, false);

            var rect = statusRoot.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(16f, 60f);
            rect.sizeDelta = new Vector2(360f, 34f);

            var background = statusRoot.GetComponent<Image>();
            background.color = new Color(0.055f, 0.065f, 0.08f, 0.94f);
            background.raycastTarget = false;

            var labelObject = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(statusRoot.transform, false);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(12f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);

            statusText = labelObject.GetComponent<TextMeshProUGUI>();
            statusText.fontSize = 14f;
            statusText.color = Color.white;
            statusText.alignment = TextAlignmentOptions.MidlineLeft;
            statusText.raycastTarget = false;

            statusRoot.SetActive(false);
        }

        private void SetStatus(string text) {
            if (statusRoot == null || statusText == null)
                return;

            statusText.text = text;
            statusRoot.SetActive(true);
        }

        private void OnDisable() {
            if (toolActive)
                SetActive(false);
        }

        private void OnDestroy() {
            if (toolbarToggle != null)
                toolbarToggle.onValueChanged.RemoveListener(SetActive);

            if (IsCapturingPoints)
                IsCapturingPoints = false;
        }
    }
}
