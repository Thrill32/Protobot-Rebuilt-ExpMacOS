using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Protobot.Collaboration.Server {
    public sealed partial class Room {
        private Dictionary<string, byte[]> assetBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private long minimumCursor;
        private readonly Dictionary<string, string> assetNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> assetArrayBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        private void IndexAsset(GeometryAsset asset) { assetNames.Add(asset.hash, asset.name); assetArrayBytes.Add(asset.hash, GeometryTransfer.MaterializedBytes(asset)); }
        public void AuthorizeAsset(string token, string hash, bool upload) {
            lock (gate) {
                if (upload) Editor(token); else Authenticate(token);
                DocumentModel.Require(DocumentModel.ValidAssetHash(hash), "invalid-asset-hash");
            }
        }
        public int AssetLength(string token, string hash) {
            lock (gate) { AuthorizeAsset(token, hash, false); DocumentModel.Require(assetBytes.ContainsKey(hash), "missing-asset"); return assetBytes[hash].Length; }
        }
        public byte[] GetAsset(string token, string hash) {
            lock (gate) {
                AuthorizeAsset(token, hash, false);
                DocumentModel.Require(assetBytes.TryGetValue(hash, out var bytes), "missing-asset");
                return bytes.ToArray(); // Immutable private canonical encoding; streamed by the HTTP adapter.
            }
        }
        public void PutAsset(string token, string hash, byte[] bytes) {
            AuthorizeAsset(token, hash, true);
            var asset = GeometryTransfer.Decode(bytes);
            DocumentModel.Require(asset.hash == hash, "asset-hash-mismatch");
            var canonical = GeometryTransfer.Encode(asset);
            lock (gate) {
                AuthorizeAsset(token, hash, true);
                if (assetBytes.ContainsKey(hash)) return;
                var retained = DocumentModel.Assets(document).Select(a => a.hash).ToHashSet(StringComparer.Ordinal);
                var unused = assetBytes.Where(a => !retained.Contains(a.Key)).ToArray();
                DocumentModel.Require(assetArrayBytes.Values.Sum() + GeometryTransfer.MaterializedBytes(asset) <= GeometryTransfer.MaxMaterializedBytes, "asset-materialization-quota");
                DocumentModel.Require(assetBytes.Count < DocumentModel.MaxAssets && assetBytes.Values.Sum(b => (long)b.Length) + canonical.Length <= GeometryTransfer.MaxRoomBytes, "asset-room-quota");
                DocumentModel.Require(unused.Length < GeometryTransfer.MaxUnusedAssets && unused.Sum(a => (long)a.Value.Length) + canonical.Length <= GeometryTransfer.MaxUnusedBytes, "asset-unused-quota");
                Persist(() => store.PutAsset(hash, canonical));
                assetBytes.Add(hash, canonical); IndexAsset(asset);
            }
        }
    }
    public sealed partial class RoomStore {
        private void InsertAsset(string hash, byte[] bytes, SqliteTransaction transaction) =>
            Execute("INSERT INTO assets VALUES($hash,$data)", transaction, ("$hash", hash), ("$data", bytes));
        internal void PutAsset(string hash, byte[] bytes) {
            using var transaction = db.BeginTransaction(); InsertAsset(hash, bytes, transaction); Finish(transaction, "asset");
        }
        private static void ValidateStoredAssets(RoomArchive archive) {
            var metadata = archive.assets.ToDictionary(a => a.Key, a => {
                var asset = GeometryTransfer.Decode(a.Value); return (name: asset.name, arrayBytes: GeometryTransfer.MaterializedBytes(asset));
            }, StringComparer.Ordinal);
            var retained = archive.document.assets.ToDictionary(a => a.hash, StringComparer.Ordinal);
            foreach (var reference in retained.Values) {
                GeometryTransfer.ValidateReference(reference);
                DocumentModel.Require(metadata.TryGetValue(reference.hash, out var asset) && asset.name == reference.name, "missing-asset");
            }
            foreach (var part in archive.members.Values.SelectMany(m => m.history).SelectMany(h => h.before.Concat(h.after)))
                DocumentModel.Require(part.catalogId != "CUSTOM" || retained.ContainsKey(part.assetHash), "missing-history-asset");
            GeometryTransfer.RequireSceneBudget(archive.document, hash => metadata[hash].arrayBytes);
            var unused = archive.assets.Where(a => !retained.ContainsKey(a.Key)).ToArray();
            DocumentModel.Require(metadata.Values.Sum(a => a.arrayBytes) <= GeometryTransfer.MaxMaterializedBytes, "asset-materialization-quota");
            DocumentModel.Require(archive.assets.Values.Sum(b => (long)b.Length) <= GeometryTransfer.MaxRoomBytes &&
                unused.Length <= GeometryTransfer.MaxUnusedAssets && unused.Sum(a => (long)a.Value.Length) <= GeometryTransfer.MaxUnusedBytes, "asset-room-quota");
        }
        private void Migrate(RoomArchive archive) {
            // Load/replay/validate v1 completely before touching it. All changes commit in one SQLite transaction.
            var converted = WireJson.Clone(GeometryTransfer.References(archive.document)); converted.schema = 2;
            foreach (var asset in archive.document.assets) archive.assets.Add(asset.hash, GeometryTransfer.Encode(asset));
            archive.document = converted;
            ValidateStoredAssets(archive);
            using var transaction = db.BeginTransaction();
            Execute("CREATE TABLE assets(hash TEXT PRIMARY KEY,data BLOB NOT NULL)", transaction);
            foreach (var asset in archive.assets) InsertAsset(asset.Key, asset.Value, transaction);
            // Old journal receipts/costs/history stay intact; geometry is replaced with hash references.
            foreach (var edit in archive.events) {
                edit.transaction.assetWrites = DocumentModel.AssetWrites(edit.transaction).Select(GeometryTransfer.Reference).ToArray();
                Execute("UPDATE journal SET data=$data WHERE revision=$revision", transaction, ("$data", WireJson.Encode(edit)), ("$revision", edit.revision));
            }
            // This migration checkpoint fences earlier event cursors. Clients join a consistent new snapshot.
            Execute("DELETE FROM checkpoints", transaction); Checkpoint(converted, transaction);
            archive.info.migrationRevision = converted.revision;
            Execute("UPDATE info SET data=$data WHERE id=1", transaction, ("$data", WireJson.Encode(archive.info)));
            Execute("UPDATE state SET checkpoint_revision=$revision WHERE id=1; PRAGMA user_version=2;", transaction, ("$revision", converted.revision));
            Finish(transaction, "migration"); checkpointRevision = converted.revision; checkpointBytes = 0;
        }
    }
}
