using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using UnityEngine;

namespace Protobot {
    public static class CollaborationScene {
        // Tool scopes can contain selection/group pivots and hole markers. Only saved bodies own shared identity.
        public static List<GameObject> Bodies(IEnumerable<GameObject> roots) => roots.Where(root => root != null)
            .SelectMany(root => root.GetComponentsInChildren<SavedObject>())
            .Where(saved => saved.id != "Error" && !saved.gameObject.IsDeleted()).Select(saved => saved.gameObject).Distinct().ToList();
        private static Dictionary<string, VersionRef> reads;
        public static void BeginReads() { reads = CollaborationSession.IsShared ? new Dictionary<string, VersionRef>() : null; }
        public static void Read(GameObject target) {
            if (reads == null) return;
            var body = PartColors.BodyOf(target);
            if (body == null) return;
            foreach (var version in CollaborationSession.Versions(new[] { body.GetComponent<SavedObject>().ConnectionId })) reads[version.id] = version;
        }
        public static VersionRef[] EndReads() { var result = reads?.Values.ToArray() ?? Array.Empty<VersionRef>(); reads = null; return result; }
        public static CatalogManifest Catalog {
            get {
                var text = Resources.Load<TextAsset>("Collaboration/catalog");
                if (text == null) throw new InvalidOperationException("Export the collaboration catalog through Tools → Protobot → Collaboration before hosting.");
                return WireJson.Decode<CatalogManifest>(text.bytes);
            }
        }
        public static Part Capture(GameObject body, long version = 0) {
            return CapturePose(body, Protobot.Simulation.SimulationTool.SavedPose(body), version);
        }
        public static Part CaptureLive(GameObject body) => CapturePose(body, new Pose(body.transform.position, body.transform.rotation), 0);
        private static Part CapturePose(GameObject body, Pose pose, long version) {
            var saved = body.GetComponent<SavedObject>();
            var color = PartColors.GetColor(body);
            return new Part { id = saved.ConnectionId, version = version, catalogId = saved.id, state = saved.state,
                position = new double[] { pose.position.x, pose.position.y, pose.position.z },
                rotation = new double[] { pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w },
                hasPaint = saved.hasPaint, color = new double[] { color.r, color.g, color.b } };
        }
        public static Document CaptureDocument(CatalogManifest catalog) {
            var bodies = PartsManager.FindLoadedObjects();
            if (bodies.Any(b => b.GetComponent<CustomModel>() != null) || Simulation.ChainLoop.SaveAll().Any())
                throw new InvalidOperationException("This collaboration alpha supports catalog builds without chains or custom models.");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var body in bodies) {
                var saved = body.GetComponent<SavedObject>();
                if (!DocumentModel.ValidId(saved.ConnectionId) || !used.Add(saved.ConnectionId)) {
                    saved.ConnectionId = Guid.NewGuid().ToString("N"); used.Add(saved.ConnectionId);
                }
            }
            var document = new Document { catalogHash = catalog.hash, parts = bodies.Select(b => Capture(b, 1)).ToArray() };
            DocumentModel.Validate(document, catalog); return document;
        }
        public static ObjectData ToObjectData(Part part) {
            var rotation = Rotation(part).eulerAngles;
            return new ObjectData { connectionId = part.id, partId = part.catalogId, states = part.state, hasPaint = part.hasPaint,
                xPos = part.position[0], yPos = part.position[1], zPos = part.position[2],
                xRot = rotation.x, yRot = rotation.y, zRot = rotation.z,
                rColor = part.color[0], gColor = part.color[1], bColor = part.color[2] };
        }
        public static BuildData ToBuild(Document document) {
            var local = SceneBuild.ToBuildData();
            local.parts = document.parts.Select(ToObjectData).ToArray(); local.chains = Array.Empty<ChainData>(); return local;
        }
        public static void Load(Document document) {
            SceneBuild.GenerateBuild(ToBuild(document));
            // The file adapter uses Euler angles; restore the accepted quaternion without a round trip.
            ApplyParts(document.parts);
            StateSystems.StateSystem.instance?.ResetStates();
        }
        public static void Apply(AcceptedEdit edit, Document accepted) {
            var existing = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            var replacements = edit.transaction.writes.Where(p => existing.TryGetValue(p.id, out var body) && body.GetComponent<SavedObject>().id != p.catalogId).Select(p => p.id).ToHashSet();
            var ids = edit.transaction.deletes.ToHashSet();
            var reselect = new List<(SelectionManager manager, string[] ids, bool individual)>();
            foreach (var manager in UnityEngine.Object.FindObjectsByType<SelectionManager>(FindObjectsSortMode.None)) {
                if (manager.current?.gameObject == null) continue;
                var selected = BuildTransfer.SelectedBodies(manager.current).Select(b => b.GetComponent<SavedObject>().ConnectionId).ToArray();
                if (selected.Any(replacements.Contains) && !selected.Any(ids.Contains)) reselect.Add((manager, selected, manager.current.individual));
                if (selected.Any(id => ids.Contains(id) || replacements.Contains(id)))
                    manager.ClearCurrent(false, true, true);
            }
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            var changed = edit.transaction.writes.Select(p => p.id).Concat(edit.transaction.deletes).Where(bodies.ContainsKey).Select(id => bodies[id]).ToArray();
            var detectors = HoleDetector.GetAffectedDetectors(changed);
            foreach (var id in edit.transaction.deletes) if (bodies.TryGetValue(id, out var body)) { body.SetActive(false); UnityEngine.Object.Destroy(body); }
            foreach (var write in edit.transaction.writes) {
                var part = accepted.parts.First(p => p.id == write.id);
                bodies.TryGetValue(part.id, out var body);
                if (body != null && replacements.Contains(part.id)) { body.SetActive(false); UnityEngine.Object.Destroy(body); body = null; }
                if (body == null) body = SceneBuild.GenerateObject(ToObjectData(part), ToBuild(accepted));
                ApplyAppearance(body, part);
                SetPose(body, part);
            }
            foreach (var selection in reselect) {
                var selected = PartsManager.FindLoadedObjects().Where(b => selection.ids.Contains(b.GetComponent<SavedObject>().ConnectionId)).ToList();
                if (selected.Count > 0) selection.manager.SetCurrent(selected.Count == 1 ? new ObjectSelection { gameObject = selected[0], individual = selection.individual } :
                    new MultiSelection(null, selected) { individual = selection.individual }, true, bypassResponseSelectors: true);
            }
            Physics.SyncTransforms();
            foreach (var write in edit.transaction.writes) {
                var body = PartsManager.FindLoadedObjects().FirstOrDefault(b => b.GetComponent<SavedObject>().ConnectionId == write.id);
                if (body != null) HoleDetector.RefreshPart(body, detectors);
            }
            foreach (var detector in detectors) if (detector != null && detector.isActiveAndEnabled) detector.RefreshOverlaps();
            foreach (var manager in UnityEngine.Object.FindObjectsByType<SelectionManager>(FindObjectsSortMode.None))
                if (manager.current is MultiSelection multi && multi.pivot != null && BuildTransfer.SelectedBodies(multi)
                    .Any(b => edit.transaction.writes.Any(p => p.id == b.GetComponent<SavedObject>().ConnectionId))) multi.pivot.Center();
            foreach (var outlines in UnityEngine.Object.FindObjectsByType<GroupOutlineSelectionResponse>(FindObjectsSortMode.None)) { outlines.ClearDisplay(); outlines.SetSelectionDisplay(); }
            UnityEngine.Object.FindFirstObjectByType<PartListOutput>()?.CalculatePartsList();
        }
        public static void ApplyParts(IEnumerable<Part> parts) {
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            foreach (var part in parts) if (bodies.TryGetValue(part.id, out var body)) SetPose(body, part);
            Physics.SyncTransforms();
        }
        private static void SetPose(GameObject body, Part part) {
            // Work in world space; selection and connection pivots remain temporary, local objects.
            body.transform.SetPositionAndRotation(new Vector3((float)part.position[0], (float)part.position[1], (float)part.position[2]), Rotation(part));
        }
        private static void ApplyAppearance(GameObject body, Part part) {
            body.GetComponent<SavedObject>().state = part.state;
            var color = new Color((float)part.color[0], (float)part.color[1], (float)part.color[2]);
            if (part.hasPaint) PartColors.ApplyPaint(body, color);
            else {
                PartColors.RestoreDefaults(body);
                var material = PartColors.SampleMaterial(body);
                if (material != null) { color.a = material.color.a; material.color = color; }
            }
        }
        private static Quaternion Rotation(Part part) => new Quaternion((float)part.rotation[0], (float)part.rotation[1], (float)part.rotation[2], (float)part.rotation[3]);
    }
}
