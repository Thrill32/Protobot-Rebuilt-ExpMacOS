using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot.Simulation {
    /// <summary>Explicit ordered route editing. Its ribbon is a visual preview, never a logical chain.</summary>
    public sealed class ChainRoutingPanel : IDisposable {
        private readonly List<SimulationPart> parts;
        private readonly List<int> wraps;
        private readonly GameObject canvasObject, preview;
        private readonly RectTransform content;
        private readonly Text status;
        private readonly Button apply;
        private readonly LineRenderer line;
        private readonly Material material;
        private readonly List<RectTransform> markers = new();
        private readonly ChainLoop original;
        private readonly Action closed;
        private bool disposed, valid;
        public ChainData Data => new() { members = parts.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray(),
            routeVersion = 1, wraps = wraps.ToArray() };
        public bool IsValid => valid;
        public ChainRoutingPanel(IReadOnlyList<SimulationPart> selection, Action closed) {
            this.closed = closed;
            original = ChainLoop.Find(selection.Select(p => p.GetComponent<SavedObject>().ConnectionId));
            var old = original?.Data;
            if (old?.routeVersion == 1) {
                var byId = selection.ToDictionary(p => p.GetComponent<SavedObject>().ConnectionId);
                parts = old.members.Select(id => byId[id]).ToList(); wraps = old.wraps.ToList();
            }
            else {
                parts = ChainRouteGeometry.OuterOrder(selection);
                if (parts.Count != selection.Count) {
                    // A deterministic starting suggestion; Apply remains disabled
                    // until the user supplies a valid order and wrap choices.
                    var n = ChainRouteGeometry.PlaneNormal(selection);
                    var x = Vector3.ProjectOnPlane(Mathf.Abs(n.x) < .9f ? Vector3.right : Vector3.up, n).normalized;
                    var y = Vector3.Cross(n, x); var center = selection.Aggregate(Vector3.zero, (sum, p) => sum + p.ContactCenter) / selection.Count;
                    parts = selection.OrderBy(p => Mathf.Atan2(Vector3.Dot(p.ContactCenter - center, y), Vector3.Dot(p.ContactCenter - center, x)))
                        .ThenBy(p => p.GetComponent<SavedObject>().ConnectionId, StringComparer.Ordinal).ToList();
                }
                wraps = parts.Select(_ => 0).ToList();
            }
            if (original != null) { original.RefreshVisual(); var renderer = original.GetComponent<LineRenderer>(); if (renderer != null) renderer.forceRenderingOff = true; }
            canvasObject = new GameObject("Chain Routing", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 150;
            var scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900); scaler.matchWidthOrHeight = .5f;
            var backdrop = Rect("Input capture", canvasObject.transform, Vector2.zero, Vector2.zero, Vector2.zero);
            backdrop.anchorMax = Vector2.one; backdrop.sizeDelta = Vector2.zero; Background(backdrop, new Color(0, 0, 0, .04f));
            var panel = Rect("Route editor", canvasObject.transform, new Vector2(1, .5f), new Vector2(1, .5f), new Vector2(-18, 0), new Vector2(540, 620));
            Background(panel, new Color(.055f, .065f, .08f, .98f));
            Label(panel, "Title", "Chain route", new Vector2(16, -12), new Vector2(508, 28), 20);
            Label(panel, "Help", "Order the numbered sprockets around the closed chain.\nOuter wraps enclose; inner wraps bend around an idler.\nUse the preview, then Apply. Escape cancels.", new Vector2(16, -48), new Vector2(508, 64), 13);
            Button(panel, "Reverse", "Reverse order", 16, -119, 130, Reverse);
            status = Label(panel, "Route status", "", new Vector2(16, -156), new Vector2(508, 58), 13);
            var viewport = Rect("Sprockets", panel, Vector2.up, Vector2.up, new Vector2(16, -220), new Vector2(508, 330));
            Background(viewport, new Color(0, 0, 0, .15f)); viewport.gameObject.AddComponent<RectMask2D>();
            content = Rect("Route rows", viewport, Vector2.up, Vector2.up, Vector2.zero, new Vector2(508, 0));
            var scroll = viewport.gameObject.AddComponent<ScrollRect>(); scroll.content = content; scroll.viewport = viewport;
            scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
            apply = Button(panel, "Apply", "Apply route", 16, -568, 145, Apply);
            Button(panel, "Cancel", "Cancel", 175, -568, 100, Dispose);
            if (original != null) Button(panel, "Remove", "Remove chain", 356, -568, 152, Remove);
            preview = new GameObject("Chain route preview") { layer = LayerMask.NameToLayer("Ignore Raycast") };
            line = preview.AddComponent<LineRenderer>(); line.useWorldSpace = true; line.loop = true; line.widthMultiplier = ChainRouteGeometry.Width;
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); material.color = new Color(.2f, .8f, 1);
            line.sharedMaterial = material; line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Rebuild();
        }
        public void Move(int index, int delta) {
            int target = index + delta;
            if (index < 0 || index >= parts.Count || target < 0 || target >= parts.Count) return;
            (parts[index], parts[target]) = (parts[target], parts[index]);
            (wraps[index], wraps[target]) = (wraps[target], wraps[index]); Rebuild();
        }
        public void ToggleWrap(int index) { wraps[index] = 1 - wraps[index]; Rebuild(); }
        public void Reverse() { parts.Reverse(); wraps.Reverse(); Rebuild(); }
        private void Rebuild() {
            foreach (var child in content.Cast<Transform>().ToArray()) { child.gameObject.SetActive(false); Release(child.gameObject); }
            foreach (var marker in markers) Release(marker.gameObject); markers.Clear();
            for (int i = 0; i < parts.Count; i++) {
                int index = i;
                var row = Rect("Sprocket " + (i + 1), content, Vector2.up, Vector2.up, new Vector2(0, -i * 42), new Vector2(508, 40));
                Label(row, "Number and teeth", $"{i + 1}.   {parts[i].teeth}T sprocket", new Vector2(6, 0), new Vector2(210, 38), 14);
                Button(row, "Wrap", wraps[i] == 0 ? "Outer" : "Inner", 226, -4, 100, () => ToggleWrap(index));
                Button(row, "Earlier", "Up", 338, -4, 68, () => Move(index, -1)).interactable = i > 0;
                Button(row, "Later", "Down", 414, -4, 82, () => Move(index, 1)).interactable = i < parts.Count - 1;
                var marker = Rect("Route marker " + (i + 1), canvasObject.transform, Vector2.zero, new Vector2(.5f, .5f), Vector2.zero, new Vector2(32, 28));
                var bg = Background(marker, new Color(.04f, .15f, .2f, .9f)); bg.raycastTarget = false;
                var text = Label(marker, "Number", (i + 1).ToString(), new Vector2(0, 0), new Vector2(32, 28), 16); text.alignment = TextAnchor.MiddleCenter;
                markers.Add(marker);
            }
            content.sizeDelta = new Vector2(508, parts.Count * 42);
            Refresh();
        }
        public void Refresh() {
            if (disposed) return;
            if (parts.Any(p => p == null || !p.gameObject.activeInHierarchy) || !CollaborationSession.CanEdit) { Dispose(); return; }
            valid = ChainRouteGeometry.TryBuild(parts, 1, wraps.ToArray(), out var contour, out var reason);
            line.enabled = valid;
            if (valid) { line.positionCount = contour.points.Length; line.SetPositions(contour.points); }
            apply.interactable = valid;
            status.text = valid ? "Route is clear. Apply to save this chain." : reason;
            var camera = PivotCamera.Main?.camera;
            if (camera != null) for (int i = 0; i < markers.Count; i++) {
                var point = camera.WorldToScreenPoint(parts[i].ContactCenter);
                markers[i].gameObject.SetActive(point.z > 0);
                markers[i].anchoredPosition = new Vector2(point.x, point.y) / canvasObject.GetComponent<Canvas>().scaleFactor;
            }
        }
        public void Apply() {
            Refresh(); if (disposed || !valid) return;
            var data = Data;
            Dispose();
            if (CollaborationSession.IsShared) CollaborationSession.SetChainRoute(data);
            else if (!ChainLoop.SetRoute(data, out var reason)) Protobot.UI.InteractionFeedback.Notify(reason);
        }
        private void Remove() {
            var data = Data; Dispose();
            if (CollaborationSession.IsShared) CollaborationSession.SetChainRoute(data, true);
            else if (original != null) ChainLoop.Remove(original);
        }
        public void Dispose() {
            if (disposed) return; disposed = true;
            if (original != null && original.TryGetComponent<LineRenderer>(out var renderer)) renderer.forceRenderingOff = false;
            canvasObject.SetActive(false); preview.SetActive(false);
            Release(canvasObject); Release(preview); Release(material);
            closed?.Invoke();
        }
        private static void Release(UnityEngine.Object obj) {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj); else UnityEngine.Object.DestroyImmediate(obj);
        }
        private static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size = default) {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor; rect.pivot = pivot; rect.anchoredPosition = pos; rect.sizeDelta = size; return rect;
        }
        private static Image Background(RectTransform rect, Color color) { var image = rect.gameObject.AddComponent<Image>(); image.color = color; return image; }
        private static Text Label(Transform parent, string name, string text, Vector2 pos, Vector2 size, int fontSize) {
            var rect = Rect(name, parent, Vector2.up, Vector2.up, pos, size);
            var label = rect.gameObject.AddComponent<Text>(); label.font = TransformFeedback.FeedbackFont; label.fontSize = fontSize;
            label.color = Color.white; label.alignment = TextAnchor.MiddleLeft; label.text = text; label.raycastTarget = false; return label;
        }
        private static Button Button(Transform parent, string name, string text, float x, float y, float width, UnityEngine.Events.UnityAction action) {
            var rect = Rect(name, parent, Vector2.up, Vector2.up, new Vector2(x, y), new Vector2(width, 32));
            var image = Background(rect, new Color(.15f, .21f, .27f)); var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None }; button.onClick.AddListener(action);
            var label = Label(rect, "Label", text, Vector2.zero, new Vector2(width, 32), 13); label.alignment = TextAnchor.MiddleCenter; return button;
        }
    }
}
