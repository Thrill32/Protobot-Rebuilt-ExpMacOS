using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.StateSystems;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protobot.Simulation {
    /// <summary>Persistent logical chain connection with a simple moving rope/chain contour.</summary>
    public sealed class ChainLoop : MonoBehaviour {
        [SerializeField] private string[] memberIds;
        public static Material MaterialTemplate;
        private LineRenderer line;
        private Material material;
        private Texture2D texture;
        private float travel;
        private float pitch = .1475f;
        public string[] MemberIds => memberIds;

        public List<SimulationPart> ResolveParts() {
            var objects = UnityEngine.Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None)
                .Where(s => s.isActiveAndEnabled && !s.gameObject.IsDeleted()).GroupBy(s => s.ConnectionId).ToDictionary(g => g.Key, g => g.First());
            return (memberIds ?? Array.Empty<string>()).Select(id => objects.TryGetValue(id, out var saved) ? saved.GetComponent<SimulationPart>() : null).ToList();
        }

        public static bool Compatible(IReadOnlyList<SimulationPart> parts, out string reason) {
            reason = "Select at least two sprockets";
            if (parts == null || parts.Count < 2 || parts.Any(p => p == null || p.kind != DriveKind.Sprocket)) return false;
            var first = parts[0];
            for (int i = 0; i < parts.Count; i++) {
                var part = parts[i];
                reason = "Sprockets must use the same chain size and lie in one plane";
                if (Mathf.Abs(Vector3.Dot(first.Axis, part.Axis)) < .995f ||
                    Mathf.Abs(Vector3.Dot(part.ContactCenter - first.ContactCenter, first.Axis)) > .035f ||
                    Mathf.Abs(part.chainPitch - first.chainPitch) > .005f) return false;
                for (int j = 0; j < i; j++) {
                    reason = "Move overlapping sprockets apart before adding a chain";
                    if (Vector3.Distance(part.ContactCenter, parts[j].ContactCenter) <= part.Radius + parts[j].Radius + .02f) return false;
                }
            }
            reason = "Selected sprockets must form a clear outer loop; connect inner/idler routes in pairs";
            if (parts.Count > 2 && Ordered(parts).Count != parts.Count) return false;
            reason = null; return true;
        }

        private static List<SimulationPart> Ordered(IReadOnlyList<SimulationPart> parts) {
            if (parts.Count <= 2) return parts.ToList();
            var n = parts[0].Axis;
            var x = Vector3.ProjectOnPlane(Mathf.Abs(Vector3.Dot(n, Vector3.right)) < .9f ? Vector3.right : Vector3.up, n).normalized;
            var y = Vector3.Cross(n, x);
            Vector2 Point(SimulationPart p) => new(Vector3.Dot(p.ContactCenter, x), Vector3.Dot(p.ContactCenter, y));
            float Cross(SimulationPart a, SimulationPart b, SimulationPart c) {
                var u = Point(b) - Point(a); var v = Point(c) - Point(a); return u.x * v.y - u.y * v.x;
            }
            var sorted = parts.OrderBy(p => Point(p).x).ThenBy(p => Point(p).y).ToArray();
            var hull = new List<SimulationPart>();
            foreach (var p in sorted) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= .0001f) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            int lower = hull.Count;
            for (int i = sorted.Length - 2; i >= 0; i--) { while (hull.Count > lower && Cross(hull[^2], hull[^1], sorted[i]) <= .0001f) hull.RemoveAt(hull.Count - 1); hull.Add(sorted[i]); }
            hull.RemoveAt(hull.Count - 1); return hull;
        }

        public static ChainLoop Create(IEnumerable<SimulationPart> parts) {
            var list = parts.Distinct().ToList();
            if (!Compatible(list, out _)) return null;
            return FromData(new ChainData { members = list.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray() });
        }
        public static ChainLoop FromData(ChainData data) {
            if (data?.members == null || data.members.Length < 2 || data.members.Any(string.IsNullOrEmpty) || data.members.Distinct().Count() < 2) return null;
            var obj = new GameObject("Sprocket Chain") { layer = LayerMask.NameToLayer("Ignore Raycast") };
            var chain = obj.AddComponent<ChainLoop>(); chain.memberIds = data.members.Distinct().ToArray(); return chain;
        }
        public static ChainData[] SaveAll() => UnityEngine.Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None)
            .Where(c => Compatible(c.ResolveParts(), out _)).Select(c => new ChainData { members = c.memberIds.ToArray() }).ToArray();
        public static void ClearAll() {
            foreach (var loop in UnityEngine.Object.FindObjectsByType<ChainLoop>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                loop.gameObject.SetActive(false); if (Application.isPlaying) Destroy(loop.gameObject); else DestroyImmediate(loop.gameObject);
            }
        }

        public static bool Toggle(IEnumerable<SimulationPart> parts, out string message) {
            var list = parts.Distinct().ToList();
            if (!Compatible(list, out message)) return false;
            var ids = list.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToHashSet();
            var existing = UnityEngine.Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).FirstOrDefault(c => ids.SetEquals(c.memberIds));
            bool adding = existing == null;
            var loop = existing != null ? existing : Create(list);
            StateSystem.AddElement(new ChainElement(loop, !adding));
            loop.gameObject.SetActive(adding);
            StateSystem.AddState(new ChainElement(loop, adding));
            message = adding ? $"Connected {list.Count} sprockets with a chain" : "Chain removed";
            return true;
        }
        private sealed class ChainElement : IElement {
            private readonly ChainLoop loop;
            private readonly bool active;
            public ChainElement(ChainLoop loop, bool active) { this.loop = loop; this.active = active; }
            public void Load() { SimulationTool.ExitForEdit(); if (loop != null) loop.gameObject.SetActive(active); }
        }

        public void SetTravel(float distance) { travel = distance; if (material != null) material.mainTextureOffset = new Vector2(-travel / pitch, 0); }
        private void EnsureRenderer() {
            if (line != null) return;
            line = gameObject.AddComponent<LineRenderer>(); line.loop = true; line.useWorldSpace = true;
            line.widthMultiplier = .045f; line.numCornerVertices = 3; line.numCapVertices = 3;
            line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
            material = MaterialTemplate != null ? new Material(MaterialTemplate) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            texture = new Texture2D(32, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
            for (int x = 0; x < 32; x++) for (int y = 0; y < 2; y++) texture.SetPixel(x, y, x < 23 ? new Color(.55f, .59f, .63f) : new Color(.12f, .15f, .18f));
            texture.Apply(); material.mainTexture = texture;
            line.sharedMaterial = material; line.textureMode = LineTextureMode.Tile;
            SetTravel(travel);
        }
        private void LateUpdate() => RefreshVisual();
        public void RefreshVisual() {
            var parts = ResolveParts();
            if (!Compatible(parts, out _)) { if (line != null) line.enabled = false; return; }
            EnsureRenderer(); line.enabled = true;
            var ordered = Ordered(parts); var n = ordered[0].Axis;
            var normals = new Vector3[ordered.Count];
            for (int i = 0; i < ordered.Count; i++) {
                var a = ordered[i]; var b = ordered[(i + 1) % ordered.Count];
                var d = b.ContactCenter - a.ContactCenter;
                float cosine = Mathf.Clamp((a.Radius - b.Radius) / d.magnitude, -.999f, .999f);
                var u = d.normalized;
                normals[i] = u * cosine - Vector3.Cross(n, u) * Mathf.Sqrt(1 - cosine * cosine);
            }
            var points = new List<Vector3>();
            for (int i = 0; i < ordered.Count; i++) {
                var a = ordered[i]; var incoming = normals[(i + ordered.Count - 1) % ordered.Count]; var outgoing = normals[i];
                float angle = Mathf.Repeat(Vector3.SignedAngle(incoming, outgoing, n), 360);
                int samples = Mathf.Max(2, Mathf.CeilToInt(angle / 8));
                for (int sample = 0; sample <= samples; sample++) points.Add(a.ContactCenter + Quaternion.AngleAxis(angle * sample / samples, n) * incoming * a.Radius);
            }
            line.positionCount = points.Count; line.SetPositions(points.ToArray());
            pitch = ordered[0].chainPitch;
            material.mainTextureScale = new Vector2(1 / pitch, 1);
            SetTravel(travel);
        }
        private void OnDisable() { if (line != null) line.enabled = false; }
        private void OnDestroy() { if (material != null) Destroy(material); if (texture != null) Destroy(texture); }
    }
}
