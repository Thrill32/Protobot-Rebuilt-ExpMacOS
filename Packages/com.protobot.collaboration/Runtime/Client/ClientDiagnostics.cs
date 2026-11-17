using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Protobot.Collaboration {
    public sealed partial class CollaborationClient {
        private readonly Queue<DiagnosticEvent> diagnosticEvents = new Queue<DiagnosticEvent>();
        private readonly Stopwatch diagnosticClock = Stopwatch.StartNew();
        private string diagnosticStage, diagnosticCode, diagnosticHash;
        private ConnectionProgress diagnosticTransfer;
        private sealed class DiagnosticProgressRelay : IProgress<ConnectionProgress> {
            private readonly Action<ConnectionProgress> action;
            public DiagnosticProgressRelay(Action<ConnectionProgress> action) { this.action = action; }
            public void Report(ConnectionProgress value) => action(value);
        }
        public bool SupportsDiagnostics { get; private set; }
        private void DiagnosticProgress(ConnectionProgress value, IProgress<ConnectionProgress> progress) {
            lock (gate) {
                if (value.Stage == ConnectionStage.DownloadingGeometry) diagnosticTransfer = value;
                if (diagnosticStage != value.Stage.ToString()) {
                    diagnosticStage = value.Stage.ToString(); DiagnosticEvent(null);
                }
            }
            progress?.Report(value);
        }
        private void DiagnosticEvent(string code) {
            lock (gate) {
                if (code != null) diagnosticCode = DiagnosticExport.SafeCode(code);
                if (diagnosticEvents.Count == DiagnosticExport.MaxEvents) diagnosticEvents.Dequeue();
                diagnosticEvents.Enqueue(new DiagnosticEvent { stage = diagnosticStage, code = DiagnosticExport.SafeCode(code), milliseconds = diagnosticClock.ElapsedMilliseconds });
            }
        }
        public DiagnosticReport CaptureDiagnostics() {
            lock (gate) return DiagnosticExport.Sanitize(new DiagnosticReport { source = "client", connected = IsConnected, durable = IsDurable,
                networkRoute = network.Route, proxyAuthentication = network.Authentication,
                pendingOperations = pending.Count, transport = ActiveTransport ?? (Transport == CollaborationTransport.Auto ? "auto" : Transport == CollaborationTransport.WebSocket ? "websocket" : Transport == CollaborationTransport.ShortPolling ? "short-polling" : "long-polling"),
                stage = diagnosticStage, code = diagnosticCode, elapsedMilliseconds = diagnosticClock.ElapsedMilliseconds,
                completedModels = diagnosticTransfer?.CompletedItems ?? 0, totalModels = diagnosticTransfer?.TotalItems ?? 0, transferAttempt = diagnosticTransfer?.Attempt ?? 0,
                transferredBytes = diagnosticTransfer?.TransferredBytes ?? 0, totalTransferBytes = diagnosticTransfer?.TotalBytes,
                selectedRoom = new RoomDiagnostic { room = room, catalogHash = catalog.hash, generation = document?.generation, revision = document?.revision, documentHash = diagnosticHash },
                events = diagnosticEvents.ToArray() });
        }
        public Task<DiagnosticReport> ResourcesAsync(CancellationToken cancellation = default) {
            string credential; lock (gate) {
                DocumentModel.Require(SupportsDiagnostics, "diagnostics-unavailable"); credential = token;
            }
            // Independent of edit/pump lifetime: failure/cancellation never freezes a session.
            return DiagnosticExport.FetchAsync(endpoint.GetLeftPart(UriPartial.Authority), room, credential, cancellation, network);
        }
    }
}
