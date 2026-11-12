using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task DurabilityChecks(string directory) {
        string root = Path.Combine(directory, "private room recovery");
        RecoveryFiles.CreateDirectory(root);
        var seed = Fixture(); seed.password = "durable-room-password";
        JoinResponse alice, bob;
        Transaction original, invalid;
        string hash, oldLease, incarnation;
        long sequence = 0;
        string database = Path.Combine(root, "room.sqlite");
        using (var store = new RoomStore(root, checkpointEvery: 2)) {
            var room = new Room(seed, storage: store);
            Reject(() => { using var second = new RoomStore(root); }, "room-data-in-use");
            alice = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Owner", ownerCredential = seed.ownerCredential });
            bob = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Editor", password = seed.password });
            original = Tx(room.Snapshot, "AddParts", new[] { Cube("a"), Cube("b", 10) }, sequence: ++sequence);
            Check(room.Commit(alice.token, original).accepted, "durable fixture create");
            var paint = Cube("a"); paint.hasPaint = true; paint.color = new double[] { .2, .4, .6 };
            var held = room.Reserve(alice.token, new LeaseRequest { ids = new[] { "a" } });
            Check(room.Commit(alice.token, Tx(room.Snapshot, "SetParts", new[] { paint }, lease: held.token, sequence: ++sequence)).accepted, "durable paint");
            held = room.Reserve(alice.token, new LeaseRequest { ids = new[] { "b" } });
            Check(room.Commit(alice.token, Tx(room.Snapshot, "DeleteParts", Array.Empty<Part>(), new[] { "b" }, held.token, ++sequence)).accepted, "durable tombstone");
            Check(room.Commit(alice.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: ++sequence)).accepted, "durable inverse");
            Check(room.Commit(alice.token, Tx(room.Snapshot, "Redo", Array.Empty<Part>(), sequence: ++sequence)).accepted, "durable redo");
            Check(room.Commit(bob.token, Tx(room.Snapshot, "AddParts", new[] { Cube("c", 20) })).accepted, "independent durable edit");
            Check(room.Commit(alice.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: ++sequence)).accepted, "durable history cursor");
            held = room.Reserve(alice.token, new LeaseRequest { ids = new[] { "a" } }); oldLease = held.token;
            var bad = WireJson.Clone(paint); bad.position[0] = double.NaN;
            invalid = Tx(room.Snapshot, "SetPoses", new[] { bad }, lease: held.token, sequence: ++sequence);
            Check(room.Commit(alice.token, invalid).error == "invalid-position", "rejected receipts persist too");
            room.Preview(alice.token, new Preview { lease = held.token, sequence = 1, parts = new[] { paint } });
            hash = DocumentModel.Hash(room.Snapshot); incarnation = room.Incarnation;
        }
        using (var store = new RoomStore(root)) {
            var room = new Room(null, storage: store);
            Check(room.Recovered && room.IsDurable && DocumentModel.Hash(room.Snapshot) == hash && room.Incarnation != incarnation, "checkpoint plus journal tail restores exact accepted state");
            Reject(() => room.Reserve(alice.token, new LeaseRequest { ids = new[] { "a" } }), "invalid-session");
            Reject(() => room.Reserve(alice.resumeToken, new LeaseRequest { ids = new[] { "a" } }), "invalid-session");
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = alice.resumeToken });
            Check(resumed.participant.id == alice.participant.id && resumed.participant.role == "owner" && resumed.token != alice.token, "durable identity issues a fresh access token");
            Check(room.Outcome(resumed.token, invalid.operationId).error == "invalid-position", "rejected receipt recovered");
            Check(room.Commit(resumed.token, original).revision == 1 && DocumentModel.Hash(room.Snapshot) == hash, "accepted receipt deduplicates after restart");
            Reject(() => room.Preview(resumed.token, new Preview { lease = oldLease, sequence = 2, parts = new[] { Cube("a") } }), "lease-expired");
            var held = room.Reserve(resumed.token, new LeaseRequest { ids = new[] { "a" } }); room.Release(resumed.token, held.token);
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: ++sequence)).accepted && !room.Snapshot.parts.Single(p => p.id == "a").hasPaint,
                "personal undo history survives restart and ignores another participant's disjoint edit");
            var resumedBob = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = bob.resumeToken });
            held = room.Reserve(resumedBob.token, new LeaseRequest { ids = new[] { "a" } });
            Check(room.Commit(resumedBob.token, Tx(room.Snapshot, "SetPoses", new[] { Cube("a", 4) }, lease: held.token, sequence: 2)).accepted, "remote durable modification");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Redo", Array.Empty<Part>(), sequence: ++sequence)).error == "history-changed-by-another-participant", "durable stale inverse refused");
            room.Leave(resumedBob.token);
            alice = resumed;
        }
        using (var store = new RoomStore(root)) {
            var room = new Room(null, storage: store);
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = alice.resumeToken });
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Redo", Array.Empty<Part>(), sequence: ++sequence)).error == "history-changed-by-another-participant", "inverse invalidation survives a second restart");
            Reject(() => room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = bob.resumeToken }), "invalid-session");
            Reject(() => room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Wrong password", password = "wrong" }), "wrong-password");
        }
        using (var db = new SqliteConnection("Data Source=" + database + ";Pooling=False")) {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            string version = Convert.ToString(command.ExecuteScalar());
            Check(Version.Parse(version) >= new Version(3, 51, 3), "bundled SQLite includes the WAL-reset fix");
            command.CommandText = "SELECT COUNT(*) FROM checkpoints"; Check(Convert.ToInt64(command.ExecuteScalar()) <= 2, "document checkpoints remain bounded");
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; command.ExecuteNonQuery();
        }
        string bytes = Encoding.UTF8.GetString(File.ReadAllBytes(database));
        Check(!bytes.Contains(seed.password) && !bytes.Contains(seed.ownerCredential) && !bytes.Contains(alice.resumeToken) && !bytes.Contains(alice.token), "authority storage contains hashes rather than raw credentials");

        string failureRoot = Path.Combine(directory, "failed write recovery");
        JoinResponse failedMember; Transaction failed;
        using (var store = new RoomStore(failureRoot)) {
            var room = new Room(Fixture(), storage: store);
            failedMember = room.Join(new JoinRequest { name = "Failure client", catalogHash = seed.catalog.hash });
            failed = Tx(room.Snapshot, "AddParts", new[] { Cube("not-published") });
            store.BeforeCommit = stage => { if (stage == "edit") throw new IOException("Injected before durable commit"); };
            Reject(() => room.Commit(failedMember.token, failed), "room-storage-failed-restart-required");
            Check(!room.IsHealthy && room.Snapshot.revision == 0 && room.Snapshot.parts.Length == 0, "failed durable transaction is never published");
        }
        using (var store = new RoomStore(failureRoot)) {
            var room = new Room(null, storage: store);
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = failedMember.resumeToken });
            Check(room.Snapshot.revision == 0 && !room.Outcome(resumed.token, failed.operationId).accepted, "failed transaction leaves no accepted receipt after restart");
            Check(room.Commit(resumed.token, failed).accepted && room.Snapshot.revision == 1, "an explicitly retried absent operation can commit once");
        }
        using (var db = new SqliteConnection("Data Source=" + failureRoot + "/room.sqlite;Pooling=False")) {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE checkpoints SET hash='corrupt'"; command.ExecuteNonQuery();
        }
        using (var store = new RoomStore(failureRoot)) Reject(() => new Room(null, storage: store), "room-storage-corrupt");
        LegacyChainFreeChecks(Path.Combine(directory, "legacy chain-free recovery"));
        DurableChainChecks(Path.Combine(directory, "durable chain recovery"));
        await CrashRecoveryChecks(Path.Combine(directory, "process crash recovery"));
    }

    private static void LegacyChainFreeChecks(string directory) {
        var seed = Fixture(); JoinResponse owner; Document accepted;
        using (var store = new RoomStore(directory)) {
            var room = new Room(seed, storage: store);
            owner = room.Join(new JoinRequest { name = "Legacy owner", catalogHash = seed.catalog.hash, ownerCredential = seed.ownerCredential });
            Check(room.Commit(owner.token, Tx(room.Snapshot, "AddParts", new[] { Cube("legacy-part") }, sequence: 1)).accepted,
                "legacy fixture edit accepted");
            accepted = room.Snapshot;
        }
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "room.sqlite") + ";Pooling=False")) {
            db.Open();
            byte[] Read(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return (byte[])command.ExecuteScalar(); }
            byte[] Remove(byte[] data, params string[] fields) {
                var value = JsonNode.Parse(data).AsObject(); foreach (var field in fields) value.Remove(field);
                return Encoding.UTF8.GetBytes(value.ToJsonString());
            }
            using (var command = db.CreateCommand()) {
                command.CommandText = "UPDATE checkpoints SET document=$document,hash=$hash";
                command.Parameters.AddWithValue("$document", Remove(Read("SELECT document FROM checkpoints"), "chains", "chainTombstones"));
                command.Parameters.AddWithValue("$hash", DocumentModel.LegacyHash(seed.document)); command.ExecuteNonQuery();
            }
            using (var command = db.CreateCommand()) {
                var journal = JsonNode.Parse(Read("SELECT data FROM journal")).AsObject();
                var transaction = journal["transaction"].AsObject();
                foreach (var field in new[] { "chainWrites", "chainDeletes", "expectedChains" }) transaction.Remove(field);
                journal["hash"] = DocumentModel.LegacyHash(accepted);
                command.CommandText = "UPDATE journal SET data=$data";
                command.Parameters.AddWithValue("$data", Encoding.UTF8.GetBytes(journal.ToJsonString())); command.ExecuteNonQuery();
            }
            using (var command = db.CreateCommand()) {
                command.CommandText = "UPDATE history SET data=$data";
                command.Parameters.AddWithValue("$data", Remove(Read("SELECT data FROM history"), "chainIds", "chainsBefore", "chainsAfter"));
                command.ExecuteNonQuery();
            }
        }
        using (var store = new RoomStore(directory)) {
            var room = new Room(null, storage: store);
            Check(room.Recovered && room.Snapshot.revision == 1 && room.Snapshot.parts.Single().id == "legacy-part" &&
                DocumentModel.Chains(room.Snapshot).Length == 0, "0.3 chain-free checkpoint and journal recover under 0.4");
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken });
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 2)).accepted && room.Snapshot.parts.Length == 0,
                "0.3 chain-free history remains undoable under 0.4");
        }
    }

    private static void DurableChainChecks(string directory) {
        var seed = Fixture(); seed.document.parts = new[] { Cube("a"), Cube("b", 10) };
        foreach (var part in seed.document.parts) part.version = 1;
        JoinResponse owner;
        using (var store = new RoomStore(directory, checkpointEvery: 1)) {
            var room = new Room(seed, storage: store);
            owner = room.Join(new JoinRequest { name = "Chain owner", catalogHash = seed.catalog.hash, ownerCredential = seed.ownerCredential });
            var lease = room.Reserve(owner.token, new LeaseRequest { ids = new[] { "a", "b" } });
            var add = Tx(room.Snapshot, "SetChains", Array.Empty<Part>(), lease: lease.token, sequence: 1,
                chains: new[] { Chain("durable-chain", "a", "b") });
            Check(room.Commit(owner.token, add).accepted && DocumentModel.Chains(room.Snapshot).Single().version == 1,
                "durable chain transaction accepted");
        }
        using (var store = new RoomStore(directory, checkpointEvery: 1)) {
            var room = new Room(null, storage: store);
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken });
            Check(DocumentModel.Chains(room.Snapshot).Single().id == "durable-chain", "chain checkpoint recovers topology");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 2)).accepted &&
                DocumentModel.Chains(room.Snapshot).Length == 0, "durable personal undo removes chain after restart");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Redo", Array.Empty<Part>(), sequence: 3)).accepted &&
                DocumentModel.Chains(room.Snapshot).Single().version == 3, "durable personal redo restores chain with newer version");
        }
        using (var store = new RoomStore(directory)) {
            var room = new Room(null, storage: store);
            Check(DocumentModel.Chains(room.Snapshot).Single().id == "durable-chain" && room.Snapshot.revision == 3,
                "chain undo/redo journal survives second restart");
        }
    }

    private static async Task CrashRecoveryChecks(string directory) {
        RecoveryFiles.CreateDirectory(directory);
        string data = Path.Combine(directory, "authority");
        string profile = Path.Combine(directory, "client", "session.json");
        var seed = Fixture();
        await using var first = await RecoveryChild.Start(data, "http://127.0.0.1:0", seed);
        await using var proxy = new LoopbackProxy(first.Ready.url); await proxy.StartAsync();
        Transaction lost;
        string identity, oldLease;
        using (var client = new CollaborationClient(proxy.Url, "team", seed.catalog, CollaborationTransport.LongPolling, recoveryFile: profile)) {
            await client.JoinAsync("Durable owner", ownerCredential: seed.ownerCredential); identity = client.Participant.id;
            Check(first.Ready.durable && !first.Ready.recovered && client.IsDurable, "CLI advertises a newly durable room");
            if (!OperatingSystem.IsWindows()) {
                Check(File.GetUnixFileMode(profile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "resume credentials are private to the Unix user");
                Check(File.GetUnixFileMode(Path.GetDirectoryName(profile)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "resume directory is private to the Unix user");
            }
            Check((await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("durable-part") }))).accepted, "acknowledged edit before process kill");
            var lease = await client.ReserveAsync(new[] { "durable-part" }); oldLease = lease.token;
            proxy.DropNextCommitResponse = true;
            lost = client.CreateTransaction("SetPoses", new[] { Cube("durable-part", 55) }, lease: lease.token);
            try { await client.SubmitAsync(lost); throw new Exception("Expected dropped acknowledgment"); }
            catch (Exception error) when (error is System.Net.Http.HttpRequestException || error is OperationCanceledException) { }
            Check(client.PendingCount == 1 && File.ReadAllText(profile).Contains(lost.operationId), "pending operation ID is recorded before sending");
        }
        await first.Kill();
        await using var second = await RecoveryChild.Start(data, first.Ready.url, null);
        using (var client = new CollaborationClient(proxy.Url, "team", seed.catalog, CollaborationTransport.LongPolling, recoveryFile: profile)) {
            await client.JoinAsync("Resumed owner");
            Check(second.Ready.recovered && client.Participant.id == identity && client.Participant.role == "owner" && client.Snapshot.revision == 2 &&
                client.Snapshot.parts.Single().position[0] == 55, "SIGKILL recovery retains acknowledged and lost-ack edits with the same identity");
            Check(client.PendingCount == 0 && client.RecoveryMessages.Single().Contains("Recovered accepted edit"), "a new client process resolves the durable pending receipt without replay");
            try { await client.ReserveAsync(new[] { "durable-part" }, oldLease); throw new Exception("Expected fenced lease"); }
            catch (CollaborationException error) { Check(error.Code == "lease-expired", "restart fences old reservations"); }
            Check((await client.SubmitAsync(client.CreateTransaction("Undo", Array.Empty<Part>()))).accepted && client.Snapshot.parts.Single().position[0] == 0, "personal undo survives a real process kill");
            Check((await client.SubmitAsync(client.CreateTransaction("Redo", Array.Empty<Part>()))).accepted && client.Snapshot.parts.Single().position[0] == 55, "personal redo after process restart");
            Check((await client.SubmitAsync(lost)).revision == 2 && client.Snapshot.revision == 4, "lost-ack operation cannot apply twice after restart");
            using (var duplicate = new CollaborationClient(proxy.Url, "team", seed.catalog, recoveryFile: profile)) {
                try { await duplicate.JoinAsync("Duplicate profile"); throw new Exception("Expected recovery file ownership conflict"); }
                catch (IOException) { Check(true, "another process cannot overwrite the active client recovery cache"); }
            }
            await client.LeaveAsync(); Check(!File.Exists(profile), "explicit leave clears the local membership cache after revocation");
        }
    }

    private sealed class RecoveryChild : IAsyncDisposable {
        private Process process;
        private Task<string> stderr, stdout;
        public Ready Ready { get; private set; }
        public static async Task<RecoveryChild> Start(string directory, string listen, Bootstrap seed) {
            var child = new RecoveryChild();
            var start = new ProcessStartInfo { FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Path.GetFullPath("src/Cli/bin/Debug/net10.0/protobot-collab.dll"), "serve", "--bootstrap-stdin", "--listen", listen, "--data-dir", directory })
                start.ArgumentList.Add(argument);
            child.process = Process.Start(start); child.stderr = child.process.StandardError.ReadToEndAsync();
            try {
                await child.process.StandardInput.WriteLineAsync(WireJson.Text(seed)); await child.process.StandardInput.FlushAsync();
                var line = child.process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(line, Task.Delay(15000)) != line || await line == null) throw new Exception("Durable CLI did not become ready.");
                child.Ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(await line));
                child.stdout = child.process.StandardOutput.ReadToEndAsync(); return child;
            }
            catch { await child.DisposeAsync(); throw; }
        }
        public async Task Kill() { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
        public async ValueTask DisposeAsync() {
            if (process == null) return;
            try {
                if (!process.HasExited) {
                    process.StandardInput.Close();
                    var exited = process.WaitForExitAsync();
                    if (await Task.WhenAny(exited, Task.Delay(10000)) != exited) {
                        if (!process.HasExited) process.Kill();
                    }
                    await process.WaitForExitAsync();
                }
                if (stdout != null) await stdout; if (stderr != null) await stderr;
            }
            finally { process.Dispose(); process = null; }
        }
    }
}
