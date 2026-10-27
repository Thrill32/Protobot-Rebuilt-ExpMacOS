using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Protobot.StateSystems;
using Protobot.InputEvents;
using Protobot.Outlining;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

namespace Protobot {
    public class ObjectDuplicator : MonoBehaviour {
        [SerializeField] private MovementManager movementManager;
        [SerializeField] private InputEvent input;

        private List<GameObject> prevDuplicatedObjs = new List<GameObject>();
        private bool duplicatedOnMove;
        private GameObject duplicateIndicator;

        // Poll the binding itself so G/R works even when its InputEvent updates
        // earlier than the Duplicate modifier or modal capture is active.
        public bool IsDuplicateHeld => input != null && input.IsCurrentlyPressed &&
            !(Keyboard.current != null && Keyboard.current.tabKey.isPressed && input.GetCurrentKeybind() == "Alt");

        public void Awake() {
            movementManager.OnStartMoving += () => {
                if (IsDuplicateHeld) {
                    DuplicateObject();
                    
                    foreach (GameObject obj in prevDuplicatedObjs) {
                        ObjectElement objElement = new ObjectElement(obj);
                        objElement.existing = false;

                        StateSystem.AddElement(objElement);
                    }

                    duplicatedOnMove = true;
                }
            };

            StateSystem.OnChangeState += () => {
                if (duplicatedOnMove) {
                    ObjectElement.AddObjectElements(prevDuplicatedObjs);
                    duplicatedOnMove = false;
                }
            };
        }

        private void Start() {
            CreateDuplicateIndicator();
        }

        private void Update() {
            if (duplicateIndicator == null)
                return;

            bool shouldShow = input != null && !RebindAction.Rebinding && input.IsPressed;
            if (duplicateIndicator.activeSelf != shouldShow)
                duplicateIndicator.SetActive(shouldShow);
        }

        private void CreateDuplicateIndicator() {
            var hudObject = new GameObject(
                "Duplicate Mode HUD",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler));
            hudObject.transform.SetParent(transform, false);

            var hudCanvas = hudObject.GetComponent<Canvas>();
            hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            hudCanvas.overrideSorting = true;
            hudCanvas.sortingOrder = 100;

            var scaler = hudObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            duplicateIndicator = new GameObject(
                "Duplicate Mode Indicator",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            duplicateIndicator.transform.SetParent(hudObject.transform, false);

            var rect = duplicateIndicator.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(16f, 18f);
            rect.sizeDelta = new Vector2(132f, 34f);

            var background = duplicateIndicator.GetComponent<Image>();
            background.color = new Color(0.055f, 0.065f, 0.08f, 0.94f);
            background.raycastTarget = false;

            var accentObject = new GameObject(
                "Accent",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            accentObject.transform.SetParent(duplicateIndicator.transform, false);
            var accentRect = accentObject.GetComponent<RectTransform>();
            accentRect.anchorMin = Vector2.zero;
            accentRect.anchorMax = new Vector2(0f, 1f);
            accentRect.pivot = Vector2.zero;
            accentRect.anchoredPosition = Vector2.zero;
            accentRect.sizeDelta = new Vector2(4f, 0f);
            var accent = accentObject.GetComponent<Image>();
            accent.color = new Color(0f, 0.596f, 1f, 1f);
            accent.raycastTarget = false;

            var labelObject = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(duplicateIndicator.transform, false);
            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(14f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);

            var label = labelObject.GetComponent<TextMeshProUGUI>();
            label.text = "Duplicating";
            label.fontSize = 15f;
            label.fontStyle = FontStyles.Normal;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.raycastTarget = false;

            duplicateIndicator.SetActive(false);
        }

        private void DuplicateObject() {
            if (movementManager.MovingObj != null) {
                var movingObjs = movementManager.GetMovingConnectedObjs();
                prevDuplicatedObjs = CreateCopies(movingObjs);
            }
        }

        /// <summary>Shared by axis dragging and keyboard transforms. Copy each
        /// part once, leaving selection pivots out of the cloned hierarchy.</summary>
        public List<GameObject> CreateCopies(IEnumerable<GameObject> objects) {
            var parts = objects.Where(obj => obj != null)
                .SelectMany(obj => obj.GetComponentsInChildren<SavedObject>())
                .Select(saved => saved.gameObject).Distinct().ToList();
            var sourceRoots = parts.Where(part => !parts.Any(other =>
                other != part && part.transform.IsChildOf(other.transform))).ToList();
            var copies = new List<GameObject>();
            foreach (var part in sourceRoots) {
                var copy = Instantiate(part, part.transform.position, part.transform.rotation);
                copy.DisableOutline();
                foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(material =>
                        material != null ? new Material(material) : null).ToArray();
                copies.Add(copy);
            }
            return copies;
        }

        public static void DiscardCopies(List<GameObject> copies) {
            foreach (var copy in copies.Where(copy => copy != null)) {
                copy.SetActive(false);
                foreach (var material in copy.GetComponentsInChildren<Renderer>(true)
                             .SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null).Distinct()) {
                    if (Application.isPlaying) Destroy(material);
                    else DestroyImmediate(material);
                }
                if (Application.isPlaying) Destroy(copy);
                else DestroyImmediate(copy);
            }
            copies.Clear();
        }
    }
}
