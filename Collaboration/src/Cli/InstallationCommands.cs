using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static string installationRoot;
    private sealed class InstalledProfile {
        [JsonRequired] public int schema { get; set; } = 1;
        [JsonRequired] public string name { get; set; }
        [JsonRequired] public string kind { get; set; }
        [JsonRequired] public string stateDirectory { get; set; }
    }
    private static string InstallRoot(Options options) {
        string root = Path.GetFullPath(options.Get("--install-dir", installationRoot) ?? throw new CollaborationException("installation-directory-required"));
        InstallationData.Regular(root); return root;
    }
    private static FileStream InstallLock(string root, bool running = false) {
        string path = Path.Combine(root, running ? "running.lock" : "installation.lock"); InstallationData.Regular(path);
        try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new CollaborationException("installation-in-use-stop-managed-servers"); }
    }
    private static string[] Active(string root) {
        string file = Path.Combine(root, "active"); InstallationData.Regular(file);
        DocumentModel.Require(new FileInfo(file).Length <= 132, "installation-pointer-invalid");
        var lines = File.ReadAllLines(file);
        DocumentModel.Require(lines.Length == 2 && DocumentModel.ValidAssetHash(lines[0]) && (lines[1] == "" || DocumentModel.ValidAssetHash(lines[1])), "installation-pointer-invalid");
        return lines;
    }
    private static void Activate(string root, string id, string previous) {
        string temporary = Path.Combine(root, ".active-" + Guid.NewGuid().ToString("N"));
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                ProtectOwnerFile(temporary); file.Write(Encoding.ASCII.GetBytes(id + "\n" + previous + "\n")); file.Flush(true);
            }
            File.Move(temporary, Path.Combine(root, "active"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static InstalledProfile[] Profiles(string root) {
        string directory = Path.Combine(root, "profiles"); InstallationData.Regular(directory);
        var paths = Directory.EnumerateFileSystemEntries(directory).Take(33).ToArray();
        DocumentModel.Require(paths.Length <= 32, "installation-profile-limit");
        return paths.Select(path => {
            var profile = InstallRead<InstalledProfile>(path, 4096);
            DocumentModel.Require(profile != null && profile.schema == 1 && DocumentModel.ValidId(profile.name) &&
                Path.GetFileName(path) == profile.name + ".json" && (profile.kind == "room" || profile.kind == "server") &&
                Path.IsPathFullyQualified(profile.stateDirectory), "installation-profile-incompatible");
            return profile;
        }).ToArray();
    }
    private static void RequireSelectedBinary(string root) {
        string active = Active(root)[0]; var selected = VerifyArtifact(Path.Combine(root, "versions", active), active);
        var actual = DescribeBuild(AppContext.BaseDirectory, NativeRuntime);
        DocumentModel.Require(actual.build == selected.build.build, "installation-changed-run-launcher-again");
    }
    private static List<InstallationData> CheckProfiles(string root, BuildDescription candidate) {
        var leases = new List<InstallationData>();
        try {
            DocumentModel.Require(candidate.profile == 1 && candidate.protocol == CollaborationProtocol.Version && candidate.capabilities != null &&
                new[] { "geometry-v1", "participants-v1", "diagnostics-v1", "short-polling-v1", "room-creation-v1" }.All(candidate.capabilities.Contains), "installation-capability-incompatible");
            foreach (var profile in Profiles(root)) {
                string data = Path.Combine(profile.stateDirectory, "data");
                var lease = InstallationData.Open(data, profile.kind == "server"); leases.Add(lease);
                DocumentModel.Require(!lease.RequiresEditorReplacement || candidate.capabilities.Contains("editor-room-replacement-v1"), "installation-capability-incompatible");
                DocumentModel.Require(!lease.StorageVersions.Contains(3) || candidate.capabilities.Contains("room-replacement-v1"), "installation-capability-incompatible");
                DocumentModel.Require(candidate.storage != null && candidate.documents != null && candidate.configFields != null &&
                    lease.StorageVersions.All(candidate.storage.Contains) && lease.DocumentVersions.All(candidate.documents.Contains) &&
                    lease.ConfigFields.All(candidate.configFields.Contains) && lease.RegistryVersion <= candidate.registry, "installation-data-or-config-incompatible");
            }
            return leases;
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
    }
    private static async Task<int> InstallArtifact(Options options, bool update) {
        string source = Path.GetFullPath(options.Required("--artifact")), hash = options.Required("--manifest-sha256");
        var artifact = VerifyArtifact(source, hash);
        string target = Path.GetFullPath(options.Get("--install-dir", installationRoot) ?? throw new CollaborationException("installation-directory-required"));
        if (!update) {
            DocumentModel.Require(!File.Exists(target) && !Directory.Exists(target) && new FileInfo(target).LinkTarget == null, "installation-requires-new-directory");
            string stage = NewPrivateStage(target);
            try {
                PrivateDirectory(Path.Combine(stage, "versions")); PrivateDirectory(Path.Combine(stage, "profiles"));
                string version = Path.Combine(stage, "versions", hash); PrivateDirectory(version);
                CopyArtifact(source, version, artifact); VerifyArtifact(version, hash); await VerifyExecutable(version, artifact);
                File.WriteAllText(Path.Combine(stage, "installation.lock"), ""); ProtectOwnerFile(Path.Combine(stage, "installation.lock"));
                File.WriteAllText(Path.Combine(stage, "running.lock"), ""); ProtectOwnerFile(Path.Combine(stage, "running.lock"));
                Activate(stage, hash, ""); WriteLauncher(stage);
                PublishInstallation(stage, target);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        else {
            target = InstallRoot(options);
            using var mutation = InstallLock(target); using var stopped = InstallLock(target, true);
            var active = Active(target); DocumentModel.Require(active[0] != hash, "installation-already-selected");
            string versions = Path.Combine(target, "versions"); InstallationData.Regular(versions);
            string version = Path.Combine(versions, hash);
            DocumentModel.Require(!Directory.Exists(version) && !File.Exists(version), "installation-version-exists-use-select");
            string stage = NewPrivateStage(version);
            try {
                CopyArtifact(source, stage, artifact); VerifyArtifact(stage, hash); var candidate = await VerifyExecutable(stage, artifact);
                var leases = CheckProfiles(target, candidate);
                try { PublishInstallation(stage, version); Activate(target, hash, active[0]); }
                finally { foreach (var lease in leases) lease.Dispose(); }
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { installed = hash, directory = target, build = artifact.build.build, catalog = artifact.build.catalog })); return 0;
    }
    private static async Task<int> SelectInstallation(Options options) {
        string root = InstallRoot(options); using var mutation = InstallLock(root); using var stopped = InstallLock(root, true);
        var active = Active(root); string id = options.Get("--version", active[1]);
        DocumentModel.Require(DocumentModel.ValidAssetHash(id), "installation-no-previous-version");
        DocumentModel.Require(id != active[0], "installation-already-selected");
        string version = Path.Combine(root, "versions", id); var artifact = VerifyArtifact(version, id);
        var candidate = await VerifyExecutable(version, artifact); var leases = CheckProfiles(root, candidate);
        try { Activate(root, id, active[0]); }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Console.WriteLine(JsonSerializer.Serialize(new { selected = id, previous = active[0] })); return 0;
    }
    private static int InstallationInfo(Options options) {
        string root = InstallRoot(options); var active = Active(root);
        var versions = Directory.EnumerateDirectories(Path.Combine(root, "versions")).Where(p => DocumentModel.ValidAssetHash(Path.GetFileName(p))).Select(path => {
            string id = Path.GetFileName(path); BuildDescription build = null; string status;
            try { build = VerifyArtifact(path, id).build; status = "verified"; }
            catch { status = "invalid-artifact-use-a-verified-package"; }
            return new { id, active = id == active[0], previous = id == active[1], status, build };
        }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { schema = 1, versions, profiles = Profiles(root) })); return 0;
    }
    private static void PrivateDirectory(string path) {
        // Reuse init's private staging ACL on Windows, including for empty durable roots.
        string stage = NewPrivateStage(path);
        try { PublishInstallation(stage, path); }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static string NewPrivateStage(string target) => InstallationPaths.Stage(target);
    private static void PublishInstallation(string source, string target) => InstallationPaths.Publish(source, target);
    private static void WriteLauncher(string root) {
        string name = OperatingSystem.IsWindows() ? "protobot-collab.cmd" : "protobot-collab";
        string script = OperatingSystem.IsWindows() ?
            "@echo off\r\nsetlocal DisableDelayedExpansion\r\nset /p pb_version=<\"%~dp0active\"\r\n" +
            "echo(%pb_version%| findstr /r /x \"[0-9a-f][0-9a-f]*\" >nul || exit /b 1\r\n" +
            "if not \"%pb_version:~64%\"==\"\" exit /b 1\r\nif \"%pb_version:~63,1%\"==\"\" exit /b 1\r\n" +
            "\"%~dp0versions\\%pb_version%\\protobot-collab.exe\" --installation \"%~dp0.\" %*\r\nexit /b %errorlevel%\r\n" :
            "#!/bin/sh\nset -eu\npb_root=$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\nIFS= read -r pb_version < \"$pb_root/active\"\n" +
            "case $pb_version in ''|*[!0-9a-f]*) exit 1;; esac\n[ ${#pb_version} -eq 64 ] || exit 1\n" +
            "exec \"$pb_root/versions/$pb_version/protobot-collab\" --installation \"$pb_root\" \"$@\"\n";
        string path = Path.Combine(root, name); File.WriteAllText(path, script, new UTF8Encoding(false)); ProtectOwnerFile(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
