using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    // Private local ownership, independent of participant recovery and portable content.
    // A prepared record is deliberately retained even when publication is unknown.
    [Serializable] public sealed class CreatedRoom {
        public string format = "protobot-created-room";
        public int schema = 1;
        public string origin, serverId, room, generation, catalogHash, ownerCredential;
        public bool confirmed;
        [System.Runtime.Serialization.OptionalField] public bool authorizationRefused;
    }
    public static class CreatedRoomRecovery {
        public const int MaxRecords = 32, MaxBytes = 4096;
        public static string FileName(string root, string origin, string room) {
            using var hash = SHA256.Create();
            return Path.Combine(root, DocumentModel.Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(origin + "\n" + room))) + ".json");
        }
        public static CreatedRoom[] List(string root) => List(root, out _);
        public static CreatedRoom[] List(string root, out bool unreadable) {
            unreadable = false;
            if (!Directory.Exists(root)) return Array.Empty<CreatedRoom>();
            var files = Directory.EnumerateFiles(root, "*.json").Take(MaxRecords + 1).ToArray();
            DocumentModel.Require(files.Length <= MaxRecords, "created-room-record-limit");
            var records = new System.Collections.Generic.List<CreatedRoom>();
            foreach (string file in files) { try { records.Add(Read(file)); } catch { unreadable = true; } }
            return records.ToArray();
        }
        public static CreatedRoom Read(string file) {
            using var input = File.OpenRead(file);
            DocumentModel.Require(input.Length > 0 && input.Length <= MaxBytes && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0, "invalid-created-room-record");
            var bytes = new byte[(int)input.Length]; int offset = 0, read;
            while (offset < bytes.Length && (read = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
            DocumentModel.Require(offset == bytes.Length, "invalid-created-room-record");
            CreatedRoom value;
            try { value = WireJson.Decode<CreatedRoom>(bytes); }
            catch (System.Runtime.Serialization.SerializationException) { throw new CollaborationException("invalid-created-room-record"); }
            DocumentModel.Require(value != null && value.format == "protobot-created-room" && value.schema == 1 &&
                DocumentModel.ValidId(value.serverId) && DocumentModel.ValidId(value.room) && DocumentModel.ValidId(value.generation) &&
                DocumentModel.ValidAssetHash(value.ownerCredential) && value.catalogHash != null && value.catalogHash.Length <= 128 &&
                !value.catalogHash.Any(char.IsControl) && ConnectionDoctor.Origin(value.origin, true) == value.origin, "invalid-created-room-record");
            return value;
        }
        internal static FileStream Acquire(string root) {
            RecoveryFiles.CreateDirectory(root);
            string lockPath = Path.Combine(root, "creation.lock");
            DocumentModel.Require(!File.Exists(lockPath) || (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) == 0, "invalid-created-room-record");
            FileStream held;
            try { held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new CollaborationException("created-room-record-in-use"); }
            try { RecoveryFiles.PrivateFile(lockPath); return held; } catch { held.Dispose(); throw; }
        }
        internal static void Prepare(string root, CreatedRoom value) {
            RecoveryFiles.CreateDirectory(root);
            string path = FileName(root, value.origin, value.room);
            if (File.Exists(path)) {
                var prior = Read(path);
                DocumentModel.Require(prior.authorizationRefused && prior.origin == value.origin && prior.room == value.room && prior.serverId == value.serverId, "created-room-record-exists");
                // Only an authenticated endpoint's definitive pre-publication 403 permits
                // a new explicit Create confirmation to replace a refused preparation.
                RecoveryFiles.Write(path, value, MaxBytes); return;
            }
            DocumentModel.Require(Directory.EnumerateFiles(root, "*.json").Take(MaxRecords).Count() < MaxRecords, "created-room-record-limit");
            // Exclusive destination and durable bytes precede the first mutation send.
            // A partial local write can only cause refusal, never a remote creation.
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            RecoveryFiles.PrivateFile(path);
            var bytes = WireJson.Encode(value); DocumentModel.Require(bytes.Length <= MaxBytes, "invalid-created-room-record");
            output.Write(bytes, 0, bytes.Length); output.Flush(true);
        }
    }
    public sealed class RemoteRoomCreation : IDisposable {
        private readonly HttpClient http;
        public string Origin { get; }
        public RemoteRoomCreation(string origin, bool allowLanHttp = false, NetworkPolicy network = null) {
            Origin = ConnectionDoctor.Origin(origin, allowLanHttp);
            http = (network ?? NetworkPolicy.SystemDefault).CreateHttpClient();
        }
        public async Task<Capabilities> InspectAsync(CancellationToken cancellation) {
            var value = await ConnectionRetry.GetAsync<Capabilities>(http, Origin + "/capabilities", cancellation).ConfigureAwait(false);
            ServerRooms.RequireCreation(value); return value;
        }
        public async Task<CreatedRoom> CreateAsync(RoomExport content, string room, string password, string credential, bool administrator,
            string recoveryRoot, CancellationToken cancellation) {
            var caps = await InspectAsync(cancellation).ConfigureAwait(false);
            DocumentModel.Require(administrator || caps.roomCreationPolicy != "admin-only", "server-admin-required");
            DocumentModel.Require(administrator || caps.roomCreationPolicy != "key" || DocumentModel.ValidAssetHash(credential), "room-creation-key-required");
            DocumentModel.Require(string.IsNullOrEmpty(credential) || DocumentModel.ValidAssetHash(credential), "invalid-creation-credential");
            DocumentModel.Require(!administrator || DocumentModel.ValidAssetHash(credential), "server-admin-required");
            string owner; using (var random = RandomNumberGenerator.Create()) { var bytes = new byte[32]; random.GetBytes(bytes); owner = DocumentModel.Hex(bytes); }
            var seed = content.CreateRoom(room, password, owner);
            var body = await Task.Run(() => LocalJson.Encode(seed), cancellation).ConfigureAwait(false);
            var record = new CreatedRoom { origin = Origin, serverId = caps.serverId, room = room, generation = seed.document.generation,
                catalogHash = seed.catalog.hash, ownerCredential = owner };
            cancellation.ThrowIfCancellationRequested();
            // Serialize private preparation/publication/outcome updates across processes.
            // This also keeps the local record count exact and prevents a second window
            // from replacing ownership during a refused-key correction.
            using var ownership = CreatedRoomRecovery.Acquire(recoveryRoot);
            CreatedRoomRecovery.Prepare(recoveryRoot, record);
            cancellation.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var request = new HttpRequestMessage(HttpMethod.Post, Origin + (administrator ? "/admin/rooms" : "/rooms"));
            if (!string.IsNullOrEmpty(credential)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.ExpectContinue = true;
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            // Single send. Cancellation or a lost reply leaves the private record for explicit Check/Join.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            NetworkPolicy.CheckProxyResponse(response);
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                DocumentModel.Require(output.Length + count <= 65536, "invalid-server-response"); output.Write(buffer, 0, count);
            }
            if (!response.IsSuccessStatusCode) {
                var rejection = ConnectionRetry.Rejection(response, output.ToArray());
                if (rejection.HttpStatusCode == 403 && (rejection.Code == "room-creation-key-required" || rejection.Code == "server-admin-required")) {
                    record.authorizationRefused = true;
                    RecoveryFiles.Write(CreatedRoomRecovery.FileName(recoveryRoot, Origin, room), record, CreatedRoomRecovery.MaxBytes);
                }
                throw rejection;
            }
            var result = WireJson.Decode<HostedRoomStatus>(output.ToArray());
            DocumentModel.Require(result != null && result.room == room && result.generation == record.generation && result.catalogHash == record.catalogHash, "created-room-mismatch");
            record.confirmed = true; RecoveryFiles.Write(CreatedRoomRecovery.FileName(recoveryRoot, Origin, room), record, CreatedRoomRecovery.MaxBytes);
            return record;
        }
        public async Task CheckAsync(CreatedRoom record, CancellationToken cancellation) {
            DocumentModel.Require(record.origin == Origin, "created-room-mismatch");
            var caps = await InspectAsync(cancellation).ConfigureAwait(false);
            DocumentModel.Require(caps.serverId == record.serverId, "created-room-mismatch");
            var room = await ConnectionRetry.GetAsync<Capabilities>(http, Origin + "/rooms/" + record.room + "/capabilities", cancellation).ConfigureAwait(false);
            DocumentModel.Require(room.room == record.room && room.generation == record.generation && room.catalogHash == record.catalogHash, "created-room-mismatch");
            var report = await ConnectionRetry.GetAsync<DiagnosticReport>(http, Origin + "/rooms/" + record.room + "/diagnostics", cancellation, record.ownerCredential).ConfigureAwait(false);
            DocumentModel.Require(report.selectedRoom?.generation == record.generation, "created-room-mismatch");
        }
        public async Task<HostedRoomList> ListAsync(string administrator, CancellationToken cancellation) {
            var caps = await ConnectionRetry.GetAsync<Capabilities>(http, Origin + "/capabilities", cancellation).ConfigureAwait(false);
            DocumentModel.Require(caps != null && caps.protocol == 1 && caps.version == CollaborationProtocol.Version && caps.multiRoom && caps.registryVersion == 1, "server-administration-unavailable");
            DocumentModel.Require(DocumentModel.ValidAssetHash(administrator), "server-admin-required");
            var result = await ConnectionRetry.GetAsync<HostedRoomList>(http, Origin + "/admin/rooms", cancellation, administrator).ConfigureAwait(false);
            DocumentModel.Require(result.registryVersion == 1 && result.rooms != null && result.rooms.Length <= 8 &&
                result.rooms.All(r => r != null && DocumentModel.ValidId(r.room)), "invalid-server-response");
            return result;
        }
        public void Dispose() => http.Dispose();
    }
}
