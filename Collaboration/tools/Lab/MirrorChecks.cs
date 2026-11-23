using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task MirrorChecks(string directory) {
        var seed = Fixture();
        seed.document.parts = new[] { Cube("a"), Cube("b", 3) };
        foreach (var part in seed.document.parts) part.version = 1;
        var old = new JoinRequest { catalogHash = seed.catalog.hash, name = "Old player", mirrorVersion = 0 };
        Reject(() => new Room(seed).Join(old), "mirror-client-required");
        var changedHand = WireJson.Clone(seed.document);
        changedHand.parts[0].mirrored = true;
        Check(!DocumentModel.SamePart(seed.document.parts[0], changedHand.parts[0]) &&
            DocumentModel.Hash(seed.document) != DocumentModel.Hash(changedHand), "handedness contributes to document and history identity");
        string storage = Path.Combine(directory, "mirror-room");
        string acceptedHash;
        await using (var server = new CollaborationServer(seed, dataDirectory: storage)) {
            await server.StartAsync();
            using var editor = new CollaborationClient(server.Url, seed.room, seed.catalog, CollaborationTransport.WebSocket);
            using var peer = new CollaborationClient(server.Url, seed.room, seed.catalog, CollaborationTransport.LongPolling);
            await editor.JoinAsync("Mirror editor");
            await peer.JoinAsync("Mirror peer");
            Check(editor.SupportsMirror && peer.SupportsMirror, "both transports negotiate handedness");

            var lease = await editor.ReserveAsync(new[] { "a", "b" });
            var a = editor.Snapshot.parts.Single(part => part.id == "a");
            var b = editor.Snapshot.parts.Single(part => part.id == "b");
            var pose = WireJson.Clone(a);
            pose.mirrored = true;
            var ordinary = await editor.SubmitAsync(editor.CreateTransaction("SetPoses", new[] { pose }, lease: lease.token));
            Check(!ordinary.accepted && ordinary.error == "pose-only", "ordinary Move cannot change handedness");
            Check((await editor.SubmitAsync(editor.CreateTransaction("MirrorParts", new[] { pose }, lease: lease.token))).accepted,
                "handedness-only edit commits with a lease");
            await peer.WaitForRevisionAsync(1);
            Check(peer.Snapshot.parts.Single(part => part.id == "a").mirrored, "remote client sees the reflected part");
            await editor.ReleaseAsync(lease.token);

            var copy = Cube("copy", -3);
            copy.mirrored = true;
            var stale = new VersionRef { id = "a", version = a.version };
            var current = new VersionRef { id = "b", version = b.version };
            lease = await editor.ReserveAsync(new[] { "a", "b" });
            var rejected = await editor.SubmitAsync(editor.CreateTransaction("MirrorAndCopy", new[] { copy }, dependencies: new[] { stale }, lease: lease.token));
            Check(!rejected.accepted && rejected.error == "stale-entity" && editor.Snapshot.parts.Length == 2, "stale copy source cannot publish a partial edit");
            var withoutLease = await editor.SubmitAsync(editor.CreateTransaction("MirrorAndCopy", new[] { copy }, dependencies: new[] { current }));
            Check(!withoutLease.accepted && editor.Snapshot.parts.Length == 2, "copy source requires its reservation");
            Check((await editor.SubmitAsync(editor.CreateTransaction("MirrorAndCopy", new[] { copy }, dependencies: new[] { current }, lease: lease.token))).accepted,
                "copy commits without writing its source");
            await editor.ReleaseAsync(lease.token);
            await peer.WaitForRevisionAsync(2);
            Check(peer.Snapshot.parts.Single(part => part.id == "b").version == b.version, "source version is unchanged by mirror-copy");
            Check((await editor.SubmitAsync(editor.CreateTransaction("Undo", Array.Empty<Part>()))).accepted, "undo mirror-copy");
            await peer.WaitForRevisionAsync(3);
            Check(peer.Snapshot.parts.Length == 2, "undo removes the copy");
            Check((await editor.SubmitAsync(editor.CreateTransaction("Redo", Array.Empty<Part>()))).accepted, "redo mirror-copy");
            await peer.WaitForRevisionAsync(4);
            acceptedHash = DocumentModel.Hash(peer.Snapshot);
            Check(acceptedHash == DocumentModel.Hash(editor.Snapshot) && peer.Snapshot.parts.Single(part => part.id == "copy").mirrored,
                "redo restores the copied handedness and clients converge");
        }
        using var store = new RoomStore(storage);
        var recovered = new Room(null, storage: store);
        Check(DocumentModel.Hash(recovered.Snapshot) == acceptedHash, "SQLite recovery preserves both mirrored edits");
    }
}
