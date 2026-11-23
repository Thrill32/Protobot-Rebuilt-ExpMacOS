using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task EditorReplacementChecks(string directory) {
        foreach (bool allow in new[] { false, true }) {
            string root = Path.Combine(directory, "editor-policy-" + allow); var seed = HostedSeed("editor-policy");
            seed.allowEditorProjectReplacement = allow;
            string editorResume, editorId, hash;
            using (var store = new RoomStore(root)) {
                var room = new Room(seed, storage: store);
                RoomRegistry.WriteNew(Path.Combine(root, "owner.json"), new { schema = 1, room = seed.room, credential = seed.ownerCredential });
                var owner = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Owner", ownerCredential = seed.ownerCredential });
                var editor = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Editor", password = seed.password });
                var viewer = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Viewer", password = seed.password, viewer = true });
                editorResume = editor.resumeToken; editorId = editor.participant.id;
                Check(room.Capabilities.allowEditorProjectReplacement == allow, "creation policy is independent room metadata: " + allow);
                var request = ReplacementRequest(room, owner.token);
                Reject(() => room.Replace(viewer.token, request), "owner-required");
                Reject(() => room.ReplacementOutcome(viewer.token, request.operationId), "owner-required");
                Reject(() => room.ConfigureEditorReplacement(editor.token, !allow), "owner-required");
                if (!allow) {
                    Reject(() => room.InspectReplacement(editor.token), "owner-required");
                    Reject(() => room.Replace(editor.token, request), "owner-required");
                }
                else {
                    Check(RoomReplacement.SameTarget(request.expected, room.InspectReplacement(editor.token)), "allowed editor reads the exact frozen target");
                    room.Commit(owner.token, Tx(room.Snapshot, "AddParts", new[] { Cube("policy-concurrent") }));
                    Reject(() => room.Replace(editor.token, request), "replacement-target-changed");
                    request = ReplacementRequest(room, editor.token);
                }
                var result = room.Replace(allow ? editor.token : owner.token, request);
                Check(room.Capabilities.allowEditorProjectReplacement == allow && room.ReplacementOutcome(seed.ownerCredential, result.operationId).found,
                    "owner/editor replacement retains policy and complete outcome: " + allow);
                Reject(() => room.Replace(editor.token, request), "room-replaced");
                var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = editorResume });
                var prepared = ReplacementRequest(room, seed.ownerCredential);
                room.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = resumed.participant.id });
                Reject(() => room.Replace(resumed.token, prepared), "membership-revoked");
                Reject(() => room.ReplacementOutcome(resumed.token, result.operationId), "membership-revoked");
                hash = DocumentModel.Hash(room.Snapshot);
            }
            // Restart input is never an authority to change stored room policy.
            seed.allowEditorProjectReplacement = !allow;
            using (var store = new RoomStore(root)) {
                var room = new Room(seed, storage: store);
                Check(room.Capabilities.allowEditorProjectReplacement == allow && DocumentModel.Hash(room.Snapshot) == hash,
                    "restart preserves policy despite contradictory bootstrap content");
                Reject(() => room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = editorResume }), "membership-revoked");
                Check(room.Memberships(seed.ownerCredential).memberships.Single(m => m.participant.id == editorId).revoked, "revocation survives replacement and restart");
            }
            string archive = root + ".zip", restored = root + "-restored";
            RoomBackup.Backup(root, archive); RoomBackup.Restore(archive, restored);
            using (var store = new RoomStore(restored)) {
                var room = new Room(null, storage: store);
                Check(room.Capabilities.allowEditorProjectReplacement == allow && DocumentModel.Hash(room.Snapshot) == hash, "archive preserves room policy: " + allow);
            }
            var configured = await RunCli("room-policy", "--data-dir", restored, "--allow-editor-project-replacement", allow ? "no" : "yes");
            Check(configured.code == 0, "actual offline CLI deliberately configures existing room policy");
            using (var store = new RoomStore(restored)) {
                var room = new Room(null, storage: store);
                Check(room.Capabilities.allowEditorProjectReplacement == !allow && DocumentModel.Hash(room.Snapshot) == hash,
                    "explicit policy configuration persists without changing project content/revision");
            }
        }
        // Simulate genuine old metadata with the field absent, rather than merely
        // constructing a new Bootstrap whose CLR default happens to be false.
        string legacy = Path.Combine(directory, "policy-absent"); var legacySeed = HostedSeed("old-policy");
        using (var store = new RoomStore(legacy)) _ = new Room(legacySeed, storage: store);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(legacy, "room.sqlite") + ";Pooling=False")) {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT data FROM info WHERE id=1";
            var metadata = JsonNode.Parse((byte[])command.ExecuteScalar()); metadata.AsObject().Remove("allowEditorProjectReplacement");
            command.CommandText = "UPDATE info SET data=$data WHERE id=1"; command.Parameters.AddWithValue("$data", Encoding.UTF8.GetBytes(metadata.ToJsonString())); command.ExecuteNonQuery();
        }
        using (var store = new RoomStore(legacy)) Check(!new Room(null, storage: store).Capabilities.allowEditorProjectReplacement, "missing legacy policy remains owner-only");
        await EditorReplacementNetworkChecks(directory);
        await EditorReplacementCliChecks(directory);
    }

    private static async Task EditorReplacementNetworkChecks(string directory) {
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
            var seed = HostedSeed("editor-flow"); seed.allowEditorProjectReplacement = true;
            string root = Path.Combine(directory, "editor-flow-" + transport), session = root + "-participant.json";
            await using var server = new CollaborationServer(seed, dataDirectory: root); await server.StartAsync();
            await using var relay = new LoopbackProxy(server.Url); await relay.StartAsync();
            using var owner = new CollaborationClient(server.Url, seed.room, seed.catalog, transport);
            var joined = await owner.JoinAsync("Owner observing", ownerCredential: seed.ownerCredential);
            string oldHash = DocumentModel.Hash(owner.Snapshot), id;
            var local = CreationContent(); string localHash = DocumentModel.Hash(local.document);
            RoomReplacementTarget target;
            using (var editor = new CollaborationClient(relay.Url, seed.room, seed.catalog, recoveryFile: session)) {
                await editor.AuthenticateForReplacementAsync("Local project editor", seed.password); id = editor.Participant.id;
                Check(editor.Participant.role == "editor" && editor.Snapshot == null && !editor.IsConnected, "replacement admission never hydrates/publishes an online document: " + transport);
                target = await editor.InspectRoomReplacementAsync();
                var changed = owner.CreateTransaction("AddParts", new[] { Cube("intervening-editor-flow") });
                Check((await owner.SubmitAsync(changed)).accepted, "owner concurrently edits before editor confirmation");
                await RejectAsync(() => editor.ReplaceRoomProjectAsync(local, target, root + "-attempts"), "replacement-target-changed");
            }
            string pendingId = Guid.NewGuid().ToString("N");
            var saved = JsonNode.Parse(File.ReadAllText(session)); saved["pendingIds"] = new JsonArray(pendingId); saved["pendingGeneration"] = seed.document.generation;
            File.WriteAllText(session, saved.ToJsonString());
            string attempt;
            relay.ContinueReplacementReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var editor = new CollaborationClient(relay.Url, seed.room, seed.catalog, recoveryFile: session)) {
                await editor.AuthenticateForReplacementAsync("Retained editor");
                Check(editor.Participant.id == id && editor.PendingCount == 1 && File.ReadAllText(session).Contains(pendingId),
                    "replacement authentication reuses participant credentials and retains unresolved edits without replay");
                target = await editor.InspectRoomReplacementAsync();
                using var cancel = new CancellationTokenSource();
                var replace = editor.ReplaceRoomProjectAsync(local, target, root + "-attempts", cancel.Token);
                await relay.ReplacementReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Canceled(replace);
                attempt = Directory.GetFiles(root + "-attempts", "*.json").Single();
                Check(!RemoteRoomReplacement.ReadAttempt(attempt).confirmed, "editor retains the ambiguous sent operation");
            }
            relay.ContinueReplacementReply.TrySetResult(true); relay.ContinueReplacementReply = null;
            for (int i = 0; i < 500 && owner.IsConnected; i++) await Task.Delay(20);
            Check(!owner.IsConnected && owner.RequiresResynchronization && owner.Snapshot.generation == joined.document.generation,
                "actual owner client pauses with its previous accepted scene: " + transport);
            using (var editor = new CollaborationClient(relay.Url, seed.room, seed.catalog, recoveryFile: session)) {
                await editor.AuthenticateForReplacementAsync("Outcome checker");
                Check(editor.Participant.id == id && (await editor.CheckRoomReplacementAsync(attempt)).found && relay.ReplacementRequests == 1 &&
                    editor.PendingCount == 1 && JsonNode.Parse(File.ReadAllText(session))["pendingGeneration"].GetValue<string>() == seed.document.generation,
                    "same editor checks the lost outcome after generation fencing without replay or owner credentials");
            }
            await owner.PrepareReconnectAsync(); await owner.JoinAsync("Explicit owner resync", acceptReplacement: true);
            Check(owner.Participant.id == joined.participant.id && owner.Snapshot.generation != joined.document.generation &&
                DocumentModel.Hash(GeometryTransfer.References(owner.Snapshot)) == DocumentModel.Hash(server.Room.Snapshot) &&
                GeometryTransfer.HasGeometry(owner.Snapshot.assets.Single()) && DocumentModel.Hash(local.document) == localHash && oldHash != DocumentModel.Hash(owner.Snapshot),
                "two clients replace/resynchronize actual custom content with stable membership and unchanged local input: " + transport);
            var revoked = server.Room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "Revoked", password = seed.password });
            var current = ReplacementRequest(server.Room, revoked.token);
            server.Room.RemoveParticipant(seed.ownerCredential, new RemoveParticipant { participantId = revoked.participant.id });
            foreach (string suffix in new[] { "/replacement", "/replacement-result?operationId=" + current.operationId }) {
                var refused = await HostingRequest(server.Url, "/rooms/" + seed.room + suffix, revoked.token);
                Check(refused.code != 200 && WireJson.Decode<WireError>(refused.bytes).error == "membership-revoked", "direct API reads enforce revoked membership");
            }
            var mutation = await HostingRequest(server.Url, "/rooms/" + seed.room + "/replacement", revoked.token, HttpMethod.Post, LocalJson.Encode(current));
            Check(mutation.code != 200 && WireJson.Decode<WireError>(mutation.bytes).error == "membership-revoked", "direct API publication enforces revoked membership");
        }
    }

    private static async Task EditorReplacementCliChecks(string directory) {
        string root = Path.Combine(directory, "editor-cli"); RecoveryFiles.CreateDirectory(root);
        string catalog = Path.Combine(root, "catalog.json"), snapshot = Path.Combine(root, "snapshot.json");
        RecoveryFiles.Write(catalog, CreationContent().catalog); RecoveryFiles.Write(snapshot, CreationContent());
        foreach (bool allow in new[] { false, true }) {
            string data = Path.Combine(root, "room-" + allow);
            Check((await RunCli("init", "--data-dir", data, "--room", "cli-policy", "--catalog", catalog, "--allow-editor-project-replacement", allow ? "yes" : "no")).code == 0,
                "actual CLI init selects yes/no independently of project export");
            await using var server = new CollaborationServer(null, dataDirectory: data); await server.StartAsync();
            string target = Path.Combine(root, "target-" + allow + ".json"), session = Path.Combine(root, "session-" + allow + ".json");
            string[] authority = { "--url", server.Url, "--participant-session", session, "--room", "cli-policy", "--catalog", catalog };
            var inspect = await RunCli(new[] { "room", "replacement-target" }.Concat(authority).Concat(new[] { "--output", target }).ToArray());
            Check(inspect.code == (allow ? 0 : 1) && File.Exists(target) == allow, "actual CLI participant target follows room policy: " + allow);
            if (allow) {
                string attempt = Path.Combine(root, "attempt.json");
                var replace = await RunCli(new[] { "room", "replace" }.Concat(authority).Concat(new[] { "--target", target, "--snapshot", snapshot, "--attempt-output", attempt, "--confirm-room", "cli-policy" }).ToArray());
                Check(replace.code == 0 && RemoteRoomReplacement.ReadAttempt(attempt).confirmed, "actual CLI editor replaces using participant credentials");
                var outcome = await RunCli(new[] { "room", "replacement-check" }.Concat(authority).Concat(new[] { "--attempt", attempt }).ToArray());
                Check(outcome.code == 0 && WireJson.Decode<RoomReplacementOutcome>(Encoding.UTF8.GetBytes(outcome.output)).found, "actual CLI editor checks after replacement using retained membership");
            }
        }
        string registry = Path.Combine(root, "registry"); RoomRegistry.Initialize(registry, "key");
        string key = RoomRegistry.Read<RoomCreationKey>(Path.Combine(registry, "creator.json")).credential;
        string admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(registry, "admin.json")).credential;
        await using var shared = new CollaborationServer(null, multiRoomConfig: Path.Combine(registry, "server.json")); await shared.StartAsync();
        using var remote = new RemoteRoomCreation(shared.Url);
        foreach (bool allow in new[] { false, true }) {
            var created = await remote.CreateAsync(CreationContent(), "remote-" + allow, "", key, false, Path.Combine(root, "owners"), CancellationToken.None, allow);
            var room = shared.Registry.Resolve(created.room);
            Check(room.Capabilities.allowEditorProjectReplacement == allow, "remote creation persists explicit yes/no room policy");
            foreach (string credential in new[] { key, admin }) {
                var denied = await HostingRequest(shared.Url, "/rooms/" + created.room + "/replacement", credential, HttpMethod.Post, new byte[] { 0 });
                Check(denied.code != 200, "creator and administrator cannot replace despite enabled editor policy");
            }
            var viewer = room.Join(new JoinRequest { catalogHash = room.Capabilities.catalogHash, name = "API viewer", viewer = true });
            var request = ReplacementRequest(room, created.ownerCredential);
            var rejected = await HostingRequest(shared.Url, "/rooms/" + created.room + "/replacement", viewer.token, HttpMethod.Post, LocalJson.Encode(request));
            Check(rejected.code != 200 && WireJson.Decode<WireError>(rejected.bytes).error == "owner-required", "direct viewer API cannot replace with policy on or off");
            var editor = room.Join(new JoinRequest { catalogHash = room.Capabilities.catalogHash, name = "API editor" });
            var edited = await HostingRequest(shared.Url, "/rooms/" + created.room + "/replacement", editor.token, HttpMethod.Post, LocalJson.Encode(request));
            Check((edited.code == 200) == allow && (allow || WireJson.Decode<WireError>(edited.bytes).error == "owner-required"),
                "direct editor API publication follows the persisted yes/no policy");
        }
        string outputOwner = Path.Combine(root, "cli-created-owner.json");
        var cliCreated = await RunCli("room", "create", "--url", shared.Url, "--creation-key-file", Path.Combine(registry, "creator.json"),
            "--room", "cli-created-policy", "--snapshot", snapshot, "--owner-output", outputOwner, "--allow-editor-project-replacement", "yes");
        Check(cliCreated.code == 0 && File.Exists(outputOwner) && shared.Registry.Resolve("cli-created-policy").Capabilities.allowEditorProjectReplacement,
            "actual remote CLI creation applies Yes independently of snapshot contents");
    }
}
