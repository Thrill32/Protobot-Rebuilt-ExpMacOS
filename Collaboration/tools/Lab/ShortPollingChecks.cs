using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task ShortPollingChecks(string directory) {
        var seed = Fixture();
        await using var server = new CollaborationServer(seed, dataDirectory: Path.Combine(directory, "short-polling-room"));
        await server.StartAsync();
        var owner = server.Room.Join(new JoinRequest { name = "Short polling authority", catalogHash = seed.catalog.hash, ownerCredential = seed.ownerCredential });
        var initial = await server.Room.ReadAsync(owner.token, 0, -1, CancellationToken.None, wait: false);
        var watch = Stopwatch.StartNew();
        var idle = await server.Room.ReadAsync(owner.token, 0, initial.transientVersion, CancellationToken.None, wait: false).WaitAsync(TimeSpan.FromSeconds(2));
        Check(watch.ElapsedMilliseconds < 1000 && idle.edits.Length == 0 && server.Room.Revision == 0, "immediate idle reads return without waiting or changing the document");
        using (var cancellation = new CancellationTokenSource()) {
            var held = server.Room.ReadAsync(owner.token, 0, initial.transientVersion, cancellation.Token);
            await Task.Delay(50); Check(!held.IsCompleted, "ordinary requests retain held-event behavior");
            cancellation.Cancel(); try { await held; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        }
        var caps = WireJson.Decode<Capabilities>((await HostingRequest(server.Url, "/rooms/team/capabilities")).bytes);
        Check(caps.shortPollingVersion == 1 && caps.transports.Contains("short-polling"), "selected room advertises the versioned optional transport");
        foreach (string query in new[] { "wait=1", "wait=", "wait=0&wait=0" }) {
            var invalid = await HostingRequest(server.Url, "/rooms/team/events?after=0&transient=-1&" + query, owner.token);
            Check(invalid.code == 409 && WireJson.Decode<WireError>(invalid.bytes).error == "invalid-event-wait", "immediate-read selection is strict and bounded");
        }
        Check((await HostingRequest(server.Url, "/rooms/team/events?after=0&transient=-1&wait=0")).code == 401 &&
            (await HostingRequest(server.Url, "/rooms/missing/events?after=0&transient=-1&wait=0", owner.token)).code == 404, "immediate reads preserve authentication and exact room resolution");
        await using var proxy = new LoopbackProxy(server.Url) { RejectHeldEvents = true, HideShortPollingCapability = true };
        await proxy.StartAsync();
        string rejectedCache = Path.Combine(directory, "unsupported-short-polling-membership.json");
        using (var unsupported = new CollaborationClient(proxy.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, recoveryFile: rejectedCache)) {
            await RejectAsync(() => unsupported.JoinAsync("Unsupported mode"), "short-polling-unavailable");
            Check(proxy.JoinRequests == 0 && !File.Exists(rejectedCache) && unsupported.Snapshot == null && !unsupported.IsConnected,
                "absent capability refuses before admission/recovery writes and retains the accepted scene");
        }
        proxy.HideShortPollingCapability = false;
        using (var held = new CollaborationClient(proxy.Url, seed.room, seed.catalog, CollaborationTransport.LongPolling)) {
            await held.JoinAsync("Held requests blocked"); await Until(() => !held.IsConnected);
            Check(!held.IsConnected, "the simulated intermediary actually rejects held event requests");
        }
        using (var automatic = new CollaborationClient(proxy.Url, seed.room, seed.catalog, CollaborationTransport.Auto)) {
            await automatic.JoinAsync("Auto requests blocked"); await Until(() => !automatic.IsConnected);
            Check(proxy.ShortPollTimes.IsEmpty, "Auto retains its WebSocket/held-request behavior without silently switching to short mode");
        }
        string cache = Path.Combine(directory, "short-polling-membership.json");
        using var client = new CollaborationClient(proxy.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, recoveryFile: cache);
        int frames = 0; client.Presence += _ => Interlocked.Increment(ref frames);
        await client.JoinAsync("Short polling editor"); string identity = client.Participant.id;
        await Until(() => Volatile.Read(ref frames) >= 4);
        var times = proxy.ShortPollTimes.ToArray();
        Check(client.IsConnected && client.ActiveTransport == "short-polling" && times.Length <= 5 &&
            times.Zip(times.Skip(1), (a, b) => (b - a) * 1000.0 / Stopwatch.Frequency).All(ms => ms >= CollaborationClient.IdleShortPollMilliseconds - 20),
            "idle client stays connected through blocked held requests with at most one request per second");
        Check(client.CaptureDiagnostics().transport == "short-polling" && server.Room.Revision == 0, "bounded diagnostics name the selected transport and polling adds no revision");
        using var observer = new CollaborationClient(proxy.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling);
        await observer.JoinAsync("Short polling observer", viewer: true);
        var asset = Asset("Short polling model");
        var created = await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Custom("short-custom", asset) }, assetWrites: new[] { asset }));
        await observer.WaitForRevisionAsync(created.revision);
        Check(created.accepted && observer.Snapshot.assets.Single().vertices.SequenceEqual(asset.vertices) && DocumentModel.Hash(observer.Snapshot) == DocumentModel.Hash(client.Snapshot),
            "reference-only short events hydrate authenticated geometry and converge");
        var raw = await HostingRequest(server.Url, "/rooms/team/events?after=0&transient=-1&wait=0", owner.token);
        var batch = WireJson.Decode<EventBatch>(raw.bytes);
        Check(batch.edits.Length == 1 && DocumentModel.AssetWrites(batch.edits[0].transaction).Single().hash == asset.hash &&
            DocumentModel.AssetWrites(batch.edits[0].transaction).All(a => !GeometryTransfer.HasGeometry(a)), "immediate events carry only geometry references with empty payload arrays");
        proxy.DropNextCommitResponse = true;
        try { await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("short-lost-ack") })); throw new Exception("Expected lost reply"); } catch (HttpRequestException) { }
        await client.PrepareReconnectAsync();
        Check(client.PendingCount == 1 && server.Room.Revision == 2, "lost short-mode acknowledgment retains its durable pending ID");
        string accepted = DocumentModel.Hash(client.Snapshot); byte[] recovery = File.ReadAllBytes(cache);
        using (var cancel = new CancellationTokenSource()) {
            cancel.Cancel(); await Canceled(client.JoinAsync("Canceled short reconnect", cancellation: cancel.Token));
        }
        Check(client.PendingCount == 1 && DocumentModel.Hash(client.Snapshot) == accepted && recovery.SequenceEqual(File.ReadAllBytes(cache)), "canceled short reconnect preserves accepted state and private pending membership");
        await client.JoinAsync("Retry short reconnect");
        Check(client.Participant.id == identity && client.PendingCount == 0 && server.Room.Revision == 2 && DocumentModel.Hash(client.Snapshot) == DocumentModel.Hash(server.Room.Snapshot),
            "short reconnect resolves the receipt without replaying the operation or creating a membership");
        await observer.PrepareReconnectAsync();
        proxy.PauseShortPoll = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await proxy.ShortPollPaused.Task.WaitAsync(TimeSpan.FromSeconds(3)); watch.Restart();
        await client.PrepareReconnectAsync();
        Check(watch.ElapsedMilliseconds < 1500 && client.Participant.id == identity && client.PendingCount == 0, "canceling an in-flight immediate read is prompt and retains identity");
        proxy.PauseShortPoll.TrySetResult(true); proxy.PauseShortPoll = null;
        await client.JoinAsync("Final short reconnect");
        await observer.JoinAsync("Resumed short observer");
        server.Room.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = identity });
        await Until(() => client.IsMembershipRevoked);
        Check(!client.IsConnected && observer.IsConnected, "short polling observes durable revocation while other memberships continue");
        string catalog = Path.Combine(directory, "short-polling-catalog.json"), output = Path.Combine(directory, "short-polling-doctor.json"); RecoveryFiles.Write(catalog, seed.catalog);
        var doctor = await RunCli("doctor", "--url", proxy.Url, "--room", seed.room, "--catalog", catalog, "--transport", "short-polling", "--quick", "--output", output);
        var report = RecoveryFiles.Read<ConnectionReport>(output);
        Check(doctor.code == 0 && report.passed && report.checks.Any(c => c.name == "short-polling" && c.status == "passed") &&
            report.diagnostics.Single().transport == "short-polling", "actual CLI doctor proves immediate transport through the held-request-blocking intermediary");
        var all = await RunCli("doctor", "--url", server.Url, "--room", seed.room, "--catalog", catalog, "--quick");
        var allReport = WireJson.Decode<ConnectionReport>(Encoding.UTF8.GetBytes(all.output));
        Check(all.code == 0 && allReport.passed && allReport.diagnostics.Length == 3 && new[] { "websocket", "requests", "short-polling" }.All(label => allReport.checks.Any(c => c.name == label && c.status == "passed")),
            "default actual CLI all probes all three advertised transports");
        watch.Restart(); await observer.LeaveAsync();
        Check(watch.ElapsedMilliseconds < 1500, "Leave cancels the paced idle delay without waiting for the next poll");
    }
}
