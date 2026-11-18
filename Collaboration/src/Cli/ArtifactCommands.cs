using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private const string ArtifactFile = "artifact.json";
    private static readonly JsonSerializerOptions InstallJson = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed class BuildDescription {
        [JsonRequired] public int schema { get; set; } = 1;
        [JsonRequired] public string protocol { get; set; } = CollaborationProtocol.Version;
        [JsonRequired] public string runtime { get; set; }
        [JsonRequired] public string build { get; set; }
        [JsonRequired] public string catalog { get; set; }
        [JsonRequired] public string[] capabilities { get; set; } = new[] { "geometry-v1", "participants-v1", "diagnostics-v1", "short-polling-v1", "room-creation-v1", "room-replacement-v1", "editor-room-replacement-v1" };
        [JsonRequired] public int[] storage { get; set; } = new[] { 1, 2, 3 };
        [JsonRequired] public int[] documents { get; set; } = new[] { 1, 2 };
        [JsonRequired] public int registry { get; set; } = 1;
        [JsonRequired] public int profile { get; set; } = 1;
        [JsonRequired] public string[] configFields { get; set; } = new[] { "schema", "serverId", "adminHash", "creationPolicy", "creationKeyHash" };
    }
    private sealed class ArtifactEntry {
        [JsonRequired] public string path { get; set; }
        [JsonRequired] public long length { get; set; }
        [JsonRequired] public string sha256 { get; set; }
        [JsonRequired] public bool executable { get; set; }
    }
    private sealed class ArtifactDescription {
        [JsonRequired] public string format { get; set; } = "protobot-cli-artifact";
        [JsonRequired] public int schema { get; set; } = 1;
        [JsonRequired] public BuildDescription build { get; set; }
        [JsonRequired] public ArtifactEntry[] files { get; set; }
    }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return DocumentModel.Hex(SHA256.HashData(stream)); }
    private static string NativeRuntime => (OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux") + "-" +
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
    private static string HelperName => OperatingSystem.IsWindows() ? "protobot-collab.exe" : "protobot-collab";
    private static BuildDescription DescribeBuild(string directory, string runtime) {
        // Build identity includes all application assemblies and the matching source catalog.
        var identities = new[] { "protobot-collab.dll", "Server.dll", "Shared.dll", "catalog.json" }
            .Select(name => name + ":" + HashFile(Path.Combine(directory, name))).ToArray();
        string catalogPath = Path.Combine(directory, "catalog.json");
        DocumentModel.Require(new FileInfo(catalogPath).Length <= WireJson.MaxMessageBytes, "installation-catalog-invalid");
        var catalog = WireJson.Decode<CatalogManifest>(File.ReadAllBytes(catalogPath));
        DocumentModel.Require(catalog != null && !string.IsNullOrEmpty(catalog.hash), "installation-catalog-invalid");
        return new BuildDescription { runtime = runtime, catalog = catalog.hash,
            build = DocumentModel.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", identities)))) };
    }
    private static T InstallRead<T>(string path, int maximum = 1024 * 1024) {
        InstallationData.Regular(path);
        DocumentModel.Require(new FileInfo(path).Length <= maximum, "installation-metadata-limit");
        byte[] bytes = File.ReadAllBytes(path);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        void Unique(JsonElement value) {
            if (value.ValueKind == JsonValueKind.Object) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in value.EnumerateObject()) { DocumentModel.Require(names.Add(field.Name), "installation-duplicate-field"); Unique(field.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        }
        Unique(json.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, InstallJson);
    }
    private static void InstallWriteNew<T>(string path, T value) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, InstallJson);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        ProtectOwnerFile(path); file.Write(bytes); file.Flush(true);
    }
    private static string[] ArtifactPaths(string root) {
        var files = new List<string>(); int entries = 0;
        void Walk(string directory, int depth = 0) {
            DocumentModel.Require(depth <= 16, "installation-path-depth-limit");
            InstallationData.Regular(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                DocumentModel.Require(++entries <= 2048, "installation-file-limit"); InstallationData.Regular(path);
                if (Directory.Exists(path)) Walk(path, depth + 1);
                else if (path != Path.Combine(root, ArtifactFile)) files.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }
        Walk(root); return files.OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }
    private static int MakeArtifact(Options options) {
        string root = Path.GetFullPath(options.Required("--artifact")), runtime = options.Required("--runtime");
        DocumentModel.Require(new[] { "osx-arm64", "win-x64", "linux-x64", "linux-arm64" }.Contains(runtime), "installation-runtime-unsupported");
        var manifest = new ArtifactDescription { build = DescribeBuild(root, runtime), files = ArtifactPaths(root).Select(path => {
            string file = Path.Combine(root, path);
            return new ArtifactEntry { path = path, length = new FileInfo(file).Length, sha256 = HashFile(file),
                executable = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0 };
        }).ToArray() };
        InstallWriteNew(Path.Combine(root, ArtifactFile), manifest);
        Console.WriteLine(HashFile(Path.Combine(root, ArtifactFile))); return 0;
    }
    private static ArtifactDescription VerifyArtifact(string root, string expected) {
        InstallationData.Regular(Path.Combine(root, ArtifactFile));
        DocumentModel.Require(new FileInfo(Path.Combine(root, ArtifactFile)).Length <= 1024 * 1024, "installation-metadata-limit");
        DocumentModel.Require(DocumentModel.ValidAssetHash(expected) && HashFile(Path.Combine(root, ArtifactFile)) == expected, "installation-manifest-hash-mismatch");
        var manifest = InstallRead<ArtifactDescription>(Path.Combine(root, ArtifactFile));
        DocumentModel.Require(manifest != null && manifest.format == "protobot-cli-artifact" && manifest.schema == 1 && manifest.build != null &&
            manifest.build.schema == 1 && manifest.build.runtime == NativeRuntime && manifest.files != null && manifest.files.Length > 0 && manifest.files.Length <= 2048, "installation-artifact-incompatible");
        var paths = ArtifactPaths(root); var names = manifest.files.Select(f => f.path).ToArray();
        DocumentModel.Require(paths.SequenceEqual(names.OrderBy(p => p, StringComparer.Ordinal)) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length &&
            names.All(p => !string.IsNullOrEmpty(p) && !p.Contains('\\') && p.Split('/').All(c => c != "" && c != "." && c != ".." && c.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)), "installation-artifact-files-mismatch");
        long total = 0;
        foreach (var entry in manifest.files) {
            string path = Path.Combine(root, entry.path); total = checked(total + entry.length);
            DocumentModel.Require(entry.length >= 0 && total <= 1024L * 1024 * 1024 && new FileInfo(path).Length == entry.length && HashFile(path) == entry.sha256, "installation-artifact-integrity-failed");
        }
        var actual = DescribeBuild(root, NativeRuntime);
        DocumentModel.Require(actual.build == manifest.build.build && actual.catalog == manifest.build.catalog, "installation-build-mismatch");
        return manifest;
    }
    private static async Task<BuildDescription> VerifyExecutable(string root, ArtifactDescription artifact) {
        using var child = new Process { StartInfo = new ProcessStartInfo(Path.Combine(root, HelperName)) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root } };
        child.StartInfo.ArgumentList.Add("build-info"); child.Start();
        // Only the staged trusted artifact runs; no credentials or data paths are supplied.
        async Task<string> Bounded(StreamReader reader) {
            var text = new StringBuilder(); var buffer = new char[1024]; int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0) {
                DocumentModel.Require(text.Length + read <= 16384, "installation-probe-invalid"); text.Append(buffer, 0, read);
            }
            return text.ToString();
        }
        var output = Bounded(child.StandardOutput); var errors = Bounded(child.StandardError);
        try {
            await Task.WhenAll(child.WaitForExitAsync(), output, errors).WaitAsync(TimeSpan.FromSeconds(20));
            DocumentModel.Require(child.ExitCode == 0, "installation-probe-unsupported");
            var actual = JsonSerializer.Deserialize<BuildDescription>(await output, InstallJson);
            DocumentModel.Require(JsonSerializer.Serialize(actual, InstallJson) == JsonSerializer.Serialize(artifact.build, InstallJson), "installation-probe-mismatch");
            return actual;
        }
        catch { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } throw; }
    }
    private static void CopyArtifact(string source, string target, ArtifactDescription artifact) {
        foreach (string name in artifact.files.Select(f => f.path).Append(ArtifactFile)) {
            string destination = Path.Combine(target, name); Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(Path.Combine(source, name), destination, false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                (artifact.files.Any(f => f.path == name && f.executable) ? UnixFileMode.UserExecute : 0));
            using var file = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.None); file.Flush(true);
        }
    }
}
