using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.StateSystems;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protobot.Simulation {
    /// <summary>Persistent chain identity and route; all consumers use the same validated planar contour.</summary>
    public sealed class ChainLoop : MonoBehaviour {

        [SerializeField] private string chainId;
        [SerializeField] private string[] memberIds;
        [SerializeField] private int routeVersion;
        [SerializeField] private int[] wraps;
        public static Material MaterialTemplate;
        private LineRenderer line;
        private Material material;
        private Texture2D texture;
        private float travel;
        private float pitch = .1475f;
        public string[] MemberIds => memberIds;
        public string ConnectionId {
            get {
                if (!Protobot.Collaboration.DocumentModel.ValidId(chainId)) chainId = Guid.NewGuid().ToString("N");
                return chainId;
            }
        }
        internal void SetConnectionId(string value) { chainId = value; }

        public List<SimulationPart> ResolveParts() {
            var objects = UnityEngine.Object.FindObjectsByType<SavedObject>(FindObjectsSortMode.None)
                .Where(s => s.isActiveAndEnabled && !s.gameObject.IsDeleted()).GroupBy(s => s.ConnectionId).ToDictionary(g => g.Key, g => g.First());
            return (memberIds ?? Array.Empty<string>()).Select(id => objects.TryGetValue(id, out var saved) ? saved.GetComponent<SimulationPart>() : null).ToList();
        }

        public static bool Compatible(IReadOnlyList<SimulationPart> parts, out string reason) =>
            ChainRouteGeometry.TryBuild(parts, 0, null, out _, out reason);
        public bool TryContour(out ChainRouteGeometry.Contour contour, out string reason) =>
            ChainRouteGeometry.TryBuild(ResolveParts(), routeVersion, wraps, out contour, out reason);
        public ChainData Data => new() { id = ConnectionId, members = memberIds.ToArray(), routeVersion = routeVersion, wraps = wraps?.ToArray() };
        private void SetData(ChainData data) { memberIds = data.members.ToArray(); routeVersion = data.routeVersion; wraps = data.wraps?.ToArray(); }
        public static ChainLoop Find(IEnumerable<string> ids) {
            var set = ids.ToHashSet();
            return UnityEngine.Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None).FirstOrDefault(c => set.SetEquals(c.memberIds));
        }

        public static ChainLoop Create(IEnumerable<SimulationPart> parts) {
            var list = parts.Distinct().ToList();
            if (!Compatible(list, out _)) return null;
            return FromData(new ChainData { members = list.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray() });
        }
        public static ChainLoop FromData(ChainData data, string connectionId = null, bool retainInvalidGeometry = false) {
            if (data == null || !Protobot.Collaboration.DocumentModel.ValidRoute(data.members, data.routeVersion, data.wraps)) return null;
            var obj = new GameObject("Sprocket Chain") { layer = LayerMask.NameToLayer("Ignore Raycast") };
            var chain = obj.AddComponent<ChainLoop>(); chain.SetData(data); chain.chainId = connectionId ?? data.id; _ = chain.ConnectionId;
            // Legacy files can contain dangling optional records. Never partially
            // materialize a malformed or physically impossible explicit route.
            if (!retainInvalidGeometry && !chain.TryContour(out _, out _)) {
                obj.SetActive(false); if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj); return null;
            }
            return chain;
        }
        public Protobot.Collaboration.ChainRecord Capture(IEnumerable<SimulationPart> parts = null, long version = 0) {
            if (parts != null) memberIds = parts.Select(p => p.GetComponent<SavedObject>().ConnectionId).ToArray();
            return new Protobot.Collaboration.ChainRecord { id = ConnectionId, version = version, members = memberIds.ToArray(),
                routeVersion = routeVersion, wraps = wraps?.ToArray() };
        }
        public static void ApplyRecords(IEnumerable<Protobot.Collaboration.ChainRecord> records) {
            ClearAll();
            foreach (var record in records ?? Array.Empty<Protobot.Collaboration.ChainRecord>())
                FromData(new ChainData { members = record.members.ToArray(), routeVersion = record.routeVersion, wraps = record.wraps?.ToArray() }, record.id, retainInvalidGeometry: true);
        }
        public static ChainData[] SaveAll() => UnityEngine.Object.FindObjectsByType<ChainLoop>(FindObjectsSortMode.None)
            .Where(c => c.TryContour(out _, out _)).Select(c => c.Data).ToArray();
        public static void ClearAll() {
            foreach (var loop in UnityEngine.Object.FindObjectsByType<ChainLoop>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                loop.gameObject.SetActive(false); if (Application.isPlaying) Destroy(loop.gameObject); else DestroyImmediate(loop.gameObject);
            }
        }

        public static bool Toggle(IEnumerable<SimulationPart> parts, out string message) {
            var list = parts.Distinct().ToList();
            var existing = Find(list.Where(p => p != null).Select(p => p.GetComponent<SavedObject>().ConnectionId));
            // Removing an invalid chain must remain possible after moving an endpoint.
            if (existing != null) { Remove(existing); message = "Chain removed"; return true; }
            if (!Compatible(list, out message)) return false;
            var loop = Create(list);
            StateSystem.AddElement(new ChainElement(loop, false, loop.Data));
            StateSystem.AddState(new ChainElement(loop, true, loop.Data));
            message = $"Connected {list.Count} sprockets · Shift + Chain: edit route";
            return true;
        }
        public static void Remove(ChainLoop loop) {
            StateSystem.AddElement(new ChainElement(loop, true, loop.Data));
            loop.gameObject.SetActive(false);
            StateSystem.AddState(new ChainElement(loop, false, loop.Data));
        }
        public static bool SetRoute(ChainData data, out string reason) {
            reason = "Invalid route metadata";
            if (data == null || !Protobot.Collaboration.DocumentModel.ValidRoute(data.members, data.routeVersion, data.wraps)) return false;
            var loop = Find(data.members);
            var partsById = UnityEngine.Object.FindObjectsByType<SimulationPart>(FindObjectsSortMode.None)
                .Where(p => p.GetComponent<SavedObject>() != null).GroupBy(p => p.GetComponent<SavedObject>().ConnectionId).ToDictionary(g => g.Key, g => g.First());
            var parts = data.members.Select(id => partsById.TryGetValue(id, out var part) ? part : null).ToArray();
            if (!ChainRouteGeometry.TryBuild(parts, data.routeVersion, data.wraps, out _, out reason)) return false;
            if (loop == null) {
                loop = FromData(data);
                StateSystem.AddElement(new ChainElement(loop, false, loop.Data));
            }
            else {
                if (Protobot.Collaboration.DocumentModel.RouteKey(loop.memberIds, loop.routeVersion, loop.wraps) ==
                    Protobot.Collaboration.DocumentModel.RouteKey(data.members, data.routeVersion, data.wraps)) return true;
                StateSystem.AddElement(new ChainElement(loop, true, loop.Data));
                loop.SetData(data);
            }
            StateSystem.AddState(new ChainElement(loop, true, loop.Data));
            return true;
        }
        private sealed class ChainElement : IElement {
            private readonly ChainLoop loop;
            private readonly bool active;
            private readonly ChainData data;
            public ChainElement(ChainLoop loop, bool active, ChainData data) { this.loop = loop; this.active = active; this.data = data; }
            public void Load() { SimulationTool.ExitForEdit(); if (loop != null) { loop.SetData(data); loop.gameObject.SetActive(active); loop.RefreshVisual(); } }
        }

        public void SetTravel(float distance) { travel = distance; if (material != null) material.mainTextureOffset = new Vector2(-travel / pitch, 0); }
        private void EnsureRenderer() {
            if (line != null) return;
            line = gameObject.AddComponent<LineRenderer>(); line.loop = true; line.useWorldSpace = true;
            line.widthMultiplier = ChainRouteGeometry.Width;
            line.numCornerVertices = 0; line.numCapVertices = 0;
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
            if (!TryContour(out var contour, out _)) { if (line != null) line.enabled = false; return; }
            EnsureRenderer(); line.enabled = true;
            line.positionCount = contour.points.Length; line.SetPositions(contour.points);
            pitch = contour.parts[0].chainPitch;
            material.mainTextureScale = new Vector2(1 / pitch, 1);
            SetTravel(travel);
        }
        private void OnDisable() { if (line != null) line.enabled = false; }
        private void OnDestroy() { if (material != null) Destroy(material); if (texture != null) Destroy(texture); }
    }
}
