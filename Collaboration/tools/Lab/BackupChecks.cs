using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static async Task BackupChecks(string directory) {
        string root = Path.Combine(directory, "offline backups"); Directory.CreateDirectory(root);
        string data = Path.Combine(root, "source room"), archive = Path.Combine(root, "room.zip"), restored = Path.Combine(root, "restored room");
        var seed = Fixture(); seed.password = "backup-room-password";
        string catalog = Path.Combine(root, "catalog.json"); RecoveryFiles.Write(catalog, seed.catalog);
        string passwordFile = Path.Combine(root, "password.txt"); File.WriteAllText(passwordFile, seed.password); RecoveryFiles.PrivateFile(passwordFile);
        Check((await RunCli("init", "--data-dir", data, "--room", "team", "--catalog", catalog, "--password-file", passwordFile)).code == 0, "backup fixture uses real dedicated CLI initialization");
        byte[] ownerBytes = File.ReadAllBytes(Path.Combine(data, "owner.json"));
        string ownerCredential = JsonNode.Parse(ownerBytes)["credential"].GetValue<string>();
        JoinResponse owner, revoked; Transaction accepted, rejected; string hash; long sequence = 0;
        using (var store = new RoomStore(data)) {
            var room = new Room(null, storage: store);
            owner = room.Join(new JoinRequest { name = "Backup owner", catalogHash = seed.catalog.hash, ownerCredential = ownerCredential });
            revoked = room.Join(new JoinRequest { name = "Revoked member", catalogHash = seed.catalog.hash, password = seed.password }); room.Leave(revoked.token);
            var asset = Asset();
            var route = new ChainRecord { id = "backup-chain", members = new[] { "a", "c", "b" }, routeVersion = 1, wraps = new[] { 0, 1, 0 } };
            accepted = Tx(room.Snapshot, "AddParts", new[] { Cube("a"), Cube("b", 3), Cube("c", 6), Custom("custom", asset) },
                sequence: ++sequence, chains: new[] { route }, assets: new[] { asset });
            Check(room.Commit(owner.token, accepted).accepted, "backup fixture contains custom geometry and explicit inner/outer chain routing");
            var mounts = WireJson.Clone(room.Snapshot.parts.Single(p => p.id == "custom"));
            mounts.mounts = mounts.mounts.Concat(new[] { new CustomMount { position = new[] { 1.0, 0.0, 0.0 }, diameter = .169, depth = .2 } }).ToArray();
            var lease = room.Reserve(owner.token, new LeaseRequest { ids = new[] { "custom" } });
            Check(room.Commit(owner.token, Tx(room.Snapshot, "SetCustomMounts", new[] { mounts }, lease: lease.token, sequence: ++sequence)).accepted, "backup fixture has confirmed mount history");
            lease = room.Reserve(owner.token, new LeaseRequest { ids = new[] { "a" } });
            Check(room.Commit(owner.token, Tx(room.Snapshot, "SetPoses", new[] { Cube("a", 25) }, lease: lease.token, sequence: ++sequence)).accepted, "backup fixture has a pending personal redo after undo");
            Check(room.Commit(owner.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: ++sequence)).accepted, "backup fixture saves its personal history cursor");
            rejected = Tx(room.Snapshot, "SetPoses", new[] { Cube("a", 99) }, sequence: ++sequence);
            Check(!room.Commit(owner.token, rejected).accepted, "backup fixture also contains a rejected durable receipt");
            hash = DocumentModel.Hash(room.Snapshot);
            var busy = await RunCli("backup", "--data-dir", data, "--output", archive);
            Check(busy.code == 1 && busy.error.Contains("room-data-in-use") && !File.Exists(archive), "backup refuses an exclusively owned room without creating an archive");
        }
        // Leave a real committed WAL tail by killing the CLI rather than copying DB/WAL files.
        await using (var child = await RecoveryChild.Start(data, "http://127.0.0.1:0", null)) {
            using var client = new CollaborationClient(child.Ready.url, "team", seed.catalog);
            await client.JoinAsync("WAL writer", seed.password);
            Check((await client.SubmitAsync(client.CreateTransaction("AddParts", new[] { Cube("wal-tail", 42) }))).accepted, "backup crash fixture commits into WAL");
            hash = DocumentModel.Hash(client.Snapshot);
            await child.Kill();
        }
        Check(new FileInfo(Path.Combine(data, "room.sqlite-wal")).Length > 0, "crashed backup source retains its WAL tail");
        var backup = await RunCli("backup", "--data-dir", data, "--output", archive);
        Check(backup.code == 0 && File.Exists(archive), "offline CLI backup captures a consistent SQLite snapshot after a crash: " + backup.error);
        Check(!(backup.output + backup.error).Contains(ownerCredential), "backup logs never disclose the owner capability");
        using (var zip = ZipFile.OpenRead(archive)) {
            Check(zip.Entries.Select(e => e.FullName).OrderBy(s => s).SequenceEqual(new[] { "manifest.json", "owner.json", "room.sqlite" }), "backup includes only its manifest, standalone database and owner capability");
            Check(zip.Entries.All(e => !e.FullName.Contains("wal") && !e.FullName.Contains("lock")), "backup never archives a loose WAL or ownership lock");
        }
        var restore = await RunCli("restore", "--input", archive, "--data-dir", restored);
        Check(restore.code == 0 && !(restore.output + restore.error).Contains(ownerCredential), "CLI restores a complete room without credential output: " + restore.error);
        Check(File.ReadAllBytes(Path.Combine(restored, "owner.json")).SequenceEqual(ownerBytes), "restore preserves the exact operator-owned capability");
        CheckPrivateBackupPath(archive, false); CheckPrivateBackupPath(restored, true);
        foreach (var file in Directory.GetFiles(restored)) CheckPrivateBackupPath(file, false);
        using (var store = new RoomStore(restored)) {
            var room = new Room(null, storage: store);
            Check(room.Recovered && DocumentModel.Hash(room.Snapshot) == hash && room.Snapshot.parts.Any(p => p.id == "wal-tail"), "restored database recovers the exact accepted document including committed WAL state");
            Check(room.Snapshot.chains.Single().routeVersion == 1 && room.Snapshot.chains.Single().wraps.Contains(1) &&
                room.Snapshot.parts.Single(p => p.id == "custom").mounts.Length == 2 && room.Snapshot.assets.Length == 1, "restored chains, ordered mounts and deduplicated geometry are retained");
            Reject(() => room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = revoked.resumeToken }), "invalid-session");
            var resumed = room.Join(new JoinRequest { catalogHash = seed.catalog.hash, resumeToken = owner.resumeToken });
            Check(resumed.participant.id == owner.participant.id && resumed.participant.role == "owner" && resumed.token != owner.token, "restored room retains stable identity and fences prior access tokens");
            Check(room.Outcome(resumed.token, rejected.operationId).error != null && room.Commit(resumed.token, accepted).revision == 1 && DocumentModel.Hash(room.Snapshot) == hash,
                "both receipt outcomes survive restore and accepted operations cannot replay");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Redo", Array.Empty<Part>(), sequence: ++sequence)).accepted && room.Snapshot.parts.Single(p => p.id == "a").position[0] == 25,
                "personal redo cursor survives the archive round trip");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: ++sequence)).accepted, "restored room supports personal undo");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: ++sequence)).accepted && room.Snapshot.parts.Single(p => p.id == "custom").mounts.Length == 1,
                "restored confirmed mounting layouts remain undoable");
            var newOwner = room.Join(new JoinRequest { name = "Operator", catalogHash = seed.catalog.hash, ownerCredential = ownerCredential });
            Check(newOwner.participant.role == "owner", "archived operator credential still grants ownership");
            Reject(() => room.Join(new JoinRequest { name = "Wrong password", catalogHash = seed.catalog.hash, password = "incorrect" }), "wrong-password");
            Check(room.Join(new JoinRequest { name = "Password viewer", catalogHash = seed.catalog.hash, password = seed.password, viewer = true }).participant.role == "viewer",
                "restored password verifier accepts the original password and refuses the wrong one");
        }
        await using (var server = await RecoveryChild.Start(restored, "http://127.0.0.1:0", null)) {
            using var client = new CollaborationClient(server.Ready.url, "team", seed.catalog);
            await client.JoinAsync("Restarted restored room", ownerCredential: ownerCredential);
            Check(server.Ready.recovered && client.Snapshot.parts.Single(p => p.id == "custom").mounts.Length == 1, "a real CLI restart retains post-restore edits/history and authentication");
        }
        byte[] archiveBefore = File.ReadAllBytes(archive), restoredBefore = File.ReadAllBytes(Path.Combine(restored, "room.sqlite"));
        Check((await RunCli("backup", "--data-dir", data, "--output", archive)).code == 1 && File.ReadAllBytes(archive).SequenceEqual(archiveBefore), "backup refuses to overwrite an existing archive");
        Check((await RunCli("restore", "--input", archive, "--data-dir", restored)).code == 1 && File.ReadAllBytes(Path.Combine(restored, "room.sqlite")).SequenceEqual(restoredBefore), "restore refuses to overwrite existing room data");
        string empty = Path.Combine(root, "existing empty"); Directory.CreateDirectory(empty);
        Check((await RunCli("restore", "--input", archive, "--data-dir", empty)).code == 1 && !Directory.EnumerateFileSystemEntries(empty).Any(), "restore refuses even an existing empty directory");
        string existingFile = Path.Combine(root, "existing file"); File.WriteAllText(existingFile, "keep");
        Check((await RunCli("restore", "--input", archive, "--data-dir", existingFile)).code == 1 && File.ReadAllText(existingFile) == "keep", "restore refuses an existing file destination");
        Check((await RunCli("backup", "--data-dir", data, "--output", Path.Combine(data, "nested.zip"))).code == 1, "backup cannot publish an archive inside the room directory");
        string absent = Path.Combine(root, "absent room");
        Check((await RunCli("backup", "--data-dir", absent, "--output", Path.Combine(root, "absent.zip"))).code == 1 && !Directory.Exists(absent), "backup never initializes an absent source room");

        async Task Refuse(string name, Action<ZipArchive> mutate) {
            string broken = Path.Combine(root, name + ".zip"), destination = Path.Combine(root, "failed-" + name);
            File.Copy(archive, broken);
            using (var zip = ZipFile.Open(broken, ZipArchiveMode.Update)) mutate(zip);
            var result = await RunCli("restore", "--input", broken, "--data-dir", destination);
            Check(result.code == 1 && !Directory.Exists(destination) && !(result.output + result.error).Contains(ownerCredential), "restore rejects " + name + " privately without publishing partial data");
            Check(!Directory.EnumerateDirectories(root, ".protobot-backup-*").Any(), "failed " + name + " restore cleans its private staging directory");
        }
        await Refuse("corrupt database", zip => { var bytes = ReadZip(zip, "room.sqlite"); bytes[0] ^= 1; ReplaceZip(zip, "room.sqlite", bytes); });
        await Refuse("corrupt owner", zip => ReplaceZip(zip, "owner.json", Encoding.UTF8.GetBytes("{}")));
        await Refuse("wrong hash", zip => ChangeManifest(zip, node => node["files"][0]["sha256"] = new string('0', 64)));
        await Refuse("wrong length", zip => ChangeManifest(zip, node => node["files"][0]["length"] = 1));
        await Refuse("future format", zip => ChangeManifest(zip, node => node["version"] = 2));
        await Refuse("wrong protocol", zip => ChangeManifest(zip, node => node["protocolVersion"] = "future"));
        await Refuse("wrong storage", zip => ChangeManifest(zip, node => node["storageVersion"] = 99));
        await Refuse("wrong state", zip => ChangeManifest(zip, node => node["generation"] = "wrong-generation"));
        await Refuse("invalid manifest", zip => ReplaceZip(zip, "manifest.json", Encoding.UTF8.GetBytes("{")));
        await Refuse("oversized manifest", zip => ReplaceZip(zip, "manifest.json", new byte[16385]));
        await Refuse("duplicate entry", zip => { using var stream = zip.CreateEntry("owner.json").Open(); stream.Write(ownerBytes); });
        await Refuse("missing entry", zip => zip.GetEntry("owner.json").Delete());
        await Refuse("path traversal", zip => { zip.GetEntry("owner.json").Delete(); ReplaceZip(zip, "../escape-owner.json", ownerBytes); });
        await Refuse("absolute path", zip => { zip.GetEntry("owner.json").Delete(); ReplaceZip(zip, "/escape-owner.json", ownerBytes); });
        await Refuse("backslash path", zip => { zip.GetEntry("owner.json").Delete(); ReplaceZip(zip, "..\\escape-owner.json", ownerBytes); });
        await Refuse("symlink entry", zip => zip.GetEntry("owner.json").ExternalAttributes = unchecked((int)(0xa180u << 16)));
        await Refuse("large central directory", zip => {
            zip.GetEntry("owner.json").Delete();
            ReplaceZip(zip, new string('x', 5000), ownerBytes);
        });
        // Recompute integrity metadata to prove SQLite/owner semantic validation is independent of hashes.
        await Refuse("invalid SQLite with valid hash", zip => {
            byte[] bytes = new byte[4096]; ReplaceZip(zip, "room.sqlite", bytes); Rehash(zip, "room.sqlite", bytes);
        });
        await Refuse("wrong owner with valid hash", zip => {
            var ownerNode = JsonNode.Parse(ownerBytes); ownerNode["credential"] = new string('0', 64);
            byte[] bytes = Encoding.UTF8.GetBytes(ownerNode.ToJsonString()); ReplaceZip(zip, "owner.json", bytes); Rehash(zip, "owner.json", bytes);
        });
        string truncated = Path.Combine(root, "truncated.zip"), truncatedDestination = Path.Combine(root, "failed-truncated");
        File.WriteAllBytes(truncated, archiveBefore.Take(archiveBefore.Length / 2).ToArray());
        Check((await RunCli("restore", "--input", truncated, "--data-dir", truncatedDestination)).code == 1 && !Directory.Exists(truncatedDestination), "restore rejects a truncated archive before publishing");
        string racedArchive = Path.Combine(root, "raced archive.zip");
        var backupRace = await Task.WhenAll(RunCli("backup", "--data-dir", data, "--output", racedArchive), RunCli("backup", "--data-dir", data, "--output", racedArchive));
        Check(backupRace.Count(r => r.code == 0) == 1 && backupRace.Count(r => r.code == 1) == 1, "concurrent backups cannot replace the published archive or bypass source ownership");
        string raced = Path.Combine(root, "raced destination");
        var race = await Task.WhenAll(RunCli("restore", "--input", archive, "--data-dir", raced), RunCli("restore", "--input", archive, "--data-dir", raced));
        Check(race.Count(r => r.code == 0) == 1 && race.Count(r => r.code == 1) == 1, "concurrent restores publish exactly one destination without replacement");
        Check(!Directory.EnumerateDirectories(root, ".protobot-backup-*").Any(), "all backup/restore staging directories are cleaned");
        if (!OperatingSystem.IsWindows()) {
            string link = Path.Combine(root, "dangling destination"); File.CreateSymbolicLink(link, Path.Combine(root, "missing target"));
            Check((await RunCli("restore", "--input", archive, "--data-dir", link)).code == 1 && new FileInfo(link).LinkTarget != null, "restore refuses a dangling symlink destination");
            string ownerPath = Path.Combine(data, "owner.json"), savedOwner = Path.Combine(root, "saved owner.json"); File.Move(ownerPath, savedOwner); File.CreateSymbolicLink(ownerPath, savedOwner);
            Check((await RunCli("backup", "--data-dir", data, "--output", Path.Combine(root, "linked source.zip"))).code == 1, "backup refuses source credential symlinks");
        }
    }
    private static byte[] ReadZip(ZipArchive zip, string name) { using var input = zip.GetEntry(name).Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
    private static void ReplaceZip(ZipArchive zip, string name, byte[] bytes) { zip.GetEntry(name)?.Delete(); using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
    private static void ChangeManifest(ZipArchive zip, Action<JsonNode> edit) { var node = JsonNode.Parse(ReadZip(zip, "manifest.json")); edit(node); ReplaceZip(zip, "manifest.json", Encoding.UTF8.GetBytes(node.ToJsonString())); }
    private static void Rehash(ZipArchive zip, string name, byte[] bytes) => ChangeManifest(zip, node => {
        var entry = node["files"].AsArray().Single(e => e["name"].GetValue<string>() == name);
        entry["length"] = bytes.Length; entry["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    });
    private static void CheckPrivateBackupPath(string path, bool directory) {
        if (OperatingSystem.IsWindows()) {
            using var identity = WindowsIdentity.GetCurrent();
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            bool privateAcl = true;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(identity.User)) privateAcl = false;
            Check(privateAcl, "backup/restore Windows ACL grants access only to the current operator");
        }
        else Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : 0)),
            "backup/restore Unix paths retain private 0700/0600 permissions");
    }
}
