using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static RoomReplacementRequest ReplacementRequest(Room room, string owner, RoomExport content = null) =>
        new RoomReplacementRequest { expected = room.InspectReplacement(owner), content = content ?? CreationContent() };
    private static async Task ReplacementStorageChecks(string directory) {
        string root = Path.Combine(directory, "replacement-room"); var seed = HostedSeed("replacement-room");
        JoinResponse owner, editor, viewer; Transaction accepted, rejected; RoomReplacementReceipt result; string hash;
        using (var store = new RoomStore(root)) {
            var room = new Room(seed, storage: store);
            RoomRegistry.WriteNew(Path.Combine(root, "owner.json"), new { schema = 1, room = seed.room, credential = seed.ownerCredential });
            owner = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Owner", ownerCredential = seed.ownerCredential });
            editor = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Editor", password = seed.password });
            viewer = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Viewer", password = seed.password, viewer = true });
            Reject(() => room.InspectReplacement(editor.token), "owner-required");
            Reject(() => room.InspectReplacement(viewer.token), "owner-required");
            Reject(() => room.InspectReplacement(RoomRegistry.Hash("master-admin-is-not-owner")), "invalid-session");
            accepted = Tx(room.Snapshot, "AddParts", new[] { Cube("old-part", 7) }); Check(room.Commit(editor.token, accepted).accepted, "old accepted outcome fixture");
            rejected = Tx(room.Snapshot, "AddParts", new[] { Cube("bad-part", double.NaN) }, sequence: 2);
            Check(!room.Commit(editor.token, rejected).accepted, "old rejected outcome fixture");
            var stale = ReplacementRequest(room, seed.ownerCredential);
            Check(room.Commit(owner.token, Tx(room.Snapshot, "AddParts", new[] { Cube("intervening", 4) })).accepted, "intervening edit fixture");
            string before = DocumentModel.Hash(room.Snapshot);
            Reject(() => room.Replace(seed.ownerCredential, stale), "replacement-target-changed");
            Check(before == DocumentModel.Hash(room.Snapshot) && store.StorageVersion == 2, "stale confirmation changes neither accepted content nor storage format");
            var invalid = ReplacementRequest(room, seed.ownerCredential); invalid.content.document.parts[0].position[0] = double.NaN;
            Reject(() => room.Replace(seed.ownerCredential, invalid), "invalid-position");
            Check(before == DocumentModel.Hash(room.Snapshot), "invalid portable content leaves accepted scene intact");
            var wrongCatalog = CreationContent(); wrongCatalog.catalog.hash = wrongCatalog.document.catalogHash = "different-catalog";
            Reject(() => room.Replace(seed.ownerCredential, ReplacementRequest(room, seed.ownerCredential, wrongCatalog)), "catalog-mismatch");
            var lease = room.Reserve(editor.token, new LeaseRequest { ids = new[] { "old-part" } });
            room.Preview(editor.token, new Preview { lease = lease.token, sequence = 1, parts = new[] { Cube("old-part", 19) } });
            var request = ReplacementRequest(room, seed.ownerCredential); string localHash = DocumentModel.Hash(request.content.document);
            result = room.Replace(owner.token, request); hash = DocumentModel.Hash(room.Snapshot);
            Check(result.previousGeneration == seed.document.generation && result.generation != result.previousGeneration &&
                result.roomIdentity == seed.document.generation && result.revision == result.previousRevision + 1 && result.documentHash == hash && store.StorageVersion == 3,
                "one authoritative replacement publishes a fresh generation/monotonic revision with a stable identity in SQLite v3");
            Check(room.Snapshot.parts.All(p => p.version == 1) && room.Snapshot.chains.All(c => c.version == 1) && room.Snapshot.tombstones.Length == 0 &&
                room.Snapshot.parts.Any(p => p.catalogId == "CUSTOM") && room.Snapshot.chains.Single().routeVersion == 1 && localHash == DocumentModel.Hash(request.content.document),
                "portable replacement retains custom geometry/mounts/chains without changing the accepted local source");
            Reject(() => room.Reserve(editor.token, new LeaseRequest { ids = new[] { "old-part" } }), "room-replaced");
            Reject(() => room.Preview(editor.token, new Preview { lease = lease.token, sequence = 2, parts = new[] { Cube("old-part") } }), "room-replaced");
            Reject(() => room.Commit(editor.token, accepted), "room-replaced");
            Reject(() => room.GetAsset(viewer.token, room.Snapshot.assets[0].hash), "room-replaced");
            var usage = room.Diagnostics().usage;
            Check(usage.participants.used == 0 && usage.journalEvents.used == 0 && usage.historyEntries == 0 && usage.receipts.used == 3 && usage.historyBytes.used > 0,
                "retirement clears presence/events/personal history but retains receipts and lifetime history budget");
            room.Leave(viewer.token);
            Reject(() => room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = viewer.resumeToken }), "membership-revoked");
            Check(DocumentModel.Hash(room.Snapshot) == hash, "an old generation may explicitly leave its own membership without opening or mutating replacement content");
            Check(room.ReplacementOutcome(seed.ownerCredential, request.operationId).receipt.documentHash == hash &&
                !room.ReplacementOutcome(seed.ownerCredential, Guid.NewGuid().ToString("N")).found, "owner checks complete retained replacement outcomes without a mutation");
            Check(room.Replace(seed.ownerCredential, request).generation == result.generation && DocumentModel.Hash(room.Snapshot) == hash, "duplicate operation identity returns the original outcome without another replacement");
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = editor.resumeToken });
            Reject(() => room.Preview(resumed.token, new Preview { lease = lease.token, sequence = 3, parts = new[] { Cube("old-part") } }), "lease-expired");
            Check(resumed.participant.id == editor.participant.id && resumed.sequence == accepted.sequence &&
                room.Outcome(resumed.token, accepted.operationId, result.previousGeneration).accepted &&
                room.Outcome(resumed.token, rejected.operationId, result.previousGeneration).error == "invalid-position", "same membership resumes with old accepted/rejected outcomes and monotonic sequence");
            Check(room.Outcome(resumed.token, Guid.NewGuid().ToString("N"), result.previousGeneration).error == "operation-not-accepted-before-replacement", "absent old operations have an honest retired-generation outcome");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 3)).error == "nothing-to-undo", "old personal undo does not cross the replacement");
            var staleEdit = Tx(room.Snapshot, "AddParts", new[] { Cube("never-replayed") }, sequence: 4); staleEdit.generation = result.previousGeneration;
            Check(room.Commit(resumed.token, staleEdit).error == "generation-mismatch" && !room.Snapshot.parts.Any(p => p.id == "never-replayed"), "old-generation commands cannot be replayed through a new access session");
            Check(room.GetAsset(resumed.token, room.Snapshot.assets[0].hash).Length > 0, "fresh access hydrates the complete replacement geometry");
            var fresh = Tx(room.Snapshot, "AddParts", new[] { Cube("new-personal", 21) }, sequence: 5);
            Check(room.Commit(resumed.token, fresh).accepted && room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 6)).accepted,
                "new-generation edits get independent usable personal history"); hash = DocumentModel.Hash(room.Snapshot);
        }
        using (var store = new RoomStore(root)) {
            var room = new Room(null, storage: store);
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = editor.resumeToken });
            Check(DocumentModel.Hash(room.Snapshot) == hash && room.Capabilities.roomIdentity == seed.document.generation &&
                room.ReplacementOutcome(seed.ownerCredential, result.operationId).found && room.Outcome(resumed.token, accepted.operationId).accepted,
                "restart retains exact content, immutable room identity, memberships and both outcome types");
        }
        string backup = Path.Combine(directory, "replacement-v3.zip"), restored = Path.Combine(directory, "replacement-restored");
        RoomBackup.Backup(root, backup); RoomBackup.Restore(backup, restored);
        using (var store = new RoomStore(restored)) {
            var room = new Room(null, storage: store); var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = editor.resumeToken });
            Check(DocumentModel.Hash(room.Snapshot) == hash && room.ReplacementOutcome(seed.ownerCredential, result.operationId).found &&
                room.Outcome(resumed.token, accepted.operationId).accepted && store.StorageVersion == 3, "v3 archive round trip retains generation chain and honest old command outcomes");
        }
        string legacy = Environment.GetEnvironmentVariable("PROTOBOT_TEST_REPLACEMENT_LEGACY");
        if (legacy != null) {
            var incompatible = await InstalledCommand(legacy, "serve", "--data-dir", restored, "--listen", "http://127.0.0.1:0");
            Check(incompatible.code == 1 && incompatible.error.Contains("room-storage-version-mismatch"), "actual previous protocol-0.9 helper refuses replaced v3 storage instead of a reverse migration");
        }
        ReplacementRegistryChecks(directory);
        ReplacementFailureChecks(directory);
        await ReplacementCrashChecks(directory);
    }
    private static void ReplacementRegistryChecks(string directory) {
        string root = Path.Combine(directory, "replacement-registry"); RoomRegistry.Initialize(root);
        string config = Path.Combine(root, "server.json"); var seed = HostedSeed("registered"); byte[] registration; string hash; RoomReplacementReceipt result;
        using (var registry = new RoomRegistry(config)) {
            registry.Create(seed); registration = File.ReadAllBytes(Path.Combine(root, "rooms/registered/registration.json"));
            var room = registry.Resolve(seed.room); result = room.Replace(seed.ownerCredential, ReplacementRequest(room, seed.ownerCredential)); hash = DocumentModel.Hash(room.Snapshot);
            Check(File.ReadAllBytes(Path.Combine(root, "rooms/registered/registration.json")).SequenceEqual(registration), "registration anchor is immutable across the one SQLite replacement transaction");
        }
        using (var registry = new RoomRegistry(config)) Check(DocumentModel.Hash(registry.Resolve(seed.room).Snapshot) == hash, "registry restart validates the original anchor against the durable generation chain");
        string backup = Path.Combine(directory, "registered-replacement.zip"); RoomBackup.Backup(Path.Combine(root, "rooms/registered"), backup);
        string other = Path.Combine(directory, "replacement-other-registry"); RoomRegistry.Initialize(other);
        using (var registry = new RoomRegistry(Path.Combine(other, "server.json"))) {
            registry.Restore(backup); Check(registry.Resolve(seed.room).ReplacementOutcome(seed.ownerCredential, result.operationId).found &&
                File.ReadAllBytes(Path.Combine(other, "rooms/registered/registration.json")).SequenceEqual(registration), "registry restore retains the anchor/outcome rather than registering a new generation identity");
            Reject(() => registry.Restore(backup), "room-already-registered");
        }
    }
    private static void ReplacementFailureChecks(string directory) {
        foreach (bool after in new[] { false, true }) {
            string root = Path.Combine(directory, after ? "failed-after" : "failed-before"); var seed = HostedSeed("failure"); string original, operation;
            using (var store = new RoomStore(root)) {
                var room = new Room(seed, storage: store); original = DocumentModel.Hash(room.Snapshot); var request = ReplacementRequest(room, seed.ownerCredential); operation = request.operationId;
                Action<string> fail = stage => { if (stage == "replacement") throw new IOException("injected replacement failure"); };
                if (after) store.AfterCommit = fail; else store.BeforeCommit = fail;
                Reject(() => room.Replace(seed.ownerCredential, request), "room-storage-failed-restart-required");
                Check(!room.IsHealthy && DocumentModel.Hash(room.Snapshot) == original, "ambiguous storage failure freezes before publishing a partial accepted scene");
            }
            using (var store = new RoomStore(root)) {
                var room = new Room(null, storage: store);
                Check(room.ReplacementOutcome(seed.ownerCredential, operation).found == after && (DocumentModel.Hash(room.Snapshot) == original) == !after,
                    "restart decides pre/post-commit failure from durable evidence, preserving the honest outcome");
            }
        }
        string bounded = Path.Combine(directory, "replacement-bounded"); var boundedSeed = HostedSeed("bounded");
        using var boundedStore = new RoomStore(bounded); var boundedRoom = new Room(boundedSeed, storage: boundedStore);
        for (int i = 0; i < RoomReplacement.MaxReplacements; i++) boundedRoom.Replace(boundedSeed.ownerCredential, ReplacementRequest(boundedRoom, boundedSeed.ownerCredential));
        string final = DocumentModel.Hash(boundedRoom.Snapshot);
        Reject(() => boundedRoom.Replace(boundedSeed.ownerCredential, ReplacementRequest(boundedRoom, boundedSeed.ownerCredential)), "room-replacement-limit");
        Check(final == DocumentModel.Hash(boundedRoom.Snapshot), "bounded complete replacement evidence is never silently discarded");
        using var budgetStore = new RoomStore(Path.Combine(directory, "replacement-budget")); var budgetRoom = new Room(HostedSeed("budget"), storage: budgetStore);
        var budget = new ProcessBudget(); budgetRoom.AttachBudget(budget);
        using var occupied = budget.Reserve(ProcessBudget.MaxRetainedBytes, 0, 0);
        string before = DocumentModel.Hash(budgetRoom.Snapshot);
        Reject(() => budgetRoom.Replace(HostedSeed("budget").ownerCredential, ReplacementRequest(budgetRoom, HostedSeed("budget").ownerCredential)), "server-geometry-quota");
        Check(before == DocumentModel.Hash(budgetRoom.Snapshot) && budgetStore.StorageVersion == 2, "process quotas are reserved before durable publication");
    }
    private static int ReplacementCrashWorker(string[] args) {
        string root = args[1], marker = args[2]; var seed = HostedSeed(Path.GetFileName(root));
        using var store = new RoomStore(root); var room = new Room(null, storage: store);
        Action<string> barrier = stage => { if (stage == "replacement") { File.WriteAllText(marker, "ready"); new ManualResetEventSlim(false).Wait(); } };
        if (args[3] == "after") store.AfterCommit = barrier; else store.BeforeCommit = barrier;
        room.Replace(seed.ownerCredential, ReplacementRequest(room, seed.ownerCredential)); return 0;
    }
    private static async Task ReplacementCrashChecks(string directory) {
        foreach (string stage in new[] { "before", "after" }) {
            string root = Path.Combine(directory, "crash-" + stage), marker = Path.Combine(directory, "crash-" + stage + ".signal"); var seed = HostedSeed(Path.GetFileName(root));
            using (var store = new RoomStore(root)) _ = new Room(seed, storage: store);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { Assembly.GetExecutingAssembly().Location, "--replacement-crash-worker", root, marker, stage }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start); var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            try {
                var timer = Stopwatch.StartNew(); while (!File.Exists(marker) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
                Check(File.Exists(marker) && !child.HasExited, "actual crash worker reached exact " + stage + " SQLite boundary");
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } await output; await error; }
            using (var store = new RoomStore(root)) {
                var room = new Room(null, storage: store);
                Check((room.Snapshot.generation != seed.document.generation) == (stage == "after") && room.Capabilities.roomIdentity == seed.document.generation,
                    "actual process kill recovers one complete old/new generation at " + stage + " boundary");
            }
        }
    }
}
