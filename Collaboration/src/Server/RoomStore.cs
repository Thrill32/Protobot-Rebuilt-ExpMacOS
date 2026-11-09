using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Protobot.Collaboration.Server {
    [Serializable] internal sealed class StoredRoomInfo {
        public int schema = 1;
        public string room, generation, ownerHash;
        public long initialRevision;
        public CatalogManifest catalog;
        public byte[] salt, passwordHash;
    }
    [Serializable] internal class StoredMember {
        public Participant participant;
        public string resumeHash;
        public long sequence;
        public int historyCursor;
        public bool revoked;
    }
    [Serializable] internal sealed class StoredHistory {
        public string[] ids;
        public Part[] before, after;
        public bool blocked;
    }
    internal sealed class LoadedMember {
        public StoredMember member;
        public readonly List<Receipt> receipts = new List<Receipt>();
        public readonly List<StoredHistory> history = new List<StoredHistory>();
    }
    internal sealed class RoomArchive {
        public StoredRoomInfo info;
        public Document document;
        public List<AcceptedEdit> events;
        public Dictionary<string, LoadedMember> members;
        public long historyBytes;
    }

    // Exactly one server/connection owns a room directory. No Unity or legacy build deserializer lives here.
    public sealed class RoomStore : IDisposable {
        private readonly FileStream ownership;
        private readonly SqliteConnection db;
        private readonly bool isNew;
        private readonly int checkpointInterval;
        private long checkpointRevision, checkpointBytes;
        public string DirectoryPath { get; }
        // Process-local failure injection for the lab; never exposed by the network or CLI.
        public Action<string> BeforeCommit { get; set; }

        public RoomStore(string directory, int checkpointEvery = 64) {
            if (checkpointEvery < 1) throw new ArgumentOutOfRangeException(nameof(checkpointEvery));
            DirectoryPath = Path.GetFullPath(directory); RecoveryFiles.CreateDirectory(DirectoryPath);
            checkpointInterval = checkpointEvery;
            try { ownership = new FileStream(Path.Combine(DirectoryPath, "room.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new CollaborationException("room-data-in-use"); }
            try {
                RecoveryFiles.PrivateFile(Path.Combine(DirectoryPath, "room.lock"));
                string path = Path.Combine(DirectoryPath, "room.sqlite"); isNew = !File.Exists(path);
                db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
                db.Open(); RecoveryFiles.PrivateFile(path);
                // A single private connection also prevents competing checkpoint writers.
                Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA locking_mode=EXCLUSIVE;");
                DocumentModel.Require(Convert.ToString(Scalar("PRAGMA journal_mode=WAL;")) == "wal", "wal-unavailable");
                Execute("PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=1000;");
                DocumentModel.Require(Convert.ToInt64(Scalar("PRAGMA synchronous;")) == 2, "durable-sync-unavailable");
                if (!isNew) {
                    DocumentModel.Require(Convert.ToInt64(Scalar("PRAGMA user_version;")) == 1, "room-storage-version-mismatch");
                    DocumentModel.Require(Convert.ToString(Scalar("PRAGMA quick_check;")) == "ok", "room-storage-corrupt");
                }
            }
            catch { db?.Dispose(); ownership.Dispose(); throw; }
        }

        internal void Initialize(StoredRoomInfo info, Document document) {
            DocumentModel.Require(isNew, "room-already-initialized");
            using var transaction = db.BeginTransaction();
            Execute(@"CREATE TABLE info(id INTEGER PRIMARY KEY CHECK(id=1), data BLOB NOT NULL);
                CREATE TABLE state(id INTEGER PRIMARY KEY CHECK(id=1), checkpoint_revision INTEGER NOT NULL, history_bytes INTEGER NOT NULL);
                CREATE TABLE checkpoints(revision INTEGER PRIMARY KEY, document BLOB NOT NULL, hash TEXT NOT NULL);
                CREATE TABLE members(id TEXT PRIMARY KEY, data BLOB NOT NULL);
                CREATE TABLE receipts(member TEXT NOT NULL REFERENCES members(id), operation TEXT NOT NULL, data BLOB NOT NULL, PRIMARY KEY(member,operation));
                CREATE TABLE history(member TEXT NOT NULL REFERENCES members(id), position INTEGER NOT NULL, blocked INTEGER NOT NULL CHECK(blocked IN (0,1)), data BLOB NOT NULL, PRIMARY KEY(member,position));
                CREATE TABLE journal(revision INTEGER PRIMARY KEY, data BLOB NOT NULL, cost INTEGER NOT NULL);
                PRAGMA user_version=1;", transaction);
            Execute("INSERT INTO info VALUES(1,$data)", transaction, ("$data", WireJson.Encode(info)));
            Execute("INSERT INTO state VALUES(1,$revision,0)", transaction, ("$revision", document.revision));
            Checkpoint(document, transaction);
            Finish(transaction, "initialize"); checkpointRevision = document.revision;
        }

        internal RoomArchive Load() {
            try { return LoadCore(); }
            catch (Exception error) when (error is CollaborationException || error is SqliteException || error is System.Runtime.Serialization.SerializationException ||
                error is ArgumentException || error is InvalidOperationException || error is KeyNotFoundException) {
                throw new CollaborationException("room-storage-corrupt");
            }
        }
        private RoomArchive LoadCore() {
            if (isNew) return null;
            RequireCount("members", 1000); RequireCount("receipts", 20000); RequireCount("history", 20000); RequireCount("journal", 20000);
            DocumentModel.Require(Query("PRAGMA foreign_key_check", r => 1).Count == 0, "room-storage-corrupt");
            var info = Query("SELECT data FROM info WHERE id=1", r => Decode<StoredRoomInfo>(r, 0)).Single();
            DocumentModel.Require(info.schema == 1 && DocumentModel.ValidId(info.room) && DocumentModel.ValidId(info.generation) &&
                info.salt?.Length == 32 && info.passwordHash?.Length == 32 && info.catalog?.protocol == 1, "room-storage-corrupt");
            var state = Query("SELECT checkpoint_revision,history_bytes FROM state WHERE id=1", r => (revision: r.GetInt64(0), bytes: r.GetInt64(1))).Single();
            checkpointRevision = state.revision;
            var checkpoint = Query("SELECT document,hash FROM checkpoints WHERE revision=" + checkpointRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r => (document: Decode<Document>(r, 0), hash: r.GetString(1))).Single();
            var document = checkpoint.document; DocumentModel.Validate(document, info.catalog);
            DocumentModel.Require(document.revision == checkpointRevision && document.generation == info.generation && DocumentModel.Hash(document) == checkpoint.hash, "room-storage-corrupt");
            var journal = Query("SELECT data,cost FROM journal ORDER BY revision", r => (edit: Decode<AcceptedEdit>(r, 0), cost: r.GetInt64(1)));
            long revision = info.initialRevision;
            foreach (var item in journal) {
                DocumentModel.Require(item.edit.revision == ++revision && DocumentModel.ValidId(item.edit.author) && item.cost > 0, "room-storage-corrupt");
                if (item.edit.revision <= checkpointRevision) continue;
                document = DocumentModel.Reduce(document, item.edit.transaction, info.catalog);
                DocumentModel.Require(document.revision == item.edit.revision && DocumentModel.Hash(document) == item.edit.hash, "room-storage-corrupt");
            }
            DocumentModel.Require(document.revision == revision && state.bytes == journal.Sum(j => j.cost) && state.bytes <= 64 * 1024 * 1024, "room-storage-corrupt");
            checkpointBytes = journal.Where(j => j.edit.revision > checkpointRevision).Sum(j => j.cost);
            var members = Query("SELECT id,data FROM members", r => (id: r.GetString(0), member: Decode<StoredMember>(r, 1)))
                .ToDictionary(p => p.id, p => new LoadedMember { member = p.member });
            foreach (var member in members) {
                var m = member.Value.member;
                DocumentModel.Require(m.participant != null && member.Key == m.participant.id && DocumentModel.ValidId(member.Key) &&
                    m.resumeHash?.Length == 64 && m.sequence >= 0 && m.historyCursor >= 0 &&
                    new[] { "owner", "editor", "viewer" }.Contains(m.participant.role), "room-storage-corrupt");
            }
            foreach (var receipt in Query("SELECT member,data FROM receipts", r => (id: r.GetString(0), receipt: Decode<Receipt>(r, 1)))) {
                DocumentModel.Require(DocumentModel.ValidId(receipt.receipt.operationId) && receipt.receipt.revision <= document.revision, "room-storage-corrupt");
                members[receipt.id].receipts.Add(receipt.receipt);
            }
            foreach (var history in Query("SELECT member,position,blocked,data FROM history ORDER BY member,position", r =>
                (id: r.GetString(0), index: r.GetInt32(1), blocked: r.GetBoolean(2), entry: Decode<StoredHistory>(r, 3)))) {
                var list = members[history.id].history;
                DocumentModel.Require(history.index == list.Count && history.entry.ids != null && history.entry.ids.Length > 0 &&
                    history.entry.ids.Length <= DocumentModel.MaxWriteSet && history.entry.ids.Distinct().Count() == history.entry.ids.Length &&
                    history.entry.before != null && history.entry.after != null, "room-storage-corrupt");
                foreach (var part in history.entry.before.Concat(history.entry.after)) DocumentModel.ValidatePart(part, info.catalog);
                history.entry.blocked = history.blocked; list.Add(history.entry);
            }
            DocumentModel.Require(members.Values.All(m => m.member.historyCursor <= m.history.Count) && journal.All(j => members.ContainsKey(j.edit.author)), "room-storage-corrupt");
            return new RoomArchive { info = info, document = document, events = journal.Select(j => j.edit).ToList(), members = members, historyBytes = state.bytes };
        }

        internal void AddMember(StoredMember member) {
            using var transaction = db.BeginTransaction();
            Execute("INSERT INTO members VALUES($id,$data)", transaction, ("$id", member.participant.id), ("$data", WireJson.Encode(member)));
            Finish(transaction, "join");
        }
        internal void Revoke(StoredMember member) {
            using var transaction = db.BeginTransaction(); SaveMember(member, transaction); Finish(transaction, "leave");
        }
        internal void RecordReceipt(string member, Receipt receipt) {
            using var transaction = db.BeginTransaction(); SaveReceipt(member, receipt, transaction); Finish(transaction, "receipt");
        }
        internal void Commit(StoredMember member, Receipt receipt, AcceptedEdit edit, Document next, StoredHistory entry,
            int entryIndex, (string member, int index)[] blocked, long cost, long totalBytes) {
            using var transaction = db.BeginTransaction();
            Execute("INSERT INTO journal VALUES($revision,$data,$cost)", transaction, ("$revision", edit.revision), ("$data", WireJson.Encode(edit)), ("$cost", cost));
            SaveMember(member, transaction); SaveReceipt(member.participant.id, receipt, transaction);
            if (entry != null) {
                Execute("DELETE FROM history WHERE member=$member AND position >= $index", transaction, ("$member", member.participant.id), ("$index", entryIndex));
                Execute("INSERT INTO history VALUES($member,$index,0,$data)", transaction,
                    ("$member", member.participant.id), ("$index", entryIndex), ("$data", WireJson.Encode(entry)));
            }
            foreach (var changed in blocked)
                Execute("UPDATE history SET blocked=1 WHERE member=$member AND position=$index", transaction, ("$member", changed.member), ("$index", changed.index));
            bool checkpoint = next.revision - checkpointRevision >= checkpointInterval || checkpointBytes + cost >= 4 * 1024 * 1024;
            if (checkpoint) {
                Checkpoint(next, transaction);
                Execute("DELETE FROM checkpoints WHERE revision NOT IN (SELECT revision FROM checkpoints ORDER BY revision DESC LIMIT 2)", transaction);
            }
            Execute("UPDATE state SET checkpoint_revision=$checkpoint,history_bytes=$bytes WHERE id=1", transaction,
                ("$checkpoint", checkpoint ? next.revision : checkpointRevision), ("$bytes", totalBytes));
            Finish(transaction, "edit");
            if (checkpoint) { checkpointRevision = next.revision; checkpointBytes = 0; } else checkpointBytes += cost;
        }
        private void Checkpoint(Document document, SqliteTransaction transaction) => Execute("INSERT INTO checkpoints VALUES($revision,$data,$hash)", transaction,
            ("$revision", document.revision), ("$data", WireJson.Encode(document)), ("$hash", DocumentModel.Hash(document)));
        private void SaveMember(StoredMember member, SqliteTransaction transaction) => Execute("UPDATE members SET data=$data WHERE id=$id", transaction,
            ("$id", member.participant.id), ("$data", WireJson.Encode(member)));
        private void SaveReceipt(string member, Receipt receipt, SqliteTransaction transaction) => Execute("INSERT INTO receipts VALUES($member,$operation,$data)", transaction,
            ("$member", member), ("$operation", receipt.operationId), ("$data", WireJson.Encode(receipt)));
        private void Finish(SqliteTransaction transaction, string stage) { BeforeCommit?.Invoke(stage); transaction.Commit(); }
        private void RequireCount(string table, int maximum) => DocumentModel.Require(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + table)) <= maximum, "room-storage-limit");
        private static T Decode<T>(SqliteDataReader reader, int column) {
            DocumentModel.Require(reader.GetBytes(column, 0, null, 0, 0) <= WireJson.MaxMessageBytes, "room-storage-limit");
            return WireJson.Decode<T>((byte[])reader.GetValue(column));
        }
        private object Scalar(string sql) { using var command = Command(sql); return command.ExecuteScalar(); }
        private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read) {
            using var command = Command(sql); using var reader = command.ExecuteReader();
            var results = new List<T>(); while (reader.Read()) results.Add(read(reader)); return results;
        }
        private void Execute(string sql, SqliteTransaction transaction = null, params (string name, object value)[] values) {
            using var command = Command(sql, transaction, values); command.ExecuteNonQuery();
        }
        private SqliteCommand Command(string sql, SqliteTransaction transaction = null, params (string name, object value)[] values) {
            var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
            foreach (var value in values) command.Parameters.AddWithValue(value.name, value.value ?? DBNull.Value);
            return command;
        }
        public void Dispose() { try { db?.Dispose(); } finally { ownership.Dispose(); } }
    }
}
