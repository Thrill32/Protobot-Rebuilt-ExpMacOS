using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private sealed class ConnectionObserver : IProgress<ConnectionProgress> {
        public readonly ConcurrentQueue<ConnectionProgress> Items = new ConcurrentQueue<ConnectionProgress>();
        public Action<ConnectionProgress> OnReport = null;
        public void Report(ConnectionProgress value) { Items.Enqueue(value); OnReport?.Invoke(value); }
    }
    private static async Task Canceled(Task task) {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); throw new Exception("Expected connection cancellation"); }
        catch (OperationCanceledException) { Check(true, "connection cancellation is observable"); }
    }
    private static async Task ConnectionChecks(string directory) {
        var seed = Fixture(); string data = Path.Combine(directory, "connection room");
        await using var server = new CollaborationServer(seed, dataDirectory: data); await server.StartAsync();
        var owner = server.Room.Join(new JoinRequest { name = "Connection owner", catalogHash = seed.catalog.hash, ownerCredential = seed.ownerCredential });
        var asset = LargeAsset("Private source name must not enter progress");
        using (var encoding = new CancellationTokenSource(TimeSpan.FromMilliseconds(10))) {
            try { LocalJson.Encode(asset, cancellation: encoding.Token); throw new Exception("Expected canceled bootstrap serialization"); }
            catch (OperationCanceledException) { Check(true, "large bootstrap serialization observes cancellation between output buffers"); }
        }
        server.Room.PutAsset(owner.token, asset.hash, GeometryTransfer.Encode(asset));
        Check(server.Room.Commit(owner.token, Tx(server.Room.Snapshot, "AddParts", new[] { Custom("initial-model", asset) }, assets: new[] { GeometryTransfer.Reference(asset) })).accepted,
            "connection fixture has real geometry above the ordinary message limit");
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling }) {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var observer = new ConnectionObserver();
            using var client = new CollaborationClient(server.Url, seed.room, seed.catalog, transport);
            int members = server.Room.Memberships(seed.ownerCredential).memberships.Length;
            await Canceled(client.JoinAsync("Pre-canceled", cancellation: cancellation.Token, progress: observer));
            Check(observer.Items.Count == 0 && !client.IsConnected && server.Room.Memberships(seed.ownerCredential).memberships.Length == members, "pre-cancel publishes no progress or admission on " + transport);
        }
        await using var proxy = new LoopbackProxy(server.Url); await proxy.StartAsync();
        string cache = Path.Combine(directory, "connection identity.json");
        using var participant = new CollaborationClient(proxy.Url, seed.room, seed.catalog, CollaborationTransport.LongPolling, recoveryFile: cache);
        proxy.ContinueJoinReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ConnectionObserver();
        using (var cancellation = new CancellationTokenSource()) {
            var joining = participant.JoinAsync("Canceled admitted member", cancellation: cancellation.Token, progress: progress);
            await proxy.JoinReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel(); Check(!joining.IsCompleted, "cancellation settles an already-sent bounded membership reply");
            proxy.ContinueJoinReply.TrySetResult(true); await Canceled(joining);
        }
        proxy.ContinueJoinReply = null;
        string identity = participant.Participant.id;
        Check(File.Exists(cache) && !participant.IsConnected && participant.Snapshot == null, "canceled admission saves identity without publishing a partial document");
        Check(!progress.Items.Any(p => p.Stage == ConnectionStage.Ready), "canceled admission never reports ready");
        proxy.AssetPauseAfterBytes = 65536; proxy.ContinueAssetDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        progress = new ConnectionObserver();
        using (var cancellation = new CancellationTokenSource()) {
            var joining = participant.JoinAsync("Retry canceled member", cancellation: cancellation.Token, progress: progress);
            await proxy.PartialAssetSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => progress.Items.Any(p => p.TransferredBytes >= 65536));
            await RejectAsync(() => participant.JoinAsync("Overlapping join"), "already-connecting");
            Check(progress.Items.Any(p => p.Stage == ConnectionStage.DownloadingGeometry && p.TotalBytes > 4 * 1024 * 1024 && p.CompletedItems == 0 && p.TotalItems == 1), "progress reports bounded model bytes/count without source metadata");
            cancellation.Cancel(); await Canceled(joining);
        }
        Check(participant.Participant.id == identity && participant.Snapshot == null && !participant.IsConnected, "partial model cancellation preserves identity and leaves editing disabled");
        proxy.ContinueAssetDownload.TrySetResult(true); proxy.ContinueAssetDownload = null; proxy.AssetPauseAfterBytes = 0;
        proxy.DropNextAssetDownload = true;
        progress = new ConnectionObserver(); await participant.JoinAsync("Retry complete", progress: progress);
        Check(participant.IsConnected && participant.Participant.id == identity && progress.Items.Last().Stage == ConnectionStage.Ready, "retry resumes the same identity and reports ready after complete hydration");
        Check(progress.Items.Any(p => p.Attempt == 2 && p.TransferredBytes > 0), "lost download response reports a fresh retry attempt with real bytes");
        Check(participant.Snapshot.assets.Single().vertices.SequenceEqual(asset.vertices), "retry discards partial bytes and materializes validated geometry");
        Check(server.Room.Memberships(seed.ownerCredential).memberships.Count(m => m.participant.id == identity) == 1, "canceled joins do not duplicate the known membership");
        proxy.DropNextCommitResponse = true;
        var pending = participant.CreateTransaction("AddParts", new[] { Cube("pending-connection-edit", 17) });
        try { await participant.SubmitAsync(pending); throw new Exception("Expected lost acknowledgment"); } catch (System.Net.Http.HttpRequestException) { }
        await participant.PrepareReconnectAsync();
        string accepted = DocumentModel.Hash(participant.Snapshot); long revision = server.Room.Revision;
        Check(participant.PendingCount == 1, "lost acknowledgment leaves a durable pending outcome for reconnect");
        string diagnosticPath = Path.Combine(directory, "pending-operation-diagnostics.json");
        var diagnostic = participant.CaptureDiagnostics(); string diagnosticHash = DocumentModel.Hash(participant.Snapshot);
        await DiagnosticExport.WriteAsync(diagnosticPath, diagnostic);
        Check(DiagnosticExport.Decode(File.ReadAllBytes(diagnosticPath)).pendingOperations == 1 && participant.PendingCount == 1 &&
            DocumentModel.Hash(participant.Snapshot) == diagnosticHash, "export reports ambiguous pending operation count without resolving or replaying it");
        var later = Asset("Later remote model"); server.Room.PutAsset(owner.token, later.hash, GeometryTransfer.Encode(later));
        Check(server.Room.Commit(owner.token, Tx(server.Room.Snapshot, "AddParts", new[] { Custom("later-model", later) }, sequence: 2, assets: new[] { GeometryTransfer.Reference(later) })).accepted, "another participant edits while a canceled peer is disconnected");
        proxy.AssetPauseAfterBytes = 32; proxy.PartialAssetSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.ContinueAssetDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancellation = new CancellationTokenSource()) {
            var joining = participant.JoinAsync("Canceled reconnect", cancellation: cancellation.Token);
            await proxy.PartialAssetSent.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel(); await Canceled(joining);
        }
        Check(DocumentModel.Hash(participant.Snapshot) == accepted && participant.PendingCount == 1, "canceled reconnect preserves the last fully accepted snapshot and unresolved receipt IDs");
        proxy.ContinueAssetDownload.TrySetResult(true); proxy.ContinueAssetDownload = null; proxy.AssetPauseAfterBytes = 0;
        progress = new ConnectionObserver(); await participant.JoinAsync("Resolved reconnect", progress: progress);
        Check(participant.PendingCount == 0 && participant.Participant.id == identity && server.Room.Revision == revision + 1 &&
            DocumentModel.Hash(participant.Snapshot) == DocumentModel.Hash(server.Room.Snapshot), "retry resolves accepted receipt without replaying the pending operation");
        Check(progress.Items.Any(p => p.Stage == ConnectionStage.RecoveringEdits) && progress.Items.Last().Stage == ConnectionStage.Ready, "pending outcomes settle before ready progress");
        Check((await participant.SubmitAsync(participant.CreateTransaction("Undo", Array.Empty<Part>()))).accepted &&
            server.Room.Snapshot.parts.Any(p => p.id == "later-model") && !server.Room.Snapshot.parts.Any(p => p.id == "pending-connection-edit"), "personal history survives canceled reconnect and leaves remote edits intact");
        await participant.LeaveAsync();
    }
}
