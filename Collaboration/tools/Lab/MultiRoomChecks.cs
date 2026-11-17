using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static Bootstrap HostedSeed(string name, string catalogHash = "catalog-small-v1") {
        var seed = Fixture(); seed.room = name; seed.password = "password-" + name;
        seed.ownerCredential = RoomRegistry.Hash("private owner " + name);
        seed.document.generation = "generation-" + name;
        seed.catalog.hash = seed.document.catalogHash = catalogHash; return seed;
    }
    private static async Task<(int code, byte[] bytes)> HostingRequest(string url, string path, string token = null, HttpMethod method = null, byte[] body = null) {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url + path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = new ByteArrayContent(body);
        using var response = await http.SendAsync(request); return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
    private static async Task MultiRoomChecks(string directory) {
        foreach (var transport in new[] { CollaborationTransport.WebSocket, CollaborationTransport.LongPolling, CollaborationTransport.ShortPolling }) {
            Console.WriteLine("Multi-room " + transport + ": starting");
            string root = Path.Combine(directory, "multi " + transport), config = Path.Combine(root, "server.json");
            Check((await RunCli("server-init", "--data-dir", root)).code == 0, "CLI initializes a versioned private registry");
            var admin = RoomRegistry.Read<ServerAdministration>(Path.Combine(root, "admin.json"));
            var a = HostedSeed("alpha"); var b = HostedSeed("beta", "different-catalog-v1");
            string aCache = Path.Combine(directory, "alpha-" + transport + ".json"), bCache = Path.Combine(directory, "beta-" + transport + ".json");
            string aId, bId, hashA, hashB, endpoint; Transaction receiptTx; JoinResponse revoked;
            var asset = Asset("Same hash, isolated rooms");
            await using (var server = new CollaborationServer(null, multiRoomConfig: config)) {
                await server.StartAsync(); endpoint = server.Url; var registry = server.Registry;
                Reject(() => new RoomRegistry(config), "server-data-in-use");
                var unknown = await HostingRequest(server.Url, "/rooms/missing/capabilities"); Check(unknown.code == 404, "unknown room never selects a default");
                Check((await HostingRequest(server.Url, "/admin/rooms", a.ownerCredential)).code == 403, "room owner cannot list server rooms");
                Check((await HostingRequest(server.Url, "/admin/rooms", admin.credential)).code == 200, "separate server capability lists rooms");
                Check((await HostingRequest(server.Url, "/admin/rooms", a.ownerCredential, HttpMethod.Post, LocalJson.Encode(a))).code == 403, "room owner cannot create server rooms");
                string ca = Path.Combine(directory, "a-catalog.json"), cb = Path.Combine(directory, "b-catalog.json"); RecoveryFiles.Write(ca, a.catalog); RecoveryFiles.Write(cb, b.catalog);
                string pw = Path.Combine(directory, "multi-password.txt"); File.WriteAllText(pw, a.password);
                var created = await RunCli("room", "create", "--url", server.Url, "--admin-file", Path.Combine(root, "admin.json"), "--room", a.room,
                    "--catalog", ca, "--password-file", pw, "--owner-output", Path.Combine(directory, "owner-" + transport + ".json"));
                Check(created.code == 0 && !created.output.Contains(admin.credential) && !created.output.Contains(a.password), "real CLI creates a room without logging secrets");
                var listed = await RunCli("room", "list", "--url", server.Url, "--admin-file", Path.Combine(root, "admin.json"));
                Check(listed.code == 0 && WireJson.Decode<HostedRoomList>(Encoding.UTF8.GetBytes(listed.output)).rooms.Single().room == "alpha", "CLI lists the registered room without exposing owner/admin secrets");
                // The CLI creates its own fresh generation/owner; use its retained protected capability.
                a.ownerCredential = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "owner-" + transport + ".json")))["credential"].GetValue<string>();
                a.document.generation = registry.Resolve(a.room).Snapshot.generation;
                Check((await HostingRequest(server.Url, "/admin/rooms", admin.credential, HttpMethod.Post, LocalJson.Encode(b))).code == 200, "online admin creates second room with a different catalog");
                Check((await HostingRequest(server.Url, "/admin/rooms", admin.credential, HttpMethod.Post, LocalJson.Encode(b))).code == 409, "duplicate creation refuses overwrite");
                var collision = HostedSeed("BETA"); Reject(() => registry.Create(collision), "room-already-registered");
                var interrupted = HostedSeed("interrupted"); registry.BeforePublish = _ => throw new IOException("Injected registry write failure");
                try { registry.Create(interrupted); throw new Exception("Expected failed registry publish"); } catch (IOException) { Check(true, "failed room publication propagates"); }
                registry.BeforePublish = null;
                Check(!Directory.Exists(Path.Combine(root, "rooms", interrupted.room)) && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "staging")).Any(), "failed creation leaves no registered or partial room");
                using var alice = new CollaborationClient(server.Url, a.room, a.catalog, transport, recoveryFile: aCache);
                using var bob = new CollaborationClient(server.Url, b.room, b.catalog, transport, recoveryFile: bCache);
                await Task.WhenAll(alice.JoinAsync("Alpha owner", ownerCredential: a.ownerCredential), bob.JoinAsync("Beta owner", ownerCredential: b.ownerCredential));
                aId = alice.Participant.id; bId = bob.Participant.id;
                var roomA = registry.Resolve(a.room); var roomB = registry.Resolve(b.room);
                var rawA = roomA.Join(new JoinRequest { name = "Raw alpha", password = a.password, catalogHash = a.catalog.hash });
                var rawB = roomB.Join(new JoinRequest { name = "Raw beta", password = b.password, catalogHash = b.catalog.hash });
                Check((await HostingRequest(server.Url, "/admin/rooms", rawA.token)).code == 403, "participant access cannot administer server");
                foreach (string route in new[] { "receipt?operationId=unknown", "participants", "events?after=0&transient=-1", "assets/" + asset.hash }) {
                    var wrong = await HostingRequest(server.Url, "/rooms/beta/" + route, rawA.token);
                    Check(wrong.code == 401, "alpha access denied by beta " + route.Split('?')[0]);
                }
                foreach (var method in new[] { HttpMethod.Head, HttpMethod.Put }) {
                    var wrong = await HostingRequest(server.Url, "/rooms/beta/assets/" + asset.hash, rawA.token, method, method == HttpMethod.Put ? GeometryTransfer.Encode(asset) : null);
                    Check(wrong.code == 401, "wrong-room hash access denied for " + method);
                }
                Reject(() => roomB.Join(new JoinRequest { catalogHash = b.catalog.hash, resumeToken = rawA.resumeToken }), "invalid-session");
                Reject(() => roomB.Join(new JoinRequest { catalogHash = b.catalog.hash, name = "Wrong owner", ownerCredential = a.ownerCredential }), "wrong-password");
                Reject(() => roomB.Join(new JoinRequest { catalogHash = b.catalog.hash, name = "Server admin", ownerCredential = admin.credential }), "wrong-password");
                var wrongOwner = await HostingRequest(server.Url, "/rooms/beta/participants", a.ownerCredential); Check(wrongOwner.code == 401, "room alpha capability has no beta authority");
                using (var wrongCatalog = new CollaborationClient(server.Url, "beta", a.catalog, transport)) {
                    try { await wrongCatalog.JoinAsync("Wrong catalog", b.password); throw new Exception("Expected catalog rejection"); }
                    catch (CollaborationException error) { Check(error.Code == "catalog-or-room-mismatch", "client checks the selected room catalog"); }
                }
                var discovery = await ConnectionDoctor.RunAsync(server.Url, room: "beta", checkIdle: false); Check(discovery.passed && discovery.catalogHash == b.catalog.hash, "doctor selects room-specific catalog and health");
                var noSelection = await ConnectionDoctor.RunAsync(server.Url, a.catalog, checkIdle: false); Check(!noSelection.passed && noSelection.checks.Any(c => c.code == "room-required"), "doctor requires a room for authenticated multi-room checks");
                receiptTx = alice.CreateTransaction("AddParts", new[] { Cube("same-id", 10) });
                var edits = await Task.WhenAll(alice.SubmitAsync(receiptTx), bob.SubmitAsync(bob.CreateTransaction("AddParts", new[] { Cube("same-id", 30) })));
                Check(edits.All(r => r.accepted), "simultaneous independent rooms accept identical entity IDs");
                await Task.WhenAll(alice.WaitForRevisionAsync(1), bob.WaitForRevisionAsync(1));
                Check(alice.Snapshot.parts.Single().position[0] == 10 && bob.Snapshot.parts.Single().position[0] == 30, "snapshots and event streams remain isolated");
                Check(!roomB.Outcome(rawB.token, receiptTx.operationId).accepted, "operation receipt scope is per membership and room");
                var leaseA = await alice.ReserveAsync(new[] { "same-id" }); var leaseB = await bob.ReserveAsync(new[] { "same-id" });
                var preview = WireJson.Clone(alice.Snapshot.parts.Single()); preview.position[1] = 7;
                await alice.PreviewAsync(new Preview { lease = leaseA.token, sequence = 1, parts = new[] { preview } });
                var presence = await roomB.ReadAsync(rawB.token, 1, -1, CancellationToken.None); Check(presence.previews.Length == 0 && presence.participants.All(p => p.id != aId), "previews and memberships do not cross rooms");
                var forged = bob.CreateTransaction("SetPoses", new[] { Cube("same-id", 99) }, lease: leaseA.token);
                Check((await bob.SubmitAsync(forged)).error == "lease-expired", "lease fencing is room-specific");
                await alice.ReleaseAsync(leaseA.token); await bob.ReleaseAsync(leaseB.token);
                Check((await alice.SubmitAsync(alice.CreateTransaction("Undo", Array.Empty<Part>()))).accepted, "alpha personal undo succeeds");
                Check(roomA.Snapshot.parts.Length == 0 && roomB.Snapshot.parts.Single().position[0] == 30, "alpha undo leaves beta history/content unchanged");
                Check((await alice.SubmitAsync(alice.CreateTransaction("Redo", Array.Empty<Part>()))).accepted, "alpha personal redo succeeds independently");
                await Task.WhenAll(alice.UploadAssetAsync(asset), bob.UploadAssetAsync(asset));
                Check(roomA.GetAsset(rawA.token, asset.hash).SequenceEqual(roomB.GetAsset(rawB.token, asset.hash)), "same geometry hash is stored/authenticated independently");
                var onlyA = Asset("Alpha exclusive asset"); await alice.UploadAssetAsync(onlyA);
                Check((await HostingRequest(server.Url, "/rooms/beta/assets/" + onlyA.hash, rawB.token)).code == 409, "knowing a hash does not fetch another room's geometry");
                Console.WriteLine("Multi-room " + transport + ": isolation passed, transfers/quotas");
                await SimultaneousRoomTransfers(server, a, b, rawA, rawB, alice, bob);
                await ContendedCreation(server, admin, b, alice);
                foreach (string resource in new[] { "retained", "decoded", "scene" }) {
                    var usage = registry.Budget.Usage;
                    using var capacity = registry.Budget.Reserve(resource == "retained" ? ProcessBudget.MaxRetainedBytes - usage.retained : 0,
                        resource == "decoded" ? ProcessBudget.MaxDecodedBytes - usage.decoded : 0, resource == "scene" ? ProcessBudget.MaxSceneBytes - usage.scene : 0);
                    if (resource == "scene") Check((await bob.SubmitAsync(bob.CreateTransaction("AddParts", new[] { Custom("over-scene", asset) }, assetWrites: new[] { GeometryTransfer.Reference(asset) }))).error == "server-geometry-quota", "process live-instance budget rejects before edit publication");
                    else {
                        var extra = Asset("Quota overflow " + resource);
                        var result = await HostingRequest(server.Url, "/rooms/alpha/assets/" + extra.hash, rawA.token, HttpMethod.Put, GeometryTransfer.Encode(extra));
                        Check(Encoding.UTF8.GetString(result.bytes).Contains("server-geometry-quota"), "process " + resource + " budget bounds asset growth");
                    }
                    Check(roomA.IsHealthy && roomB.IsHealthy, "quota refusal does not freeze healthy rooms");
                }
                Check((await alice.SubmitAsync(alice.CreateTransaction("AddParts", new[] { Custom("custom-alpha", asset) }, assetWrites: new[] { GeometryTransfer.Reference(asset) }))).accepted, "alpha references its durable geometry");
                Check((await bob.SubmitAsync(bob.CreateTransaction("AddParts", new[] { Custom("custom-beta", asset) }, assetWrites: new[] { GeometryTransfer.Reference(asset) }))).accepted, "beta references its own geometry");
                revoked = roomA.Join(new JoinRequest { name = "Removed alpha", password = a.password, catalogHash = a.catalog.hash });
                roomA.RemoveParticipant(a.ownerCredential, new RemoveParticipant { participantId = revoked.participant.id });
                Reject(() => roomB.RemoveParticipant(a.ownerCredential, new RemoveParticipant { participantId = rawB.participant.id }), "invalid-session");
                var thirdRoom = registry.Resolve("transfer-third");
                var thirdMember = thirdRoom.Join(new JoinRequest { name = "Third edit failure", catalogHash = a.catalog.hash, password = "password-transfer-third" });
                thirdRoom.PutAsset(thirdMember.token, asset.hash, GeometryTransfer.Encode(asset));
                var thirdStore = (RoomStore)typeof(Room).GetField("store", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(thirdRoom);
                thirdStore.BeforeCommit = stage => { if (stage == "edit") throw new IOException("Injected referencing edit write"); };
                var editBudget = registry.Budget.Usage;
                Reject(() => thirdRoom.Commit(thirdMember.token, Tx(thirdRoom.Snapshot, "AddParts", new[] { Custom("failed-custom", asset) }, assets: new[] { GeometryTransfer.Reference(asset) })), "room-storage-failed-restart-required");
                Check(registry.Budget.Usage == editBudget && roomB.IsHealthy, "failed referencing edit releases process capacity without freezing beta");
                // Failed durable writes freeze only this room, and release reserved process capacity.
                var storeA = (RoomStore)typeof(Room).GetField("store", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(roomA);
                var before = registry.Budget.Usage; storeA.BeforeCommit = stage => { if (stage == "asset") throw new IOException("Injected room write"); };
                var failedAsset = Asset("Failed alpha write"); Reject(() => roomA.PutAsset(rawA.token, failedAsset.hash, GeometryTransfer.Encode(failedAsset)), "room-storage-failed-restart-required");
                Check(registry.Budget.Usage == before, "failed persistence rolls back global accounting");
                Check((await bob.SubmitAsync(bob.CreateTransaction("AddParts", new[] { Cube("beta-survives", 44) }))).accepted, "unrelated room edits continue after another room freezes");
                var statuses = registry.List(); Check(statuses.rooms.Single(r => r.room == "alpha").status.Contains("failed") && statuses.rooms.Single(r => r.room == "beta").status == "ready", "operator list distinguishes failed and healthy rooms");
                hashA = DocumentModel.Hash(roomA.Snapshot); hashB = DocumentModel.Hash(roomB.Snapshot);
                alice.Dispose(); bob.Dispose();
                Check((await RunCli("backup", "--data-dir", Path.Combine(root, "rooms", "beta"), "--output", Path.Combine(directory, "online-" + transport + ".zip"))).code == 1, "per-room backup refuses a running registry-owned room");
            }
            Console.WriteLine("Multi-room " + transport + ": restart/restore");
            // Emulate a process killed in private creation/restore staging.
            string restoreStage = Path.Combine(root, "staging", ".protobot-backup-" + Guid.NewGuid().ToString("N")); RecoveryFiles.CreateDirectory(restoreStage);
            File.WriteAllText(Path.Combine(restoreStage, "room.sqlite"), "partial restore");
            // Never becomes a room.
            string abandoned = Path.Combine(root, "staging", "create-" + Guid.NewGuid().ToString("N")); RecoveryFiles.CreateDirectory(abandoned); File.WriteAllText(Path.Combine(abandoned, "owner.json"), "partial");
            await using (var child = await MultiChild.Start(config, endpoint)) {
                Check(child.Ready.multiRoom && child.Ready.registryVersion == 1 && child.Ready.room == null, "dedicated process readiness has no implicit room");
                using var alice = new CollaborationClient(child.Ready.url, a.room, a.catalog, transport, recoveryFile: aCache);
                using var bob = new CollaborationClient(child.Ready.url, b.room, b.catalog, transport, recoveryFile: bCache);
                await Task.WhenAll(alice.JoinAsync("Recovered alpha"), bob.JoinAsync("Recovered beta"));
                Check(alice.Participant.id == aId && bob.Participant.id == bId && DocumentModel.Hash(alice.Snapshot) == hashA && DocumentModel.Hash(bob.Snapshot) == hashB, "process restart restores both distinct documents, catalogs and cached identities");
                Check(!Directory.Exists(abandoned) && !Directory.Exists(restoreStage), "restart discards interrupted unregistered creation");
                var duplicate = await alice.SubmitAsync(receiptTx);
                Check(duplicate.accepted && duplicate.revision == 1 && DocumentModel.Hash(alice.Snapshot) == hashA, "accepted receipt survives restart without replaying edit");
                await child.Kill();
            }
            string archiveA = Path.Combine(directory, "alpha-" + transport + ".zip"), archiveB = Path.Combine(directory, "beta-" + transport + ".zip");
            Check((await RunCli("backup", "--data-dir", Path.Combine(root, "rooms", "alpha"), "--output", archiveA)).code == 0 &&
                (await RunCli("backup", "--data-dir", Path.Combine(root, "rooms", "beta"), "--output", archiveB)).code == 0, "offline per-room archives preserve independent rooms");
            await RegistryRestoreChecks(directory, transport, root, config, archiveA, archiveB, a, b, hashA, hashB, revoked);
        }
        await RegistryFailureChecks(directory);
    }
    private static async Task<JoinResponse> RawResume(string url, Bootstrap seed, string resume) => WireJson.Decode<JoinResponse>((await HostingRequest(url, "/rooms/" + seed.room + "/join", method: HttpMethod.Post,
        body: WireJson.Encode(new JoinRequest { room = seed.room, catalogHash = seed.catalog.hash, resumeToken = resume }))).bytes);

    private sealed class MultiChild : IAsyncDisposable {
        private Process process; private Task<string> errors, output;
        public Ready Ready;
        public static async Task<MultiChild> Start(string config, string listen = "http://127.0.0.1:0") {
            var child = new MultiChild(); var start = new ProcessStartInfo { FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { Path.GetFullPath("src/Cli/bin/Debug/net10.0/protobot-collab.dll"), "serve", "--multi-room", "--config", config, "--listen", listen }) start.ArgumentList.Add(arg);
            child.process = Process.Start(start); child.errors = child.process.StandardError.ReadToEndAsync(); child.process.StandardInput.Close();
            try {
                string line = await child.process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                if (line == null) throw new Exception("Multi-room startup failed: " + await child.errors);
                child.Ready = WireJson.Decode<Ready>(Encoding.UTF8.GetBytes(line)); child.output = child.process.StandardOutput.ReadToEndAsync(); return child;
            }
            catch { await child.DisposeAsync(); throw; }
        }
        public async Task Kill() { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
        public async ValueTask DisposeAsync() { if (process == null) return; await Kill(); await errors; if (output != null) await output; process.Dispose(); process = null; }
    }
}
