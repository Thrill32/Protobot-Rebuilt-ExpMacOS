using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Builds;
using Protobot.Collaboration;
using UnityEngine;

namespace Protobot {
    public sealed partial class CollaborationSession {
        private CancellationTokenSource connectionTest;
        private RoomExport CaptureRoomExport() {
            var manifest = CollaborationScene.Catalog;
            var content = new RoomExport { catalog = WireJson.Clone(manifest), document = IsShared ? WireJson.Clone(committed) : CollaborationScene.CaptureDocument(manifest) };
            content.Validate(); return content;
        }
        private async Task ExportRoomAsync() {
            var builds = FindFirstObjectByType<BuildsManager>();
            if (busy || builds == null || builds.IsFileOperationInProgress || NativeFileDialogs.IsOpen) return;
            busy = true;
            try {
                if (!await WaitForSaveAsync()) return;
                // Capture before the dialog; private previews and later remote
                // edits cannot change the content the user elected to export.
                var content = CaptureRoomExport();
                string path = await NativeFileDialogs.SaveFileAsync("Export Room for CLI", LocalPreferences.GetString("collaborationExportDirectory", ""), "room-export", "json");
                if (string.IsNullOrEmpty(path)) return;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
                await Task.Run(() => RecoveryFiles.Write(path, content));
                LocalPreferences.SetString("collaborationExportDirectory", Path.GetDirectoryName(path));
                status = "Exported accepted content for CLI initialization. Your local file and save status are unchanged.";
            }
            catch (Exception error) { Report(error); }
            finally { busy = false; }
        }
        private async Task TestServerAsync() {
            if (busy || IsShared) return;
            busy = true;
            connectionTest = new CancellationTokenSource();
            var activeTest = connectionTest;
            try {
                ValidateSettings(true);
                var requested = new[] { transport };
                var report = await ConnectionDoctor.RunAsync(endpoint, CollaborationScene.Catalog, room, password, requested, lan,
                    progress: step => Enqueue(() => { if (connectionTest == activeTest && !activeTest.IsCancellationRequested) status = step; }), cancellation: activeTest.Token);
                uiTestReport.text = string.Join("\n", report.checks.Select(c => c.name + ": " + c.status + (c.code == null ? "" : " (" + c.code + ")")));
                status = report.passed ? "Read-only connection checks passed. Join to open the room." : "Some connection checks failed. See the results on the Join page.";
            }
            catch (OperationCanceledException) when (connectionTest.IsCancellationRequested) { status = "Connection test canceled."; }
            catch (Exception error) { Report(error); }
            finally { connectionTest.Dispose(); connectionTest = null; ClearPassword(); busy = false; }
        }
    }
}
