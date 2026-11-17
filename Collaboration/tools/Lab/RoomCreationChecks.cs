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
    private static RoomExport CreationContent() {
        var seed = HostedSeed("portable"); var asset = Asset("Created room custom geometry");
        seed.document.assets = new[] { asset }; seed.document.parts = new[] { Custom("created-custom", asset), Cube("created-cube", 6) };
        foreach (var part in seed.document.parts) part.version = 7;
        seed.document.chains = new[] { new ChainRecord { id = "created-chain", version = 7, routeVersion = 1,
            members = new[] { "created-custom", "created-cube" }, wraps = new[] { 0, 1 } } };
        seed.document.revision = 12;
        return new RoomExport { catalog = seed.catalog, document = seed.document };
    }
    private static async Task RoomCreationChecks(string directory) {
        var content = CreationContent(); content.Validate(); string accepted = DocumentModel.Hash(content.document);
        foreach (string policy in new[] { "admin-only", "key", "open" }) {
            string root = Path.Combine(directory, "creation-" + policy), records = Path.Combine(directory, "owners-" + policy);
            Check((await RunCli("server-init", "--data-dir", root, "--creation-policy", policy)).code == 0, "actual CLI selects explicit creation policy: " + policy);
            string config = Path.Combine(root, "server.json");
            var admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(root, "admin.json"));
            string key = policy == "key" ? RoomRegistry.Read<RoomCreationKey>(Path.Combine(root, "creator.json")).credential : null;
            string authority = policy == "admin-only" ? admin.credential : key;
            CreatedRoom created;
            await using (var server = new CollaborationServer(null, multiRoomConfig: config)) {
                await server.StartAsync();
                using var remote = new RemoteRoomCreation(server.Url, network: NetworkPolicy.Direct);
                var caps = await remote.InspectAsync(CancellationToken.None);
                Check(caps.roomCreationVersion == 1 && caps.roomCreationPolicy == policy && caps.serverId == admin.serverId, "policy capability is explicit, versioned and scoped");
                if (policy == "admin-only") await RejectAsync(() => remote.CreateAsync(content, "not-admin", "", null, false, records, CancellationToken.None), "server-admin-required");
                else {
                    Check((await HostingRequest(server.Url, "/admin/rooms", key)).code != 200 && (await HostingRequest(server.Url, "/admin/diagnostics", key)).code != 200,
                        "ordinary creator cannot list rooms or inspect process diagnostics: " + policy);
                    Check((await HostingRequest(server.Url, "/admin/rooms", key, HttpMethod.Post, LocalJson.Encode(HostedSeed("no-admin")))).code != 200,
                        "creator cannot use protected admin creation");
                }
                if (key != null) {
                    var missing = await HostingRequest(server.Url, "/rooms", method: HttpMethod.Post, body: new byte[] { 0 });
                    Check(missing.code == 403 && WireJson.Decode<WireError>(missing.bytes).error == "room-creation-key-required" && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging")).Any(),
                        "key authorization precedes body parsing and staging");
                    await RejectAsync(() => remote.CreateAsync(content, "created", "", RoomRegistry.Hash("wrong"), false, records, CancellationToken.None), "room-creation-key-required");
                    Check(CreatedRoomRecovery.List(records).Single().authorizationRefused, "definitive authorization refusal permits a new explicit confirmation with corrected key");
                }
                created = await remote.CreateAsync(content, "created", "private room password", authority, policy == "admin-only", records, CancellationToken.None);
                Check(created.confirmed && created.ownerCredential != authority && created.ownerCredential != admin.credential &&
                    CreatedRoomRecovery.List(records).Single(r => r.room == "created").ownerCredential == created.ownerCredential, "private fresh ownership is durably retained before publication");
                var room = server.Registry.Resolve(created.room);
                Check(room.Revision == 0 && room.Snapshot.generation != content.document.generation && room.Snapshot.parts.All(p => p.version == 1) &&
                    room.Snapshot.parts.Single(p => p.id == "created-cube").position[0] == 6 && room.Memberships(created.ownerCredential).memberships.Length == 0,
                    "creation normalizes accepted content into a fresh history without joining or exposing local paths");
                Check(room.Snapshot.chains.Single().version == 1 && room.Snapshot.chains.Single().wraps.SequenceEqual(content.document.chains.Single().wraps) &&
                    room.Snapshot.parts.Single(p => p.id == "created-custom").mounts.Single().threaded, "fresh-room creation preserves explicit chain routes and ordered custom mounts");
                foreach (string secret in new[] { authority, admin.credential }.Where(x => x != null)) {
                    Check((await HostingRequest(server.Url, "/rooms/created/diagnostics", secret)).code != 200, "server credentials grant no owner inspection");
                    Reject(() => room.Join(new JoinRequest { name = "Wrong authority", catalogHash = content.catalog.hash, ownerCredential = secret }), "wrong-password");
                }
                Check((await HostingRequest(server.Url, "/admin/rooms", created.ownerCredential)).code == 403 && (await HostingRequest(server.Url, "/rooms")).code != 200,
                    "owner has no server admin and creation exposes no directory");
                await remote.CheckAsync(created, CancellationToken.None);
                foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
                    using var owner = new CollaborationClient(server.Url, created.room, content.catalog, transport);
                    await owner.JoinAsync("Created owner", ownerCredential: created.ownerCredential);
                    Check(owner.Participant.role == "owner" && owner.Snapshot.assets.Single().vertices.SequenceEqual(content.document.assets[0].vertices), "created owner joins with hydrated custom content: " + transport);
                    await owner.LeaveAsync();
                }
                string hash = DocumentModel.Hash(room.Snapshot); byte[] retained = File.ReadAllBytes(CreatedRoomRecovery.FileName(records, server.Url, "created"));
                await RejectAsync(() => remote.CreateAsync(content, "created", "", authority, policy == "admin-only", records, CancellationToken.None), "created-room-record-exists");
                Check(retained.SequenceEqual(File.ReadAllBytes(CreatedRoomRecovery.FileName(records, server.Url, "created"))) && DocumentModel.Hash(room.Snapshot) == hash,
                    "duplicate attempt preserves original capability and room bytes without a resend");
                using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); await Canceled(remote.CreateAsync(content, "pre-canceled", "", authority, policy == "admin-only", records, cancel.Token)); }
                Check(!server.Registry.List().rooms.Any(r => r.room == "pre-canceled"), "pre-cancel performs no creation");
                var listed = await remote.ListAsync(admin.credential, CancellationToken.None);
                Check(listed.rooms.Single().room == "created" && listed.process != null && !WireJson.Text(listed).Contains(created.ownerCredential), "admin inspection returns bounded status/resources without owner credentials");
                var alias = HostedSeed("alias"); alias.ownerCredential = authority ?? admin.credential;
                Reject(() => server.Registry.Create(alias), "duplicate-room-identity");
                var malformed = HostedSeed("bad-catalog"); malformed.catalog.entries = Enumerable.Repeat(malformed.catalog.entries[0], 1001).ToArray();
                var invalid = await HostingRequest(server.Url, "/rooms", authority, HttpMethod.Post, LocalJson.Encode(malformed));
                Check(invalid.code == 409 && WireJson.Decode<WireError>(invalid.bytes).error == "invalid-catalog" && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging")).Any(), "remote seeds reuse bounded portable-export validation before staging");
                malformed = HostedSeed("null-catalog"); malformed.catalog.entries = null;
                invalid = await HostingRequest(server.Url, "/rooms", authority, HttpMethod.Post, LocalJson.Encode(malformed));
                Check(invalid.code == 409 && WireJson.Decode<WireError>(invalid.bytes).error == "invalid-catalog", "malformed public catalog tables refuse with a fixed code before document traversal");
                Check((await RunCli("server-policy", "--config", config, "--admin-file", Path.Combine(root, "admin.json"), "--creation-policy", "open")).code != 0,
                    "policy changes refuse a live registry");
                if (policy == "key") {
                    await CreationCliChecks(directory, root, server.Url, content, key);
                    using var proxy = new OutboundProxyFixture(server.Url, "proxy-user", "private-proxy-password");
                    using var proxied = new RemoteRoomCreation(server.Url, network: ProxyPolicy(proxy));
                    string proxyRecords = Path.Combine(directory, "proxied-created-owners");
                    var throughProxy = await proxied.CreateAsync(content, "proxied-created", "", key, false, proxyRecords, CancellationToken.None);
                    await proxied.CheckAsync(throughProxy, CancellationToken.None);
                    Check((await proxied.ListAsync(admin.credential, CancellationToken.None)).rooms.Any(r => r.room == throughProxy.room) &&
                        proxy.Paths.Contains("POST /rooms") && proxy.Paths.Contains("GET /admin/rooms") && proxy.Paths.Contains("GET /rooms/proxied-created/diagnostics") &&
                        !proxy.ProxyCredentialInOrigin, "creation, private owner Check and admin inspection use the selected Basic proxy without forwarding proxy credentials to origin");
                    proxy.RejectAll = true; proxy.ReflectedError = "reflected-private-creation-key";
                    await RejectAsync(() => proxied.CreateAsync(content, "proxy-refused-creation", "", key, false, Path.Combine(directory, "proxy-refused-owners"), CancellationToken.None), "proxy-authentication-required");
                    Check(!Directory.Exists(Path.Combine(directory, "proxy-refused-owners")), "proxy auth rejection precedes owner preparation and never reflects the private body");
                }
                if (policy == "open") {
                    var noPassword = await remote.CreateAsync(content, "password-free", "", null, false, records, CancellationToken.None);
                    using var viewer = new CollaborationClient(server.Url, noPassword.room, content.catalog, CollaborationTransport.ShortPolling);
                    await viewer.JoinAsync("Password-free viewer", viewer: true);
                    Check(viewer.Participant.role == "viewer", "open creation and blank joining password are separate explicit choices"); await viewer.LeaveAsync();
                }
                for (int i = server.Registry.List().rooms.Length; i < 8; i++) server.Registry.Create(HostedSeed("fill-" + i));
                var full = await HostingRequest(server.Url, "/rooms", authority, HttpMethod.Post, LocalJson.Encode(HostedSeed("excess")));
                Check(full.code == 409 && WireJson.Decode<WireError>(full.bytes).error == "server-room-limit" && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging")).Any(), "all creation modes keep the eight-room / staging bound");
            }
            using (var recovered = new RoomRegistry(config)) {
                Check(recovered.CreationPolicy == policy && recovered.Resolve(created.room).Snapshot.generation == created.generation, "policy and ownership survive restart");
            }
            Check((await RunCli("server-policy", "--config", config, "--admin-file", Path.Combine(root, "admin.json"), "--creation-policy", "key", "--creator-output", Path.Combine(directory, "rotated-" + policy + ".json"))).code == 0,
                "actual stopped-server policy command rotates a private creation key");
            using (var rotated = new RoomRegistry(config)) {
                Check(rotated.CreationPolicy == "key" && rotated.Resolve("created").Snapshot.generation == created.generation, "offline policy rotation preserves registry identity and rooms");
                if (key != null) Reject(() => rotated.AuthorizeCreation(key), "room-creation-key-required");
            }
        }
        Check(DocumentModel.Hash(content.document) == accepted, "every success/refusal/cancel leaves caller's accepted export intact");
        await AmbiguousCreationChecks(directory, content);
        string original = Environment.GetEnvironmentVariable("PROTOBOT_TEST_CREATION_LEGACY");
        if (!string.IsNullOrEmpty(original)) await OriginalCreationCompatibility(directory, original, content);
    }
    private static async Task CreationCliChecks(string directory, string root, string url, RoomExport content, string key) {
        string export = Path.Combine(directory, "creation-cli-export.json"), owner = Path.Combine(directory, "creation-cli-owner.json"); RecoveryFiles.Write(export, content);
        var result = await RunCli("room", "create", "--url", url, "--creation-key-file", Path.Combine(root, "creator.json"), "--room", "cli-created", "--snapshot", export, "--owner-output", owner);
        Check(result.code == 0 && File.Exists(owner) && !result.output.Contains(key) && !result.error.Contains(key), "actual delegated CLI creates and retains owner without logging creator secret");
        var wrongFormat = await RunCli("room", "create", "--url", url, "--creation-key-file", Path.Combine(root, "admin.json"), "--room", "wrong-credential-format", "--snapshot", export, "--owner-output", Path.Combine(directory, "wrong-format-owner.json"));
        Check(wrongFormat.code != 0 && !File.Exists(Path.Combine(directory, "wrong-format-owner.json")), "an admin credential file cannot masquerade as the explicit versioned creator-file format");
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(owner);
        Check((mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "creator and owner files are private on Unix");
        string broad = Path.Combine(directory, "broad-creator.json"); File.Copy(Path.Combine(root, "creator.json"), broad); File.SetUnixFileMode(broad, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var refused = await RunCli("room", "create", "--url", url, "--creation-key-file", broad, "--room", "broad-refused", "--snapshot", export, "--owner-output", Path.Combine(directory, "not-written.json"));
        Check(refused.code != 0 && !File.Exists(Path.Combine(directory, "not-written.json")) && !refused.error.Contains(key), "delegated CLI refuses broad credential input before sending or retaining ownership");
    }
    private static async Task AmbiguousCreationChecks(string directory, RoomExport content) {
        string root = Path.Combine(directory, "ambiguous-creation"); RoomRegistry.Initialize(root, "open");
        await using var server = new CollaborationServer(null, multiRoomConfig: Path.Combine(root, "server.json")); await server.StartAsync();
        await using var relay = new LoopbackProxy(server.Url); await relay.StartAsync();
        using var remote = new RemoteRoomCreation(relay.Url, network: NetworkPolicy.Direct);
        string records = Path.Combine(directory, "ambiguous-owners");
        relay.DropNextCreationResponse = true;
        try { await remote.CreateAsync(content, "lost", "", null, false, records, CancellationToken.None); throw new Exception("Expected lost reply"); } catch (HttpRequestException) { }
        var lost = CreatedRoomRecovery.List(records).Single();
        Check(!lost.confirmed && relay.Requests["/rooms"] == 1 && server.Registry.Resolve("lost").Name == "lost", "lost reply publishes exactly once and keeps prepared private ownership");
        await remote.CheckAsync(lost, CancellationToken.None);
        Check(relay.Requests["/rooms"] == 1 && server.Registry.Resolve("lost").Memberships(lost.ownerCredential).memberships.Length == 0, "explicit check authenticates ownership with read-only requests, no resending or membership");
        await RejectAsync(() => remote.CreateAsync(content, "lost", "", null, false, records, CancellationToken.None), "created-room-record-exists");
        relay.ContinueCreationReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var canceled = remote.CreateAsync(content, "cancel-reply", "", null, false, records, cancel.Token);
        await Until(() => server.Registry.List().rooms.Any(r => r.room == "cancel-reply")); cancel.Cancel(); await Canceled(canceled);
        relay.ContinueCreationReply.TrySetResult(true); relay.ContinueCreationReply = null;
        var record = CreatedRoomRecovery.List(records).Single(r => r.room == "cancel-reply");
        await remote.CheckAsync(record, CancellationToken.None);
        Check(!record.confirmed && relay.Requests["/rooms"] == 2, "canceled published creation keeps capability for explicit recovery without replay");
        var changed = WireJson.Clone(record); changed.serverId = "other-server";
        await RejectAsync(() => remote.CheckAsync(changed, CancellationToken.None), "created-room-mismatch");
        changed = WireJson.Clone(record); changed.generation = "other-generation";
        await RejectAsync(() => remote.CheckAsync(changed, CancellationToken.None), "created-room-mismatch");
        await RejectAsync(() => remote.CreateAsync(content, "concurrent-correction", "", RoomRegistry.Hash("wrong-open-authority"), false, records, CancellationToken.None), "room-creation-key-required");
        relay.ContinueCreationReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancelCorrection = new CancellationTokenSource()) {
            var correction = remote.CreateAsync(content, "concurrent-correction", "", null, false, records, cancelCorrection.Token);
            await Until(() => server.Registry.List().rooms.Any(r => r.room == "concurrent-correction"));
            await RejectAsync(() => remote.CreateAsync(content, "concurrent-correction", "", null, false, records, CancellationToken.None), "created-room-record-in-use");
            cancelCorrection.Cancel(); await Canceled(correction);
        }
        relay.ContinueCreationReply.TrySetResult(true); relay.ContinueCreationReply = null;
        await remote.CheckAsync(CreatedRoomRecovery.List(records).Single(r => r.room == "concurrent-correction"), CancellationToken.None);
        Check(relay.Requests["/rooms"] == 4, "concurrent refused-key correction cannot replace in-flight ownership or send a second mutation");
        relay.FaultPathSuffix = "/rooms"; relay.FaultCode = "server-request-limit"; relay.FaultRemaining = 100;
        relay.FaultBody = "{\"error\":\"reflected-private-creation-secret\"}";
        await RejectAsync(() => remote.CreateAsync(content, "malicious", "", null, false, records, CancellationToken.None), "server-rejected-request");
        Check(relay.Requests["/rooms"] == 5 && !server.Registry.List().rooms.Any(r => r.room == "malicious"), "mutating busy/malicious responses are single-send and project fixed codes");
        relay.FaultRemaining = 0;
        string fullRecords = Path.Combine(directory, "full-local-owners"); RecoveryFiles.CreateDirectory(fullRecords);
        for (int i = 0; i < CreatedRoomRecovery.MaxRecords; i++) {
            var retained = WireJson.Clone(lost); retained.room = "retained-" + i;
            RecoveryFiles.Write(CreatedRoomRecovery.FileName(fullRecords, retained.origin, retained.room), retained, CreatedRoomRecovery.MaxBytes);
        }
        await RejectAsync(() => remote.CreateAsync(content, "over-local-limit", "", null, false, fullRecords, CancellationToken.None), "created-room-record-limit");
        Check(relay.Requests["/rooms"] == 5 && CreatedRoomRecovery.List(fullRecords).Length == 32, "private ownership-record exhaustion refuses before another mutation and retains all existing records");
        string corrupt = Path.Combine(directory, "corrupt-local-owner"), badFile = CreatedRoomRecovery.FileName(corrupt, relay.Url, "corrupt");
        RecoveryFiles.Write(badFile, new WireError { error = "private-corrupt-preparation" }); byte[] badBytes = File.ReadAllBytes(badFile);
        await RejectAsync(() => remote.CreateAsync(content, "corrupt", "", null, false, corrupt, CancellationToken.None), "invalid-created-room-record");
        Check(relay.Requests["/rooms"] == 5 && File.ReadAllBytes(badFile).SequenceEqual(badBytes), "unreadable preparation is preserved without replacement or a mutation send");
    }
    private static async Task OriginalCreationCompatibility(string directory, string helper, RoomExport content) {
        string root = Path.Combine(directory, "original-creation");
        async Task<int> Old(params string[] args) {
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); await output; await errors; return process.ExitCode;
        }
        Check(await Old("server-init", "--data-dir", root) == 0, "original pre-creation helper initializes its own genuine registry");
        Check(await Old("server-init", "--data-dir", root + "-unsupported", "--creation-policy", "open") != 0 && !Directory.Exists(root + "-unsupported"), "original CLI refuses new policy option without initializing");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "serve", "--multi-room", "--config", Path.Combine(root, "server.json"), "--listen", "http://127.0.0.1:0" }) start.ArgumentList.Add(arg);
        using var server = Process.Start(start); var stderr = server.StandardError.ReadToEndAsync();
        try {
            var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))));
            using var remote = new RemoteRoomCreation(ready.url, network: NetworkPolicy.Direct);
            string records = Path.Combine(directory, "old-no-ownership");
            await RejectAsync(() => remote.CreateAsync(content, "unsupported", "", null, false, records, CancellationToken.None), "room-creation-unavailable");
            Check(!Directory.Exists(records) && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "rooms")).Any(), "real original helper refuses new capability before ownership preparation or remote publication");
            var admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(root, "admin.json"));
            Check((await remote.ListAsync(admin.credential, CancellationToken.None)).rooms.Length == 0, "new admin view retains existing room-list compatibility on the original helper");
        }
        finally { if (!server.HasExited) { server.Kill(); await server.WaitForExitAsync(); } await stderr; }
    }
}
