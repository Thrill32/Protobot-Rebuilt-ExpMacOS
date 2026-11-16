using System;
using System.Threading;
using Protobot.Collaboration;

namespace Protobot {
    public sealed partial class CollaborationSession {
        // Coalesce background progress rather than enqueueing one Unity callback
        // per network buffer. An ended attempt can never update a later attempt.
        private sealed class ConnectionAttempt : IProgress<ConnectionProgress>, IDisposable {
            private readonly CancellationTokenSource source = new CancellationTokenSource();
            private ConnectionProgress latest;
            public readonly CancellationToken Token;
            public readonly string InitialStatus;
            public CollaborationClient Candidate;
            public bool ClosePanelRequested;
            public ConnectionAttempt(string status) { InitialStatus = status; Token = source.Token; }
            public ConnectionProgress Latest => Volatile.Read(ref latest);
            public void Report(ConnectionProgress value) { if (!Token.IsCancellationRequested) Interlocked.Exchange(ref latest, value); }
            public void Cancel() => source.Cancel();
            public void Dispose() => source.Dispose();
        }
        private ConnectionAttempt connectionAttempt;
        private bool closing;
        private ConnectionAttempt BeginConnection(string message) {
            var attempt = new ConnectionAttempt(message); connectionAttempt = attempt;
            busy = true; status = message; nextUIRefresh = 0; return attempt;
        }
        private void EndConnection(ConnectionAttempt attempt) {
            if (connectionAttempt == attempt) { connectionAttempt = null; busy = false; nextUIRefresh = 0; }
            attempt.Dispose();
        }
        private void CancelConnection() {
            if (connectionAttempt == null) return;
            connectionAttempt.Cancel(); nextUIRefresh = 0;
        }
        private string ConnectionStatus() {
            var attempt = connectionAttempt;
            if (attempt == null) return FriendlyStatus(status);
            if (attempt.Token.IsCancellationRequested) return "Canceling connection… Finishing any membership reply and keeping recovery data for retry.";
            var progress = attempt.Latest;
            if (progress == null) return attempt.InitialStatus;
            switch (progress.Stage) {
                case ConnectionStage.CheckingServer: return "Checking server…";
                case ConnectionStage.CheckingRoom: return "Checking room and build compatibility…";
                case ConnectionStage.Authenticating: return "Joining room…";
                case ConnectionStage.DownloadingGeometry:
                    if (progress.CompletedItems == progress.TotalItems) return "Models downloaded. Preparing the accepted build…";
                    string bytes = progress.TotalBytes.HasValue ? $"{progress.TransferredBytes / 1048576.0:0.0} / {progress.TotalBytes.Value / 1048576.0:0.0} MiB" :
                        progress.TransferredBytes > 0 ? $"{progress.TransferredBytes / 1048576.0:0.0} MiB" : "waiting for transfer";
                    return $"Downloading model {progress.CompletedItems + 1} of {progress.TotalItems} — {bytes}" + (progress.Attempt > 1 ? $" (attempt {progress.Attempt})" : "");
                case ConnectionStage.RecoveringEdits: return $"Checking pending edits… {progress.CompletedItems} of {progress.TotalItems}";
                default: return "Opening the accepted build…";
            }
        }
    }
}
