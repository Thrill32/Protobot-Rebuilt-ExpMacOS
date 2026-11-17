using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Protobot.Collaboration.Server {
    [Serializable] public sealed class ServerConfiguration {
        public int schema = 1;
        public string serverId, adminHash;
        [System.Runtime.Serialization.OptionalField] public string creationPolicy;
        [System.Runtime.Serialization.OptionalField] public string creationKeyHash;
    }
    [Serializable] public sealed class ServerAdministration {
        public int schema = 1;
        public string serverId, credential;
    }
    [Serializable] public sealed class RoomRegistration {
        public int schema = 1;
        public string room, generation, catalogHash, ownerHash;
    }

    // A registry entry IS an atomically published complete room directory. There is
    // no second mutable index to reconcile after a crash between two publications.
    public sealed class RoomRegistry : IDisposable {
        public const int Version = 1;
        private readonly string root;
        private readonly FileStream ownership;
        private readonly ServerConfiguration configuration;
        private readonly object gate = new object();
        private bool stopping;
        private sealed class Entry {
            public string name, failure;
            public RoomRegistration registration;
            public Room room;
            public RoomStore store;
        }
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        public ProcessBudget Budget { get; } = new ProcessBudget();
        public SemaphoreSlim Creation { get; } = new SemaphoreSlim(1, 1);
        // Local lab injection only; a failed publication must leave no registry entry.
        public Action<string> BeforePublish { get; set; }
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
            IncludeFields = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        public string ServerId => configuration.serverId;
        public string CreationPolicy => configuration.creationPolicy ?? "admin-only";
        public static void Initialize(string directory, string creationPolicy = "admin-only") {
            DocumentModel.Require(ServerRooms.ValidPolicy(creationPolicy), "invalid-creation-policy");
            string target = Path.GetFullPath(directory);
            New(target);
            string staging = RoomBackup.Stage(target);
            try {
                var admin = new ServerAdministration { serverId = Guid.NewGuid().ToString("N"), credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
                var config = new ServerConfiguration { serverId = admin.serverId, adminHash = Hash(admin.credential), creationPolicy = creationPolicy };
                if (creationPolicy == "key") {
                    var creator = new RoomCreationKey { format = "protobot-room-creator", schema = 1, serverId = admin.serverId, credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
                    config.creationKeyHash = Hash(creator.credential);
                    WriteNew(Path.Combine(staging, "creator.json"), creator);
                }
                WriteNew(Path.Combine(staging, "server.json"), config);
                WriteNew(Path.Combine(staging, "admin.json"), admin);
                WriteNew(Path.Combine(staging, "registry.json"), new RegistryHeader { serverId = config.serverId });
                RecoveryFiles.CreateDirectory(Path.Combine(staging, "rooms"));
                RecoveryFiles.CreateDirectory(Path.Combine(staging, "staging"));
                RoomBackup.PublishDirectory(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        [Serializable] private sealed class RegistryHeader { public int schema = Version; public string serverId; }
        [Serializable] private sealed class Owner { public int schema = 1; public string room, credential; }

        public RoomRegistry(string configFile, bool recover = true) {
            configFile = Path.GetFullPath(configFile);
            DocumentModel.Require(Path.GetFileName(configFile) == "server.json", "use-initialized-server-config");
            root = Path.GetDirectoryName(configFile); Regular(root); Regular(configFile);
            configuration = Read<ServerConfiguration>(configFile);
            DocumentModel.Require(configuration.schema == 1 && DocumentModel.ValidId(configuration.serverId) && DocumentModel.ValidAssetHash(configuration.adminHash), "server-config-version-or-identity-mismatch");
            DocumentModel.Require(ServerRooms.ValidPolicy(CreationPolicy) && (CreationPolicy == "key" ?
                DocumentModel.ValidAssetHash(configuration.creationKeyHash) && configuration.creationKeyHash != configuration.adminHash : configuration.creationKeyHash == null), "invalid-creation-policy");
            var header = Read<RegistryHeader>(Path.Combine(root, "registry.json"));
            DocumentModel.Require(header.schema == Version && header.serverId == configuration.serverId, "registry-version-or-identity-mismatch");
            string lockPath = Path.Combine(root, "server.lock"); if (File.Exists(lockPath)) Regular(lockPath);
            try { ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new CollaborationException("server-data-in-use"); }
            try {
                RecoveryFiles.PrivateFile(lockPath);
                Regular(Path.Combine(root, "rooms")); Regular(Path.Combine(root, "staging"));
                // Ignore interrupted staging, never infer a registration from it.
                // Bound cleanup and reject links before recursively removing private staging.
                var stages = Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging")).Take(ProcessBudget.MaxRooms + 1).ToArray();
                DocumentModel.Require(stages.Length <= ProcessBudget.MaxRooms, "registry-staging-limit");
                foreach (string stage in stages) {
                    string name = Path.GetFileName(stage);
                    DocumentModel.Require(new[] { "create-", "restore-", ".protobot-backup-" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(prefix.Length), "N", out _)), "unexpected-registry-staging-entry");
                    NoLinks(stage); Directory.Delete(stage, true);
                }
                var directories = Directory.EnumerateFileSystemEntries(Path.Combine(root, "rooms")).Take(ProcessBudget.MaxRooms + 1).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                DocumentModel.Require(directories.Length <= ProcessBudget.MaxRooms, "server-room-limit");
                foreach (string path in directories) {
                    string name = Path.GetFileName(path);
                    DocumentModel.Require(DocumentModel.ValidId(name) && !entries.ContainsKey(name), "invalid-registry-entry");
                    var entry = new Entry { name = name }; entries.Add(name, entry);
                    try {
                        Regular(path); entry.registration = Read<RoomRegistration>(Path.Combine(path, "registration.json"));
                        ValidateRegistration(entry.registration, name);
                        DocumentModel.Require(!entries.Values.Any(e => e != entry && e.registration != null &&
                            (e.registration.generation == entry.registration.generation || e.registration.ownerHash == entry.registration.ownerHash)), "duplicate-room-identity");
                        if (recover) Open(entry, path); else entry.failure = "offline";
                    }
                    catch (Exception error) { entry.failure = Failure(error); entry.store?.Dispose(); entry.store = null; entry.room = null; }
                }
            }
            catch { Dispose(); throw; }
        }
        private void Open(Entry entry, string path, bool attachBudget = true) {
            foreach (string name in new[] { "room.sqlite", "owner.json", "room.lock", "room.sqlite-wal", "room.sqlite-shm" }) {
                string file = Path.Combine(path, name); if (File.Exists(file) || new FileInfo(file).LinkTarget != null) Regular(file);
            }
            DocumentModel.Require(File.Exists(Path.Combine(path, "room.sqlite")), "registered-room-database-missing");
            entry.store = new RoomStore(path);
            DocumentModel.Require(entry.store.StorageVersion == 2, "registered-room-storage-version-mismatch");
            var archive = entry.store.Load(migrate: false);
            var expected = entry.registration;
            DocumentModel.Require(archive != null && archive.info.room == expected.room && archive.info.generation == expected.generation &&
                archive.info.catalog.hash == expected.catalogHash && archive.info.ownerHash == expected.ownerHash, "registered-room-identity-mismatch");
            RoomBackup.ReadOwner(path, archive);
            entry.room = Room.Recover(entry.store, archive); if (attachBudget) entry.room.AttachBudget(Budget);
        }
        public void Authenticate(string credential) {
            DocumentModel.Require(credential != null && credential.Length == 64 &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(credential)), Encoding.ASCII.GetBytes(configuration.adminHash)), "server-admin-required");
        }
        public void AuthorizeCreation(string credential) {
            if (Matches(credential, configuration.adminHash)) return;
            DocumentModel.Require(CreationPolicy != "admin-only", "server-admin-required");
            DocumentModel.Require(CreationPolicy == "open" ? string.IsNullOrEmpty(credential) : Matches(credential, configuration.creationKeyHash), "room-creation-key-required");
        }
        private static bool Matches(string credential, string expected) => DocumentModel.ValidAssetHash(credential) && expected != null &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(credential)), Encoding.ASCII.GetBytes(expected));
        public void Authenticate(ServerAdministration admin) {
            DocumentModel.Require(admin != null && admin.schema == 1 && admin.serverId == configuration.serverId, "server-admin-required"); Authenticate(admin.credential);
        }
        // Offline operator workflow: constructor's exclusive registry lock fences a live server.
        public void ConfigureCreation(ServerAdministration admin, string policy, string creatorOutput = null) {
            Authenticate(admin);
            DocumentModel.Require(ServerRooms.ValidPolicy(policy) && (policy == "key") == (creatorOutput != null), "invalid-creation-policy");
            var updated = new ServerConfiguration { serverId = configuration.serverId, adminHash = configuration.adminHash, creationPolicy = policy };
            if (policy == "key") {
                var key = new RoomCreationKey { format = "protobot-room-creator", schema = 1, serverId = updated.serverId, credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
                updated.creationKeyHash = Hash(key.credential);
                // Keep the new private key before an ambiguous config write. Never overwrite a key file.
                WriteNew(Path.GetFullPath(creatorOutput), key);
            }
            RecoveryFiles.Write(Path.Combine(root, "server.json"), updated, 16384);
            configuration.creationPolicy = updated.creationPolicy; configuration.creationKeyHash = updated.creationKeyHash;
        }
        public Room Resolve(string name) {
            lock (gate) {
                DocumentModel.Require(name != null && entries.TryGetValue(name, out _) && entries[name].name == name, "room-not-found");
                var entry = entries[name];
                DocumentModel.Require(entry.room != null, "room-unavailable"); return entry.room;
            }
        }
        public HostedRoomList List() {
            Entry[] snapshot; lock (gate) snapshot = entries.Values.ToArray();
            return new HostedRoomList { rooms = snapshot.OrderBy(e => e.name, StringComparer.Ordinal).Select(e => {
                var data = e.room?.Diagnostics() ?? DiagnosticExport.Room(new RoomDiagnostic {
                    room = e.name, generation = e.registration?.generation, catalogHash = e.registration?.catalogHash, status = e.failure });
                return new HostedRoomStatus { room = data.room, generation = data.generation, catalogHash = data.catalogHash,
                    documentHash = data.documentHash, status = data.status, action = data.action, revision = data.revision, usage = data.usage };
            }).ToArray() };
        }

        private void Admission(string name, string generation, string ownerHash) {
            lock (gate) {
                DocumentModel.Require(entries.Count < ProcessBudget.MaxRooms, "server-room-limit");
                DocumentModel.Require(!entries.ContainsKey(name), "room-already-registered");
                DocumentModel.Require(ownerHash != configuration.adminHash && ownerHash != configuration.creationKeyHash && !entries.Values.Any(e => e.registration != null &&
                    (e.registration.generation == generation || e.registration.ownerHash == ownerHash)), "duplicate-room-identity");
            }
            New(Path.Combine(root, "rooms", name));
        }
        // Caller owns Creation. No room edit gate is held during staging or publication.
        public HostedRoomStatus Create(Bootstrap seed) {
            DocumentModel.Require(seed != null && DocumentModel.ValidId(seed.room) && DocumentModel.ValidAssetHash(seed.ownerCredential) &&
                (seed.password == null || seed.password.Length <= 256) && seed.document != null && seed.document.revision == 0, "invalid-room-bootstrap");
            Admission(seed.room, seed.document.generation, Hash(seed.ownerCredential));
            new RoomExport { catalog = seed.catalog, document = seed.document }.Validate();
            string stage = Path.Combine(root, "staging", "create-" + Guid.NewGuid().ToString("N"));
            RecoveryFiles.CreateDirectory(stage);
            try {
                WriteNew(Path.Combine(stage, "owner.json"), new Owner { room = seed.room, credential = seed.ownerCredential });
                using (var store = new RoomStore(stage)) { _ = new Room(seed, storage: store); }
                return Publish(stage);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        public HostedRoomStatus Restore(string archive) {
            string stage = Path.Combine(root, "staging", "restore-" + Guid.NewGuid().ToString("N"));
            try { RoomBackup.Restore(archive, stage); return Publish(stage); }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        private HostedRoomStatus Publish(string stage) {
            RoomRegistration registration;
            using (var store = new RoomStore(stage)) {
                var state = store.Load(); RoomBackup.ReadOwner(stage, state);
                registration = new RoomRegistration { room = state.info.room, generation = state.info.generation, catalogHash = state.info.catalog.hash, ownerHash = state.info.ownerHash };
            }
            ValidateRegistration(registration, registration.room); Admission(registration.room, registration.generation, registration.ownerHash);
            WriteNew(Path.Combine(stage, "registration.json"), registration);
            // Validate and reserve process capacity before publishing. Close SQLite
            // before renaming (including on Windows); reopen only the published path.
            var entry = new Entry { name = registration.room, registration = registration };
            ProcessBudget.Reservation capacity = null; bool published = false;
            try {
                Open(entry, stage, attachBudget: false);
                var usage = entry.room.GeometryUsage;
                capacity = Budget.Reserve(usage.bytes, usage.arrays, usage.instances);
                entry.store.Dispose(); entry.store = null; entry.room = null;
                BeforePublish?.Invoke("room");
                RoomBackup.PublishDirectory(stage, Path.Combine(root, "rooms", registration.room)); published = true;
                Open(entry, Path.Combine(root, "rooms", registration.room), attachBudget: false);
                entry.room.AdoptBudget(Budget); capacity.Commit();
                lock (gate) {
                    // An already admitted atomic publication may finish while draining.
                    // Fence its room before it becomes visible to any existing request.
                    if (stopping) entry.room.BeginShutdown();
                    entries.Add(registration.room, entry);
                }
            }
            catch (Exception error) {
                entry.store?.Dispose(); entry.store = null; entry.room = null; entry.failure = Failure(error);
                if (published) lock (gate) entries.Add(registration.room, entry);
                throw;
            }
            finally { capacity?.Dispose(); }
            return List().rooms.Single(r => r.room == registration.room);
        }
        private static void ValidateRegistration(RoomRegistration value, string name) => DocumentModel.Require(value != null && value.schema == 1 &&
            value.room == name && DocumentModel.ValidId(value.room) && DocumentModel.ValidId(value.generation) &&
            value.catalogHash != null && value.catalogHash.Length <= 128 && !value.catalogHash.Any(char.IsControl) && DocumentModel.ValidAssetHash(value.ownerHash), "invalid-room-registration");
        private static string Failure(Exception error) => error is CollaborationException known ? DiagnosticExport.SafeCode(known.Code) :
            error is IOException || error is UnauthorizedAccessException ? "room-data-unreadable-or-locked" : "room-storage-corrupt";
        public static string Hash(string value) => DocumentModel.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        public static T Read<T>(string path) {
            Regular(path); using var file = File.OpenRead(path);
            DocumentModel.Require(file.Length <= 16384, "registry-metadata-limit");
            return JsonSerializer.Deserialize<T>(file, Json);
        }
        public static void WriteNew<T>(string path, T value) {
            using var file = RoomBackup.PrivateFile(path); file.Write(JsonSerializer.SerializeToUtf8Bytes(value, Json)); file.Flush(true);
        }
        private static void Regular(string path) => DocumentModel.Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "registry-link-refused");
        private static void New(string path) => DocumentModel.Require(!File.Exists(path) && !Directory.Exists(path) && new FileInfo(path).LinkTarget == null, "registry-destination-exists");
        private static void NoLinks(string path) {
            Regular(path);
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) { Regular(child); if (Directory.Exists(child)) NoLinks(child); }
        }
        internal void BeginShutdown() {
            Room[] rooms;
            lock (gate) { stopping = true; rooms = entries.Values.Where(e => e.room != null).Select(e => e.room).ToArray(); }
            foreach (var room in rooms) room.BeginShutdown();
        }
        public void Dispose() {
            // HTTP cancellation cannot interrupt synchronous staging/publication.
            // Drain its existing permit before releasing registry/storage ownership.
            Creation.Wait();
            try {
                Entry[] snapshot; lock (gate) { snapshot = entries.Values.ToArray(); entries.Clear(); }
                foreach (var entry in snapshot) {
                    if (entry.room != null) entry.room.CloseStorage(); else entry.store?.Dispose();
                }
                ownership?.Dispose();
            }
            finally { Creation.Release(); }
        }
    }
}
