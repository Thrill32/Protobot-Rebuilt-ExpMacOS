using System;
using Protobot.Collaboration;

namespace Protobot {
    public static class CollaborationInvite {
        private const string Header = "Protobot collaboration";
        public static string ValidateOrigin(string value) {
            if (value == null || value.Length > 256 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
                throw new ArgumentException("Enter a server origin, such as https://cad.example.org or http://127.0.0.1:8787, without a path or credentials.");
            return uri.GetLeftPart(UriPartial.Authority);
        }
        public static string Format(string endpoint, string room) {
            if (!DocumentModel.ValidId(room)) throw new ArgumentException("Invalid room ID.");
            return Header + "\nServer: " + ValidateOrigin(endpoint) + "\nRoom: " + room;
        }
        public static void Parse(string text, out string endpoint, out string room) {
            endpoint = room = null;
            if (string.IsNullOrEmpty(text) || text.Length > 512) throw new ArgumentException("Copy a Protobot invite from the host's collaboration window first.");
            var lines = text.Replace("\r\n", "\n").Trim().Split('\n');
            if (lines.Length != 3 || lines[0] != Header || !lines[1].StartsWith("Server: ", StringComparison.Ordinal) || !lines[2].StartsWith("Room: ", StringComparison.Ordinal))
                throw new ArgumentException("This clipboard text is not a Protobot collaboration invite.");
            string address = ValidateOrigin(lines[1].Substring(8)); string id = lines[2].Substring(6).Trim();
            if (!DocumentModel.ValidId(id)) throw new ArgumentException("The invite has an invalid room ID.");
            endpoint = address; room = id;
        }
    }
}
