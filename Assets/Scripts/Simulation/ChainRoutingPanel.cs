using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Protobot.Builds;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot.Simulation {
    /// <summary>Ordered wrap editing and a private preview for chains and ropes.</summary>
    public sealed class ChainRoutingPanel : IDisposable {
        private readonly List<SimulationPart> parts;
        private readonly List<int> wraps;
        private readonly GameObject canvasObject, preview;
        private readonly RectTransform content;
        private readonly Text status;
        private readonly Button apply;
        private readonly LineRenderer line;
        private readonly Material material;
        private readonly RopeVisual ropeVisual;
        private readonly RopeRoutingDraft rope;
        private RopeRoutingDraft.Pick pick;
        private string pickError;
        private readonly List<RectTransform> markers = new();
        private readonly ChainLoop original;
        private readonly Action closed;
        private bool disposed, valid;
        public ChainData Data => IsRope ? null : new() { members = parts.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray(),
            routeVersion = 1, wraps = wraps.ToArray() };
        public bool IsValid => valid;
        public bool IsRope => rope != null;
        public ChainRoutingPanel(IReadOnlyList<SimulationPart> selection, Action closed) : this(selection, null, closed) { }

        internal ChainRoutingPanel(RopeRoutingDraft rope, Action closed) : this(null, rope, closed) { }

        private ChainRoutingPanel(IReadOnlyList<SimulationPart> selection, RopeRoutingDraft rope, Action closed) {
            this.closed = closed;
            this.rope = rope;
            if (rope != null) {
                wraps = rope.wraps;
                rope.original?.SetPreviewHidden(true);
            }
            else {
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
            }
            canvasObject = new GameObject(IsRope ? "Rope Routing" : "Chain Routing", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 150;
            var scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900); scaler.matchWidthOrHeight = .5f;
            var backdrop = Rect("Input capture", canvasObject.transform, Vector2.zero, Vector2.zero, Vector2.zero);
            backdrop.anchorMax = Vector2.one; backdrop.sizeDelta = Vector2.zero;
            var background = Background(backdrop, new Color(0, 0, 0, .04f));
            if (IsRope) {
                var pickButton = backdrop.gameObject.AddComponent<Button>();
                pickButton.targetGraphic = background;
                pickButton.transition = Selectable.Transition.None;
                pickButton.navigation = new Navigation { mode = Navigation.Mode.None };
                pickButton.onClick.AddListener(PickPart);
            }
            var panel = Rect("Route editor", canvasObject.transform, new Vector2(1, .5f), new Vector2(1, .5f), new Vector2(-18, 0), new Vector2(540, 620));
            Background(panel, new Color(.055f, .065f, .08f, .98f));
            Label(panel, "Title", IsRope ? "Rope route" : "Chain route", new Vector2(16, -12), new Vector2(508, 28), 20);
            Label(panel, "Help", IsRope
                ? "Pick a start point on the drum and an end on a separate part.\nOrder the pulleys between them and choose a wrap side.\nThe end attachment and grooves share one plane."
                : "Order the numbered sprockets around the closed chain.\nOuter wraps enclose; inner wraps bend around an idler.\nUse the preview, then Apply. Escape cancels.",
                new Vector2(16, -48), new Vector2(508, 64), 13);
            if (IsRope) {
                Button(panel, "Pick Drum", "Pick drum point", 16, -119, 118, () => BeginPick(RopeRoutingDraft.Pick.Drum));
                Button(panel, "Pick End", "Pick end point", 143, -119, 112, () => BeginPick(RopeRoutingDraft.Pick.End));
                Button(panel, "Add Pulley", "Add pulley", 264, -119, 100, () => BeginPick(RopeRoutingDraft.Pick.Pulley));
                Button(panel, "Reverse", "Reverse pulleys", 373, -119, 151, Reverse);
                AddLengthField(panel);
            }
            else Button(panel, "Reverse", "Reverse order", 16, -119, 130, Reverse);
            status = Label(panel, "Route status", "", new Vector2(16, IsRope ? -192 : -156), new Vector2(508, 58), 13);
            var viewport = Rect(IsRope ? "Rope parts" : "Sprockets", panel, Vector2.up, Vector2.up,
                new Vector2(16, IsRope ? -252 : -220), new Vector2(508, IsRope ? 298 : 330));
            Background(viewport, new Color(0, 0, 0, .15f)); viewport.gameObject.AddComponent<RectMask2D>();
            content = Rect("Route rows", viewport, Vector2.up, Vector2.up, Vector2.zero, new Vector2(508, 0));
            var scroll = viewport.gameObject.AddComponent<ScrollRect>(); scroll.content = content; scroll.viewport = viewport;
            scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
            apply = Button(panel, "Apply", "Apply route", 16, -568, 145, Apply);
            Button(panel, "Cancel", "Cancel", 175, -568, 100, Dispose);
            if (original != null || rope?.original != null) Button(panel, "Remove", IsRope ? "Remove rope" : "Remove chain", 356, -568, 152, Remove);
            preview = new GameObject(IsRope ? "Rope route preview" : "Chain route preview") { layer = LayerMask.NameToLayer("Ignore Raycast") };
            if (IsRope) ropeVisual = preview.AddComponent<RopeVisual>();
            else {
                line = preview.AddComponent<LineRenderer>(); line.useWorldSpace = true; line.loop = true; line.widthMultiplier = ChainRouteGeometry.Width;
                material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); material.color = new Color(.2f, .8f, 1);
                line.sharedMaterial = material; line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            Rebuild();
        }
        private void AddLengthField(Transform panel) {
            Label(panel, "Available rope label", "Available rope (in)", new Vector2(16, -156), new Vector2(150, 30), 14);
            var rect = Rect("Available rope", panel, Vector2.up, Vector2.up, new Vector2(166, -156), new Vector2(112, 30));
            var image = Background(rect, new Color(.15f, .21f, .27f));
            var input = rect.gameObject.AddComponent<InputField>();
            input.targetGraphic = image;
            input.textComponent = Label(rect, "Length", "", new Vector2(8, 0), new Vector2(96, 30), 14);
            input.contentType = InputField.ContentType.DecimalNumber;
            input.text = rope.availableLength.ToString("G", CultureInfo.InvariantCulture);
            input.onValueChanged.AddListener(value => {
                rope.availableLength = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float length) ? length : float.NaN;
                Refresh();
            });
        }

        private void BeginPick(RopeRoutingDraft.Pick target) {
            pick = target;
            pickError = null;
            Refresh();
        }

        private void PickPart() {
            if (pick == RopeRoutingDraft.Pick.None) return;
            var camera = PivotCamera.Main?.camera;
            if (camera == null) return;
            Physics.SyncTransforms();
            Physics.Raycast(camera.ScreenPointToRay(MouseInput.Position), out var hit, Mathf.Infinity,
                LayerMask.GetMask("Default", "HoleCollisions"), QueryTriggerInteraction.Ignore);
            if (!rope.Attach(pick, hit, out pickError)) { Refresh(); return; }
            pick = RopeRoutingDraft.Pick.None;
            Rebuild();
        }

        public void Move(int index, int delta) {
            int target = index + delta;
            if (IsRope) {
                if (index > 0 && index <= rope.pulleys.Count && target > 0 && target <= rope.pulleys.Count) {
                    rope.Move(index - 1, target - 1);
                    Rebuild();
                }
                return;
            }
            if (index < 0 || index >= parts.Count || target < 0 || target >= parts.Count) return;
            (parts[index], parts[target]) = (parts[target], parts[index]);
            (wraps[index], wraps[target]) = (wraps[target], wraps[index]); Rebuild();
        }
        public void ToggleWrap(int index) { wraps[index] = 1 - wraps[index]; rope?.Invalidate(); Rebuild(); }
        public void Reverse() {
            if (IsRope) rope.Reverse();
            else { parts.Reverse(); wraps.Reverse(); }
            Rebuild();
        }
        private void Rebuild() {
            foreach (var child in content.Cast<Transform>().ToArray()) { child.gameObject.SetActive(false); Release(child.gameObject); }
            foreach (var marker in markers) Release(marker.gameObject); markers.Clear();
            int count = IsRope ? rope.pulleys.Count + 2 : parts.Count;
            for (int i = 0; i < count; i++) {
                int index = i;
                var row = Rect((IsRope ? "Rope point " : "Sprocket ") + (i + 1), content, Vector2.up, Vector2.up, new Vector2(0, -i * 42), new Vector2(508, 40));
                if (IsRope) {
                    string label = i == 0 ? (rope.Start.part == null ? "Drum point unset" : "Drum attachment") :
                        i == count - 1 ? (rope.EndBody == null ? "End point unset" : "End attachment") : "VEX Pulley";
                    Label(row, "Number and part", $"{i + 1}.   {label}", new Vector2(6, 0), new Vector2(210, 38), 14);
                    if (i < count - 1) Button(row, "Wrap", wraps[i] == 0 ? "Outer" : "Inner", 226, -4, 80, () => ToggleWrap(index));
                    if (i > 0 && i < count - 1) {
                        Button(row, "Earlier", "Up", 314, -4, 44, () => Move(index, -1)).interactable = i > 1;
                        Button(row, "Later", "Down", 365, -4, 56, () => Move(index, 1)).interactable = i < count - 2;
                        Button(row, "Remove Pulley", "Remove", 429, -4, 67, () => { rope.Remove(index - 1); Rebuild(); });
                    }
                }
                else {
                    Label(row, "Number and teeth", $"{i + 1}.   {parts[i].teeth}T sprocket", new Vector2(6, 0), new Vector2(210, 38), 14);
                    Button(row, "Wrap", wraps[i] == 0 ? "Outer" : "Inner", 226, -4, 100, () => ToggleWrap(index));
                    Button(row, "Earlier", "Up", 338, -4, 68, () => Move(index, -1)).interactable = i > 0;
                    Button(row, "Later", "Down", 414, -4, 82, () => Move(index, 1)).interactable = i < count - 1;
                }
                var marker = Rect("Route marker " + (i + 1), canvasObject.transform, Vector2.zero, new Vector2(.5f, .5f), Vector2.zero, new Vector2(32, 28));
                var bg = Background(marker, new Color(.04f, .15f, .2f, .9f)); bg.raycastTarget = false;
                var text = Label(marker, "Number", (i + 1).ToString(), new Vector2(0, 0), new Vector2(32, 28), 16); text.alignment = TextAnchor.MiddleCenter;
                markers.Add(marker);
            }
            content.sizeDelta = new Vector2(508, count * 42);
            Refresh();
        }
        public void Refresh() {
            if (disposed) return;
            if (IsRope) { RefreshRope(); return; }
            if (parts.Any(p => p == null || !p.gameObject.activeInHierarchy) || !CollaborationSession.CanEdit) { Dispose(); return; }
            valid = ChainRouteGeometry.TryBuild(parts, 1, wraps.ToArray(), out var contour, out var reason);
            line.enabled = valid;
            if (valid) { line.positionCount = contour.points.Length; line.SetPositions(contour.points); }
            apply.interactable = valid;
            status.text = valid ? "Route is clear. Apply to save this chain." : reason;
            for (int i = 0; i < markers.Count; i++) PositionMarker(i, parts[i].ContactCenter);
        }

        private void RefreshRope() {
            if (rope.drum == null || !rope.drum.gameObject.activeInHierarchy || !CollaborationSession.CanEdit) { Dispose(); return; }
            bool hasContour = rope.TryPreview(out var contour, out var reason);
            bool hasLength = ChainRouteGeometry.Finite(rope.availableLength) && rope.availableLength > 0;
            valid = hasContour && hasLength && contour.length <= rope.availableLength && pick == RopeRoutingDraft.Pick.None;
            ropeVisual.Show(contour, preview: true);
            apply.interactable = valid;
            string length = hasContour ? $"Routed {contour.length:0.00} in" : "Routed —";
            if (hasContour && hasLength)
                length += contour.length <= rope.availableLength
                    ? $"   ·   {rope.availableLength - contour.length:0.00} in remaining"
                    : $"   ·   {contour.length - rope.availableLength:0.00} in over length";
            string prompt = pick switch {
                RopeRoutingDraft.Pick.Drum => "Click the winch drum to choose the start point.",
                RopeRoutingDraft.Pick.End => "Click a part or mounting hole to choose the end point.",
                RopeRoutingDraft.Pick.Pulley => "Click the VEX Pulley to add.",
                _ => !hasLength ? "Enter a positive available rope length." : reason
            };
            status.text = length + "\n" + (pickError ?? prompt);
            PositionMarker(0, rope.Start.part == null ? rope.drum.GetComponent<RopeGroove>().Center :
                rope.Start.WorldPoint(rope.drum));
            for (int i = 0; i < rope.pulleys.Count; i++)
                PositionMarker(i + 1, rope.pulleys[i] != null ? rope.pulleys[i].GetComponent<RopeGroove>().Center : null);
            PositionMarker(markers.Count - 1, rope.EndBody != null ? rope.End.WorldPoint(rope.EndBody) : null);
        }

        private void PositionMarker(int index, Vector3? world) {
            var camera = PivotCamera.Main?.camera;
            if (camera == null || !world.HasValue) { markers[index].gameObject.SetActive(false); return; }
            var point = camera.WorldToScreenPoint(world.Value);
            markers[index].gameObject.SetActive(point.z > 0);
            markers[index].anchoredPosition = new Vector2(point.x, point.y) / canvasObject.GetComponent<Canvas>().scaleFactor;
        }
        public void Apply() {
            Refresh(); if (disposed || !valid) return;
            if (IsRope) {
                if (RopeRoute.Apply(rope.Data, out var error)) Dispose();
                else Protobot.UI.InteractionFeedback.Notify(error);
                return;
            }
            var data = Data;
            Dispose();
            if (CollaborationSession.IsShared) CollaborationSession.SetChainRoute(data);
            else if (!ChainLoop.SetRoute(data, out var reason)) Protobot.UI.InteractionFeedback.Notify(reason);
        }
        private void Remove() {
            if (IsRope) { Dispose(); if (rope.original != null) rope.original.Remove(); return; }
            var data = Data; Dispose();
            if (CollaborationSession.IsShared) CollaborationSession.SetChainRoute(data, true);
            else if (original != null) ChainLoop.Remove(original);
        }
        public void Dispose() {
            if (disposed) return; disposed = true;
            if (original != null && original.TryGetComponent<LineRenderer>(out var renderer)) renderer.forceRenderingOff = false;
            if (rope?.original != null) rope.original.SetPreviewHidden(false);
            if (canvasObject != null) canvasObject.SetActive(false);
            if (preview != null) preview.SetActive(false);
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
