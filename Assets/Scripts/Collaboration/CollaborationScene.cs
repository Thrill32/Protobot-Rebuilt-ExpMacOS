using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.Collaboration;
using Protobot.SelectionSystem;
using UnityEngine;

namespace Protobot {
    public static class CollaborationScene {
        public static CatalogManifest Catalog {
            get {
                var text = Resources.Load<TextAsset>("Collaboration/catalog");
                if (text == null) throw new InvalidOperationException("Export the collaboration catalog through Tools → Protobot → Collaboration before hosting.");
                return WireJson.Decode<CatalogManifest>(text.bytes);
            }
        }
        public static Part Capture(GameObject body, long version = 0) {
            var saved = body.GetComponent<SavedObject>();
            var pose = Protobot.Simulation.SimulationTool.SavedPose(body);
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
            var ids = edit.transaction.deletes.ToHashSet();
            foreach (var manager in UnityEngine.Object.FindObjectsByType<SelectionManager>(FindObjectsSortMode.None)) {
                if (manager.current?.gameObject == null) continue;
                if (BuildTransfer.SelectedBodies(manager.current).Any(b => ids.Contains(b.GetComponent<SavedObject>().ConnectionId)))
                    manager.ClearCurrent(false, true, true);
            }
            var bodies = PartsManager.FindLoadedObjects().ToDictionary(b => b.GetComponent<SavedObject>().ConnectionId);
            var changed = edit.transaction.writes.Select(p => p.id).Concat(edit.transaction.deletes).Where(bodies.ContainsKey).Select(id => bodies[id]).ToArray();
            var detectors = HoleDetector.GetAffectedDetectors(changed);
            foreach (var id in edit.transaction.deletes) if (bodies.TryGetValue(id, out var body)) { body.SetActive(false); UnityEngine.Object.Destroy(body); }
            foreach (var write in edit.transaction.writes) {
                var part = accepted.parts.First(p => p.id == write.id);
                if (!bodies.TryGetValue(part.id, out var body)) body = SceneBuild.GenerateObject(ToObjectData(part), ToBuild(accepted));
                SetPose(body, part);
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
        private static Quaternion Rotation(Part part) => new Quaternion((float)part.rotation[0], (float)part.rotation[1], (float)part.rotation[2], (float)part.rotation[3]);
    }
}
