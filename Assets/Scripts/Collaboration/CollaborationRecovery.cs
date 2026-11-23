using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Protobot.Collaboration;
using UnityEngine;

namespace Protobot {
    public static class CollaborationRecovery {
        [Serializable] public sealed class HostedRoom {
            public int schema = 1;
            public string room, catalogHash, generation, ownerCredential, createdUtc, directory;
            public int port;
        }
        internal static string Root => Path.Combine(LocalPreferences.ProfileDirectory ?? Application.persistentDataPath, "Collaboration");
        public static HostedRoom Create(Bootstrap seed, int port) {
            var info = new HostedRoom { room = seed.room, catalogHash = seed.catalog.hash, generation = seed.document.generation,
                ownerCredential = seed.ownerCredential, port = port, createdUtc = DateTime.UtcNow.ToString("O"),
                directory = Path.Combine(Root, "hosted", Guid.NewGuid().ToString("N")) };
            RecoveryFiles.CreateDirectory(info.directory); Save(info); return info;
        }
        public static void Save(HostedRoom info) => RecoveryFiles.Write(Path.Combine(info.directory, "host.json"), info);
        public static HostedRoom Read(string directory) {
            var info = RecoveryFiles.Read<HostedRoom>(Path.Combine(directory, "host.json"));
            DocumentModel.Require(info != null && info.schema == 1 && DocumentModel.ValidId(info.room) && DocumentModel.ValidId(info.generation) &&
                !string.IsNullOrEmpty(info.ownerCredential) && info.ownerCredential.Length < 256 && !string.IsNullOrEmpty(info.catalogHash) &&
                info.port >= 0 && info.port <= 65535, "invalid-host-recovery-record");
            info.directory = Path.GetFullPath(directory); return info;
        }
        public static HostedRoom[] List(out bool unreadable) {
            unreadable = false;
            string root = Path.Combine(Root, "hosted");
            if (!Directory.Exists(root)) return Array.Empty<HostedRoom>();
            var rooms = new List<HostedRoom>();
            foreach (var directory in Directory.GetDirectories(root)) {
                if (!File.Exists(Path.Combine(directory, "host.json")) || !File.Exists(Path.Combine(directory, "room.sqlite"))) continue;
                try { rooms.Add(Read(directory)); } catch { unreadable = true; }
            }
            return rooms.OrderByDescending(r => r.createdUtc, StringComparer.Ordinal).ToArray();
        }
        public static string OwnerSession(HostedRoom room) => Path.Combine(room.directory, "owner-session.json");
        public static string SessionFile(string origin, string room, string catalogHash) {
            var uri = new Uri(origin);
            using var hash = SHA256.Create();
            string key = DocumentModel.Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(uri.GetLeftPart(UriPartial.Authority) + "\n" + room + "\n" + catalogHash)));
            return Path.Combine(Root, "sessions", key + ".json");
        }
    }
}
