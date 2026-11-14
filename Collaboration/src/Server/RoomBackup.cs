using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Protobot.Collaboration.Server {
    // Offline, single-room recovery format. No general-purpose archive extraction,
    // network schema, storage migration or external asset store is involved.
    public static class RoomBackup {
        private const long MaxDatabaseBytes = 1024L * 1024 * 1024;
        private const int MaxMetadataBytes = 16 * 1024;
        private const UnixFileMode FileModePrivate = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        private sealed class Manifest {
            public string format { get; set; }
            public int version { get; set; }
            public int storageVersion { get; set; }
            public string protocolVersion { get; set; }
            public string room { get; set; }
            public string generation { get; set; }
            public string catalogHash { get; set; }
            public long revision { get; set; }
            public string documentHash { get; set; }
            public Entry[] files { get; set; }
        }
        private sealed class Entry {
            public string name { get; set; }
            public long length { get; set; }
            public string sha256 { get; set; }
        }
        [Serializable] private sealed class Owner {
            public int schema = 1;
            public string room = null, credential = null;
        }

        public static void Backup(string dataDirectory, string output) {
            string source = Path.GetFullPath(dataDirectory), target = Path.GetFullPath(output);
            RequireNew(target, "backup-output-exists");
            DocumentModel.Require(Directory.Exists(source), "backup-room-not-found");
            string relative = Path.GetRelativePath(source, target);
            DocumentModel.Require(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative), "backup-output-inside-room");
            Regular(source);
            foreach (string name in new[] { "room.sqlite", "owner.json", "room.lock", "room.sqlite-wal", "room.sqlite-shm" }) {
                string path = Path.Combine(source, name);
                if (Exists(path)) Regular(path);
            }
            DocumentModel.Require(File.Exists(Path.Combine(source, "room.sqlite")) && File.Exists(Path.Combine(source, "owner.json")), "backup-requires-initialized-cli-room");
            using var store = new RoomStore(source); // Same exclusive ownership as serve; never steal/unlink the lock.
            var state = store.Load();
            byte[] owner = ReadOwner(source, state);
            string staging = Stage(target);
            try {
                string database = Path.Combine(staging, "room.sqlite");
                using (PrivateFile(database)) { }
                store.BackupDatabase(database);
                using (var file = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
                DocumentModel.Require(new FileInfo(database).Length <= MaxDatabaseBytes, "backup-size-limit");
                Write(Path.Combine(staging, "owner.json"), owner);
                var manifest = new Manifest {
                    format = "protobot-room-backup", version = 1, storageVersion = 1, protocolVersion = CollaborationProtocol.Version,
                    room = state.info.room, generation = state.info.generation, catalogHash = state.info.catalog.hash,
                    revision = state.document.revision, documentHash = DocumentModel.Hash(state.document),
                    files = new[] { Describe(database), Describe(Path.Combine(staging, "owner.json")) }
                };
                string archivePath = Path.Combine(staging, "backup.zip");
                using (var file = PrivateFile(archivePath)) {
                    using (var archive = new ZipArchive(file, ZipArchiveMode.Create, true)) {
                        Add(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                        foreach (var entry in manifest.files) {
                            using var input = File.OpenRead(Path.Combine(staging, entry.name));
                            using var entryStream = ZipEntry(archive, entry.name).Open(); input.CopyTo(entryStream);
                        }
                    }
                    file.Flush(true);
                }
                // No-overwrite move also preserves the private file's mode/ACL.
                File.Move(archivePath, target);
            }
            finally { Directory.Delete(staging, true); }
        }

        public static void Restore(string input, string dataDirectory) {
            string target = Path.GetFullPath(dataDirectory);
            RequireNew(target, "restore-requires-new-directory");
            using var source = new FileStream(Path.GetFullPath(input), FileMode.Open, FileAccess.Read, FileShare.Read);
            DocumentModel.Require(source.Length <= MaxDatabaseBytes + 4 * MaxMetadataBytes, "backup-size-limit");
            ValidateEnvelope(source);
            string staging = Stage(target);
            try {
                Manifest manifest;
                using (var archive = new ZipArchive(source, ZipArchiveMode.Read, true)) {
                    string[] names = { "manifest.json", "room.sqlite", "owner.json" };
                    DocumentModel.Require(archive.Entries.Count == names.Length &&
                        archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(names.OrderBy(n => n, StringComparer.Ordinal)), "backup-invalid-entries");
                    foreach (var entry in archive.Entries) {
                        int type = (entry.ExternalAttributes >> 16) & 0xf000;
                        DocumentModel.Require((type == 0 || type == 0x8000) && (entry.ExternalAttributes & 0x10) == 0, "backup-invalid-entry-type");
                    }
                    var metadata = archive.GetEntry("manifest.json");
                    using (var bytes = new MemoryStream()) {
                        using var stream = metadata.Open(); Copy(stream, bytes, metadata.Length, MaxMetadataBytes);
                        manifest = JsonSerializer.Deserialize<Manifest>(bytes.ToArray(), Json);
                    }
                    DocumentModel.Require(manifest != null && manifest.format == "protobot-room-backup", "backup-format-mismatch");
                    DocumentModel.Require(manifest.version == 1 && manifest.storageVersion == 1 && manifest.protocolVersion == CollaborationProtocol.Version, "backup-version-mismatch");
                    DocumentModel.Require(manifest.files?.Length == 2 && manifest.files.All(e => e != null) &&
                        manifest.files.Select(e => e.name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "owner.json", "room.sqlite" }), "backup-invalid-manifest");
                    foreach (var expected in manifest.files) {
                        var entry = archive.GetEntry(expected.name);
                        DocumentModel.Require(entry.Length == expected.length && expected.sha256?.Length == 64, "backup-integrity-failed");
                        using var content = entry.Open();
                        using var file = PrivateFile(Path.Combine(staging, expected.name));
                        string hash = Copy(content, file, expected.length, expected.name == "room.sqlite" ? MaxDatabaseBytes : MaxMetadataBytes);
                        DocumentModel.Require(hash == expected.sha256, "backup-integrity-failed"); file.Flush(true);
                    }
                }
                using (var store = new RoomStore(staging)) {
                    var state = store.Load(); ReadOwner(staging, state);
                    DocumentModel.Require(state.info.room == manifest.room && state.info.generation == manifest.generation &&
                        state.info.catalog.hash == manifest.catalogHash && state.document.revision == manifest.revision &&
                        DocumentModel.Hash(state.document) == manifest.documentHash, "backup-state-mismatch");
                }
                PublishDirectory(staging, target);
            }
            catch (InvalidDataException) { throw new CollaborationException("backup-archive-invalid"); }
            catch (JsonException) { throw new CollaborationException("backup-manifest-invalid"); }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        private static byte[] ReadOwner(string directory, RoomArchive state) {
            string path = Path.Combine(directory, "owner.json");
            using var file = File.OpenRead(path); using var bytes = new MemoryStream();
            Copy(file, bytes, file.Length, MaxMetadataBytes);
            byte[] data = bytes.ToArray(); var owner = WireJson.Decode<Owner>(data);
            DocumentModel.Require(owner != null && owner.schema == 1 && owner.room == state.info.room &&
                !string.IsNullOrEmpty(owner.credential) && owner.credential.Length < 256 &&
                DocumentModel.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(owner.credential))) == state.info.ownerHash, "backup-owner-mismatch");
            return data;
        }
        private static Entry Describe(string path) {
            using var file = File.OpenRead(path);
            return new Entry { name = Path.GetFileName(path), length = file.Length, sha256 = DocumentModel.Hex(SHA256.HashData(file)) };
        }
        private static void ValidateEnvelope(FileStream source) {
            // Bound central-directory allocation before ZipArchive materializes it.
            // V1 is a small, single-disk ZIP without ZIP64 or an archive comment.
            DocumentModel.Require(source.Length >= 22, "backup-archive-invalid");
            source.Position = source.Length - 22; var end = new byte[22]; source.ReadExactly(end);
            DocumentModel.Require(BinaryPrimitives.ReadUInt32LittleEndian(end) == 0x06054b50 &&
                BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(4)) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(6)) == 0 &&
                BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(8)) == 3 && BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(10)) == 3 &&
                BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(20)) == 0, "backup-invalid-entries");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(12)), offset = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(16));
            DocumentModel.Require(size > 0 && size <= 4096 && (long)offset + size == source.Length - 22, "backup-archive-invalid");
            source.Position = 0;
        }
        private static string Copy(Stream input, Stream output, long length, long maximum) {
            DocumentModel.Require(length > 0 && length <= maximum, "backup-size-limit");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024]; long total = 0; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                total += count; DocumentModel.Require(total <= length, "backup-integrity-failed");
                output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count);
            }
            DocumentModel.Require(total == length, "backup-integrity-failed");
            return DocumentModel.Hex(hash.GetHashAndReset());
        }
        private static ZipArchiveEntry ZipEntry(ZipArchive archive, string name) {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            entry.ExternalAttributes = unchecked((int)(0x8180u << 16)); // Regular file, 0600; restore never trusts archived permissions.
            return entry;
        }
        private static void Add(ZipArchive archive, string name, byte[] bytes) { using var stream = ZipEntry(archive, name).Open(); stream.Write(bytes); }
        private static void Write(string path, byte[] bytes) { using var file = PrivateFile(path); file.Write(bytes); file.Flush(true); }
        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget != null;
        private static void RequireNew(string path, string code) => DocumentModel.Require(!Exists(path), code);
        private static void Regular(string path) => DocumentModel.Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "backup-source-link-refused");

        private static string Stage(string target) {
            string parent = Path.GetDirectoryName(target); Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, ".protobot-backup-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) {
                using var identity = WindowsIdentity.GetCurrent();
                var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(identity.User);
                acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(staging).Create(acl);
            }
            else Directory.CreateDirectory(staging, FileModePrivate | UnixFileMode.UserExecute);
            return staging;
        }
        private static FileStream PrivateFile(string path) {
            if (OperatingSystem.IsWindows()) {
                using var identity = WindowsIdentity.GetCurrent();
                var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(identity.User);
                acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
                return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, acl);
            }
            return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
                Share = FileShare.None, UnixCreateMode = FileModePrivate });
        }
        [DllImport("libc", SetLastError = true)] private static extern int renamex_np(string from, string to, uint flags);
        [DllImport("libc", SetLastError = true)] private static extern int renameat2(int fromDir, string from, int toDir, string to, uint flags);
        private static void PublishDirectory(string staging, string target) {
            // Directory.Move on Unix may replace an empty destination in a race.
            // Native exclusive rename makes the no-overwrite contract atomic too.
            if (OperatingSystem.IsWindows()) { Directory.Move(staging, target); return; }
            int result;
            if (OperatingSystem.IsMacOS()) result = renamex_np(staging, target, 4); // RENAME_EXCL
            else if (OperatingSystem.IsLinux()) result = renameat2(-100, staging, -100, target, 1); // AT_FDCWD, RENAME_NOREPLACE
            else throw new PlatformNotSupportedException();
            if (result != 0) throw new IOException("Could not publish restored room.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }
}
