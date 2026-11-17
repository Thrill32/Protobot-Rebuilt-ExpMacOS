using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Protobot.Collaboration;

internal static partial class Program {
    private static string SetupChoice(string label, string fallback, string[] choices) {
        DocumentModel.Require(!Console.IsInputRedirected, "setup-noninteractive-requires-kind-and-policy");
        Console.Error.Write(label + " [" + string.Join("/", choices) + "] (" + fallback + "): ");
        string value = Console.ReadLine(); DocumentModel.Require(value != null, "setup-canceled");
        value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        DocumentModel.Require(choices.Contains(value), "setup-invalid-choice"); return value;
    }
    private static int SetupProfile(Options options) {
        string root = InstallRoot(options); using var mutation = InstallLock(root); using var stopped = InstallLock(root, true);
        RequireSelectedBinary(root);
        string name = options.Get("--profile", "default");
        DocumentModel.Require(DocumentModel.ValidId(name), "invalid-profile-name");
        var profiles = Profiles(root);
        DocumentModel.Require(profiles.Length < 32 && profiles.All(p => !string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)), "setup-profile-already-exists-or-limit");
        string kind = options.Get("--kind") ?? SetupChoice("What will this profile host?", "server", new[] { "server", "room" });
        DocumentModel.Require(kind == "server" || kind == "room", "setup-invalid-kind");
        string policy = kind == "server" ? options.Get("--creation-policy") ?? SetupChoice("Who may create rooms?", "admin-only", new[] { "admin-only", "key", "open" }) : null;
        DocumentModel.Require(kind == "server" ? !options.Has("--snapshot") && !options.Has("--room") && !options.Has("--password-file") && !options.Has("--password-prompt") : !options.Has("--creation-policy"), "setup-options-for-other-kind");
        string state = Path.GetFullPath(options.Get("--state-dir", Path.Combine(root, "state", name)));
        // Never initialize within a program version or over an existing data/profile directory.
        DocumentModel.Require(!new[] { "versions", "profiles" }.Any(folder => {
            string reserved = Path.Combine(root, folder); return state == reserved || state.StartsWith(reserved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }) &&
            !Directory.Exists(state) && !File.Exists(state) && new FileInfo(state).LinkTarget == null, "setup-requires-new-state-directory");
        string stage = NewPrivateStage(state);
        try {
            var init = new List<string> { kind == "server" ? "server-init" : "init", "--data-dir", Path.Combine(stage, "data") };
            if (kind == "server") { init.Add("--creation-policy"); init.Add(policy); }
            else {
                init.Add("--room"); init.Add(options.Get("--room", name));
                init.Add(options.Has("--snapshot") ? "--snapshot" : "--catalog");
                init.Add(options.Get("--snapshot", Path.Combine(root, "versions", Active(root)[0], "catalog.json")));
                if (options.Has("--password-file")) { init.Add("--password-file"); init.Add(options.Get("--password-file")); }
                if (options.Has("--password-prompt")) init.Add("--password-prompt");
                if (!options.Has("--password-prompt") && !options.Has("--password-file") && !Console.IsInputRedirected) init.Add("--password-prompt");
            }
            // Existing initializers retain all portable validation, credentials and quotas.
            // Suppress staging paths in their success output; no secret enters arguments.
            var output = Console.Out;
            try {
                Console.SetOut(TextWriter.Null);
                if (kind == "server") InitializeServer(new Options(init.ToArray(), new[] { "--data-dir", "--creation-policy" }, Array.Empty<string>()));
                else Initialize(new Options(init.ToArray(), new[] { "--data-dir", "--room", "--catalog", "--snapshot", "--password-file" }, new[] { "--password-prompt" }));
            }
            finally { Console.SetOut(output); }
            PrivateDirectory(Path.Combine(stage, "participant")); PrivateDirectory(Path.Combine(stage, "private"));
            PublishInstallation(stage, state);
            // A failed final record write leaves the new durable state intact for inspection,
            // never a reason to rerun init over that directory.
            InstallWriteNew(Path.Combine(root, "profiles", name + ".json"), new InstalledProfile { name = name, kind = kind, stateDirectory = state });
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        Console.WriteLine(JsonSerializer.Serialize(new { profile = name, kind, stateDirectory = state, dataDirectory = Path.Combine(state, "data"),
            participantDirectory = Path.Combine(state, "participant"), privateDirectory = Path.Combine(state, "private") })); return 0;
    }
    private static async Task<int> StartProfile(Options options) {
        string root = InstallRoot(options); FileStream running;
        using (var mutation = InstallLock(root)) {
            // A launcher can race activation between reading its pointer and starting the
            // binary. Refuse a stale executable rather than serving with a retired build.
            RequireSelectedBinary(root);
            Protobot.Collaboration.Server.InstallationData.Regular(Path.Combine(root, "running.lock"));
            running = new FileStream(Path.Combine(root, "running.lock"), FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        using (running) {
            string name = options.Get("--profile", "default");
            var profile = Profiles(root).SingleOrDefault(p => p.name == name);
            DocumentModel.Require(profile != null, "installation-profile-not-found");
            var args = new List<string> { "serve" };
            string data = Path.Combine(profile.stateDirectory, "data");
            if (profile.kind == "server") args.AddRange(new[] { "--multi-room", "--config", Path.Combine(data, "server.json") });
            else args.AddRange(new[] { "--data-dir", data });
            foreach (string option in new[] { "--listen", "--public-url", "--tls-file" })
                if (options.Has(option)) args.AddRange(new[] { option, options.Get(option) });
            if (options.Has("--allow-lan-http")) args.Add("--allow-lan-http");
            return await Main(args.ToArray());
        }
    }
}
