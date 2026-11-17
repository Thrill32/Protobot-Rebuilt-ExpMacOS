using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Protobot.Collaboration;

internal static partial class Program {
    [Serializable] private sealed class ReplacementTargetFile {
        public string format = "protobot-room-replacement-target";
        public int schema = 1;
        public string origin;
        public RoomReplacementTarget target;
    }
    private static void PrivateAttemptDirectory(string file) {
        string parent = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!Directory.Exists(parent)) PrivateDirectory(parent);
        // Atomic recovery-file replacement inherits this directory's Windows ACL.
        // Never silently repair a broader existing operator directory.
        RequirePrivateCredential(parent);
    }
    private static async Task<int> ReplaceRoom(string operation, Options options) {
        var owner = ReadOwnerFile(options.Required("--owner-file"));
        using var stop = CommandCancellation();
        using var remote = new RemoteRoomReplacement(options.Required("--url"), options.Has("--allow-lan-http"), await Network(options, stop.Token));
        if (operation == "replacement-target") {
            var target = await remote.InspectAsync(owner.room, owner.credential, stop.Token);
            var record = new ReplacementTargetFile { origin = remote.Origin, target = target };
            Protobot.Collaboration.Server.RoomRegistry.WriteNew(Path.GetFullPath(options.Required("--output")), record);
            Console.WriteLine(WireJson.Text(record)); return 0;
        }
        if (operation == "replacement-check") {
            string file = Path.GetFullPath(options.Required("--attempt")); RequirePrivateCredential(file); PrivateAttemptDirectory(file);
            var record = RemoteRoomReplacement.ReadAttempt(file);
            DocumentModel.Require(record.target.room == owner.room, "owner-file-room-mismatch");
            var outcome = await remote.CheckAsync(file, owner.credential, stop.Token);
            Console.WriteLine(WireJson.Text(outcome));
            if (!outcome.found) Console.Error.WriteLine(RemoteRoomReplacement.ReadAttempt(file).notAccepted
                ? "The old target changed; this absent replacement cannot publish."
                : "Outcome remains unconfirmed. Keep this attempt; no replacement was resent.");
            return 0;
        }
        DocumentModel.Require(options.Required("--confirm-room") == owner.room, "confirm-exact-owner-room");
        var prepared = WireJson.Decode<ReplacementTargetFile>(Encoding.UTF8.GetBytes(ReadText(options.Required("--target"), RemoteRoomReplacement.MaxRecordBytes)));
        DocumentModel.Require(prepared != null && prepared.schema == 1 && prepared.format == "protobot-room-replacement-target", "invalid-replacement-target-file");
        DocumentModel.Require(prepared.origin == remote.Origin, "replacement-target-origin-mismatch");
        var expected = prepared.target;
        RoomReplacement.ValidateTarget(expected);
        DocumentModel.Require(expected.room == owner.room, "owner-file-room-mismatch");
        var content = LocalJson.Decode<RoomExport>(Encoding.UTF8.GetBytes(ReadText(options.Required("--snapshot"), LocalJson.MaxBytes)));
        string output = Path.GetFullPath(options.Required("--attempt-output"));
        DocumentModel.Require(!File.Exists(output) && !Directory.Exists(output) && new FileInfo(output).LinkTarget == null, "replacement-attempt-output-exists");
        PrivateAttemptDirectory(output);
        var result = await remote.ReplaceAsync(content, expected, owner.credential, output, stop.Token);
        Console.WriteLine(WireJson.Text(result)); return 0;
    }
}
