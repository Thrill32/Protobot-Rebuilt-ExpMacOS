using System;
using System.Collections.Generic;
using System.Linq;

namespace Protobot.Collaboration.Server {
    public sealed partial class RoomStore {
        internal static string Identity(StoredRoomInfo info) => info.roomIdentity ?? info.generation;
        private static void ValidateReplacements(StoredRoomInfo info, int storageVersion) {
            var results = info.replacements ?? Array.Empty<RoomReplacementReceipt>();
            if (storageVersion < 3) {
                DocumentModel.Require(info.roomIdentity == null && info.retiredHistoryBytes == 0 && results.Length == 0, "room-storage-corrupt"); return;
            }
            DocumentModel.Require(DocumentModel.ValidId(info.roomIdentity) && info.retiredHistoryBytes > 0 && info.retiredHistoryBytes <= 64L * 1024 * 1024 &&
                results.Length > 0 && results.Length <= RoomReplacement.MaxReplacements && results.All(r => r != null) && results.Select(r => r.operationId).Distinct().Count() == results.Length,
                "room-storage-corrupt");
            string generation = info.roomIdentity; long minimum = 0;
            var generations = new HashSet<string>(StringComparer.Ordinal) { generation };
            foreach (var receipt in results) {
                RoomReplacement.ValidateReceipt(receipt);
                DocumentModel.Require(receipt.room == info.room && receipt.roomIdentity == info.roomIdentity && receipt.catalogHash == info.catalog.hash &&
                    receipt.previousGeneration == generation && receipt.previousRevision >= minimum && generations.Add(receipt.generation), "room-storage-corrupt");
                generation = receipt.generation; minimum = receipt.revision;
            }
            DocumentModel.Require(generation == info.generation && minimum == info.initialRevision && info.migrationRevision == minimum, "room-storage-corrupt");
        }
        internal void ConfigurePolicy(StoredRoomInfo info) {
            using var transaction = db.BeginTransaction();
            Execute("UPDATE info SET data=$data WHERE id=1", transaction, ("$data", WireJson.Encode(info)));
            Finish(transaction, "room-policy");
        }
        internal void Replace(StoredRoomInfo info, Document next, Dictionary<string, byte[]> assets, IEnumerable<StoredMember> members, bool appearance = false) {
            ValidateReplacements(info, 3);
            using var transaction = db.BeginTransaction();
            // The active generation, portable geometry, history retirement and complete
            // operation evidence change in one durable transaction. Registration is immutable.
            Execute("DELETE FROM history; DELETE FROM journal; DELETE FROM checkpoints; DELETE FROM assets;", transaction);
            foreach (var asset in assets) InsertAsset(asset.Key, asset.Value, transaction);
            foreach (var member in members) SaveMember(member, transaction);
            Execute("UPDATE info SET data=$data WHERE id=1", transaction, ("$data", WireJson.Encode(info)));
            Execute("UPDATE state SET checkpoint_revision=$revision,history_bytes=$bytes WHERE id=1; PRAGMA user_version=3;", transaction,
                ("$revision", next.revision), ("$bytes", info.retiredHistoryBytes));
            if (appearance) Execute("PRAGMA user_version=4;", transaction);
            Checkpoint(next, transaction);
            Finish(transaction, "replacement"); checkpointRevision = next.revision; checkpointBytes = 0;
        }
    }
}
