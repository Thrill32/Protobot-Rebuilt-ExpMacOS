using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    [Serializable] public sealed class RoomReplacementAttempt {
        public string format = "protobot-room-replacement";
        public int schema = 1;
        public string origin, operationId, contentHash;
        public RoomReplacementTarget target;
        public bool confirmed, notAccepted;
        public RoomReplacementReceipt receipt;
    }
    public sealed class RemoteRoomReplacement : IDisposable {
        public const int MaxRecordBytes = 8192, MaxRecords = 64;
        private readonly HttpClient http;
        public string Origin { get; }
        public RemoteRoomReplacement(string origin, bool allowLanHttp = false, NetworkPolicy network = null) {
            Origin = ConnectionDoctor.Origin(origin, allowLanHttp); http = (network ?? NetworkPolicy.SystemDefault).CreateHttpClient();
        }
        public async Task<RoomReplacementTarget> InspectAsync(string room, string ownerCredential, CancellationToken cancellation) {
            DocumentModel.Require(DocumentModel.ValidId(room), "invalid-room");
            string path = Origin + "/rooms/" + room;
            var caps = await ConnectionRetry.GetAsync<Capabilities>(http, path + "/capabilities", cancellation).ConfigureAwait(false);
            DocumentModel.Require(caps.protocol == 1 && caps.version == CollaborationProtocol.Version && caps.room == room && caps.durable &&
                caps.roomReplacementVersion == RoomReplacement.Version && DocumentModel.ValidId(caps.roomIdentity), "room-replacement-unavailable");
            var target = await ConnectionRetry.GetAsync<RoomReplacementTarget>(http, path + "/replacement", cancellation, ownerCredential).ConfigureAwait(false);
            RoomReplacement.ValidateTarget(target);
            DocumentModel.Require(target.room == room && target.roomIdentity == caps.roomIdentity && target.generation == caps.generation &&
                target.catalogHash == caps.catalogHash, "replacement-target-changed");
            return target;
        }
        private static void ValidateResult(RoomReplacementReceipt receipt, RoomReplacementAttempt attempt) {
            RoomReplacement.ValidateReceipt(receipt);
            DocumentModel.Require(receipt.operationId == attempt.operationId && receipt.room == attempt.target.room && receipt.roomIdentity == attempt.target.roomIdentity &&
                receipt.catalogHash == attempt.target.catalogHash && receipt.previousGeneration == attempt.target.generation &&
                receipt.previousRevision == attempt.target.revision && receipt.previousHash == attempt.target.documentHash, "invalid-replacement-receipt");
        }
        public static RoomReplacementAttempt ReadAttempt(string file) {
            using var input = File.OpenRead(file);
            DocumentModel.Require(input.Length > 0 && input.Length <= MaxRecordBytes && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0, "invalid-replacement-record");
            var bytes = new byte[(int)input.Length]; int offset = 0, count;
            while (offset < bytes.Length && (count = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
            DocumentModel.Require(offset == bytes.Length, "invalid-replacement-record");
            var attempt = WireJson.Decode<RoomReplacementAttempt>(bytes);
            DocumentModel.Require(attempt != null && attempt.schema == 1 && attempt.format == "protobot-room-replacement" &&
                DocumentModel.ValidId(attempt.operationId) && DocumentModel.ValidAssetHash(attempt.contentHash) &&
                ConnectionDoctor.Origin(attempt.origin, true) == attempt.origin && !(attempt.confirmed && attempt.notAccepted) &&
                attempt.confirmed == (attempt.receipt != null), "invalid-replacement-record");
            RoomReplacement.ValidateTarget(attempt.target);
            if (attempt.confirmed) ValidateResult(attempt.receipt, attempt);
            return attempt;
        }
        private static FileStream Acquire(string file) {
            RecoveryFiles.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            string path = file + ".lock";
            DocumentModel.Require(!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "invalid-replacement-record");
            try {
                var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { RecoveryFiles.PrivateFile(path); return held; } catch { held.Dispose(); throw; }
            }
            catch (IOException) { throw new CollaborationException("replacement-record-in-use"); }
        }
        public async Task<RoomReplacementReceipt> ReplaceAsync(RoomExport content, RoomReplacementTarget target, string ownerCredential,
            string attemptFile, CancellationToken cancellation) {
            RoomReplacement.ValidateTarget(target); cancellation.ThrowIfCancellationRequested();
            var current = await InspectAsync(target.room, ownerCredential, cancellation).ConfigureAwait(false);
            DocumentModel.Require(RoomReplacement.SameTarget(current, target), "replacement-target-changed");
            if (GeometryTransfer.HasAppearance(content.document)) {
                var caps = await ConnectionRetry.GetAsync<Capabilities>(http, Origin + "/rooms/" + target.room + "/capabilities", cancellation).ConfigureAwait(false);
                GeometryTransfer.RequireAppearanceSupport(true, caps.customAppearanceVersion);
            }
            var request = new RoomReplacementRequest { expected = WireJson.Clone(target), content = content };
            var body = await Task.Run(() => { content.Validate(); DocumentModel.Require(content.catalog.hash == target.catalogHash, "catalog-mismatch");
                return LocalJson.Encode(request, cancellation: cancellation); }, cancellation).ConfigureAwait(false);
            var attempt = new RoomReplacementAttempt { origin = Origin, operationId = request.operationId, target = WireJson.Clone(target),
                contentHash = DocumentModel.Hash(GeometryTransfer.References(content.document)) };
            cancellation.ThrowIfCancellationRequested();
            attemptFile = Path.GetFullPath(attemptFile);
            using var held = Acquire(attemptFile);
            using (var output = new FileStream(attemptFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                RecoveryFiles.PrivateFile(attemptFile); var bytes = WireJson.Encode(attempt);
                DocumentModel.Require(bytes.Length <= MaxRecordBytes, "invalid-replacement-record"); output.Write(bytes, 0, bytes.Length); output.Flush(true);
            }
            if (cancellation.IsCancellationRequested) {
                attempt.notAccepted = true; RecoveryFiles.Write(attemptFile, attempt, MaxRecordBytes); cancellation.ThrowIfCancellationRequested();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var message = new HttpRequestMessage(HttpMethod.Post, Origin + "/rooms/" + target.room + "/replacement");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerCredential); message.Headers.ExpectContinue = true;
            message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            // Exactly one mutation send. Once sent, any interruption retains an
            // uncertain private attempt; only a read checks its authoritative outcome.
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            NetworkPolicy.CheckProxyResponse(response);
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false); using var received = new MemoryStream();
            var buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                DocumentModel.Require(received.Length + read <= 65536, "invalid-server-response"); received.Write(buffer, 0, read);
            }
            if (!response.IsSuccessStatusCode) {
                var rejection = ConnectionRetry.Rejection(response, received.ToArray());
                if (rejection.Code == "owner-required" || rejection.Code == "invalid-session" || rejection.Code == "replacement-target-changed") {
                    attempt.notAccepted = true; RecoveryFiles.Write(attemptFile, attempt, MaxRecordBytes);
                }
                throw rejection;
            }
            var result = WireJson.Decode<RoomReplacementReceipt>(received.ToArray()); ValidateResult(result, attempt);
            attempt.confirmed = true; attempt.receipt = result; RecoveryFiles.Write(attemptFile, attempt, MaxRecordBytes); return result;
        }
        public async Task<RoomReplacementReceipt> ReplaceInProfileAsync(RoomExport content, RoomReplacementTarget target, string ownerCredential,
            string root, CancellationToken cancellation) {
            root = Path.GetFullPath(root); RecoveryFiles.CreateDirectory(root);
            using var profile = Acquire(Path.Combine(root, "profile"));
            DocumentModel.Require(Directory.EnumerateFiles(root, "*.json").Take(MaxRecords).Count() < MaxRecords, "replacement-record-limit");
            return await ReplaceAsync(content, target, ownerCredential, Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"), cancellation).ConfigureAwait(false);
        }
        public async Task<RoomReplacementOutcome> CheckAsync(string attemptFile, string ownerCredential, CancellationToken cancellation) {
            using var held = Acquire(attemptFile); var attempt = ReadAttempt(attemptFile);
            DocumentModel.Require(attempt.origin == Origin, "invalid-replacement-record");
            var current = await InspectAsync(attempt.target.room, ownerCredential, cancellation).ConfigureAwait(false);
            DocumentModel.Require(current.roomIdentity == attempt.target.roomIdentity && current.catalogHash == attempt.target.catalogHash, "saved-room-does-not-match");
            var outcome = await ConnectionRetry.GetAsync<RoomReplacementOutcome>(http, Origin + "/rooms/" + attempt.target.room +
                "/replacement-result?operationId=" + Uri.EscapeDataString(attempt.operationId), cancellation, ownerCredential).ConfigureAwait(false);
            DocumentModel.Require(outcome != null && outcome.version == RoomReplacement.Version && outcome.found == (outcome.receipt != null) &&
                (!attempt.confirmed || outcome.found), "invalid-replacement-receipt");
            if (outcome.found) { ValidateResult(outcome.receipt, attempt); attempt.confirmed = true; attempt.notAccepted = false; attempt.receipt = outcome.receipt; }
            else if (!RoomReplacement.SameTarget(current, attempt.target)) {
                // A changed authoritative precondition fences every still-delayed
                // send for this attempt. An unchanged target alone proves no absence.
                attempt.notAccepted = true;
            }
            RecoveryFiles.Write(attemptFile, attempt, MaxRecordBytes); return outcome;
        }
        public void Dispose() => http.Dispose();
    }
}
