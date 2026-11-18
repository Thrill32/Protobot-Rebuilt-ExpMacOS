using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Protobot.Collaboration.Server {
    // Stopped-data inspection retains the ordinary ownership locks through activation.
    // It never starts a registry (which would clean staging) or migrates SQLite.
    public sealed class InstallationData : IDisposable {
        private readonly List<IDisposable> ownership = new List<IDisposable>();
        public readonly HashSet<int> StorageVersions = new HashSet<int>();
        public readonly HashSet<int> DocumentVersions = new HashSet<int>();
        public readonly HashSet<string> Catalogs = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> ConfigFields = new HashSet<string>(StringComparer.Ordinal);
        public int RegistryVersion { get; private set; }
        public bool RequiresEditorReplacement { get; private set; }
        public static InstallationData Open(string directory, bool multiRoom) {
            var result = new InstallationData();
            try { result.Inspect(Path.GetFullPath(directory), multiRoom); return result; }
            catch { result.Dispose(); throw; }
        }
        public static void Regular(string path) {
            // Each artifact entry and data file is checked individually. System path
            // aliases such as macOS /var and /tmp may occur above an explicit root.
            DocumentModel.Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "installation-link-refused");
        }
        private void Inspect(string directory, bool multiRoom) {
            Regular(directory);
            if (!multiRoom) { InspectRoom(directory, null); return; }
            string lockPath = Path.Combine(directory, "server.lock");
            if (File.Exists(lockPath)) Regular(lockPath);
            try {
                var stream = File.Exists(lockPath) ? new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : RoomBackup.PrivateFile(lockPath);
                ownership.Add(stream);
            }
            catch (IOException) { throw new CollaborationException("server-data-in-use"); }
            string configPath = Path.Combine(directory, "server.json"); Regular(configPath);
            var config = RoomRegistry.Read<ServerConfiguration>(configPath);
            DocumentModel.Require(config.schema == 1 && DocumentModel.ValidId(config.serverId) && DocumentModel.ValidAssetHash(config.adminHash) &&
                ServerRooms.ValidPolicy(config.creationPolicy ?? "admin-only") && ((config.creationPolicy ?? "admin-only") == "key" ?
                    DocumentModel.ValidAssetHash(config.creationKeyHash) && config.creationKeyHash != config.adminHash : config.creationKeyHash == null), "installation-config-incompatible");
            using (var json = JsonDocument.Parse(File.ReadAllBytes(configPath)))
                foreach (var field in json.RootElement.EnumerateObject()) ConfigFields.Add(field.Name);
            string headerPath = Path.Combine(directory, "registry.json"); Regular(headerPath);
            using (var json = JsonDocument.Parse(File.ReadAllBytes(headerPath))) {
                RegistryVersion = json.RootElement.GetProperty("schema").GetInt32();
                DocumentModel.Require(RegistryVersion == RoomRegistry.Version && json.RootElement.GetProperty("serverId").GetString() == config.serverId &&
                    json.RootElement.EnumerateObject().Count() == 2, "installation-registry-incompatible");
            }
            string rooms = Path.Combine(directory, "rooms"); Regular(rooms);
            var entries = Directory.EnumerateFileSystemEntries(rooms).Take(ProcessBudget.MaxRooms + 1).ToArray();
            DocumentModel.Require(entries.Length <= ProcessBudget.MaxRooms, "server-room-limit");
            var generations = new HashSet<string>(StringComparer.Ordinal); var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in entries) {
                Regular(entry); string registrationPath = Path.Combine(entry, "registration.json"); Regular(registrationPath);
                var registration = RoomRegistry.Read<RoomRegistration>(registrationPath);
                DocumentModel.Require(registration.schema == 1 && registration.room == Path.GetFileName(entry) && DocumentModel.ValidId(registration.room) &&
                    generations.Add(registration.generation) && owners.Add(registration.ownerHash), "installation-registration-incompatible");
                InspectRoom(entry, registration);
            }
        }
        private void InspectRoom(string directory, RoomRegistration expected) {
            foreach (string name in new[] { "room.sqlite", "owner.json", "room.lock", "room.sqlite-wal", "room.sqlite-shm" }) {
                string path = Path.Combine(directory, name); if (File.Exists(path) || new FileInfo(path).LinkTarget != null) Regular(path);
            }
            var store = new RoomStore(directory, readOnly: true); ownership.Add(store);
            var state = store.Load(migrate: false);
            DocumentModel.Require(state != null && (expected == null || expected.room == state.info.room && expected.generation == RoomStore.Identity(state.info) &&
                expected.ownerHash == state.info.ownerHash && expected.catalogHash == state.info.catalog.hash), "installation-registration-incompatible");
            RoomBackup.ReadOwner(directory, state);
            RequiresEditorReplacement |= state.info.allowEditorProjectReplacement;
            StorageVersions.Add(store.StorageVersion); DocumentVersions.Add(state.document.schema); Catalogs.Add(state.info.catalog.hash);
        }
        public void Dispose() { for (int i = ownership.Count - 1; i >= 0; i--) ownership[i].Dispose(); ownership.Clear(); }
    }
}
