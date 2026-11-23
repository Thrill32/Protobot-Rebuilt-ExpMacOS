using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task AdministrationChecks(string directory) {
        var seed = Fixture();
        var legacyCapabilities = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Encode(new Capabilities()));
        legacyCapabilities.AsObject().Remove("participantAdministration");
        Check(!WireJson.Decode<Capabilities>(System.Text.Encoding.UTF8.GetBytes(legacyCapabilities.ToJsonString())).participantAdministration,
            "earlier 0.7 capabilities default participant administration to unavailable");
        var room = new Room(seed);
        JoinResponse Join(string name, bool owner = false, bool viewer = false) => room.Join(new JoinRequest {
            catalogHash = seed.catalog.hash, name = name, ownerCredential = owner ? seed.ownerCredential : null, viewer = viewer });
        var owner = Join("Owner", owner: true); var editor = Join("Editor"); var viewer = Join("Viewer", viewer: true);
        var secondOwner = Join("Other owner", owner: true);
        var request = new RemoveParticipant { participantId = editor.participant.id };
        Reject(() => room.RemoveParticipant(editor.token, request), "owner-required");
        Reject(() => room.RemoveParticipant(viewer.token, request), "owner-required");
        Reject(() => room.Memberships(viewer.token), "owner-required");
        Reject(() => room.Memberships(editor.token), "owner-required");
        Reject(() => room.Memberships("incorrect"), "invalid-session");
        Reject(() => room.Commit(seed.ownerCredential, Tx(room.Snapshot, "AddParts", new[] { Cube("forbidden") })), "invalid-session");
        foreach (string id in new[] { owner.participant.id, secondOwner.participant.id })
            Reject(() => room.RemoveParticipant(owner.token, new RemoveParticipant { participantId = id }), "owner-removal-protected");
        Reject(() => room.RemoveParticipant(owner.token, new RemoveParticipant { participantId = "missing" }), "participant-not-found");
        Reject(() => room.RemoveParticipant(owner.token, new RemoveParticipant { participantId = "../bad" }), "invalid-participant-id");
        Check(room.Memberships(seed.ownerCredential).memberships.Length == 4, "room capability lists memberships without creating one");
        Check(room.Commit(owner.token, Tx(room.Snapshot, "AddParts", new[] { Cube("owner-part") })).accepted &&
            room.Commit(editor.token, Tx(room.Snapshot, "AddParts", new[] { Cube("editor-part", 3) })).accepted, "removal fixture has independent accepted histories");
        var held = room.Reserve(editor.token, new LeaseRequest { ids = new[] { "editor-part" } });
        room.Preview(editor.token, new Preview { lease = held.token, sequence = 1, parts = new[] { Cube("editor-part", 9) } });
        var before = await room.ReadAsync(viewer.token, room.Snapshot.revision, -1, CancellationToken.None);
        Check(before.previews.Length == 1, "removal fixture has a published ghost");
        var waiting = room.ReadAsync(editor.token, room.Snapshot.revision, before.transientVersion, CancellationToken.None);
        string hash = DocumentModel.Hash(room.Snapshot);
        Check(!room.RemoveParticipant(owner.token, request).alreadyRevoked && room.RemoveParticipant(owner.token, request).alreadyRevoked,
            "removal and duplicate retry return stable success without document receipts");
        await RejectAsync(async () => await waiting, "membership-revoked");
        Reject(() => room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = editor.resumeToken }), "membership-revoked");
        Reject(() => room.Reserve(editor.token, new LeaseRequest { ids = new[] { "editor-part" } }), "membership-revoked");
        Reject(() => room.Commit(editor.token, Tx(room.Snapshot, "AddParts", new[] { Cube("late") }, sequence: 2)), "membership-revoked");
        var after = await room.ReadAsync(viewer.token, room.Snapshot.revision, -1, CancellationToken.None);
        Check(after.previews.Length == 0 && !after.participants.Any(p => p.id == editor.participant.id) && DocumentModel.Hash(room.Snapshot) == hash,
            "revocation clears ghosts/presence and preserves all accepted content/revision");
        var released = room.Reserve(owner.token, new LeaseRequest { ids = new[] { "editor-part" } }); room.Release(owner.token, released.token);
        Check(room.Commit(owner.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 2)).accepted && room.Snapshot.parts.Single().id == "editor-part",
            "administration is outside undo and preserves the removed participant's accepted document content");
        Check(room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Fresh membership" }).participant.id != editor.participant.id,
            "revoking a membership is not a person ban or admission policy");

        // Exercise actual contention at the room gate. Either a commit precedes revocation,
        // or it is refused; no accepted content may disappear and no command may follow revocation.
        for (int iteration = 0; iteration < 8; iteration++) {
            var concurrent = new Room(seed);
            var target = concurrent.Join(new JoinRequest { name = "Racing editor", catalogHash = seed.catalog.hash });
            var edit = Tx(concurrent.Snapshot, "AddParts", new[] { Cube("race") });
            using var start = new Barrier(3);
            var commit = Task.Run(() => { start.SignalAndWait(); try { return concurrent.Commit(target.token, edit).accepted; }
                catch (CollaborationException error) when (error.Code == "membership-revoked") { return false; } });
            var remove = Task.Run(() => { start.SignalAndWait(); return concurrent.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = target.participant.id }); });
            start.SignalAndWait(); await Task.WhenAll(commit, remove);
            Check(concurrent.Snapshot.parts.Length == (commit.Result ? 1 : 0) && concurrent.Snapshot.revision == (commit.Result ? 1 : 0),
                "concurrent commit/removal has one serialized, content-preserving outcome");
            Reject(() => concurrent.Commit(target.token, edit), "membership-revoked");
            Reject(() => concurrent.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = target.resumeToken }), "membership-revoked");
            var reconnecting = concurrent.Join(new JoinRequest { name = "Racing reconnect", catalogHash = seed.catalog.hash });
            var resume = Task.Run(() => { try { return concurrent.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = reconnecting.resumeToken }); }
                catch (CollaborationException error) when (error.Code == "membership-revoked") { return null; } });
            var repeatedRemovals = Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => concurrent.RemoveParticipant(seed.ownerCredential,
                new RemoveParticipant { participantId = reconnecting.participant.id }))));
            await Task.WhenAll(resume, repeatedRemovals);
            Check(repeatedRemovals.Result.Count(result => result.alreadyRevoked) == 1, "concurrent duplicate removals perform one durable membership transition");
            if (resume.Result != null) Reject(() => concurrent.Outcome(resume.Result.token, "after-reconnect-race"), "membership-revoked");
            else Check(true, "removal fences a concurrent reconnect before it can issue access");
            Reject(() => concurrent.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = reconnecting.resumeToken }), "membership-revoked");
        }
        var reconnected = Join("Reconnect race");
        reconnected = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = reconnected.resumeToken });
        room.RemoveParticipant(owner.token, new RemoveParticipant { participantId = reconnected.participant.id });
        Reject(() => room.Outcome(reconnected.token, "late-outcome"), "membership-revoked");
        long now = 0; var inactiveRoom = new Room(seed, () => now);
        var inactive = inactiveRoom.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Inactive membership" }); now = 60001;
        Check(!inactiveRoom.Memberships(seed.ownerCredential).memberships.Single().active, "owner management includes inactive stable memberships");
        inactiveRoom.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = inactive.participant.id });
        Reject(() => inactiveRoom.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = inactive.resumeToken }), "membership-revoked");

        string failurePath = Path.Combine(directory, "removal storage failure"); JoinResponse failedTarget;
        using (var store = new RoomStore(failurePath)) {
            var durable = new Room(seed, storage: store);
            failedTarget = durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Storage target" });
            var cursor = await durable.ReadAsync(failedTarget.token, 0, -1, CancellationToken.None);
            var waitingOnFailure = durable.ReadAsync(failedTarget.token, 0, cursor.transientVersion, CancellationToken.None);
            store.BeforeCommit = transition => { if (transition == "leave") throw new IOException("Injected revoke failure"); };
            Reject(() => durable.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = failedTarget.participant.id }), "room-storage-failed-restart-required");
            Check(!durable.IsHealthy && durable.Snapshot.revision == 0, "failed revocation never reports success or publishes a document mutation");
            await RejectAsync(async () => await waitingOnFailure, "room-storage-failed-restart-required");
            Reject(() => durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = failedTarget.resumeToken }), "room-storage-failed-restart-required");
        }
        using (var store = new RoomStore(failurePath)) {
            var durable = new Room(null, storage: store);
            var resumed = durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = failedTarget.resumeToken });
            Check(resumed.participant.id == failedTarget.participant.id, "restart rolls back an uncommitted revoke and retains the original membership");
            durable.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = resumed.participant.id });
        }
        using (var store = new RoomStore(failurePath)) {
            var durable = new Room(null, storage: store);
            Reject(() => durable.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = failedTarget.resumeToken }), "membership-revoked");
            Check(durable.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = failedTarget.participant.id }).alreadyRevoked,
                "successful revocation and lost-response retry survive restart");
        }
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling })
            await AdministrationTransportChecks(directory, seed, transport);
        await AdministrationCliChecks(directory, seed);
    }

    private static async Task AdministrationTransportChecks(string directory, Bootstrap seed, CollaborationTransport transport) {
        string cache = Path.Combine(directory, "removed-" + transport + ".json");
        await using var server = new CollaborationServer(seed, dataDirectory: Path.Combine(directory, "administration-" + transport));
        await server.StartAsync();
        using var owner = new CollaborationClient(server.Url, "team", seed.catalog, transport);
        using var target = new CollaborationClient(server.Url, "team", seed.catalog, transport, recoveryFile: cache);
        using var viewer = new CollaborationClient(server.Url, "team", seed.catalog, transport);
        await owner.JoinAsync("Network owner", ownerCredential: seed.ownerCredential);
        var joined = await target.JoinAsync("Network target"); await viewer.JoinAsync("Network viewer", viewer: true);
        await Until(() => target.ActiveTransport == (transport == CollaborationTransport.WebSocket ? "websocket" : "long-polling"));
        await RejectAsync(async () => await target.RemoveParticipantAsync(viewer.Participant.id), "owner-required");
        await RejectAsync(async () => await viewer.RemoveParticipantAsync(target.Participant.id), "owner-required");
        await RejectAsync(async () => await owner.RemoveParticipantAsync(owner.Participant.id), "owner-removal-protected");
        Check((await target.SubmitAsync(target.CreateTransaction("AddParts", new[] { Cube("network-retained") }))).accepted,
            transport + " target commits accepted content before removal");
        await owner.WaitForRevisionAsync(1); await viewer.WaitForRevisionAsync(1);
        EventBatch latest = null; owner.Presence += batch => latest = batch;
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.Disconnected += message => disconnected.TrySetResult(message);
        var held = await target.ReserveAsync(new[] { "network-retained" });
        await target.PreviewAsync(new Preview { lease = held.token, sequence = 1, parts = new[] { Cube("network-retained", 12) } });
        await Until(() => latest?.previews.Length == 1);
        Check(!(await owner.RemoveParticipantAsync(target.Participant.id)).alreadyRevoked, transport + " authenticated owner removal succeeds");
        // Freeze clears IsConnected before invoking Disconnected. Await the actual
        // feedback rather than racing that property write on another thread.
        string reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => !target.IsConnected && latest?.previews.Length == 0 && !latest.participants.Any(p => p.id == target.Participant.id));
        Check(reason == "membership-revoked" && target.IsMembershipRevoked && owner.IsConnected && viewer.IsConnected,
            transport + " event connection terminates promptly with removed-member feedback while peers remain connected");
        await RejectAsync(async () => await target.ReserveAsync(new[] { "network-retained" }), "membership-revoked");
        await target.PrepareReconnectAsync(); await RejectAsync(async () => await target.JoinAsync("Removed retry"), "membership-revoked");
        // Raw resume validation is independent of the client's terminal UI guard.
        Reject(() => server.Room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = joined.resumeToken }), "membership-revoked");
        var available = await owner.ReserveAsync(new[] { "network-retained" }); await owner.ReleaseAsync(available.token);
        Check(owner.Snapshot.parts.Single().position[0] == 0 && (await owner.RemoveParticipantAsync(target.Participant.id)).alreadyRevoked,
            transport + " removal releases leases, discards previews and supports duplicate retry");
        // Close the target's file lock, then prove a new process/client cannot revive its durable cache.
        target.Dispose();
        using var recovered = new CollaborationClient(server.Url, "team", seed.catalog, transport, recoveryFile: cache);
        await RejectAsync(async () => await recovered.JoinAsync("Cached removed member"), "membership-revoked");
    }

    private static async Task AdministrationCliChecks(string directory, Bootstrap seed) {
        string path = Path.Combine(directory, "CLI administration"), catalog = Path.Combine(directory, "admin-catalog.json");
        RecoveryFiles.Write(catalog, seed.catalog);
        Check((await RunCli("init", "--data-dir", path, "--room", "team", "--catalog", catalog)).code == 0, "CLI administration initializes protected owner capability");
        string ownerFile = Path.Combine(path, "owner.json");
        string secret = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(ownerFile))["credential"].GetValue<string>();
        await using var child = await RecoveryChild.Start(path, "http://127.0.0.1:0", null);
        using var member = new CollaborationClient(child.Ready.url, "team", seed.catalog);
        await member.JoinAsync("CLI removal target"); string id = member.Participant.id;
        var listed = await RunCli("participants", "--url", child.Ready.url, "--owner-file", ownerFile);
        Check(listed.code == 0 && WireJson.Decode<MembershipList>(System.Text.Encoding.UTF8.GetBytes(listed.output)).memberships.Single().participant.id == id,
            "CLI lists stable IDs with the owner file and no temporary owner membership");
        var removed = await RunCli("remove-participant", "--url", child.Ready.url, "--owner-file", ownerFile, "--participant", id);
        var duplicate = await RunCli("remove-participant", "--url", child.Ready.url, "--owner-file", ownerFile, "--participant", id);
        Check(removed.code == 0 && duplicate.code == 0 && duplicate.output.Contains("\"alreadyRevoked\":true") &&
            !(listed.output + listed.error + removed.output + removed.error + duplicate.output + duplicate.error).Contains(secret),
            "CLI removal/retry uses no argument credential and emits credential-free results");
        await using (var proxy = new LoopbackProxy(child.Ready.url)) {
            await proxy.StartAsync(); proxy.RejectedAdministrationError = secret;
            var reflected = await RunCli("participants", "--url", proxy.Url, "--owner-file", ownerFile);
            Check(reflected.code == 1 && !(reflected.output + reflected.error).Contains(secret), "CLI suppresses a reflected capability in an untrusted error body");
            proxy.RejectedAdministrationError = null;
            using var lostReplyMember = new CollaborationClient(child.Ready.url, "team", seed.catalog);
            await lostReplyMember.JoinAsync("Lost removal reply");
            proxy.DropNextRemovalResponse = true;
            var lost = await RunCli("remove-participant", "--url", proxy.Url, "--owner-file", ownerFile, "--participant", lostReplyMember.Participant.id);
            var retried = await RunCli("remove-participant", "--url", proxy.Url, "--owner-file", ownerFile, "--participant", lostReplyMember.Participant.id);
            Check(lost.code == 1 && retried.code == 0 && retried.output.Contains("\"alreadyRevoked\":true"), "CLI safely retries a removal whose successful durable acknowledgment was lost");
        }
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(ownerFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var broad = await RunCli("participants", "--url", child.Ready.url, "--owner-file", ownerFile);
            Check(broad.code == 1 && broad.error.Contains("owner-file-must-be-private") && !broad.error.Contains(secret), "CLI refuses a broadly readable owner file");
            RecoveryFiles.PrivateFile(ownerFile);
            string link = Path.Combine(directory, "owner-link.json"); File.CreateSymbolicLink(link, ownerFile);
            Check((await RunCli("participants", "--url", child.Ready.url, "--owner-file", link)).code == 1, "CLI refuses owner-file symlinks");
        }
        byte[] correctOwner = File.ReadAllBytes(ownerFile);
        try {
            var wrong = System.Text.Json.Nodes.JsonNode.Parse(correctOwner); wrong["credential"] = "wrong-owner-capability";
            File.WriteAllText(ownerFile, wrong.ToJsonString());
            var denied = await RunCli("participants", "--url", child.Ready.url, "--owner-file", ownerFile);
            Check(denied.code == 1 && denied.error.Contains("401") && !denied.error.Contains("wrong-owner-capability"), "CLI rejects wrong ownership without logging credentials or response bodies");
        }
        finally { File.WriteAllBytes(ownerFile, correctOwner); }
    }

    private static async Task RejectAsync(Func<Task> action, string code) {
        try { await action(); throw new Exception("Expected rejection: " + code); }
        catch (CollaborationException error) { Check(error.Code == code, "expected " + code + ", received " + error.Code); }
    }
}
