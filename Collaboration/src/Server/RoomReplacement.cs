using System;
using System.Collections.Generic;
using System.Linq;

namespace Protobot.Collaboration.Server {
    public sealed partial class Room {
        public void AuthorizeReplacement(string credential) { lock (gate) { Owner(credential); DocumentModel.Require(IsDurable, "room-replacement-requires-durable-room"); } }
        private RoomReplacementTarget ReplacementTarget() => new RoomReplacementTarget { room = Name, roomIdentity = RoomStore.Identity(roomInfo),
            catalogHash = catalog.hash, generation = document.generation, revision = document.revision, documentHash = diagnosticHash };
        public RoomReplacementTarget InspectReplacement(string credential) {
            lock (gate) { AuthorizeReplacement(credential); return ReplacementTarget(); }
        }
        public RoomReplacementOutcome ReplacementOutcome(string credential, string operationId) {
            lock (gate) {
                AuthorizeReplacement(credential); DocumentModel.Require(DocumentModel.ValidId(operationId), "invalid-operation");
                var receipt = (roomInfo.replacements ?? Array.Empty<RoomReplacementReceipt>()).FirstOrDefault(r => r.operationId == operationId);
                return new RoomReplacementOutcome { found = receipt != null, receipt = receipt == null ? null : WireJson.Clone(receipt) };
            }
        }
        public RoomReplacementReceipt Replace(string credential, RoomReplacementRequest request) {
            AuthorizeReplacement(credential);
            DocumentModel.Require(request != null && request.version == RoomReplacement.Version && DocumentModel.ValidId(request.operationId), "invalid-replacement-request");
            RoomReplacement.ValidateTarget(request.expected);
            // Bounded portable validation/materialization happens before taking the edit
            // gate. Authority and the exact precondition are checked again at publication.
            DocumentModel.Require(request.content != null, "invalid-replacement-request"); request.content.Validate();
            DocumentModel.Require(request.content.catalog.hash == catalog.hash, "catalog-mismatch");
            var content = GeometryTransfer.CloneDocument(request.content.document);
            DocumentModel.Validate(content, catalog); GeometryTransfer.RequireHydrated(content);
            var bytes = DocumentModel.Assets(content).ToDictionary(a => a.hash, GeometryTransfer.Encode, StringComparer.Ordinal);
            var names = DocumentModel.Assets(content).ToDictionary(a => a.hash, a => a.name, StringComparer.Ordinal);
            var arrays = DocumentModel.Assets(content).ToDictionary(a => a.hash, GeometryTransfer.MaterializedBytes, StringComparer.Ordinal);
            GeometryTransfer.RequireSceneBudget(content, hash => arrays[hash]);
            var next = GeometryTransfer.References(content);
            next.schema = 2;
            next.generation = Guid.NewGuid().ToString("N"); next.tombstones = Array.Empty<VersionRef>(); next.chainTombstones = Array.Empty<VersionRef>();
            foreach (var part in next.parts) part.version = 1;
            foreach (var chain in DocumentModel.Chains(next)) chain.version = 1;
            lock (gate) {
                AuthorizeReplacement(credential);
                var previous = roomInfo.replacements ?? Array.Empty<RoomReplacementReceipt>();
                var known = previous.FirstOrDefault(r => r.operationId == request.operationId);
                if (known != null) return WireJson.Clone(known);
                DocumentModel.Require(RoomReplacement.SameTarget(request.expected, ReplacementTarget()), "replacement-target-changed");
                DocumentModel.Require(previous.Length < RoomReplacement.MaxReplacements, "room-replacement-limit");
                DocumentModel.Require(document.revision < long.MaxValue, "alpha-history-limit");
                next.revision = document.revision + 1; DocumentModel.Validate(next, catalog);
                string hash = DocumentModel.Hash(next);
                var result = new RoomReplacementReceipt { operationId = request.operationId, room = Name, roomIdentity = RoomStore.Identity(roomInfo), catalogHash = catalog.hash,
                    previousGeneration = document.generation, previousRevision = document.revision, previousHash = diagnosticHash,
                    generation = next.generation, revision = next.revision, documentHash = hash };
                long lifetimeCost = historyBytes + WireJson.Encode(result).Length;
                DocumentModel.Require(lifetimeCost <= 64L * 1024 * 1024, "alpha-history-limit");
                var info = new StoredRoomInfo { schema = 2, room = Name, roomIdentity = RoomStore.Identity(roomInfo), generation = next.generation,
                    initialRevision = next.revision, migrationRevision = next.revision, ownerHash = ownerHash, salt = salt, passwordHash = passwordHash,
                    catalog = catalog, replacements = previous.Append(result).ToArray(), retiredHistoryBytes = lifetimeCost };
                var records = members.Values.Select(m => { var record = m.Record(); record.historyCursor = 0; return record; }).ToArray();
                var old = GeometryUsage;
                long byteDelta = bytes.Values.Sum(b => (long)b.Length) - old.bytes;
                long arrayDelta = arrays.Values.Sum() - old.arrays;
                long sceneDelta = next.parts.Where(p => p.catalogId == "CUSTOM").Sum(p => arrays[p.assetHash]) - old.instances;
                using var capacity = processBudget?.Reserve(Math.Max(0, byteDelta), Math.Max(0, arrayDelta), Math.Max(0, sceneDelta));
                Persist(() => store.Replace(info, next, bytes, records));
                roomInfo = info; document = next; diagnosticHash = hash; historyBytes = lifetimeCost; minimumCursor = next.revision;
                assetBytes = bytes; assetNames = names; assetArrayBytes = arrays;
                events.Clear(); leases.Clear();
                foreach (var member in members.Values) { member.history.Clear(); member.historyCursor = 0; member.lastSeen = clock() - 60001; }
                capacity?.Commit();
                using var release = processBudget?.Reserve(Math.Min(0, byteDelta), Math.Min(0, arrayDelta), Math.Min(0, sceneDelta)); release?.Commit();
                Pulse(); return WireJson.Clone(result);
            }
        }
    }
}
