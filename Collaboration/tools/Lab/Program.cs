using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
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
            ModelChecks(); RoomChecks(); HistoryChecks();
            await DurabilityChecks(directory);
            await DeploymentChecks(directory);
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
    private static ChainRecord Chain(string id, params string[] members) => new ChainRecord { id = id, members = members };
    private static GeometryAsset Asset(string name = "Lab custom triangle") {
        var asset = new GeometryAsset { name = name, vertices = new[] { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 }, triangles = new[] { 0, 1, 2 } };
        asset.hash = DocumentModel.GeometryHash(asset); return asset;
    }
    private static Part Custom(string id, GeometryAsset asset, double x = 0) => new Part { id = id, catalogId = "CUSTOM", assetHash = asset.hash,
        position = new[] { x, 0.0, 0.0 }, mounts = new[] { new CustomMount { position = new[] { .25, .5, 0.0 },
            rotation = new[] { 0.0, 0.0, 0.0, 1.0 }, diameter = .169, depth = .046, threaded = true } } };
    private static Transaction Tx(Document document, string kind, Part[] parts, string[] deletes = null, string lease = null, long sequence = 1,
        ChainRecord[] chains = null, string[] chainDeletes = null, GeometryAsset[] assets = null) => new Transaction {
        generation = document.generation, sequence = sequence, kind = kind, writes = parts, deletes = deletes ?? Array.Empty<string>(), lease = lease,
        chainWrites = chains ?? Array.Empty<ChainRecord>(), chainDeletes = chainDeletes ?? Array.Empty<string>(),
        expected = parts.Select(p => p.id).Concat(deletes ?? Array.Empty<string>()).Concat((chains ?? Array.Empty<ChainRecord>()).SelectMany(c => c.members))
            .Concat((chainDeletes ?? Array.Empty<string>()).SelectMany(id => DocumentModel.Chains(document).FirstOrDefault(c => c.id == id)?.members ?? Array.Empty<string>()))
            .Distinct().Select(id => new VersionRef { id = id, version = document.parts.FirstOrDefault(p => p.id == id)?.version ??
                document.tombstones.FirstOrDefault(p => p.id == id)?.version ?? 0 }).ToArray(),
        expectedChains = (chains ?? Array.Empty<ChainRecord>()).Select(c => c.id).Concat(chainDeletes ?? Array.Empty<string>()).Distinct()
            .Select(id => new VersionRef { id = id, version = DocumentModel.Chains(document).FirstOrDefault(c => c.id == id)?.version ??
                DocumentModel.ChainTombstones(document).FirstOrDefault(c => c.id == id)?.version ?? 0 }).ToArray(),
        assetWrites = assets ?? Array.Empty<GeometryAsset>()
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
        var mechanismPose = Tx(added, "SetPoses", new[] { Cube("b", 7) });
        mechanismPose.expectedRevision = added.revision;
        Check(WireJson.Clone(mechanismPose).expectedRevision == added.revision, "mechanism revision precondition survives wire serialization");
        Reject(() => DocumentModel.Reduce(moved, mechanismPose, seed.catalog), "stale-document");
        Check(both.parts.Single(p => p.id == "b").position[0] == 7, "old global revision permits disjoint edit");
        var reversed = WireJson.Clone(both); Array.Reverse(reversed.parts);
        reversed.parts[0].rotation = new double[] { 0, 0, 0, -1 };
        Check(DocumentModel.Hash(reversed) == DocumentModel.Hash(both), "canonical order and quaternion sign");
        reversed.parts[0].state = "changed";
        Check(DocumentModel.Hash(reversed) != DocumentModel.Hash(both), "saved state included in hash");
        var chained = DocumentModel.Reduce(both, Tx(both, "SetChains", Array.Empty<Part>(), chains: new[] { Chain("chain-ab", "b", "a") }), seed.catalog);
        Check(chained.chains.Single().version == 1 && chained.chains.Single().members.SequenceEqual(new[] { "a", "b" }), "chain create is versioned and canonical");
        var compound = DocumentModel.Reduce(both, Tx(both, "AddParts", new[] { Cube("copy-a", 20), Cube("copy-b", 24) },
            chains: new[] { Chain("chain-copy", "copy-b", "copy-a") }), seed.catalog);
        Check(compound.parts.Count(p => p.id.StartsWith("copy-", StringComparison.Ordinal)) == 2 &&
            compound.chains.Single().members.SequenceEqual(new[] { "copy-a", "copy-b" }), "parts and their chain create atomically");
        var movedAndCopied = DocumentModel.Reduce(both, Tx(both, "TransformAndCopy", new[] { Cube("a", 2), Cube("alt-a", 20), Cube("alt-b", 24) },
            chains: new[] { Chain("alt-chain", "alt-a", "alt-b") }), seed.catalog);
        Check(movedAndCopied.parts.Single(p => p.id == "a").position[0] == 2 && movedAndCopied.chains.Single().members.SequenceEqual(new[] { "alt-a", "alt-b" }),
            "transform and copied chain create atomically");
        Reject(() => DocumentModel.Reduce(both, Tx(both, "AddParts", new[] { Cube("copy-only", 20) },
            chains: new[] { Chain("chain-cross", "a", "copy-only") }), seed.catalog), "invalid-chain-mutation");
        Reject(() => DocumentModel.Reduce(chained, Tx(chained, "TransformAndCopy", new[] { Cube("chain-ab") }), seed.catalog), "identity-in-use");
        Reject(() => DocumentModel.Reduce(chained, Tx(chained, "SetChains", Array.Empty<Part>(), chains: new[] { Chain("duplicate-ab", "a", "b") }), seed.catalog),
            "duplicate-chain");
        var withThird = DocumentModel.Reduce(chained, Tx(chained, "AddParts", new[] { Cube("c", 8) }), seed.catalog);
        Reject(() => DocumentModel.Reduce(withThird, Tx(withThird, "SetChains", Array.Empty<Part>(), chains: new[] { Chain("chain-ab", "b", "c") }), seed.catalog),
            "missing-chain-member-version");
        var reorderedChain = WireJson.Clone(chained); Array.Reverse(reorderedChain.chains[0].members);
        Check(DocumentModel.Hash(reorderedChain) == DocumentModel.Hash(chained), "chain member order is canonical in document hash");
        var staleChain = Tx(chained, "SetChains", Array.Empty<Part>(), chainDeletes: new[] { "chain-ab" }); staleChain.expectedChains[0].version--;
        Reject(() => DocumentModel.Reduce(chained, staleChain, seed.catalog), "stale-chain");
        Reject(() => DocumentModel.Reduce(chained, Tx(chained, "DeleteParts", Array.Empty<Part>(), new[] { "a" }), seed.catalog), "missing-chain-member");
        var cascade = DocumentModel.Reduce(chained, Tx(chained, "DeleteParts", Array.Empty<Part>(), new[] { "a" },
            chainDeletes: new[] { "chain-ab" }), seed.catalog);
        Check(cascade.chains.Length == 0 && cascade.chainTombstones.Single().version == 2, "part and dependent chain delete atomically");
        var deleted = DocumentModel.Reduce(both, Tx(both, "DeleteParts", Array.Empty<Part>(), new[] { "a" }), seed.catalog);
        Check(deleted.parts.Length == 1 && deleted.tombstones.Single().version == 3, "delete tombstone has next version");
        var reuse = Tx(deleted, "AddParts", new[] { Cube("a") }); reuse.expected[0].version = 3;
        Reject(() => DocumentModel.Reduce(deleted, reuse, seed.catalog), "identity-in-use");
        var unknown = Tx(both, "ReplaceDocument", new[] { Cube("a") });
        Reject(() => DocumentModel.Reduce(both, unknown, seed.catalog), "unsupported-mutation");

        var asset = Asset();
        var custom = DocumentModel.Reduce(both, Tx(both, "AddParts", new[] { Custom("custom-a", asset, 30) }, assets: new[] { asset }), seed.catalog);
        Check(DocumentModel.Assets(custom).Single().hash == asset.hash && DocumentModel.Mounts(custom.parts.Single(p => p.id == "custom-a")).Single().threaded,
            "custom geometry and ordered mounts create atomically");
        var mountWrite = WireJson.Clone(custom.parts.Single(p => p.id == "custom-a"));
        mountWrite.mounts = mountWrite.mounts.Concat(new[] { new CustomMount { position = new[] { .75, .5, 0.0 },
            rotation = new[] { 0.0, 0.0, 0.0, 1.0 }, diameter = .25, depth = .125, threaded = false } }).ToArray();
        var customMounts = DocumentModel.Reduce(custom, Tx(custom, "SetCustomMounts", new[] { mountWrite }), seed.catalog);
        Check(DocumentModel.Mounts(customMounts.parts.Single(p => p.id == "custom-a")).Length == 2 &&
            customMounts.parts.Single(p => p.id == "custom-a").assetHash == asset.hash && DocumentModel.Assets(customMounts).Single().hash == asset.hash,
            "whole-instance mounting layout changes without mutating geometry");
        var forgedMountPose = WireJson.Clone(mountWrite); forgedMountPose.position[1] = 4;
        Reject(() => DocumentModel.Reduce(custom, Tx(custom, "SetCustomMounts", new[] { forgedMountPose }), seed.catalog), "mounts-only");
        Reject(() => DocumentModel.Reduce(custom, Tx(custom, "SetParts", new[] { mountWrite }), seed.catalog), "custom-content-immutable");
        var alternateAsset = Asset("Alternate immutable triangle");
        var twoAssets = WireJson.Clone(custom); twoAssets.assets = twoAssets.assets.Concat(new[] { alternateAsset }).ToArray();
        var forgedGeometry = WireJson.Clone(twoAssets.parts.Single(p => p.id == "custom-a")); forgedGeometry.assetHash = alternateAsset.hash;
        Reject(() => DocumentModel.Reduce(twoAssets, Tx(twoAssets, "SetCustomMounts", new[] { forgedGeometry }), seed.catalog), "mounts-only");
        var customMove = Custom("custom-a", asset, 31);
        customMove.mounts = WireJson.Clone(custom.parts.Single(p => p.id == "custom-a").mounts);
        var customMoved = DocumentModel.Reduce(custom, Tx(custom, "SetPoses", new[] { customMove }), seed.catalog);
        Check(customMoved.parts.Single(p => p.id == "custom-a").position[0] == 31 && DocumentModel.Assets(customMoved).Length == 1,
            "custom pose reuses immutable geometry");
        var customCopy = DocumentModel.Reduce(customMoved, Tx(customMoved, "AddParts", new[] { Custom("custom-copy", asset, 40) }), seed.catalog);
        Check(customCopy.parts.Count(p => p.catalogId == "CUSTOM") == 2 && DocumentModel.Assets(customCopy).Length == 1,
            "custom copies deduplicate document geometry");
        Reject(() => DocumentModel.Reduce(both, Tx(both, "AddParts", new[] { Custom("missing-asset", asset) }), seed.catalog), "missing-asset");
        var corruptAsset = WireJson.Clone(asset); corruptAsset.vertices[0] = 2;
        Reject(() => DocumentModel.Reduce(both, Tx(both, "AddParts", new[] { Custom("bad-asset", corruptAsset) }, assets: new[] { corruptAsset }), seed.catalog), "asset-hash-mismatch");
        Reject(() => DocumentModel.Reduce(custom, Tx(custom, "AddParts", new[] { Custom("duplicate-asset", asset) }, assets: new[] { asset }), seed.catalog),
            "asset-already-exists");
        var reorderedAssets = WireJson.Clone(customCopy); Array.Reverse(reorderedAssets.parts); Array.Reverse(reorderedAssets.assets);
        Check(DocumentModel.Hash(reorderedAssets) == DocumentModel.Hash(customCopy), "custom assets participate canonically in document hash");
        var atAssetCapacity = WireJson.Clone(both);
        atAssetCapacity.assets = Enumerable.Range(0, DocumentModel.MaxAssets).Select(index => Asset("Capacity asset " + index)).ToArray();
        DocumentModel.Validate(atAssetCapacity, seed.catalog);
        var overflowAsset = Asset("Capacity overflow");
        Reject(() => DocumentModel.Reduce(atAssetCapacity, Tx(atAssetCapacity, "AddParts", new[] { Custom("asset-overflow", overflowAsset) },
            assets: new[] { overflowAsset }), seed.catalog), "document-limit");
    }

    private static void RoomChecks() {
        long now = 0;
        var seed = Fixture();
        seed.document.parts = new[] { Cube("a"), Cube("b") };
        foreach (var part in seed.document.parts) part.version = 1;
        var authority = new Room(seed, () => now);
        var oldClient = new JoinRequest { catalogHash = seed.catalog.hash, name = "Old client", version = "0.1.0-alpha" };
        Reject(() => authority.Join(oldClient), "server-version-mismatch");
        JoinRequest Join(string name, bool viewer = false) => new JoinRequest { name = name, catalogHash = seed.catalog.hash, viewer = viewer };
        var alice = authority.Join(Join("Alice"));
        var bob = authority.Join(Join("Bob"));
        var view = authority.Join(Join("Viewer", true));
        Reject(() => authority.Reserve(view.token, new LeaseRequest { ids = new[] { "a" } }), "viewer-read-only");
        var hold = authority.Reserve(alice.token, new LeaseRequest { ids = new[] { "a", "b" } });
        Reject(() => authority.Reserve(bob.token, new LeaseRequest { ids = new[] { "b" } }), "parts-reserved");
        var preview = new Preview { lease = hold.token, sequence = 2, parts = new[] { Cube("a", 3), Cube("b", 3) } };
        authority.Preview(alice.token, preview);
        var snapshot = authority.Join(new JoinRequest { resumeToken = bob.resumeToken, catalogHash = seed.catalog.hash });
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
        Reject(() => capacity.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = inactive.resumeToken }), "room-full");
    }

    private static void HistoryChecks() {
        var seed = Fixture();
        seed.catalog.entries = seed.catalog.entries.Concat(new[] { new CatalogEntry { id = "PLATE" } }).ToArray();
        var room = new Room(seed);
        var alice = room.Join(new JoinRequest { name = "Alice", catalogHash = seed.catalog.hash });
        var bob = room.Join(new JoinRequest { name = "Bob", catalogHash = seed.catalog.hash });
        var document = seed.document;
        long aliceSequence = 0, bobSequence = 0;
        Receipt Commit(JoinResponse author, string kind, Part[] writes, string[] deletes = null, ChainRecord[] chains = null, string[] chainDeletes = null,
            GeometryAsset[] assets = null) {
            var ids = writes.Select(p => p.id).Concat(deletes ?? Array.Empty<string>()).Where(id => document.parts.Any(p => p.id == id))
                .Concat((chains ?? Array.Empty<ChainRecord>()).SelectMany(c => c.members).Where(id => document.parts.Any(p => p.id == id)))
                .Concat((chainDeletes ?? Array.Empty<string>()).SelectMany(id => DocumentModel.Chains(document).FirstOrDefault(c => c.id == id)?.members ?? Array.Empty<string>()))
                .Distinct().ToArray();
            string lease = ids.Length == 0 || kind == "Undo" || kind == "Redo" ? null : room.Reserve(author.token, new LeaseRequest { ids = ids }).token;
            var tx = Tx(document, kind, writes, deletes, lease, author == alice ? ++aliceSequence : ++bobSequence, chains, chainDeletes, assets);
            var receipt = room.Commit(author.token, tx);
            if (receipt.accepted) {
                var edit = room.ReadAsync(author.token, document.revision, -1, CancellationToken.None).GetAwaiter().GetResult().edits.Single();
                document = DocumentModel.Reduce(document, edit.transaction, seed.catalog);
                Check(document.revision == receipt.revision && DocumentModel.Hash(document) == edit.hash, "history event replays to authority hash");
                Check(room.Commit(author.token, tx).revision == receipt.revision, "history receipt deduplicates");
            }
            if (lease != null) room.Release(author.token, lease);
            return receipt;
        }
        Receipt History(JoinResponse author, bool redo = false) => Commit(author, redo ? "Redo" : "Undo", Array.Empty<Part>());
        Part Part(string id) => WireJson.Clone(document.parts.Single(p => p.id == id));
        var customAsset = Asset("History custom triangle");
        Check(Commit(alice, "AddParts", new[] { Custom("history-custom", customAsset) }, assets: new[] { customAsset }).accepted,
            "custom part and geometry history create");
        Check(History(alice).accepted && document.parts.Length == 0 && DocumentModel.Assets(document).Single().hash == customAsset.hash,
            "custom undo retains immutable geometry for redo");
        Check(History(alice, true).accepted && document.parts.Single().assetHash == customAsset.hash, "custom redo restores the same geometry reference");
        var mountLayout = Part("history-custom");
        mountLayout.mounts = mountLayout.mounts.Concat(new[] { new CustomMount { position = new[] { 1.0, 0.0, 0.0 },
            rotation = new[] { 0.0, 0.0, 0.0, 1.0 }, diameter = .2, depth = .1 } }).ToArray();
        Check(Commit(alice, "SetCustomMounts", new[] { mountLayout }).accepted && DocumentModel.Mounts(Part("history-custom")).Length == 2,
            "mount layout history commit accepted");
        Check(History(alice).accepted && DocumentModel.Mounts(Part("history-custom")).Length == 1 && Part("history-custom").assetHash == customAsset.hash,
            "mount layout undo restores order without changing geometry");
        Check(History(alice, true).accepted && DocumentModel.Mounts(Part("history-custom")).Length == 2,
            "mount layout redo restores confirmed component");
        Check(History(alice).accepted && History(alice).accepted && document.parts.Length == 0, "custom history fixture reset");
        Check(Commit(alice, "AddParts", new[] { Cube("paste-a", -10), Cube("paste-b", -6) },
            chains: new[] { Chain("paste-chain", "paste-a", "paste-b") }).accepted && DocumentModel.Chains(document).Single().id == "paste-chain",
            "compound part/chain import needs no lease for fresh endpoints");
        Check(History(alice).accepted && document.parts.Length == 0 && DocumentModel.Chains(document).Length == 0,
            "compound import undo removes parts and chain together");
        Check(History(alice, true).accepted && document.parts.Length == 2 && DocumentModel.Chains(document).Single().id == "paste-chain",
            "compound import redo restores parts and stable chain identity");
        Check(History(alice).accepted, "compound import fixture reset");
        Check(Commit(alice, "AddParts", new[] { Cube("a"), Cube("b", 10) }).accepted, "history fixture create");
        Check(Commit(bob, "AddParts", new[] { Cube("c", 20) }).accepted, "other participant create");
        Check(Commit(alice, "SetChains", Array.Empty<Part>(), chains: new[] { Chain("chain-ab", "a", "b") }).accepted, "chain create accepted");
        Check(History(alice).accepted && DocumentModel.Chains(document).Length == 0, "personal undo removes own chain");
        Check(History(alice, true).accepted && DocumentModel.Chains(document).Single().id == "chain-ab", "personal redo restores chain identity with newer version");
        var bobEndpoint = room.Reserve(bob.token, new LeaseRequest { ids = new[] { "a" } });
        var rewriteLease = room.Reserve(alice.token, new LeaseRequest { ids = new[] { "b", "c" } });
        var rewrite = Tx(document, "SetChains", Array.Empty<Part>(), lease: rewriteLease.token, sequence: ++aliceSequence,
            chains: new[] { Chain("chain-ab", "b", "c") });
        rewrite.expected = rewrite.expected.Concat(new[] { new VersionRef { id = "a", version = Part("a").version } }).ToArray();
        Check(room.Commit(alice.token, rewrite).error == "lease-set-mismatch", "chain topology replacement requires old and new endpoint leases");
        room.Release(alice.token, rewriteLease.token); room.Release(bob.token, bobEndpoint.token);
        Check(Commit(alice, "SetChains", Array.Empty<Part>(), chainDeletes: new[] { "chain-ab" }).accepted, "chain removal accepted");
        var paint = Part("a"); paint.hasPaint = true; paint.color = new double[] { .2, .3, .4 };
        Check(Commit(alice, "SetParts", new[] { paint }).accepted, "leased painting accepted");
        var replacement = Part("a"); replacement.catalogId = "PLATE";
        Check(Commit(alice, "SetParts", new[] { replacement }).accepted, "stable-ID property replacement accepted");
        Check(History(alice).accepted && Part("a").catalogId == "CUBE" && Part("a").hasPaint, "undo replacement preserves paint");
        Check(History(alice).accepted && !Part("a").hasPaint, "sequential own undo rebases increasing versions");
        Check(History(alice, true).accepted && History(alice, true).accepted && Part("a").catalogId == "PLATE", "sequential redo restores content");
        long beforeDelete = Part("a").version;
        Check(Commit(alice, "DeleteParts", Array.Empty<Part>(), new[] { "a" }).accepted, "history delete");
        Check(History(alice).accepted && Part("a").version == beforeDelete + 2 && !document.tombstones.Any(p => p.id == "a"), "undo delete resurrects same identity with a newer version");
        Check(History(alice, true).accepted && !document.parts.Any(p => p.id == "a"), "redo delete");
        Check(History(alice).accepted, "restore before compound copy");
        var moving = Part("a"); moving.position[0] = 30;
        var copy = Part("a"); copy.id = "copy";
        var copyB = Part("b"); copyB.id = "copy-b";
        Check(Commit(alice, "TransformAndCopy", new[] { moving, copy, copyB }, chains: new[] { Chain("copy-chain", "copy", "copy-b") }).accepted,
            "atomic Alt transform plus copied chain create");
        Check(History(alice).accepted && Part("a").position[0] == 0 && !document.parts.Any(p => p.id == "copy") &&
            !DocumentModel.Chains(document).Any(c => c.id == "copy-chain"), "compound undo restores source and removes copies/chain");
        Check(History(alice, true).accepted && Part("a").position[0] == 30 && document.parts.Any(p => p.id == "copy") &&
            DocumentModel.Chains(document).Any(c => c.id == "copy-chain"), "compound redo resurrects copies and chain");
        Check(History(bob).accepted && !document.parts.Any(p => p.id == "c") && document.parts.Any(p => p.id == "copy"), "personal undo preserves other participants' operations");
        var held = room.Reserve(bob.token, new LeaseRequest { ids = new[] { "a" } });
        Check(History(alice).error == "parts-reserved", "undo cannot bypass another live reservation");
        room.Release(bob.token, held.token);
        var remote = Part("a"); remote.position[1] = 8;
        Check(Commit(bob, "SetPoses", new[] { remote }).accepted, "remote move after own edit");
        Check(History(alice).error == "history-changed-by-another-participant" && Part("a").position[1] == 8, "stale inverse never overwrites a remote edit");
        Check(History(bob).accepted && History(alice).error == "history-changed-by-another-participant", "remote undo does not revive an invalidated inverse");
        var forged = Tx(document, "RestoreParts", new[] { Part("a") }, sequence: ++aliceSequence);
        Check(room.Commit(alice.token, forged).error == "authority-only-mutation", "clients cannot forge inverse content");
        var dependency = Tx(document, "AddParts", new[] { Cube("anchored") }, sequence: ++aliceSequence);
        dependency.expected = dependency.expected.Concat(new[] { new VersionRef { id = "b", version = Part("b").version - 1 } }).ToArray();
        Check(room.Commit(alice.token, dependency).error == "stale-entity" && !document.parts.Any(p => p.id == "anchored"), "stale placement target rejects create atomically");
        bob = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = bob.resumeToken });
        Check(History(bob, true).accepted && Part("a").position[1] == 8, "resumed identity retains personal redo history");
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
            var chainMembers = new[] { "part0", "part1" };
            var chainSnapshot = clients[0].Snapshot;
            var chainLease = await clients[0].ReserveAsync(chainMembers);
            var chainTx = clients[0].CreateTransaction("SetChains", Array.Empty<Part>(), dependencies: chainMembers.Select(id => new VersionRef {
                id = id, version = chainSnapshot.parts.Single(p => p.id == id).version
            }).ToArray(), lease: chainLease.token, chainWrites: new[] { Chain("network-chain", chainMembers) });
            var chainReceipt = await clients[0].SubmitAsync(chainTx);
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(chainReceipt.revision)));
            Check(chainReceipt.accepted && clients.All(c => DocumentModel.Chains(c.Snapshot).Single().id == "network-chain") &&
                clients.Select(c => DocumentModel.Hash(c.Snapshot)).Distinct().Count() == 1, transport + " chain create converges");
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
            var deleteSnapshot = clients[0].Snapshot;
            var deleteLease = await clients[0].ReserveAsync(chainMembers);
            var deletion = clients[0].CreateTransaction("DeleteParts", Array.Empty<Part>(), new[] { "part0" },
                dependencies: chainMembers.Select(id => new VersionRef { id = id, version = deleteSnapshot.parts.Single(p => p.id == id).version }).ToArray(),
                lease: deleteLease.token, chainDeletes: new[] { "network-chain" }, chainDependencies: new[] {
                    new VersionRef { id = "network-chain", version = DocumentModel.Chains(deleteSnapshot).Single().version }
                });
            var deleteReceipt = await clients[0].SubmitAsync(deletion);
            Check(deleteReceipt.accepted, "network part and chain delete accepted");
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(deleteReceipt.revision)));
            Check(clients.All(c => c.Snapshot.parts.Length == count - 1 && c.Snapshot.tombstones.Length == 1 &&
                DocumentModel.Chains(c.Snapshot).Length == 0 && DocumentModel.ChainTombstones(c.Snapshot).Length == 1), "part and chain delete propagate to every client");
            var restoreTopology = await clients[0].SubmitAsync(clients[0].CreateTransaction("Undo", Array.Empty<Part>()));
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(restoreTopology.revision)));
            Check(restoreTopology.accepted && clients.All(c => c.Snapshot.parts.Any(p => p.id == "part0") && DocumentModel.Chains(c.Snapshot).Single().id == "network-chain"),
                transport + " undo restores deleted part and chain atomically");
            var removeTopology = await clients[0].SubmitAsync(clients[0].CreateTransaction("Redo", Array.Empty<Part>()));
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(removeTopology.revision)));
            Check(removeTopology.accepted && clients.All(c => !c.Snapshot.parts.Any(p => p.id == "part0") && DocumentModel.Chains(c.Snapshot).Length == 0),
                transport + " redo removes part and chain atomically");
            var paint = WireJson.Clone(clients[1].Snapshot.parts.Single(p => p.id == "part1"));
            paint.hasPaint = true; paint.color = new double[] { .2, .4, .6 };
            var paintLease = await clients[1].ReserveAsync(new[] { paint.id });
            Check((await clients[1].SubmitAsync(clients[1].CreateTransaction("SetParts", new[] { paint }, lease: paintLease.token))).accepted, transport + " paint accepted");
            Check((await clients[1].SubmitAsync(clients[1].CreateTransaction("Undo", Array.Empty<Part>()))).accepted && !clients[1].Snapshot.parts.Single(p => p.id == paint.id).hasPaint, transport + " personal undo");
            var redo = await clients[1].SubmitAsync(clients[1].CreateTransaction("Redo", Array.Empty<Part>()));
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(redo.revision)));
            Check(redo.accepted && clients.All(c => c.Snapshot.parts.Single(p => p.id == paint.id).hasPaint) && clients.Select(c => DocumentModel.Hash(c.Snapshot)).Distinct().Count() == 1, transport + " property history converges");
            var mechanism = clients[1].CreateTransaction("SetPoses", new[] { WireJson.Clone(paint) });
            mechanism.expectedRevision = clients[1].Snapshot.revision;
            var mechanismLease = await clients[1].ReserveAsync(new[] { paint.id }); mechanism.lease = mechanismLease.token;
            await clients[0].SubmitAsync(clients[0].CreateTransaction("AddParts", new[] { Cube("new-mechanism-part", 300) }));
            Check((await clients[1].SubmitAsync(mechanism)).error == "stale-document", transport + " topology changes fence a privately evaluated mechanism result");
            await clients[1].ReleaseAsync(mechanismLease.token);
            var topologyUndo = await clients[0].SubmitAsync(clients[0].CreateTransaction("Undo", Array.Empty<Part>()));
            await Task.WhenAll(clients.Select(c => c.WaitForRevisionAsync(topologyUndo.revision)));
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
        start.ArgumentList.Add("--listen"); start.ArgumentList.Add("http://127.0.0.1:0"); start.ArgumentList.Add("--ephemeral");
        using var child = Process.Start(start);
        try {
            var stderr = child.StandardError.ReadToEndAsync();
            await child.StandardInput.WriteLineAsync(WireJson.Text(Fixture())); await child.StandardInput.FlushAsync();
            var read = child.StandardOutput.ReadLineAsync();
            Check(await Task.WhenAny(read, Task.Delay(15000)) == read, "child readiness bounded");
            var ready = WireJson.Decode<Ready>(System.Text.Encoding.UTF8.GetBytes(await read ?? ""));
            Check(ready.type == "ready" && new Uri(ready.url).Port > 0, "CLI readiness reports actual bound port");
            Check(ready.version == CollaborationProtocol.Version, "CLI readiness reports the required collaboration version");
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
