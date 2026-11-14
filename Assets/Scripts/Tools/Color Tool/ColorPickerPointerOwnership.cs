using System.Collections.Generic;
using Protobot;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>Keeps RGB slider gestures owned by the custom-color picker.</summary>
public sealed class ColorPickerPointerOwnership : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler {
    private static readonly HashSet<int> pointers = new();
    private static RectTransform panel;
    private static int releaseFrame = -1;

    public static bool BlocksScenePointer {
        get {
            if (pointers.Count > 0 || releaseFrame == Time.frameCount) return true;
            return panel != null && panel.gameObject.activeInHierarchy && Mouse.current != null &&
                RectTransformUtility.RectangleContainsScreenPoint(panel, MouseInput.Position);
        }
    }

    public static void Configure(RectTransform pickerPanel) => panel = pickerPanel;

    public void OnPointerDown(PointerEventData eventData) => Claim(eventData);
    public void OnBeginDrag(PointerEventData eventData) => Claim(eventData);
    public void OnDrag(PointerEventData eventData) => Claim(eventData);

    public void OnPointerUp(PointerEventData eventData) => Release(eventData);
    public void OnEndDrag(PointerEventData eventData) => Release(eventData);

    private static void Claim(PointerEventData eventData) {
        if (eventData.button == PointerEventData.InputButton.Left) pointers.Add(eventData.pointerId);
    }

    private static void Release(PointerEventData eventData) {
        if (eventData.button != PointerEventData.InputButton.Left) return;
        pointers.Remove(eventData.pointerId);
        releaseFrame = Time.frameCount;
    }

    private void OnDisable() {
        pointers.Clear();
        releaseFrame = Time.frameCount;
    }

    private void OnDestroy() {
        pointers.Clear();
    }
}
