using System.Linq;

namespace Protobot.Collaboration.Server {
    public sealed partial class Room {
        public void AuthorizeDiagnostics(string credential) {
            lock (gate) {
                // Inspection also works after storage failure and never touches presence.
                if (ownerHash != null && credential != null && credential.Length < 256 && TokenHash(credential) == ownerHash) return;
                DocumentModel.Require(credential != null && credential.Length < 256 && sessions.TryGetValue(TokenHash(credential), out var member) &&
                    !member.revoked && member.participant.role == "owner", "owner-required");
                DocumentModel.Require(sessions[TokenHash(credential)].sessionGeneration == document.generation, "room-replaced");
            }
        }
        public RoomDiagnostic Diagnostics() {
            lock (gate) {
                var retained = document.assets.Select(a => a.hash).ToHashSet(System.StringComparer.Ordinal);
                long unusedBytes = 0; int unused = 0;
                foreach (var item in assetBytes) if (!retained.Contains(item.Key)) { unused++; unusedBytes += item.Value.Length; }
                var geometry = GeometryUsage;
                return DiagnosticExport.Room(new RoomDiagnostic { room = Name, generation = document.generation, catalogHash = catalog.hash,
                    documentHash = diagnosticHash, revision = document.revision, status = IsHealthy ? "ready" : "room-storage-failed-restart-required",
                    usage = new RoomUsage {
                        participants = new ResourceMetric(ActiveCount(), 10), memberships = new ResourceMetric(members.Count, 1000),
                        assets = new ResourceMetric(assetBytes.Count, DocumentModel.MaxAssets),
                        retainedGeometryBytes = new ResourceMetric(geometry.bytes, GeometryTransfer.MaxRoomBytes),
                        decodedArrayBytes = new ResourceMetric(geometry.arrays, GeometryTransfer.MaxMaterializedBytes),
                        liveInstanceArrayBytes = new ResourceMetric(geometry.instances, GeometryTransfer.MaxSceneGeometryBytes),
                        unusedUploads = new ResourceMetric(unused, GeometryTransfer.MaxUnusedAssets), unusedUploadBytes = new ResourceMetric(unusedBytes, GeometryTransfer.MaxUnusedBytes),
                        parts = new ResourceMetric(document.parts.Length, DocumentModel.MaxParts), chains = new ResourceMetric(document.chains.Length, DocumentModel.MaxChains),
                        historyBytes = new ResourceMetric(historyBytes, 64L * 1024 * 1024), receipts = new ResourceMetric(receiptCount, 20000),
                        journalEvents = new ResourceMetric(events.Count, 20000), historyEntries = members.Values.Sum(m => (long)m.history.Count)
                    } });
            }
        }
    }
}
