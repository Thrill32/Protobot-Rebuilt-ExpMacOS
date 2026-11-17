using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static string InstallationHash(string path) { using var file = File.OpenRead(path); return DocumentModel.Hex(SHA256.HashData(file)); }
    private static async Task<(int code, string output, string error)> InstalledCommand(string executable, params string[] args) {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start); child.StandardInput.Close();
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try { using var timeout = new CancellationTokenSource(60000); await child.WaitForExitAsync(timeout.Token); return (child.ExitCode, await output, await error); }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }
    private static async Task InstallationChecks(string directory) {
        string current = Environment.GetEnvironmentVariable("PROTOBOT_TEST_INSTALL_ARTIFACT");
        string previous = Environment.GetEnvironmentVariable("PROTOBOT_TEST_INSTALL_PREVIOUS");
        DocumentModel.Require(current != null && previous != null, "installation-tests-require-two-local-artifacts");
        string currentHash = InstallationHash(Path.Combine(current, "artifact.json")), previousHash = InstallationHash(Path.Combine(previous, "artifact.json"));
        var currentDescription = JsonNode.Parse(File.ReadAllText(Path.Combine(current, "artifact.json")));
        var previousDescription = JsonNode.Parse(File.ReadAllText(Path.Combine(previous, "artifact.json")));
        Check(currentHash != previousHash && (string)currentDescription["build"]["build"] != (string)previousDescription["build"]["build"], "installer uses two different actual application builds sharing protocol 0.9");
        string install = Path.Combine(directory, "portable install with spaces"), state = Path.Combine(directory, "external durable state");
        var first = await RunCli("install", "--artifact", previous, "--manifest-sha256", previousHash, "--install-dir", install);
        Check(first.code == 0, "install a verified local self-contained artifact without a runtime dependency: " + first.error);
        string launcher = Path.Combine(install, OperatingSystem.IsWindows() ? "protobot-collab.cmd" : "protobot-collab");
        var version = await InstalledCommand(launcher, "build-info");
        Check(version.code == 0 && (string)JsonNode.Parse(version.output)["build"] == (string)previousDescription["build"]["build"], "stable launcher executes the selected actual build");
        Check((await RunCli("install", "--artifact", current, "--manifest-sha256", currentHash, "--install-dir", install)).code == 1, "install never replaces an existing directory");
        var setup = await InstalledCommand(launcher, "setup", "--profile", "team", "--state-dir", state, "--kind", "server", "--creation-policy", "key");
        Check(setup.code == 0 && Directory.Exists(Path.Combine(state, "participant")) && Directory.Exists(Path.Combine(state, "private")), "setup creates isolated external data, private-config and participant paths");
        string data = Path.Combine(state, "data"), config = Path.Combine(data, "server.json");
        string participantFile = Path.Combine(state, "participant", "opaque-profile.json"); RoomRegistry.WriteNew(participantFile, new { pendingOperation = "retain-pending", localSave = "personal.pbb" });
        string privateFile = Path.Combine(state, "private", "opaque-tls-config.json"); RoomRegistry.WriteNew(privateFile, new { password = "private-test-only" });
        string profileFile = Path.Combine(install, "profiles", "team.json");
        var preserved = new[] { config, Path.Combine(data, "admin.json"), Path.Combine(data, "creator.json"), profileFile, participantFile, privateFile }
            .ToDictionary(path => path, File.ReadAllBytes);
        var modes = OperatingSystem.IsWindows() ? null : preserved.Keys.ToDictionary(path => path, File.GetUnixFileMode);
        var content = CreationContent(); JoinResponse member; Transaction accepted, rejected; string roomHash;
        using (var registry = new RoomRegistry(config)) {
            registry.Create(content.CreateRoom("retained", "retained-password", RoomRegistry.Hash("retained-owner")));
            var room = registry.Resolve("retained"); member = room.Join(new JoinRequest { catalogHash = content.catalog.hash, name = "Retained", password = "retained-password" });
            accepted = Tx(room.Snapshot, "AddParts", new[] { Cube("upgrade-edit", 20) });
            Check(room.Commit(member.token, accepted).accepted, "setup fixture retains accepted edit and personal history");
            rejected = Tx(room.Snapshot, "AddParts", new[] { Cube("invalid-upgrade", double.NaN) }, sequence: 2);
            Check(!room.Commit(member.token, rejected).accepted, "setup fixture retains a rejected receipt"); roomHash = DocumentModel.Hash(room.Snapshot);
            var locked = await InstalledCommand(launcher, "update", "--artifact", current, "--manifest-sha256", currentHash);
            Check(locked.code == 1 && locked.error.Contains("server-data-in-use") && File.ReadAllLines(Path.Combine(install, "active"))[0] == previousHash,
                "updates respect an ordinary running registry ownership lock and leave selection unchanged");
        }
        byte[] owner = File.ReadAllBytes(Path.Combine(data, "rooms", "retained", "owner.json"));
        Check((await InstalledCommand(launcher, "setup", "--profile", "team", "--kind", "server", "--creation-policy", "open")).code == 1 &&
            (await InstalledCommand(launcher, "setup", "--profile", "other", "--state-dir", state, "--kind", "server", "--creation-policy", "open")).code == 1,
            "setup refuses existing profiles and existing state, without initialization over data");
        var wrong = await InstalledCommand(launcher, "update", "--artifact", current, "--manifest-sha256", new string('0', 64));
        Check(wrong.code == 1 && wrong.error.Contains("installation-manifest-hash-mismatch"), "wrong trusted digest refuses before staging or switching");
        string malformed = Path.Combine(directory, "incomplete format declaration"); Directory.CreateDirectory(malformed);
        var incomplete = currentDescription.DeepClone(); incomplete["build"].AsObject().Remove("capabilities");
        string incompleteFile = Path.Combine(malformed, "artifact.json"); File.WriteAllText(incompleteFile, incomplete.ToJsonString());
        string refusedInstall = Path.Combine(directory, "must remain absent");
        var missing = await RunCli("install", "--artifact", malformed, "--manifest-sha256", InstallationHash(incompleteFile), "--install-dir", refusedInstall);
        Check(missing.code == 1 && !Directory.Exists(refusedInstall), "an omitted capability declaration cannot inherit current defaults or publish an installation");
        string badSnapshot = Path.Combine(directory, "invalid portable content.json"), failedState = Path.Combine(directory, "failed setup state");
        File.WriteAllText(badSnapshot, "{}");
        Check((await InstalledCommand(launcher, "setup", "--profile", "invalid", "--kind", "room", "--snapshot", badSnapshot, "--state-dir", failedState)).code == 1 &&
            !Directory.Exists(failedState) && !File.Exists(Path.Combine(install, "profiles", "invalid.json")), "failed portable-content setup leaves no published state/profile or overwritten data");
        var update = await InstalledCommand(launcher, "update", "--artifact", current, "--manifest-sha256", currentHash);
        Check(update.code == 0 && Directory.Exists(Path.Combine(install, "versions", previousHash)), "validated staged upgrade retains the complete previous program");
        var info = await InstalledCommand(launcher, "installation-info");
        Check(info.code == 0 && info.output.Contains(currentHash) && info.output.Contains(previousHash) && !info.output.Contains("private-test-only") && !info.output.Contains("retained-password"),
            "installation inspection reports build/catalog/formats/profile paths without reading private configuration");
        Check(preserved.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) && File.ReadAllBytes(Path.Combine(data, "rooms", "retained", "owner.json")).SequenceEqual(owner),
            "upgrade preserves exact policy, creator/admin/owner keys, profile records, pending IDs and private TLS bytes");
        if (!OperatingSystem.IsWindows()) {
            bool unchanged = true; foreach (var pair in modes) unchanged &= File.GetUnixFileMode(pair.Key) == pair.Value;
            Check(unchanged, "update preserves existing private file permissions without repairing or broadening them");
        }
        using (var registry = new RoomRegistry(config)) {
            var room = registry.Resolve("retained"); var resumed = room.Join(new JoinRequest { catalogHash = content.catalog.hash, resumeToken = member.resumeToken });
            Check(DocumentModel.Hash(room.Snapshot) == roomHash && room.Outcome(resumed.token, accepted.operationId).accepted && !room.Outcome(resumed.token, rejected.operationId).accepted,
                "upgrade preserves generation/catalog/content/membership and accepted/rejected outcomes");
            Check(room.Commit(resumed.token, Tx(room.Snapshot, "Undo", Array.Empty<Part>(), sequence: 3)).accepted, "personal undo remains usable after an installed upgrade");
        }
        var rollback = await InstalledCommand(launcher, "select");
        Check(rollback.code == 0 && File.ReadAllLines(Path.Combine(install, "active"))[0] == previousHash, "explicit compatible rollback uses the same validation and switches atomically");
        Check((await InstalledCommand(launcher, "select")).code == 0, "explicit forward selection retains the prior pointer on every switch");
        string pointer = File.ReadAllText(Path.Combine(install, "active"));
        var profile = JsonNode.Parse(File.ReadAllText(profileFile)); profile["schema"] = 2; File.WriteAllText(profileFile, profile.ToJsonString());
        Check((await InstalledCommand(launcher, "select")).code == 1 && File.ReadAllText(Path.Combine(install, "active")) == pointer, "rollback refuses an incompatible local profile format without reverse migration");
        File.WriteAllBytes(profileFile, preserved[profileFile]);
        var configJson = JsonNode.Parse(File.ReadAllText(config)); configJson["futurePolicy"] = "unsupported"; File.WriteAllText(config, configJson.ToJsonString());
        Check((await InstalledCommand(launcher, "select")).code == 1 && File.ReadAllText(Path.Combine(install, "active")) == pointer && File.ReadAllText(config).Contains("futurePolicy"),
            "rollback refuses actual strict unknown config fields despite unchanged schema/protocol and never rewrites them");
        File.WriteAllBytes(config, preserved[config]);
        string database = Path.Combine(data, "rooms", "retained", "room.sqlite");
        void StorageVersion(int value) { using var db = new SqliteConnection("Data Source=" + database + ";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=" + value; command.ExecuteNonQuery(); }
        StorageVersion(3);
        Check((await InstalledCommand(launcher, "select")).code == 1 && File.ReadAllText(Path.Combine(install, "active")) == pointer, "rollback refuses incompatible actual SQLite storage before activation"); StorageVersion(2);
        string selectedBinary = Path.Combine(install, "versions", previousHash, "protobot-collab.dll");
        using (var stream = new FileStream(selectedBinary, FileMode.Append, FileAccess.Write)) stream.WriteByte(0);
        Check((await InstalledCommand(launcher, "select")).code == 1 && File.ReadAllText(Path.Combine(install, "active")) == pointer, "rollback revalidates installed bytes and refuses a damaged previous program");
        var damagedInfo = await InstalledCommand(launcher, "installation-info");
        Check(damagedInfo.code == 0 && damagedInfo.output.Contains("invalid-artifact-use-a-verified-package"), "inspection remains usable and labels a damaged retained installation");
        File.Copy(Path.Combine(previous, "protobot-collab.dll"), selectedBinary, true);
        using (var running = new FileStream(Path.Combine(install, "running.lock"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Check((await InstalledCommand(launcher, "select")).code == 1, "managed server lifetime lock fences selection before inspecting data");
        Check((await InstalledCommand(launcher, "select")).code == 0 && preserved.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "repaired compatible rollback preserves all independent private/profile bytes");
        var serving = new ProcessStartInfo(launcher) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "start", "--profile", "team", "--listen", "http://127.0.0.1:0" }) serving.ArgumentList.Add(arg);
        using (var child = Process.Start(serving)) {
            var errors = child.StandardError.ReadToEndAsync(); child.StandardInput.Close();
            try {
                string ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Check(ready != null && JsonNode.Parse(ready)["multiRoom"].GetValue<bool>(), "actual stable launcher starts its initialized named server profile");
                Check((await InstalledCommand(launcher, "select")).code == 1 && !child.HasExited, "selection respects the actual managed server lifetime without stopping it");
                if (!OperatingSystem.IsWindows()) {
                    using var signal = Process.Start(new ProcessStartInfo("/bin/kill") { UseShellExecute = false, ArgumentList = { "-TERM", child.Id.ToString() } });
                    await signal.WaitForExitAsync(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check(child.ExitCode == 0, "named-profile server retains ordinary graceful SIGTERM shutdown");
                }
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } await errors; }
        }
        if (!OperatingSystem.IsWindows()) {
            Check((File.GetUnixFileMode(state) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0 &&
                (File.GetUnixFileMode(Path.Combine(data, "creator.json")) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "installation/setup retain private Unix directory/credential modes");
            string link = Path.Combine(directory, "linked artifact"); Directory.CreateSymbolicLink(link, current);
            Check((await InstalledCommand(launcher, "update", "--artifact", link, "--manifest-sha256", currentHash)).code == 1, "artifact parent links refuse before any publication");
        }
        string strictLegacy = Environment.GetEnvironmentVariable("PROTOBOT_TEST_INSTALL_LEGACY");
        if (strictLegacy != null) {
            var legacy = await InstalledCommand(strictLegacy, "serve", "--multi-room", "--config", config, "--listen", "http://127.0.0.1:0");
            Check(legacy.code == 1 && (await InstalledCommand(strictLegacy, "--version")).output.Trim() == "0.9.0-alpha", "actual pre-creation helper refuses creation-policy config even though its protocol/config versions match");
            string legacyCopy = Path.Combine(directory, "unsupported same-protocol artifact"); Directory.CreateDirectory(legacyCopy);
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(strictLegacy), "*", SearchOption.AllDirectories)) {
                string target = Path.Combine(legacyCopy, Path.GetRelativePath(Path.GetDirectoryName(strictLegacy), file)); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
            var packaged = await RunCli("artifact-manifest", "--artifact", legacyCopy, "--runtime", (string)currentDescription["build"]["runtime"]);
            Check(packaged.code == 0, "legacy refusal fixture retains its real assemblies/catalog in a distinct task-owned copy");
            var unsupported = await RunCli("install", "--artifact", legacyCopy, "--manifest-sha256", InstallationHash(Path.Combine(legacyCopy, "artifact.json")), "--install-dir", refusedInstall);
            Check(unsupported.code == 1 && unsupported.error.Contains("installation-probe-unsupported") && !Directory.Exists(refusedInstall),
                "manifest claims and the same wire version cannot substitute for actual executable build/format support");
        }
        if (currentDescription["build"]["storage"].AsArray().Any(n => n.GetValue<int>() == 3) &&
            !previousDescription["build"]["storage"].AsArray().Any(n => n.GetValue<int>() == 3)) {
            if (File.ReadAllLines(Path.Combine(install, "active"))[0] != currentHash)
                Check((await InstalledCommand(launcher, "select", "--version", currentHash)).code == 0, "select replacement-capable helper before deliberate storage migration");
            string replacedHash;
            using (var registry = new RoomRegistry(config)) {
                var room = registry.Resolve("retained"); string authority = RoomRegistry.Hash("retained-owner");
                room.Replace(authority, new RoomReplacementRequest { expected = room.InspectReplacement(authority), content = content });
                replacedHash = DocumentModel.Hash(room.Snapshot);
            }
            var durable = new[] { database, Path.Combine(data, "rooms", "retained", "registration.json"), Path.Combine(data, "rooms", "retained", "owner.json") }
                .ToDictionary(path => path, File.ReadAllBytes);
            pointer = File.ReadAllText(Path.Combine(install, "active"));
            var refused = await InstalledCommand(launcher, "select", "--version", previousHash);
            Check(refused.code == 1 && refused.error.Contains("installation-capability-incompatible") && File.ReadAllText(Path.Combine(install, "active")) == pointer,
                "actual SQLite-v3 replacement refuses a same-protocol pre-replacement rollback before switching");
            Check(durable.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) && preserved.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "refused v3 rollback preserves exact database/identity/owner/config/profile/private bytes without reverse migration");
            using var reopened = new RoomRegistry(config);
            Check(DocumentModel.Hash(reopened.Resolve("retained").Snapshot) == replacedHash && reopened.Resolve("retained").Capabilities.roomReplacementVersion == 1,
                "replacement-capable helper retains the authoritative migrated room after rollback refusal");
        }
        File.WriteAllText(Path.Combine(directory, "installation-summary.json"), new JsonObject { ["passed"] = true, ["current"] = currentHash, ["previous"] = previousHash, ["assertions"] = assertions }.ToJsonString());
    }
}
