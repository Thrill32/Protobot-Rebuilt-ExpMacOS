using System.Collections;
using System.Collections.Generic;
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

        public void Awake() {
            movementManager.OnStartMoving += () => {
                if (input.IsPressed) {
                    if (input.IsKeyPressed("Tab") && input.GetCurrentKeybind() == "Alt") {
                        print("ohyeahj");
                        return;
                    }
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
                var movingObjs = movementManager.MovingObj.GetConnectedObjects(true, true);

                prevDuplicatedObjs = new List<GameObject>();

                foreach (GameObject obj in movingObjs) {
                    GameObject clone = Instantiate(obj, obj.transform.position, obj.transform.rotation);
                    clone.DisableOutline();
                    if(clone.name != "MultiPivot(Clone)")
                    {
                        clone.GetComponent<Renderer>().material = new Material(obj.GetComponent<Renderer>().material);
                    }
                    prevDuplicatedObjs.Add(clone);
                }
            }
        }
    }
}
