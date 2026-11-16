using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    [Serializable] public sealed class ResourceMetric {
        public long used, limit;
        public ResourceMetric(long used, long limit) { this.used = used; this.limit = limit; }
    }
    // Logical content costs, not managed/native/GPU memory measurements.
    [Serializable] public sealed class RoomUsage {
        public ResourceMetric participants, memberships, requests, eventStreams, transfers;
        public ResourceMetric assets, retainedGeometryBytes, decodedArrayBytes, liveInstanceArrayBytes;
        public ResourceMetric unusedUploads, unusedUploadBytes, parts, chains, historyBytes, receipts, journalEvents;
        public long historyEntries;
    }
    [Serializable] public sealed class RoomDiagnostic {
        public string room, generation, catalogHash, documentHash, status, action;
        public long? revision;
        // Null means unavailable; never substitute an empty room after a failed open.
        public RoomUsage usage;
    }
    [Serializable] public sealed class ProcessUsage {
        public ResourceMetric rooms, requests, eventStreams, transfers, connections;
        public ResourceMetric retainedGeometryBytes, decodedArrayBytes, liveInstanceArrayBytes;
        public long measuredManagedBytes, measuredWorkingSetBytes;
    }
    [Serializable] public sealed class DiagnosticEvent {
        public string stage, code;
        public long milliseconds;
    }
    [Serializable] public sealed class DiagnosticReport {
        public string format = "protobot-diagnostics";
        public int schema = 1;
        public string version = CollaborationProtocol.Version;
        public string source, transport, stage, code;
        public bool connected, durable;
        public int pendingOperations;
        public int documentVersion = 2;
        public int completedModels, totalModels, transferAttempt;
        public long transferredBytes;
        public long? totalTransferBytes;
        public long elapsedMilliseconds;
        public long capturedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public RoomDiagnostic selectedRoom;
        public RoomDiagnostic[] rooms = Array.Empty<RoomDiagnostic>();
        public ProcessUsage process;
        public DiagnosticEvent[] events = Array.Empty<DiagnosticEvent>();
    }
    public static class DiagnosticExport {
        public const int Version = 1, MaxBytes = 65536, MaxEvents = 32, MaxRooms = 8;
        private static readonly HashSet<string> Codes = new HashSet<string>(StringComparer.Ordinal) {
            "canceled", "ready", "export-failed", "diagnostics-unavailable", "invalid-diagnostics", "owner-required", "server-admin-required",
            "membership-revoked", "pending-operation-limit", "asset-transfer-busy", "server-transfer-busy", "asset-byte-limit", "asset-room-quota",
            "asset-materialization-quota", "asset-scene-quota", "asset-client-quota", "asset-unused-quota", "server-geometry-quota", "alpha-history-limit",
            "room-request-limit", "room-data-in-use", "room-data-unreadable-or-locked", "room-storage-corrupt", "registered-room-database-missing",
            "registered-room-storage-version-mismatch", "registered-room-identity-mismatch", "invalid-room-registration", "registry-link-refused",
            "duplicate-room-identity", "offline", "room-storage-version-mismatch", "incompatible-room", "room-full", "server-rejected-request",
            "invalid-server-response", "tls-trust-or-name-failed", "dns-failed", "connection-refused", "network-unreachable", "request-timeout",
            "websocket-unavailable", "http-connection-failed", "event-stream-disconnected"
        };
        public static string Code(Exception error) => error is OperationCanceledException ? "canceled" :
            error is CollaborationException known && Codes.Contains(known.Code ?? "") ? known.Code : ConnectionDoctor.Code(error);
        public static string SafeCode(string value) => value == null ? null : Code(new CollaborationException(value));
        public static string Transport(string value) => new[] { "auto", "websocket", "long-polling" }.Contains(value) ? value : null;
        public static string Stage(string value) => Enum.TryParse<ConnectionStage>(value, out var parsed) && Enum.IsDefined(typeof(ConnectionStage), parsed) ? parsed.ToString() : null;
        public static string Id(string value) => DocumentModel.ValidId(value) ? value : null;
        public static string Catalog(string value) => value != null && value.Length <= 128 && value.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_') ? value : null;
        public static string Hash(string value) => DocumentModel.ValidAssetHash(value) ? value : null;
        public static string ActionFor(string status) {
            if (status == "ready") return null;
            if (status == "room-data-in-use" || status == "room-data-unreadable-or-locked") return "Stop the other process; check service account permissions. Never remove an active lock.";
            if (status == "server-geometry-quota") return "Use another host or reduce content with the matching release; restart. Data was retained.";
            if (status == "registered-room-storage-version-mismatch" || status == "room-storage-version-mismatch" || status == "incompatible-room") return "Use the matching release and catalog. Inspect does not migrate data.";
            return "Stop service; preserve room data; check the matching release and restore a private backup if damaged. Never initialize over it.";
        }
        public static RoomDiagnostic Room(RoomDiagnostic value) {
            if (value == null) return null;
            string status = value.status == null ? null : value.status == "ready" ? "ready" : SafeCode(value.status);
            return new RoomDiagnostic { room = Id(value.room), generation = Id(value.generation), catalogHash = Catalog(value.catalogHash),
                documentHash = Hash(value.documentHash), revision = value.revision >= 0 ? value.revision : null,
                status = status, action = status == null ? null : ActionFor(status), usage = value.usage };
        }
        // Explicit DTO projection. No object reflection, arbitrary text, paths, endpoints,
        // identities, payloads or credentials are accepted into the export.
        public static DiagnosticReport Sanitize(DiagnosticReport value) {
            DocumentModel.Require(value != null && value.schema == Version && value.format == "protobot-diagnostics" && value.version == CollaborationProtocol.Version && value.documentVersion == 2, "invalid-diagnostics");
            return new DiagnosticReport { source = new[] { "unity", "client", "doctor", "room-owner", "server-admin" }.Contains(value.source) ? value.source : null,
                transport = Transport(value.transport), stage = Stage(value.stage), code = SafeCode(value.code), connected = value.connected, durable = value.durable,
                pendingOperations = Math.Max(0, Math.Min(256, value.pendingOperations)), elapsedMilliseconds = Math.Max(0, value.elapsedMilliseconds),
                completedModels = Math.Max(0, Math.Min(256, value.completedModels)), totalModels = Math.Max(0, Math.Min(256, value.totalModels)),
                transferAttempt = Math.Max(0, Math.Min(3, value.transferAttempt)), transferredBytes = Math.Max(0, value.transferredBytes),
                totalTransferBytes = value.totalTransferBytes >= 0 ? value.totalTransferBytes : null, capturedUnixMilliseconds = value.capturedUnixMilliseconds,
                selectedRoom = Room(value.selectedRoom), rooms = (value.rooms ?? Array.Empty<RoomDiagnostic>()).Take(MaxRooms).Select(Room).ToArray(), process = value.process,
                events = (value.events ?? Array.Empty<DiagnosticEvent>()).Take(MaxEvents).Where(e => e != null).Select(e => new DiagnosticEvent {
                    stage = Stage(e.stage), code = SafeCode(e.code), milliseconds = Math.Max(0, e.milliseconds) }).ToArray() };
        }
        public static byte[] Encode(DiagnosticReport value) {
            var bytes = WireJson.Encode(Sanitize(value)); DocumentModel.Require(bytes.Length <= MaxBytes, "invalid-diagnostics"); return bytes;
        }
        public static DiagnosticReport Decode(byte[] bytes) {
            DocumentModel.Require(bytes != null && bytes.Length <= MaxBytes, "invalid-diagnostics"); return Sanitize(WireJson.Decode<DiagnosticReport>(bytes));
        }
        public static async Task<DiagnosticReport> FetchAsync(string origin, string room, string credential, CancellationToken cancellation = default) {
            string path = room == null ? "/admin/diagnostics" : "/rooms/" + Id(room) + "/diagnostics";
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, origin.TrimEnd('/') + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[4096]; int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                DocumentModel.Require(bytes.Length + read <= MaxBytes, "invalid-diagnostics"); bytes.Write(buffer, 0, read);
            }
            if (!response.IsSuccessStatusCode) {
                string code = null; try { code = WireJson.Decode<WireError>(bytes.ToArray())?.error; } catch { }
                throw new CollaborationException(SafeCode(code) ?? "diagnostics-unavailable");
            }
            return Decode(bytes.ToArray());
        }
        // New destination only. Cancellation/failure leaves existing files untouched;
        // a private sibling is published atomically after the last cancellation check.
        public static Task WriteAsync(string path, DiagnosticReport report, CancellationToken cancellation = default) => Task.Run(() => {
            cancellation.ThrowIfCancellationRequested(); var bytes = Encode(report);
            path = Path.GetFullPath(path); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    RecoveryFiles.PrivateFile(temporary); cancellation.ThrowIfCancellationRequested(); output.Write(bytes, 0, bytes.Length); output.Flush(true);
                }
                cancellation.ThrowIfCancellationRequested(); File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, cancellation);
    }
}
