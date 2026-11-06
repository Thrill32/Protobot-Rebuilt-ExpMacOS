using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Protobot.Collaboration;
using UnityEngine;

namespace Protobot {
    public static class CollaborationArguments {
        private static readonly string[] ValueOptions = { "--collab-profile", "--collab-name", "--collab-join", "--collab-room", "--collab-transport", "--collab-probe", "--collab-test-results" };
        private static readonly Dictionary<string, string> values = Parse();
        public static string Get(string option, string fallback = null) => values.TryGetValue(option, out var value) ? value : fallback;
        public static bool Enabled => Environment.GetCommandLineArgs().Contains("--collaboration") || Get("--collab-join") != null;
        public static IEnumerable<string> StartupFiles {
            get {
                var args = Environment.GetCommandLineArgs();
                for (int i = 1; i < args.Length; i++) {
                    if (ValueOptions.Contains(args[i])) { i++; continue; }
                    if (string.Equals(Path.GetExtension(args[i]), ".pbb", StringComparison.OrdinalIgnoreCase)) yield return args[i];
                }
            }
        }
        private static Dictionary<string, string> Parse() {
            var result = new Dictionary<string, string>();
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++) if (ValueOptions.Contains(args[i])) {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + args[i]);
                result.Add(args[i], args[++i]);
            }
            return result;
        }
    }

    // All existing user-setting paths pass through this store so local player instances are independent.
    public static class LocalPreferences {
        [Serializable] private sealed class Entry { public string key, value; }
        [Serializable] private sealed class Store { public Entry[] entries = Array.Empty<Entry>(); }
        private static readonly Dictionary<string, string> profile;
        private static readonly string path;
        private static readonly FileStream ownership;
        public static string ProfileDirectory { get; }
        static LocalPreferences() {
            var selected = CollaborationArguments.Get("--collab-profile");
            if (selected == null) return;
            // Profile failures propagate; never use the developer's normal preferences as a fallback.
            ProfileDirectory = Path.GetFullPath(selected); Directory.CreateDirectory(ProfileDirectory);
            ownership = new FileStream(Path.Combine(ProfileDirectory, "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            AppDomain.CurrentDomain.DomainUnload += (_, __) => ownership.Dispose();
            Application.quitting += () => ownership.Dispose();
            path = Path.Combine(ProfileDirectory, "preferences.json");
            profile = File.Exists(path) ? WireJson.Decode<Store>(File.ReadAllBytes(path)).entries.ToDictionary(e => e.key, e => e.value) : new Dictionary<string, string>();
        }
        public static string GetString(string key, string fallback = "") => profile == null ? PlayerPrefs.GetString(key, fallback) : profile.TryGetValue(key, out var value) ? value : fallback;
        public static int GetInt(string key, int fallback = 0) => profile == null ? PlayerPrefs.GetInt(key, fallback) : int.TryParse(GetString(key), out var value) ? value : fallback;
        public static void SetString(string key, string value) { if (profile == null) PlayerPrefs.SetString(key, value); else { profile[key] = value; Save(); } }
        public static void SetInt(string key, int value) { if (profile == null) PlayerPrefs.SetInt(key, value); else SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        public static void DeleteKey(string key) { if (profile == null) PlayerPrefs.DeleteKey(key); else { profile.Remove(key); Save(); } }
        public static void Save() {
            if (profile == null) { PlayerPrefs.Save(); return; }
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, WireJson.Encode(new Store { entries = profile.Select(e => new Entry { key = e.Key, value = e.Value }).ToArray() }));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            GC.KeepAlive(ownership);
        }
    }
}
