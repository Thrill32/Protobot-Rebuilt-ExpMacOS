using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task ShutdownChecks(string directory) {
        string root = Path.Combine(directory, "shutdown registry"), config = Path.Combine(root, "server.json");
        RoomRegistry.Initialize(root);
        Console.WriteLine("Shutdown: constructing registry host");
        var a = HostedSeed("shutdown-a"); var b = HostedSeed("shutdown-b"); var late = HostedSeed("shutdown-late");
        var server = new CollaborationServer(null, multiRoomConfig: config);
        Console.WriteLine("Shutdown: creating fixture rooms");
        server.Registry.Create(a); server.Registry.Create(b); await server.StartAsync();
        Console.WriteLine("Shutdown: connecting participants");
        string origin = server.Url;
        var transports = new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling };
        var seeds = new[] { a, a, a, b, b, b };
        string[] caches = Enumerable.Range(0, seeds.Length).Select(i => Path.Combine(directory, "shutdown-member-" + i + ".json")).ToArray();
        var clients = seeds.Select((seed, i) => new CollaborationClient(origin, seed.room, seed.catalog, transports[i % 3], recoveryFile: caches[i])).ToArray();
        var notices = clients.Select(_ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (int i = 0; i < clients.Length; i++) { int slot = i; clients[i].Disconnected += reason => notices[slot].TrySetResult(reason); }
        await using var independent = new CollaborationServer(Fixture()); await independent.StartAsync();
        using var unrelated = new CollaborationClient(independent.Url, "team", Fixture().catalog, CollaborationTransport.LongPolling);
        await unrelated.JoinAsync("Independent host");
        await using var relay = new LoopbackProxy(origin); await relay.StartAsync();
        string pendingCache = Path.Combine(directory, "shutdown-pending.json");
        using var pending = new CollaborationClient(relay.Url, a.room, a.catalog, CollaborationTransport.LongPolling, recoveryFile: pendingCache);
        using var slowSocket = new ClientWebSocket();
        using var publishReached = new ManualResetEventSlim(); using var publishRelease = new ManualResetEventSlim();
        Task publication = null; Task stopped = null;
        try {
            for (int i = 0; i < clients.Length; i++) await clients[i].JoinAsync("Member " + i, seeds[i].password);
            await Until(() => clients.All(c => c.ActiveTransport != null));
            var asset = Asset("Survives deliberate stop");
            Check((await clients[0].SubmitAsync(clients[0].CreateTransaction("AddParts", new[] { Custom("saved-custom", asset) }, assetWrites: new[] { asset }))).accepted,
                "shutdown fixture accepts durable geometry and history");
            Check((await clients[3].SubmitAsync(clients[3].CreateTransaction("AddParts", new[] { Cube("other-room", 31) }))).accepted,
                "shutdown fixture accepts independent room history");
            await pending.JoinAsync("Ambiguous outcome", a.password);
            relay.DropNextCommitResponse = true;
            var operation = pending.CreateTransaction("AddParts", new[] { Cube("pending-at-shutdown", 19) });
            try { await pending.SubmitAsync(operation); throw new Exception("Expected lost acknowledgment"); } catch (HttpRequestException) { }
            Check(pending.PendingCount == 1 && server.Registry.Resolve(a.room).Revision == 2, "lost reply is durable before shutdown");
            await Until(() => clients.Take(3).All(c => c.Snapshot.revision == 2) && clients.Skip(3).All(c => c.Snapshot.revision == 1));
            string[] identities = clients.Select(c => c.Participant.id).ToArray();
            string[] hashes = clients.Select(c => DocumentModel.Hash(c.Snapshot)).ToArray();
            byte[][] credentialBytes = caches.Select(File.ReadAllBytes).ToArray(); byte[] pendingBytes = File.ReadAllBytes(pendingCache);
            var room = server.Registry.Resolve(a.room);
            var admitted = room.Join(new JoinRequest { name = "Already admitted", catalogHash = a.catalog.hash, password = a.password });
            string hash = DocumentModel.Hash(room.Snapshot);
            var slowMember = room.Join(new JoinRequest { name = "Delayed close reader", catalogHash = a.catalog.hash, password = a.password });
            slowSocket.Options.SetRequestHeader("Authorization", "Bearer " + slowMember.token);
            await slowSocket.ConnectAsync(new UriBuilder(origin + "/rooms/" + a.room + "/events?after=" + room.Revision + "&transient=-1") { Scheme = "ws" }.Uri, CancellationToken.None);
            var slowBuffer = new byte[8192]; WebSocketReceiveResult first;
            do { first = await slowSocket.ReceiveAsync(new ArraySegment<byte>(slowBuffer), CancellationToken.None); } while (!first.EndOfMessage);
            server.Registry.BeforePublish = _ => { publishReached.Set(); if (!publishRelease.Wait(10000)) throw new TimeoutException("Publication fixture stalled"); };
            publication = Task.Run(() => {
                server.Registry.Creation.Wait();
                try {
                    server.Registry.Create(late);
                    Reject(() => server.Registry.Resolve(late.room).Join(new JoinRequest { name = "Late room", catalogHash = late.catalog.hash, password = late.password }), "server-stopping");
                }
                finally { server.Registry.Creation.Release(); }
            });
            Check(publishReached.Wait(10000), "atomic room creation reaches publication before stop");
            var timer = Stopwatch.StartNew(); stopped = Task.Run(async () => await server.DisposeAsync());
            var delayedClose = Task.Run(async () => {
                await Task.Delay(300); using var timeout = new CancellationTokenSource(3000);
                return await slowSocket.ReceiveAsync(new ArraySegment<byte>(slowBuffer), timeout.Token);
            });
            var response = await StoppingResponse(origin);
            Check(response.code == 503 && WireJson.Decode<WireError>(response.bytes).error == "server-stopping", "listener rejects new work with a fixed stopping reason while draining");
            await RejectAsync(async () => {
                using var socket = await (Task<WebSocket>)typeof(CollaborationClient).GetMethod("OpenEventSocketAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(clients[0], new object[] { CancellationToken.None });
            }, "server-stopping");
            using (var lateClient = new CollaborationClient(origin, a.room, a.catalog)) await RejectAsync(() => lateClient.JoinAsync("No new admission", a.password), "server-stopping");
            Reject(() => room.Commit(admitted.token, Tx(room.Snapshot, "AddParts", new[] { Cube("after-stop") })), "server-stopping");
            Reject(() => room.Join(new JoinRequest { name = "Too late", catalogHash = a.catalog.hash, password = a.password }), "server-stopping");
            Reject(() => room.Leave(admitted.token), "server-stopping");
            Check(DocumentModel.Hash(room.Snapshot) == hash, "shutdown fence does not mutate accepted content or revoke membership");
            Console.WriteLine("Shutdown: verifying transport notices");
            var reasons = await Task.WhenAll(notices.Select(n => n.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Check(reasons.All(r => r == "server-stopping"), "WebSocket, held and idle short polls receive deliberate shutdown in both rooms (" + string.Join(", ", reasons) + ")");
            var close = await delayedClose;
            Check(close.MessageType == WebSocketMessageType.Close && close.CloseStatus == WebSocketCloseStatus.EndpointUnavailable && close.CloseStatusDescription == "server-stopping",
                "a delayed WebSocket reader receives the close frame before transport abort");
            for (int i = 0; i < clients.Length; i++) {
                Check(!clients[i].IsConnected && !clients[i].IsMembershipRevoked && clients[i].Participant.id == identities[i] &&
                    DocumentModel.Hash(clients[i].Snapshot) == hashes[i] && File.ReadAllBytes(caches[i]).SequenceEqual(credentialBytes[i]),
                    "shutdown retains accepted snapshot and exact recovery credentials on " + transports[i % 3] + " room " + seeds[i].room);
                Check(clients[i].CaptureDiagnostics().events.Any(e => e.code == "server-stopping"), "diagnostics allowlists deliberate shutdown");
            }
            Check(pending.PendingCount == 1 && File.ReadAllBytes(pendingCache).SequenceEqual(pendingBytes) && relay.CommitRequests == 1,
                "shutdown does not resolve or replay an ambiguous command");
            Check((await unrelated.SubmitAsync(unrelated.CreateTransaction("AddParts", new[] { Cube("still-running") }))).accepted && unrelated.IsConnected,
                "stopping one listener leaves an independent host running");
            await Task.Delay(2000);
            Check(!stopped.IsCompleted, "registry teardown waits for the admitted publication after listener drain");
            Reject(() => new RoomRegistry(config), "server-data-in-use");
            publishRelease.Set(); await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await stopped.WaitAsync(TimeSpan.FromSeconds(7)); Check(timer.Elapsed < TimeSpan.FromSeconds(7), "notification and listener shutdown share a bounded deadline");
            await using var restarted = new CollaborationServer(null, listen: origin, multiRoomConfig: config); await restarted.StartAsync();
            Console.WriteLine("Shutdown: explicit recovery");
            for (int i = 0; i < clients.Length; i++) {
                await clients[i].PrepareReconnectAsync(); await clients[i].JoinAsync("Returned member");
                Check(clients[i].Participant.id == identities[i] && DocumentModel.Hash(clients[i].Snapshot) == hashes[i], "explicit restart/reconnect retains identity and accepted content");
            }
            string pendingIdentity = pending.Participant.id;
            await pending.PrepareReconnectAsync(); await pending.JoinAsync("Resolved member");
            Check(pending.Participant.id == pendingIdentity && pending.PendingCount == 0 && relay.CommitRequests == 1 && restarted.Registry.Resolve(a.room).Revision == 2,
                "explicit reconnect resolves the accepted receipt without command replay");
            Check((await pending.SubmitAsync(pending.CreateTransaction("Undo", Array.Empty<Part>()))).accepted &&
                restarted.Registry.Resolve(a.room).Snapshot.parts.Single().id == "saved-custom" && restarted.Registry.Resolve(b.room).Revision == 1,
                "personal history and geometry survive shutdown without changing the other room");
            var resumed = restarted.Registry.Resolve(a.room).Join(new JoinRequest { catalogHash = a.catalog.hash, resumeToken = admitted.resumeToken });
            Check(resumed.participant.id == admitted.participant.id, "shutdown itself never revokes even an inactive admission");
            Check(restarted.Registry.Resolve(late.room).Revision == 0, "in-flight atomic registration is recoverable after shutdown");
            foreach (var client in clients) await client.LeaveAsync(); await pending.LeaveAsync();
        }
        finally {
            publishRelease.Set();
            if (publication != null) { try { await publication; } catch { } }
            foreach (var client in clients) client.Dispose();
            if (stopped != null) await stopped; else await server.DisposeAsync();
        }
        await ShutdownDuringCommit(directory); await StalledShutdown(directory);
        Console.WriteLine("Shutdown: process controls");
        foreach (string mode in OperatingSystem.IsWindows() ? new[] { "eof", "line" } : new[] { "eof", "line", "sigterm", "sigint" }) await ShutdownProcess(directory, mode);
        string legacy = Environment.GetEnvironmentVariable("PROTOBOT_TEST_SHUTDOWN_LEGACY");
        if (legacy != null) await ShutdownProcess(directory, "legacy-eof", legacy);
    }

    private static async Task<(int code, byte[] bytes)> StoppingResponse(string origin) {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5)) {
            var response = await HostingRequest(origin, "/health"); if (response.code == 503) return response;
            await Task.Delay(10);
        }
        throw new TimeoutException("Server did not enter shutdown");
    }

    private static async Task StalledShutdown(string directory) {
        string data = Path.Combine(directory, "stalled shutdown"); var seed = Fixture();
        var server = new CollaborationServer(seed, dataDirectory: data); await server.StartAsync();
        var member = server.Room.Join(new JoinRequest { name = "Stalled uploader", catalogHash = seed.catalog.hash });
        using var socket = new TcpClient(); var endpoint = new Uri(server.Url); await socket.ConnectAsync(endpoint.Host, endpoint.Port);
        string hash = Asset().hash;
        byte[] request = Encoding.ASCII.GetBytes("PUT /rooms/team/assets/" + hash + " HTTP/1.1\r\nHost: " + endpoint.Authority + "\r\nAuthorization: Bearer " + member.token + "\r\nContent-Length: 100000\r\nExpect: 100-continue\r\n\r\n");
        await socket.GetStream().WriteAsync(request);
        var interim = new byte[1024]; using var timeout = new CancellationTokenSource(5000);
        int read = await socket.GetStream().ReadAsync(interim, timeout.Token);
        Check(Encoding.ASCII.GetString(interim, 0, read).StartsWith("HTTP/1.1 100", StringComparison.Ordinal), "stalled upload has entered authenticated body reading");
        var timer = Stopwatch.StartNew(); await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        Check(timer.Elapsed < TimeSpan.FromSeconds(7), "a stalled upload cannot extend the five-second host shutdown budget indefinitely");
        using var storage = new RoomStore(data); var recovered = new Room(null, storage: storage);
        var joined = recovered.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = member.resumeToken });
        Check(recovered.Revision == 0 && joined.participant.id == member.participant.id, "stalled upload shutdown leaves no accepted edit and retains membership");
        Reject(() => recovered.GetAsset(joined.token, hash), "missing-asset");
    }

    private static async Task ShutdownDuringCommit(string directory) {
        string data = Path.Combine(directory, "shutdown during commit"); var seed = Fixture();
        var server = new CollaborationServer(seed, dataDirectory: data); await server.StartAsync();
        var member = server.Room.Join(new JoinRequest { name = "Admitted writer", catalogHash = seed.catalog.hash });
        var transaction = Tx(server.Room.Snapshot, "AddParts", new[] { Cube("settled-during-stop", 23) });
        var storage = (RoomStore)typeof(CollaborationServer).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        storage.BeforeCommit = stage => { if (stage == "edit") { reached.Set(); if (!release.Wait(10000)) throw new TimeoutException("Commit fixture stalled"); } };
        var commit = Task.Run(() => server.Room.Commit(member.token, transaction)); Task stop = null;
        try {
            Check(reached.Wait(10000), "admitted durable edit reaches its atomic commit before stop");
            stop = Task.Run(async () => await server.DisposeAsync());
            Check((await StoppingResponse(server.Url)).code == 503, "shutdown fences listener work without waiting on an admitted durable edit");
            await Task.Delay(2500);
            Check(!stop.IsCompleted, "storage teardown waits for an admitted synchronous write after listener drain");
            Reject(() => new RoomStore(data), "room-data-in-use");
            release.Set(); Check((await commit).accepted, "an already admitted atomic edit settles normally during shutdown");
            await stop.WaitAsync(TimeSpan.FromSeconds(7));
        }
        finally { release.Set(); await commit; if (stop != null) await stop; else await server.DisposeAsync(); }
        using var reopened = new RoomStore(data); var room = new Room(null, storage: reopened);
        var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = member.resumeToken });
        Check(room.Revision == 1 && room.Outcome(resumed.token, transaction.operationId).accepted && room.Snapshot.parts.Single().position[0] == 23,
            "restart resolves the exact accepted receipt for an edit settling during shutdown");
    }

    private static async Task ShutdownProcess(string directory, string mode, string legacy = null) {
        Console.WriteLine("Shutdown process: " + mode);
        var seed = Fixture(); string data = Path.Combine(directory, "shutdown process " + mode);
        using (var storage = new RoomStore(data)) _ = new Room(seed, storage: storage);
        bool owned = legacy != null || mode == "eof" || mode == "line";
        string published = legacy ?? Environment.GetEnvironmentVariable("PROTOBOT_TEST_CLI");
        var start = new ProcessStartInfo { FileName = published ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (published == null) start.ArgumentList.Add(Path.GetFullPath("src/Cli/bin/Debug/net10.0/protobot-collab.dll"));
        foreach (string argument in new[] { "serve", "--data-dir", data, "--listen", "http://127.0.0.1:0" }) start.ArgumentList.Add(argument);
        if (owned) start.ArgumentList.Add("--bootstrap-stdin");
        using var process = Process.Start(start); var errors = process.StandardError.ReadToEndAsync();
        try {
            if (owned) { await process.StandardInput.WriteLineAsync("null"); await process.StandardInput.FlushAsync(); }
            else process.StandardInput.Close();
            string line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(line ?? "{}"));
            var clients = new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }
                .Select(t => new CollaborationClient(ready.url, seed.room, seed.catalog, t)).ToArray();
            try {
                var notices = clients.Select(_ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                for (int i = 0; i < clients.Length; i++) { int slot = i; clients[i].Disconnected += r => notices[slot].TrySetResult(r); await clients[i].JoinAsync("Process member " + i); }
                await Until(() => clients.All(c => c.ActiveTransport != null));
                Check(!process.HasExited, "standalone stdin EOF does not stop a dedicated service on " + mode);
                if (mode == "eof" || legacy != null) process.StandardInput.Close();
                else if (mode == "line") { await process.StandardInput.WriteLineAsync("stop"); await process.StandardInput.FlushAsync(); }
                else Check(SignalProcess(process.Id, mode == "sigterm" ? 15 : 2) == 0, "task-owned process receives " + mode);
                // Older helpers used Generic Host's default thirty-second drain.
                // They retain generic loss; do not apply the new stop budget to them.
                string[] reasons = await Task.WhenAll(notices.Select(n => n.Task)).WaitAsync(TimeSpan.FromSeconds(legacy == null ? 7 : 35));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Check(process.ExitCode == 0 && reasons.All(r => legacy == null ? r == "server-stopping" : r != "server-stopping") && clients.All(c => !c.IsMembershipRevoked && c.Snapshot.revision == 0),
                    "actual CLI " + mode + " exits cleanly with compatible transport feedback and retained membership/content");
                Check(string.IsNullOrWhiteSpace(await errors), "CLI shutdown produces no unbounded error output on " + mode);
            }
            finally { foreach (var client in clients) client.Dispose(); }
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } await errors; }
    }
}
