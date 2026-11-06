using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static class Program {
    private static int assertions;
    public static async Task<int> Main(string[] args) {
        string directory = Path.Combine(Path.GetTempPath(), "protobot-collab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int bots = 10;
        int index = Array.IndexOf(args, "--bots");
        if (index >= 0) bots = int.Parse(args[index + 1]);
        if (bots < 2 || bots > 10) throw new ArgumentException("--bots must be 2–10");
        var transports = new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling };
        index = Array.IndexOf(args, "--transport");
        if (index >= 0) transports = new[] { args[index + 1] == "long-polling" ? CollaborationTransport.LongPolling : CollaborationTransport.WebSocket };
        var timer = Stopwatch.StartNew();
        try {
            ModelChecks(); RoomChecks();
            foreach (var transport in transports) await NetworkChecks(bots, transport, directory);
            await FailureChecks();
            await ProcessChecks(directory);
            index = Array.IndexOf(args, "--player");
            if (index >= 0) await PlayerChecks(args[index + 1], directory);
            var summary = "{\"passed\":true,\"bots\":" + bots + ",\"assertions\":" + assertions + ",\"elapsedSeconds\":" + timer.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
            File.WriteAllText(Path.Combine(directory, "summary.json"), summary);
            Console.WriteLine(summary); Console.WriteLine("Artifacts: " + directory); return 0;
        }
        catch (Exception error) {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error); Console.Error.WriteLine("Failure artifacts: " + directory); return 1;
        }
    }

    private static Bootstrap Fixture() => new Bootstrap {
        room = "team", password = "", ownerCredential = "lab-owner-secret",
        catalog = new CatalogManifest { hash = "catalog-small-v1", entries = new[] { new CatalogEntry { id = "CUBE" } } },
        document = new Document { generation = "fixture-generation", catalogHash = "catalog-small-v1" }
    };
    private static Part Cube(string id, double x = 0) => new Part { id = id, catalogId = "CUBE", position = new[] { x, 0.0, 0.0 } };
    private static Transaction Tx(Document document, string kind, Part[] parts, string[] deletes = null, string lease = null, long sequence = 1) => new Transaction {
        generation = document.generation, sequence = sequence, kind = kind, writes = parts, deletes = deletes ?? Array.Empty<string>(), lease = lease,
        expected = parts.Select(p => p.id).Concat(deletes ?? Array.Empty<string>()).Select(id => new VersionRef { id = id, version = document.parts.FirstOrDefault(p => p.id == id)?.version ?? 0 }).ToArray()
    };
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception("Assertion failed: " + message); }
    private static void Reject(Action action, string code) {
        try { action(); throw new Exception("Expected rejection: " + code); }
        catch (CollaborationException error) { Check(error.Code == code, "rejection " + code + " (got " + error.Code + ")"); }
    }

    private static void ModelChecks() {
        var seed = Fixture();
        var original = WireJson.Clone(seed.document);
        var add = Tx(original, "AddParts", new[] { Cube("a"), Cube("b", 4) });
        var added = DocumentModel.Reduce(original, add, seed.catalog);
        Check(original.parts.Length == 0 && original.revision == 0, "reducer leaves input untouched");
        Check(added.revision == 1 && added.parts.All(p => p.version == 1), "independent add oracle");
        var stale = Tx(original, "AddParts", new[] { Cube("a") });
        Reject(() => DocumentModel.Reduce(added, stale, seed.catalog), "stale-entity");
        var invalid = Tx(added, "SetPoses", new[] { Cube("a", 2), Cube("b", double.NaN) });
        Reject(() => DocumentModel.Reduce(added, invalid, seed.catalog), "invalid-position");
        Check(added.parts[0].position[0] == 0 && added.parts[1].position[0] == 4, "failed transaction is atomic");
        var pose = Tx(added, "SetPoses", new[] { Cube("a", 3) });
        var moved = DocumentModel.Reduce(added, pose, seed.catalog);
        Check(moved.parts.Single(p => p.id == "a").position[0] == 3 && moved.parts.Single(p => p.id == "b").version == 1, "pose changes one entity");
        var disjoint = Tx(added, "SetPoses", new[] { Cube("b", 7) });
        var both = DocumentModel.Reduce(moved, disjoint, seed.catalog);
        Check(both.parts.Single(p => p.id == "b").position[0] == 7, "old global revision permits disjoint edit");
        var reversed = WireJson.Clone(both); Array.Reverse(reversed.parts);
        reversed.parts[0].rotation = new double[] { 0, 0, 0, -1 };
        Check(DocumentModel.Hash(reversed) == DocumentModel.Hash(both), "canonical order and quaternion sign");
        reversed.parts[0].state = "changed";
        Check(DocumentModel.Hash(reversed) != DocumentModel.Hash(both), "saved state included in hash");
        var deleted = DocumentModel.Reduce(both, Tx(both, "DeleteParts", Array.Empty<Part>(), new[] { "a" }), seed.catalog);
        Check(deleted.parts.Length == 1 && deleted.tombstones.Single().version == 3, "delete tombstone has next version");
        var reuse = Tx(deleted, "AddParts", new[] { Cube("a") }); reuse.expected[0].version = 3;
        Reject(() => DocumentModel.Reduce(deleted, reuse, seed.catalog), "identity-in-use");
        var unknown = Tx(both, "ReplaceDocument", new[] { Cube("a") });
        Reject(() => DocumentModel.Reduce(both, unknown, seed.catalog), "unsupported-mutation");
    }

    private static void RoomChecks() {
        long now = 0;
        var seed = Fixture();
        seed.document.parts = new[] { Cube("a"), Cube("b") };
        foreach (var part in seed.document.parts) part.version = 1;
        var authority = new Room(seed, () => now);
        JoinRequest Join(string name, bool viewer = false) => new JoinRequest { name = name, catalogHash = seed.catalog.hash, viewer = viewer };
        var alice = authority.Join(Join("Alice"));
        var bob = authority.Join(Join("Bob"));
        var view = authority.Join(Join("Viewer", true));
        Reject(() => authority.Reserve(view.token, new LeaseRequest { ids = new[] { "a" } }), "viewer-read-only");
        var hold = authority.Reserve(alice.token, new LeaseRequest { ids = new[] { "a", "b" } });
        Reject(() => authority.Reserve(bob.token, new LeaseRequest { ids = new[] { "b" } }), "parts-reserved");
        var preview = new Preview { lease = hold.token, sequence = 2, parts = new[] { Cube("a", 3), Cube("b", 3) } };
        authority.Preview(alice.token, preview);
        var snapshot = authority.Join(new JoinRequest { resumeToken = bob.token, catalogHash = seed.catalog.hash });
        string oldToken = bob.token;
        bob = snapshot;
        Reject(() => authority.Outcome(oldToken, "delayed-old-connection"), "invalid-session");
        Check(snapshot.document.parts.All(p => p.position[0] == 0), "previews excluded from snapshot");
        now += Room.LeaseMilliseconds + 1;
        var replacement = authority.Reserve(bob.token, new LeaseRequest { ids = new[] { "a", "b" } });
        Reject(() => authority.Preview(alice.token, preview), "lease-expired");
        var delayed = Tx(seed.document, "SetPoses", preview.parts, lease: hold.token);
        Check(authority.Commit(alice.token, delayed).error == "lease-expired", "obsolete commit fenced after reassignment");
        var accepted = Tx(seed.document, "SetPoses", new[] { Cube("a", 5), Cube("b", 5) }, lease: replacement.token);
        var receipt = authority.Commit(bob.token, accepted);
        Check(receipt.accepted && receipt.revision == 1, "all-or-none reservation commit");
        Check(authority.Commit(bob.token, accepted).revision == 1 && authority.Outcome(bob.token, accepted.operationId).accepted, "lost ack retry and outcome lookup");
        authority.Leave(bob.token);
        Reject(() => authority.Outcome(bob.token, accepted.operationId), "invalid-session");
        seed.password = "secret";
        var protectedRoom = new Room(seed);
        Reject(() => protectedRoom.Join(Join("Wrong")), "wrong-password");
        var correct = Join("Correct"); correct.password = "secret";
        Check(protectedRoom.Join(correct).participant.role == "editor", "password permits editor");
        correct.name = "Owner"; correct.ownerCredential = seed.ownerCredential;
        Check(protectedRoom.Join(correct).participant.role == "owner", "owner credential separate from room password");
        long capacityTime = 0;
        var capacity = new Room(Fixture(), () => capacityTime);
        var inactive = capacity.Join(Join("Sleeping client"));
        capacityTime = 60001;
        for (int i = 0; i < 10; i++) capacity.Join(Join("Active " + i));
        Reject(() => capacity.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = inactive.token }), "room-full");
    }

    private static async Task NetworkChecks(int count, CollaborationTransport transport, string directory) {
        var seed = Fixture();
        await using var server = new CollaborationServer(seed);
        await server.StartAsync();
        var clients = new List<CollaborationClient>();
        try {
            for (int i = 0; i < count; i++) {
                var client = new CollaborationClient(server.Url, "team", seed.catalog, transport);
                clients.Add(client); await client.JoinAsync("Bot " + i);
            }
            var transactions = clients.Select((c, i) => c.CreateTransaction("AddParts", new[] { Cube("part" + i, i) })).ToArray();
            await Task.WhenAll(clients.Select((c, i) => c.SubmitAsync(transactions[i])));
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(count)));
            Check(clients.Select(c => DocumentModel.Hash(c.Snapshot)).Distinct().Count() == 1, transport + " concurrent creates converge");
            // Independent oracle uses generated IDs/coordinates, not a second reducer invocation.
            Check(clients.All(c => Enumerable.Range(0, count).All(i => c.Snapshot.parts.Single(p => p.id == "part" + i).position[0] == i)), "all expected coordinates materialized");
            var reservations = await Task.WhenAll(clients.Select((c, i) => c.ReserveAsync(new[] { "part" + i })));
            var moves = clients.Select((c, i) => c.CreateTransaction("SetPoses", new[] { Cube("part" + i, i + 100) }, lease: reservations[i].token)).ToArray();
            await Task.WhenAll(clients.Select((c, i) => c.SubmitAsync(moves[i])));
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(2 * count)));
            Check(clients.Select(c => DocumentModel.Hash(c.Snapshot)).Distinct().Count() == 1, transport + " disjoint moves converge");
            var one = await clients[0].ReserveAsync(new[] { "part0" });
            try { await clients[1].ReserveAsync(new[] { "part0" }); throw new Exception("Expected contention"); }
            catch (CollaborationException error) { Check(error.Code == "parts-reserved", transport + " contention denied"); }
            bool sawGhost = false, clearedGhost = false;
            clients[1].Presence += batch => { if (batch.previews.Any()) sawGhost = true; else if (sawGhost) clearedGhost = true; };
            await clients[0].PreviewAsync(new Preview { lease = one.token, sequence = 1, parts = new[] { Cube("part0", 900) } });
            await Until(() => sawGhost);
            Check(clients[1].Snapshot.parts.Single(p => p.id == "part0").position[0] == 100, "network ghost leaves committed document intact");
            await clients[0].ReleaseAsync(one.token); await Until(() => clearedGhost);
            // Retry a previously accepted command after other edits; revision and pose must not change.
            var duplicate = await clients[0].SubmitAsync(transactions[0]);
            Check(duplicate.accepted && clients[0].Snapshot.parts.Single(p => p.id == "part0").position[0] == 100, "dedup across intervening operations");
            var before = clients[0].Snapshot;
            var dependencyLease = await clients[0].ReserveAsync(new[] { "part0" });
            var staleDependency = clients[0].CreateTransaction("SetPoses", new[] { Cube("part0", 555) }, dependencies: new[] {
                new VersionRef { id = "part1", version = before.parts.Single(p => p.id == "part1").version - 1 }
            }, lease: dependencyLease.token);
            Check(!(await clients[0].SubmitAsync(staleDependency)).accepted, "stale target rejected atomically");
            await clients[0].ReleaseAsync(dependencyLease.token);
            await clients[0].PrepareReconnectAsync();
            await clients[0].JoinAsync("same identity");
            Check(DocumentModel.Hash(clients[0].Snapshot) == DocumentModel.Hash(clients[1].Snapshot), "explicit reconnect snapshot converges");
            var deleteLease = await clients[0].ReserveAsync(new[] { "part0" });
            var deletion = clients[0].CreateTransaction("DeleteParts", Array.Empty<Part>(), new[] { "part0" }, lease: deleteLease.token);
            Check((await clients[0].SubmitAsync(deletion)).accepted, "network delete accepted");
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(2 * count + 1)));
            Check(clients.All(c => c.Snapshot.parts.Length == count - 1 && c.Snapshot.tombstones.Length == 1), "delete propagates to every client");
            File.WriteAllBytes(Path.Combine(directory, transport + "-document.json"), WireJson.Encode(clients[0].Snapshot));
            Console.WriteLine(transport + ": " + count + " clients converged at revision " + clients[0].Snapshot.revision + " (" + DocumentModel.Hash(clients[0].Snapshot) + ")");
        }
        finally {
            foreach (var client in clients) { try { await client.LeaveAsync(); } finally { client.Dispose(); } }
        }
    }

    private static async Task ProcessChecks(string directory) {
        // Run the actual CLI child, then close only its private stdin.
        string dll = Path.GetFullPath("src/Cli/bin/Debug/net10.0/protobot-collab.dll");
        if (!File.Exists(dll)) { Console.WriteLine("CLI lifecycle check skipped: build src/Cli first."); return; }
        var start = new ProcessStartInfo { FileName = Environment.ProcessPath, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // dotnet run creates an apphost for this lab, so resolve the dotnet host independently.
        start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        start.ArgumentList.Add(dll); start.ArgumentList.Add("serve"); start.ArgumentList.Add("--bootstrap-stdin");
        start.ArgumentList.Add("--listen"); start.ArgumentList.Add("http://127.0.0.1:0");
        using var child = Process.Start(start);
        try {
            var stderr = child.StandardError.ReadToEndAsync();
            await child.StandardInput.WriteLineAsync(WireJson.Text(Fixture())); await child.StandardInput.FlushAsync();
            var read = child.StandardOutput.ReadLineAsync();
            Check(await Task.WhenAny(read, Task.Delay(15000)) == read, "child readiness bounded");
            var ready = WireJson.Decode<Ready>(System.Text.Encoding.UTF8.GetBytes(await read ?? ""));
            Check(ready.type == "ready" && new Uri(ready.url).Port > 0, "CLI readiness reports actual bound port");
            child.StandardInput.Close();
            using var timeout = new CancellationTokenSource(10000); await child.WaitForExitAsync(timeout.Token);
            Check(child.ExitCode == 0, "owned helper exits on parent channel EOF");
            Check(!(await stderr).Contains("lab-owner-secret"), "credentials absent from process logs");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
    [Serializable] private sealed class PlayerReport {
        public bool passed = false;
        public string hash = null, transport = null, error = null;
        public long revision = 0;
        public int parts = 0;
    }
    private static async Task PlayerChecks(string path, string directory) {
        string executable = Directory.Exists(path) && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetFiles(Path.Combine(path, "Contents/MacOS")).Single(f => !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal)) : path;
        executable = Path.GetFullPath(executable);
        var players = new List<Process>();
        var drains = new List<Task>();
        Process Launch(string profile, string role, string url, string transport) {
            var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            var options = new List<string> { "--collaboration", "--collab-profile", Path.Combine(directory, profile + " profile"), "--collab-name", profile,
                "--collab-probe", role, "--collab-test-results", Path.Combine(directory, profile + ".json"), "--collab-transport", transport,
                "-batchmode", "-nographics", "-logFile", Path.Combine(directory, profile + ".log") };
            if (url != null) options.AddRange(new[] { "--collab-join", url });
            foreach (var option in options) start.ArgumentList.Add(option);
            var process = Process.Start(start); players.Add(process);
            drains.Add(Drain(process.StandardOutput, Path.Combine(directory, profile + ".stdout.txt")));
            drains.Add(Drain(process.StandardError, Path.Combine(directory, profile + ".stderr.txt")));
            return process;
        }
        async Task WaitFile(string file) {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(file)) {
                if (file.EndsWith(".ready", StringComparison.Ordinal) && File.Exists(file.Substring(0, file.Length - 6)))
                    throw new Exception("Host probe failed: " + await File.ReadAllTextAsync(file.Substring(0, file.Length - 6)));
                if (players.Any(p => p.HasExited)) throw new Exception("A player exited; see isolated player logs.");
                if (watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("Player report timed out: " + file);
                await Task.Delay(100);
            }
        }
        try {
            Launch("Host", "host", null, "websocket");
            string hostReport = Path.Combine(directory, "Host.json");
            await WaitFile(hostReport + ".ready");
            var ready = WireJson.Decode<Ready>(await File.ReadAllBytesAsync(hostReport + ".ready"));
            Launch("Guest", "join", ready.url, "long-polling");
            string guestReport = Path.Combine(directory, "Guest.json");
            await WaitFile(hostReport); await WaitFile(guestReport);
            var host = WireJson.Decode<PlayerReport>(await File.ReadAllBytesAsync(hostReport));
            var guest = WireJson.Decode<PlayerReport>(await File.ReadAllBytesAsync(guestReport));
            Check(host.passed && guest.passed, "both built-player scene probes pass: " + host.error + " " + guest.error);
            Check(host.hash == guest.hash && host.revision == 2 && guest.revision == 2, "built players converge after in-app Host/Join");
            Check(host.transport == "websocket" && guest.transport == "long-polling", "built players use both real runtime transports");
            Check(File.ReadAllText(Path.Combine(directory, "Host profile/preferences.json")).Contains("Host") &&
                File.ReadAllText(Path.Combine(directory, "Guest profile/preferences.json")).Contains("Guest"), "player preferences are isolated in separate profiles");
            Console.WriteLine("Built players: in-app Host/Join converged; scene poses, colliders, save snapshots and profiles checked.");
        }
        finally {
            foreach (var player in players) if (!player.HasExited) { player.Kill(); await player.WaitForExitAsync(); }
            try { await Task.WhenAll(drains); }
            finally { foreach (var player in players) player.Dispose(); }
        }
    }
    private static async Task Drain(StreamReader input, string path) {
        using var output = new StreamWriter(path);
        string line;
        while ((line = await input.ReadLineAsync()) != null) await output.WriteLineAsync(line);
    }
    private static async Task FailureChecks() {
        var seed = Fixture();
        await using var server = new CollaborationServer(seed); await server.StartAsync();
        await using var proxy = new LoopbackProxy(server.Url); await proxy.StartAsync();
        using var client = new CollaborationClient(proxy.Url, "team", seed.catalog);
        await client.JoinAsync("Fallback and lost-ack bot");
        await Until(() => client.ActiveTransport == "long-polling");
        Check(client.IsConnected, "Auto falls back when a proxy refuses WebSocket upgrades");
        proxy.DropNextCommitResponse = true;
        var transaction = client.CreateTransaction("AddParts", new[] { Cube("lost-ack") });
        try { await client.SubmitAsync(transaction); throw new Exception("Expected a dropped acknowledgment"); }
        catch (System.Net.Http.HttpRequestException) { }
        Check(client.PendingCount == 1 && !client.IsConnected, "lost ack freezes commits and retains pending operation ID");
        await client.PrepareReconnectAsync(); await client.JoinAsync("Resume");
        Check(client.PendingCount == 0 && client.Snapshot.revision == 1 && client.Snapshot.parts.Single().id == "lost-ack", "reconnect resolves receipt without resubmitting edit");
        proxy.CommitDelayMilliseconds = 300;
        int echoes = 0;
        client.Accepted += _ => System.Threading.Interlocked.Increment(ref echoes);
        var second = client.CreateTransaction("AddParts", new[] { Cube("echo-before-ack") });
        var receipt = await client.SubmitAsync(second);
        Check(receipt.accepted && echoes == 1 && client.Snapshot.revision == 2, "broadcast before acknowledgment applies exactly once");
        // Join during a stream of commits: snapshot revision and event cursor share one authority gate.
        using var bob = new CollaborationClient(server.Url, "team", seed.catalog, CollaborationTransport.LongPolling);
        proxy.CommitDelayMilliseconds = 0;
        var writer = Task.Run(async () => {
            for (int i = 0; i < 30; i++) await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("handoff" + i, i) }));
        });
        await bob.JoinAsync("Joining during edits"); await writer;
        await bob.WaitForRevisionAsync(32);
        Check(DocumentModel.Hash(bob.Snapshot) == DocumentModel.Hash(client.Snapshot), "snapshot-to-event handoff is gap-free during active commits");
        await client.LeaveAsync(); await bob.LeaveAsync();
    }
    private static async Task Until(Func<bool> condition) {
        var deadline = Stopwatch.StartNew();
        while (!condition()) { if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Scenario timed out"); await Task.Delay(10); }
    }
}
