using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Protobot;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task AppearanceChecks(string root) {
        string package = Path.Combine(root, "appearance-package");
        Directory.CreateDirectory(Path.Combine(package, "materials"));
        string model = Path.Combine(package, "test.obj");
        string mtl = Path.Combine(package, "materials", "finish.mtl");
        const string faces = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\n" +
            "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvt .5 .5\n" +
            "usemtl red\nf 1 2 3\nusemtl map\nf -4/-1 -2/-3 -1/-2\n";
        File.WriteAllText(model, "mtllib materials/finish.mtl\n" + faces);
        File.WriteAllText(mtl, "newmtl red\nKd .85 .1 .2\nnewmtl map\nKd 1 1 1\n");
        var color = CustomModelImporter.Read(model, 1);
        Check(color.appearance.materials[0].r == .85f && color.appearance.faceMaterials.SequenceEqual(new[] { 0, 1 }) &&
            color.appearance.uv.Length == 0, "OBJ preserves ordered diffuse colors and per-triangle material assignments");

        var imagePath = Path.Combine(package, "materials", "checker.png");
        byte[] sourceImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+X2WQAAAAASUVORK5CYII=");
        File.WriteAllBytes(imagePath, sourceImage);
        File.AppendAllText(mtl, "map_Kd checker.png\n");
        var parsed = CustomModelImporter.ReadPackage(model, 1, out var images);
        Check(images.Count == 1 && images[0].SequenceEqual(sourceImage) && parsed.vertices.Length == 18 &&
            parsed.appearance.uv.Contains(.5f) && parsed.appearance.materials[1].texture == 0,
            "negative independent UV indices split seams and resolve the map beside its MTL");
        var rgb = new byte[] { 255, 0, 88 };
        parsed.appearance.textures[0].SetPixels(rgb);
        var painted = new GeometryAsset { name = parsed.name, vertices = parsed.vertices.Select(v => (double)v).ToArray(),
            triangles = parsed.triangles, appearance = parsed.appearance };
        painted.hash = DocumentModel.GeometryHash(painted);
        var bytes = GeometryTransfer.Encode(painted);
        var decoded = GeometryTransfer.Decode(bytes);
        Check(decoded.appearance.SameAs(painted.appearance) && decoded.appearance.textures[0].GetPixels().SequenceEqual(rgb),
            "canonical geometry transfer round trips all appearance data");
        Reject(() => GeometryTransfer.Encode(painted, bytes.Length - 1), "local-serialization-limit");
        var plain = new GeometryAsset { name = painted.name, vertices = painted.vertices, triangles = painted.triangles };
        plain.hash = DocumentModel.GeometryHash(plain);
        Check(plain.hash != painted.hash && !System.Text.Encoding.UTF8.GetString(GeometryTransfer.Encode(plain)).Contains("appearance"),
            "appearance changes identity while plain assets retain their original wire shape");
        decoded.appearance.materials[0].r = .5f;
        Reject(() => GeometryTransfer.Encode(decoded), "asset-hash-mismatch");
        decoded.appearance.textures[0].pixels = "xxxx=";
        Reject(() => GeometryTransfer.Encode(decoded), "invalid-appearance-texture");
        decoded.appearance.textures[0].width = 2049;
        Reject(() => GeometryTransfer.Encode(decoded), "appearance-texture-dimensions");
        decoded = GeometryTransfer.Decode(bytes);
        decoded.appearance.materials = new DiffuseMaterial[CustomAppearance.MaxMaterials + 1];
        Reject(() => GeometryTransfer.Encode(decoded), "invalid-appearance");
        decoded = GeometryTransfer.Decode(bytes);
        decoded.appearance.textures = new DiffuseTexture[CustomAppearance.MaxTextures + 1];
        Reject(() => GeometryTransfer.Encode(decoded), "invalid-appearance");
        var overRgb = new CustomAppearance { materials = new[] { new DiffuseMaterial { texture = 0 }, new DiffuseMaterial { texture = 1 } },
            faceMaterials = new[] { 0, 1 }, uv = new float[12], textures = Enumerable.Range(0, 2)
                .Select(_ => new DiffuseTexture { width = 2048, height = 2048 }).ToArray() };
        Reject(() => overRgb.Validate(6, 2, pendingPixels: true), "appearance-texture-budget");
        Reject(() => GeometryTransfer.RequireAppearanceSupport(true, 0), "appearance-helper-required");
        GeometryTransfer.RequireAppearanceSupport(false, 0);
        File.Delete(imagePath);
        try { CustomModelImporter.ReadPackage(model, 1, out _); throw new Exception("Missing texture was accepted"); }
        catch (FileNotFoundException) { Check(true, "missing declared texture fails before model creation"); }
        File.WriteAllBytes(imagePath, sourceImage);
        foreach (string map in new[] { "https://example.invalid/checker.png", "../outside.png", "/tmp/checker.png", "-s 1 1 1 checker.png" }) {
            File.WriteAllText(mtl, "newmtl red\nKd 1 0 0\nnewmtl map\nmap_Kd " + map);
            try { CustomModelImporter.ReadPackage(model, 1, out _); throw new Exception("Out-of-package or unsupported map was accepted"); }
            catch (InvalidDataException) { Check(true, "out-of-package paths and map options fail closed"); }
        }
        string link = Path.Combine(package, "materials", "link.png");
        if (!OperatingSystem.IsWindows()) {
            File.CreateSymbolicLink(link, imagePath);
            File.WriteAllText(mtl, "newmtl red\nKd 1 0 0\nnewmtl map\nmap_Kd link.png");
            try { CustomModelImporter.ReadPackage(model, 1, out _); throw new Exception("Symbolic link was accepted"); }
            catch (InvalidDataException) { Check(true, "model dependencies cannot follow symbolic links"); }
        }
        Directory.Delete(package, true);

        var seed = Fixture();
        string data = Path.Combine(root, "appearance-room");
        JoinResponse owner;
        string priorHash;
        using (var store = new RoomStore(data)) {
            var room = new Room(seed, storage: store);
            RoomRegistry.WriteNew(Path.Combine(data, "owner.json"), new { schema = 1, room = seed.room, credential = seed.ownerCredential });
            Reject(() => room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "old", customAppearanceVersion = 0 }),
                "appearance-client-required");
            owner = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "owner", ownerCredential = seed.ownerCredential });
            room.PutAsset(owner.token, painted.hash, bytes);
            room.PutAsset(owner.token, painted.hash, bytes);
            Check(store.StorageVersion == 4 && room.GeometryUsage.bytes == bytes.Length, "first appearance upload fences old storage readers and duplicates charge once");
            Check(room.Commit(owner.token, Tx(room.Snapshot, "AddParts", new[] { Custom("visual", painted) },
                assets: new[] { GeometryTransfer.Reference(painted) })).accepted, "appearance is accepted by immutable reference");
            var editor = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "late editor" });
            Check(GeometryTransfer.Decode(room.GetAsset(editor.token, painted.hash)).appearance.SameAs(painted.appearance),
                "a late editor obtains appearance independently of the source files");
            var changed = WireJson.Clone(room.Snapshot.parts.Single());
            changed.hasPaint = true; changed.color = new[] { .4, .7, .3 };
            var lease = room.Reserve(owner.token, new LeaseRequest { ids = new[] { changed.id } });
            Check(room.Commit(owner.token, Tx(room.Snapshot, "SetParts", new[] { changed }, lease: lease.token, sequence: 2)).accepted,
                "paint overrides remain per instance");
            room.Release(owner.token, lease.token);
            Check(room.Commit(owner.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 3)).accepted &&
                !room.Snapshot.parts.Single().hasPaint && room.Commit(owner.token, Tx(room.Snapshot, "Redo", Array.Empty<Part>(), sequence: 4)).accepted &&
                room.Snapshot.parts.Single().hasPaint && room.Snapshot.assets.Single().hash == painted.hash,
                "paint Undo and Redo preserve the original appearance reference");
            priorHash = DocumentModel.Hash(room.Snapshot);
        }
        using (var store = new RoomStore(data)) {
            var room = new Room(null, storage: store);
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken });
            Check(resumed.participant.id == owner.participant.id && DocumentModel.Hash(room.Snapshot) == priorHash, "appearance survives a durable restart and resume");
            var content = new RoomExport { schema = 2, catalog = seed.catalog, document = new Document {
                catalogHash = seed.catalog.hash, parts = new[] { Custom("replacement", painted) }, assets = new[] { painted } } };
            content.document.parts[0].version = 1;
            var oldExport = new RoomExport { catalog = seed.catalog, document = content.document };
            Reject(oldExport.Validate, "room-export-version-mismatch");
            var request = ReplacementRequest(room, seed.ownerCredential, LocalJson.Clone(content));
            room.Replace(seed.ownerCredential, request);
            var editor = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, name = "replacement editor" });
            Check(room.Snapshot.parts.Single().id == "replacement" && store.StorageVersion == 4 &&
                GeometryTransfer.Decode(room.GetAsset(editor.token, painted.hash)).appearance.SameAs(painted.appearance),
                "room replacement atomically retains embedded appearance and schema fencing");

            var large = Asset("quota appearance");
            var tex = new DiffuseTexture { width = 2048, height = 2048 };
            tex.SetPixels(new byte[tex.RgbBytes]);
            large.appearance = new CustomAppearance { materials = new[] { new DiffuseMaterial { texture = 0 } },
                faceMaterials = new[] { 0 }, uv = new[] { 0f, 0f, 1f, 0f, 0f, 1f }, textures = new[] { tex } };
            large.hash = DocumentModel.GeometryHash(large);
            var payload = GeometryTransfer.Encode(large);
            room.PutAsset(editor.token, large.hash, payload);
            string hash = DocumentModel.Hash(room.Snapshot);
            var candidates = Enumerable.Range(0, 3).Select(i => Custom("over-" + i, large)).ToArray();
            Check(room.Commit(editor.token, Tx(room.Snapshot, "AddParts", candidates,
                assets: new[] { GeometryTransfer.Reference(large) })).error == "asset-scene-quota" &&
                DocumentModel.Hash(room.Snapshot) == hash, "repeated texture instances charge live resources and quota rejection is atomic");
            Console.WriteLine($"Appearance 2048x2048: RGB={tex.RgbBytes} encoded={payload.Length} room-cost={GeometryTransfer.MaterializedBytes(large)}");
        }
        string legacy = Environment.GetEnvironmentVariable("PROTOBOT_TEST_APPEARANCE_LEGACY");
        if (legacy != null) {
            var result = await InstalledCommand(legacy, "serve", "--data-dir", data, "--listen", "http://127.0.0.1:0");
            Check(result.code == 1 && result.error.Contains("room-storage-version-mismatch"), "actual previous helper refuses appearance storage explicitly");
        }
        string backup = Path.Combine(root, "appearance.zip"), restored = Path.Combine(root, "appearance-restored");
        RoomBackup.Backup(data, backup); RoomBackup.Restore(backup, restored);
        using (var store = new RoomStore(restored)) {
            var room = new Room(null, storage: store);
            Check(room.Snapshot.parts.Single().id == "replacement" && store.StorageVersion == 4, "appearance archives restore their content and compatibility fence");
        }
        var colorAsset = new GeometryAsset { name = color.name, vertices = color.vertices.Select(v => (double)v).ToArray(),
            triangles = color.triangles, appearance = color.appearance };
        colorAsset.hash = DocumentModel.GeometryHash(colorAsset);
        var plainColorMesh = new GeometryAsset { name = colorAsset.name, vertices = colorAsset.vertices, triangles = colorAsset.triangles };
        plainColorMesh.hash = DocumentModel.GeometryHash(plainColorMesh);
        Console.WriteLine($"Color fixture: plain={GeometryTransfer.Encode(plainColorMesh).Length} color={GeometryTransfer.Encode(colorAsset).Length}");
    }
}
