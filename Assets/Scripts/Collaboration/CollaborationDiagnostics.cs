using System;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEngine;
using UnityEngine.UI;

namespace Protobot {
    public sealed partial class CollaborationSession {
        private CancellationTokenSource diagnosticExport;
        private DiagnosticReport lastDiagnostic;
        private RoomDiagnostic roomResources;
        private CollaborationClient resourceClient;
        private Text uiResources;
        private Button uiRefreshResources;
        private bool loadingResources;
        private void CancelDiagnosticExport() => diagnosticExport?.Cancel();
        private Task<DiagnosticReport> CaptureDiagnosticExportAsync(CancellationToken cancellation) {
            var connection = client;
            var fallback = lastDiagnostic ?? new DiagnosticReport { selectedRoom = new RoomDiagnostic { room = room, catalogHash = CollaborationScene.Catalog.hash } };
            var resources = resourceClient == connection ? roomResources : null;
            return Task.Run(() => {
                cancellation.ThrowIfCancellationRequested();
                var report = connection?.CaptureDiagnostics() ?? DiagnosticExport.Sanitize(fallback);
                report.source = "unity"; report.rooms = resources == null ? Array.Empty<RoomDiagnostic>() : new[] { resources };
                return DiagnosticExport.Sanitize(report);
            }, cancellation);
        }
        private async Task ExportDiagnosticsAsync() {
            if (busy || NativeFileDialogs.IsOpen || FindFirstObjectByType<BuildsManager>()?.IsFileOperationInProgress == true) return;
            busy = true; status = "Preparing diagnostic export…"; nextUIRefresh = 0; diagnosticExport = new CancellationTokenSource(); var active = diagnosticExport;
            try {
                // Small counters/metadata only; no snapshot, scene capture, file reads or save barrier.
                var report = await CaptureDiagnosticExportAsync(active.Token);
                active.Token.ThrowIfCancellationRequested();
                string path = await NativeFileDialogs.SaveFileAsync("Export Collaboration Diagnostics", "", "collaboration-diagnostics", "json");
                active.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(path)) { status = "Diagnostic export canceled."; return; }
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
                await DiagnosticExport.WriteAsync(path, report, active.Token);
                status = "Diagnostics exported. Scene, local save and membership are unchanged.";
            }
            catch (OperationCanceledException) when (active.IsCancellationRequested) { status = "Diagnostic export canceled."; }
            catch (Exception) { status = "Diagnostic export failed. Choose a new filename in a writable folder; existing files were retained."; }
            finally { diagnosticExport = null; active.Dispose(); busy = false; nextUIRefresh = 0; }
        }
        private async Task RefreshResourcesAsync() {
            var connection = client;
            if (connection == null || connection.Participant?.role != "owner" || loadingResources || busy) return;
            long epoch = connection.ConnectionEpoch; loadingResources = true;
            try {
                var report = await connection.ResourcesAsync();
                if (client != connection || epoch != connection.ConnectionEpoch) return;
                resourceClient = connection; roomResources = report.selectedRoom; status = "Room usage refreshed (snapshot, not continuous monitoring).";
            }
            catch (Exception error) {
                if (client == connection && epoch == connection.ConnectionEpoch) status = "Room usage unavailable (" + DiagnosticExport.Code(error) + "). Local diagnostics can still be exported.";
            }
            finally { loadingResources = false; nextUIRefresh = 0; }
        }
        private void RefreshResourcesUI() {
            bool owner = client?.Participant?.role == "owner" && client.SupportsDiagnostics;
            uiRefreshResources.gameObject.SetActive(owner); uiResources.gameObject.SetActive(owner);
            uiRefreshResources.interactable = !busy && !loadingResources;
            if (!owner) return;
            var usage = resourceClient == client ? roomResources?.usage : null;
            if (usage == null) { uiResources.text = "Refresh to inspect this room's usage against its limits."; return; }
            string Count(ResourceMetric metric) => metric == null ? "unavailable" : metric.used + " / " + metric.limit;
            string Bytes(ResourceMetric metric) => metric == null ? "unavailable" : $"{metric.used / 1048576.0:0.00} / {metric.limit / 1048576.0:0.00} MiB";
            uiResources.text = "Last sampled room usage\nParticipants " + Count(usage.participants) + "; event streams " + Count(usage.eventStreams) +
                "; transfers " + Count(usage.transfers) + "\nLogical geometry costs (not measured memory):\nRetained " + Bytes(usage.retainedGeometryBytes) +
                "; decoded arrays " + Bytes(usage.decodedArrayBytes) + "\nLive instance arrays " + Bytes(usage.liveInstanceArrayBytes) +
                "\nUnused uploads " + Count(usage.unusedUploads) + " (" + Bytes(usage.unusedUploadBytes) + ")\nHistory " + Bytes(usage.historyBytes) +
                "; receipts " + Count(usage.receipts);
        }
    }
}
