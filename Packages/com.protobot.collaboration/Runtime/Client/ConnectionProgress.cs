using System;

namespace Protobot.Collaboration {
    public enum ConnectionStage { CheckingServer, CheckingRoom, Authenticating, DownloadingGeometry, RecoveringEdits, Ready, WaitingToRetry }

    // Local presentation only: never serialized or sent to the authority. Names,
    // hashes, endpoints and credentials cannot enter progress messages.
    public sealed class ConnectionProgress {
        public ConnectionStage Stage { get; }
        public int CompletedItems { get; }
        public int TotalItems { get; }
        public long TransferredBytes { get; }
        public long? TotalBytes { get; }
        public int Attempt { get; }
        public int RetryAfterMilliseconds { get; }
        public string RetryCode { get; }
        public ConnectionProgress(ConnectionStage stage, int completedItems = 0, int totalItems = 0,
            long transferredBytes = 0, long? totalBytes = null, int attempt = 1, int retryAfterMilliseconds = 0, string retryCode = null) {
            Stage = stage; CompletedItems = completedItems; TotalItems = totalItems;
            TransferredBytes = transferredBytes; TotalBytes = totalBytes; Attempt = attempt;
            RetryAfterMilliseconds = Math.Max(0, Math.Min(5000, retryAfterMilliseconds)); RetryCode = DiagnosticExport.SafeCode(retryCode);
        }
    }
}
