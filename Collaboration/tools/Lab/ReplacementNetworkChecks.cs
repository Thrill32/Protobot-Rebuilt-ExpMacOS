using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private sealed class ReplacementProgress : IProgress<ConnectionProgress> {
        public Action<ConnectionProgress> OnReport;
        public void Report(ConnectionProgress value) => OnReport?.Invoke(value);
    }
    private static async Task ReplacementNetworkChecks(string directory) {
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
            string root = Path.Combine(directory, "network-" + transport); var seed = HostedSeed("replace-" + transport);
            await using var server = new CollaborationServer(seed, dataDirectory: root); await server.StartAsync();
            await using var relay = new LoopbackProxy(server.Url); await relay.StartAsync();
            using var observer = new CollaborationClient(server.Url, seed.room, seed.catalog, transport);
            var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); observer.Disconnected += code => stopped.TrySetResult(code);
            await observer.JoinAsync("Observer", seed.password);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (observer.ActiveTransport == null && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Check(observer.ActiveTransport != null, "event transport running before replacement: " + transport);
            string cache = Path.Combine(directory, "pending-" + transport + ".json");
            using var pending = new CollaborationClient(relay.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, recoveryFile: cache);
            var admitted = await pending.JoinAsync("Pending member", seed.password); string identity = pending.Participant.id;
            relay.DropNextCommitResponse = true;
            var oldCommand = pending.CreateTransaction("AddParts", new[] { Cube("old-accepted", 13) });
            try { await pending.SubmitAsync(oldCommand); throw new Exception("Expected lost command reply"); } catch (HttpRequestException) { }
            await pending.PrepareReconnectAsync(); string acceptedBaseline = DocumentModel.Hash(pending.Snapshot); string oldGeneration = pending.Snapshot.generation;
            var rejected = Tx(server.Room.Snapshot, "AddParts", new[] { Cube("old-rejected", double.NaN) }, sequence: 2);
            Check(!server.Room.Commit(admitted.token, rejected).accepted, "retained rejected lost-outcome fixture");
            string missing = Guid.NewGuid().ToString("N"), schema1Cache = Path.Combine(directory, "schema1-" + transport + ".json");
            var legacyCache = JsonNode.Parse(File.ReadAllText(cache)); legacyCache["schema"] = 1;
            legacyCache.AsObject().Remove("roomIdentity"); legacyCache.AsObject().Remove("pendingGeneration");
            legacyCache.AsObject().Remove("resynchronizationRequired");
            legacyCache["pendingIds"] = new JsonArray(oldCommand.operationId, rejected.operationId, missing);
            File.WriteAllText(schema1Cache, legacyCache.ToJsonString()); RecoveryFiles.PrivateFile(schema1Cache);
            using var remote = new RemoteRoomReplacement(relay.Url);
            var target = await remote.InspectAsync(seed.room, seed.ownerCredential, CancellationToken.None);
            string attemptFile = Path.Combine(directory, "attempt-" + transport + ".json"); relay.DropNextReplacementResponse = true;
            try { await remote.ReplaceAsync(CreationContent(), target, seed.ownerCredential, attemptFile, CancellationToken.None); throw new Exception("Expected lost replacement reply"); }
            catch (HttpRequestException) { }
            var attempt = RemoteRoomReplacement.ReadAttempt(attemptFile); var replacement = server.Room.ReplacementOutcome(seed.ownerCredential, attempt.operationId).receipt;
            int membersBeforeLegacyJoin = server.Room.Memberships(seed.ownerCredential).memberships.Length;
            var oldJoin = await HostingRequest(server.Url, "/rooms/" + seed.room + "/join", method: HttpMethod.Post,
                body: WireJson.Encode(new JoinRequest { catalogHash = seed.catalog.hash, name = "Old fresh client", password = seed.password }));
            Check(oldJoin.code == 409 && WireJson.Decode<WireError>(oldJoin.bytes).error == "room-replacement-client-required" &&
                server.Room.Memberships(seed.ownerCredential).memberships.Length == membersBeforeLegacyJoin,
                "a same-protocol client without generation proof refuses admission before creating an orphaned membership");
            Check(!attempt.confirmed && replacement != null && !File.ReadAllText(attemptFile).Contains(seed.ownerCredential), "lost replacement reply retains its private operation identity without owner-key duplication");
            Check(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(8)) == "room-replaced" && observer.RequiresResynchronization && !observer.IsConnected,
                "live transport pauses with explicit room-replaced feedback: " + transport);
            Check(DocumentModel.Hash(pending.Snapshot) == acceptedBaseline && pending.PendingCount == 1 && relay.CommitRequests == 1,
                "replacement retains the disconnected client's accepted baseline and unresolved command without replay");
            var checkedOutcome = await remote.CheckAsync(attemptFile, seed.ownerCredential, CancellationToken.None);
            Check(checkedOutcome.found && RemoteRoomReplacement.ReadAttempt(attemptFile).confirmed && server.Room.Revision == replacement.revision,
                "read-only lost-reply Check finds the authoritative result without replacing again");
            Check(relay.ReplacementRequests == 1 && relay.CommitRequests == 1, "only read inspections occur after the single lost replacement send");
            using (var migrated = new CollaborationClient(relay.Url, seed.room, seed.catalog, CollaborationTransport.ShortPolling, recoveryFile: schema1Cache)) {
                await RejectAsync(() => migrated.JoinAsync("Old recovery"), "saved-room-does-not-match");
                Check(migrated.PendingCount == 3 && migrated.RequiresResynchronization, "schema1 recovery retains all old pending IDs before explicit resynchronization");
                await migrated.JoinAsync("Explicit recovery", acceptReplacement: true);
                Check(migrated.Participant.id == identity && migrated.PendingCount == 0 && migrated.RecoveryMessages.Any(m => m.Contains("Recovered accepted")) &&
                    migrated.RecoveryMessages.Any(m => m == "invalid-position") && migrated.RecoveryMessages.Any(m => m == "operation-not-accepted-before-replacement") &&
                    JsonNode.Parse(File.ReadAllText(schema1Cache))["schema"].GetValue<int>() == 2, "explicit schema1 upgrade honestly resolves accepted/rejected/absent old outcomes without replay");
            }
            await observer.PrepareReconnectAsync();
            await RejectAsync(() => observer.JoinAsync("Ordinary reconnect"), "saved-room-does-not-match");
            await observer.JoinAsync("Explicit observer resync", acceptReplacement: true);
            Check(observer.Snapshot.generation == replacement.generation && !observer.RequiresResynchronization && observer.IsConnected,
                "the selected transport explicitly resumes the same generation transition: " + transport);
            relay.ContinueAssetDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); relay.AssetPauseAfterBytes = 16;
            using (var cancel = new CancellationTokenSource()) {
                var resync = pending.JoinAsync("Canceled replacement resync", cancellation: cancel.Token, acceptReplacement: true);
                await relay.PartialAssetSent.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Canceled(resync);
            }
            Check(DocumentModel.Hash(pending.Snapshot) == acceptedBaseline && pending.PendingCount == 1 &&
                JsonNode.Parse(File.ReadAllText(cache))["pendingGeneration"].GetValue<string>() == oldGeneration &&
                JsonNode.Parse(File.ReadAllText(cache))["resynchronizationRequired"].GetValue<bool>(),
                "partial hydration cancellation keeps the old accepted snapshot and original-generation pending identity");
            relay.ContinueAssetDownload.TrySetResult(true); relay.ContinueAssetDownload = null; relay.AssetPauseAfterBytes = 0;
            await RejectAsync(() => pending.JoinAsync("A canceled transition still needs explicit confirmation"), "saved-room-does-not-match");
            using (var cancel = new CancellationTokenSource()) {
                var progress = new ReplacementProgress { OnReport = value => { if (value.Stage == ConnectionStage.RecoveringEdits) cancel.Cancel(); } };
                await Canceled(pending.JoinAsync("Canceled outcome recovery", cancellation: cancel.Token, progress: progress, acceptReplacement: true));
            }
            Check(pending.PendingCount == 1 && DocumentModel.Hash(pending.Snapshot) == acceptedBaseline && File.ReadAllText(cache).Contains(oldCommand.operationId),
                "canceled receipt recovery cannot remove pending IDs or publish the candidate scene");
            await pending.JoinAsync("Final explicit resync", acceptReplacement: true);
            Check(pending.IsConnected && pending.PendingCount == 0 && pending.Participant.id == identity &&
                pending.Snapshot.generation == replacement.generation && !pending.Snapshot.parts.Any(p => p.id == "old-accepted") &&
                GeometryTransfer.HasGeometry(pending.Snapshot.assets[0]) && relay.CommitRequests == 1 && server.Room.Revision == replacement.revision &&
                pending.RecoveryMessages.Single().Contains("superseded"), "successful explicit resync hydrates current content and reports the superseded old acceptance without replay");
            Check((await HostingRequest(server.Url, "/capabilities")).code == 200 &&
                WireJson.Decode<Capabilities>((await HostingRequest(server.Url, "/capabilities")).bytes).generation == replacement.generation,
                "single-room root capabilities follow the active generation");
        }
        await ReplacementAuthorityChecks(directory);
        await ReplacementRouteChecks(directory);
    }
    private static async Task ReplacementRouteChecks(string directory) {
        var seed = HostedSeed("replacement-route");
        await using var server = new CollaborationServer(seed, dataDirectory: Path.Combine(directory, "replacement-route")); await server.StartAsync();
        using var proxy = new OutboundProxyFixture(server.Url, "proxy-user", "private-proxy-password");
        using var remote = new RemoteRoomReplacement(server.Url, network: ProxyPolicy(proxy));
        var target = await remote.InspectAsync(seed.room, seed.ownerCredential, CancellationToken.None);
        string file = Path.Combine(directory, "proxied-replacement.json");
        var accepted = await remote.ReplaceAsync(CreationContent(), target, seed.ownerCredential, file, CancellationToken.None);
        Check((await remote.CheckAsync(file, seed.ownerCredential, CancellationToken.None)).found && proxy.Authenticated > 0 &&
            proxy.Paths.Any(p => p.EndsWith("/replacement")) && proxy.Paths.Any(p => p.Contains("/replacement-result?")) &&
            !proxy.ProxyCredentialInOrigin && !proxy.OriginCredentialInConnect && server.Room.Revision == accepted.revision,
            "replacement inspection/single mutation/outcome reads use the selected Basic proxy without mixing origin/proxy credentials");
        proxy.RejectAll = true; string refusedFile = Path.Combine(directory, "refused-proxy-replacement.json");
        await RejectAsync(() => remote.ReplaceAsync(CreationContent(), target, seed.ownerCredential, refusedFile, CancellationToken.None), "proxy-authentication-required");
        Check(!File.Exists(refusedFile) && server.Room.Revision == accepted.revision, "proxy refusal before inspection prepares no attempt and changes no online content");
        proxy.RejectAll = false;
        var current = await remote.InspectAsync(seed.room, seed.ownerCredential, CancellationToken.None);
        string profile = Path.Combine(directory, "bounded-replacement-profile"); RecoveryFiles.CreateDirectory(profile);
        using (var held = new FileStream(Path.Combine(profile, "profile.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            await RejectAsync(() => remote.ReplaceInProfileAsync(CreationContent(), current, seed.ownerCredential, profile, CancellationToken.None), "replacement-record-in-use");
            Check(!Directory.EnumerateFiles(profile, "*.json").Any(), "profile ownership lock refuses a second preparation without duplicating an attempt");
        }
        for (int i = 0; i < RemoteRoomReplacement.MaxRecords; i++) File.Copy(file, Path.Combine(profile, i + ".json"));
        await RejectAsync(() => remote.ReplaceInProfileAsync(CreationContent(), current, seed.ownerCredential, profile, CancellationToken.None), "replacement-record-limit");
        Check(Directory.EnumerateFiles(profile, "*.json").Count() == 64 && server.Room.Revision == accepted.revision,
            "the exact private-profile attempt bound refuses another mutation without dropping previous outcomes");
        using var tls = new UntrustedTlsFixture();
        using var connect = new OutboundProxyFixture(tls.Url, "proxy-user", "private-proxy-password");
        foreach (var route in new[] { NetworkPolicy.Direct, ProxyPolicy(connect) }) {
            using var untrusted = new RemoteRoomReplacement(tls.Url, network: route); string unsafeFile = Path.Combine(directory, "untrusted-replacement.json");
            try { await untrusted.ReplaceAsync(CreationContent(), current, seed.ownerCredential, unsafeFile, CancellationToken.None); throw new Exception("Untrusted replacement TLS was accepted"); }
            catch (Exception error) when (WireFailure.Code(error) == "tls-trust-or-name-failed") {
                Check(!File.Exists(unsafeFile), "direct/CONNECT replacement retains production certificate trust before preparing a private mutation");
            }
        }
        Check(connect.Connects > 0 && !connect.OriginCredentialInConnect && !connect.ProxyCredentialInOrigin,
            "untrusted replacement CONNECT reaches only the selected proxy and exposes no owner credential in its tunnel setup");
    }
    private static async Task ReplacementAuthorityChecks(string directory) {
        string root = Path.Combine(directory, "replacement-authority"); RoomRegistry.Initialize(root, "key"); string config = Path.Combine(root, "server.json");
        var admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(root, "admin.json"));
        var creator = RoomRegistry.Read<RoomCreationKey>(Path.Combine(root, "creator.json")); var seed = HostedSeed("owned-target");
        await using var server = new CollaborationServer(null, multiRoomConfig: config); await server.StartAsync(); server.Registry.Create(seed);
        foreach (string authority in new[] { admin.credential, creator.credential }) {
            var refused = await HostingRequest(server.Url, "/rooms/owned-target/replacement", authority, HttpMethod.Post, new byte[] { 0 });
            Check(refused.code != 200 && server.Registry.Resolve(seed.room).Snapshot.generation == seed.document.generation,
                "master-admin/creator authority never becomes replacement owner authority, even before body parsing");
        }
        using var remote = new RemoteRoomReplacement(server.Url);
        var target = await remote.InspectAsync(seed.room, seed.ownerCredential, CancellationToken.None);
        await Canceled(remote.ReplaceAsync(CreationContent(), target, seed.ownerCredential,
            Path.Combine(directory, "wrong-target.json"), new CancellationToken(true)));
        var ownerRecord = new CreatedRoom { origin = server.Url, serverId = admin.serverId, room = seed.room, generation = seed.document.generation,
            catalogHash = seed.catalog.hash, ownerCredential = seed.ownerCredential, confirmed = true };
        await remote.ReplaceAsync(CreationContent(), target, seed.ownerCredential, Path.Combine(directory, "owner-confirmed.json"), CancellationToken.None);
        using var creation = new RemoteRoomCreation(server.Url);
        await creation.CheckAsync(ownerRecord, CancellationToken.None);
        Check(ownerRecord.generation == seed.document.generation && ownerRecord.ownerCredential == seed.ownerCredential,
            "private creation ownership follows its stable room anchor without rewriting original identity or credentials");
        await using var relay = new LoopbackProxy(server.Url) { ContinueReplacementReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        await relay.StartAsync(); using var cancellable = new RemoteRoomReplacement(relay.Url);
        var current = await cancellable.InspectAsync(seed.room, seed.ownerCredential, CancellationToken.None);
        string canceledFile = Path.Combine(directory, "canceled-replacement.json");
        using (var cancel = new CancellationTokenSource()) {
            var replacing = cancellable.ReplaceAsync(CreationContent(), current, seed.ownerCredential, canceledFile, cancel.Token);
            await relay.ReplacementReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Canceled(replacing);
        }
        relay.ContinueReplacementReply.TrySetResult(true); relay.ContinueReplacementReply = null;
        Check(!RemoteRoomReplacement.ReadAttempt(canceledFile).confirmed &&
            (await cancellable.CheckAsync(canceledFile, seed.ownerCredential, CancellationToken.None)).found && relay.ReplacementRequests == 1,
            "canceled sent replacement retains its ambiguous identity and checks its accepted result without another send");
        var untouched = await remote.InspectAsync(seed.room, seed.ownerCredential, CancellationToken.None);
        string uncertainFile = Path.Combine(directory, "not-yet-observed.json");
        RoomRegistry.WriteNew(uncertainFile, new RoomReplacementAttempt { origin = server.Url, target = untouched, operationId = Guid.NewGuid().ToString("N"),
            contentHash = DocumentModel.Hash(CreationContent().document) });
        Check(!(await remote.CheckAsync(uncertainFile, seed.ownerCredential, CancellationToken.None)).found && !RemoteRoomReplacement.ReadAttempt(uncertainFile).notAccepted,
            "an unchanged target and missing replacement outcome remain uncertain because a delayed send may still arrive");
        var actor = server.Registry.Resolve(seed.room).Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Owner", ownerCredential = seed.ownerCredential });
        Check(server.Registry.Resolve(seed.room).Commit(actor.token, Tx(server.Registry.Resolve(seed.room).Snapshot, "AddParts", new[] { Cube("fences-late-replacement") })).accepted,
            "intervening edit fences the uncertain old precondition");
        Check(!(await remote.CheckAsync(uncertainFile, seed.ownerCredential, CancellationToken.None)).found && RemoteRoomReplacement.ReadAttempt(uncertainFile).notAccepted,
            "only a changed authoritative precondition proves an absent replacement can never publish later");
        await ReplacementCliChecks(root, server.Url, seed);
    }
    private static async Task ReplacementCliChecks(string root, string origin, Bootstrap seed) {
        string owner = Path.Combine(root, "rooms", seed.room, "owner.json"), target = Path.Combine(root, "cli-target.json"),
            snapshot = Path.Combine(root, "cli-portable.json"), attempt = Path.Combine(root, "cli-attempt.json");
        RecoveryFiles.Write(snapshot, CreationContent());
        var inspect = await RunCli("room", "replacement-target", "--url", origin, "--owner-file", owner, "--output", target);
        Check(inspect.code == 0 && File.Exists(target), "actual CLI obtains an owner-authenticated explicit replacement precondition");
        await using (var alias = new LoopbackProxy(origin)) {
            await alias.StartAsync(); string aliasAttempt = Path.Combine(root, "wrong-origin-attempt.json");
            var changedOrigin = await RunCli("room", "replace", "--url", alias.Url, "--owner-file", owner, "--target", target, "--snapshot", snapshot,
                "--attempt-output", aliasAttempt, "--confirm-room", seed.room);
            Check(changedOrigin.code == 1 && changedOrigin.error.Contains("replacement-target-origin-mismatch") && !File.Exists(aliasAttempt) && alias.Requests.Count == 0,
                "CLI preconditions retain their original origin even if another address serves an identical room identity");
        }
        var mismatch = await RunCli("room", "replace", "--url", origin, "--owner-file", owner, "--target", target, "--snapshot", snapshot,
            "--attempt-output", attempt, "--confirm-room", "different-room");
        Check(mismatch.code == 1 && !File.Exists(attempt), "CLI requires confirmation of the exact owner-file room before preparing a mutation");
        var replaced = await RunCli("room", "replace", "--url", origin, "--owner-file", owner, "--target", target, "--snapshot", snapshot,
            "--attempt-output", attempt, "--confirm-room", seed.room);
        Check(replaced.code == 0 && RemoteRoomReplacement.ReadAttempt(attempt).confirmed && !replaced.output.Contains(seed.ownerCredential),
            "actual CLI sends one portable replacement and retains a private accepted outcome without credential output");
        byte[] retained = File.ReadAllBytes(attempt);
        var duplicate = await RunCli("room", "replace", "--url", origin, "--owner-file", owner, "--target", target, "--snapshot", snapshot,
            "--attempt-output", attempt, "--confirm-room", seed.room);
        Check(duplicate.code == 1 && retained.SequenceEqual(File.ReadAllBytes(attempt)), "CLI never overwrites an existing attempt or resends it");
        string staleAttempt = Path.Combine(root, "stale-attempt.json");
        var stale = await RunCli("room", "replace", "--url", origin, "--owner-file", owner, "--target", target, "--snapshot", snapshot,
            "--attempt-output", staleAttempt, "--confirm-room", seed.room);
        Check(stale.code == 1 && stale.error.Contains("replacement-target-changed") && !File.Exists(staleAttempt), "CLI stale-target refusal precedes local preparation and publication");
        var checkedResult = await RunCli("room", "replacement-check", "--url", origin, "--owner-file", owner, "--attempt", attempt);
        Check(checkedResult.code == 0 && WireJson.Decode<RoomReplacementOutcome>(Encoding.UTF8.GetBytes(checkedResult.output)).found,
            "actual CLI read-only Check recovers its exact retained result");
        var notOwner = await RunCli("room", "replacement-target", "--url", origin, "--owner-file", Path.Combine(root, "admin.json"),
            "--output", Path.Combine(root, "admin-must-not-own.json"));
        Check(notOwner.code == 1 && !File.Exists(Path.Combine(root, "admin-must-not-own.json")), "CLI does not reinterpret an administrator file as room ownership");
    }
}
