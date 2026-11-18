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
        bool asParticipant = options.Has("--participant-session");
        DocumentModel.Require(asParticipant != options.Has("--owner-file"), "use-owner-file-or-participant-session");
        var owner = asParticipant ? null : ReadOwnerFile(options.Required("--owner-file"));
        string room = owner?.room ?? options.Required("--room");
        using var stop = CommandCancellation();
        var network = await Network(options, stop.Token);
        using var remote = new RemoteRoomReplacement(options.Required("--url"), options.Has("--allow-lan-http"), network);
        CollaborationClient participant = null;
        if (asParticipant) {
            string session = Path.GetFullPath(options.Required("--participant-session")); PrivateAttemptDirectory(session);
            if (File.Exists(session)) RequirePrivateCredential(session);
            var catalog = WireJson.Decode<CatalogManifest>(Encoding.UTF8.GetBytes(ReadText(options.Required("--catalog"), WireJson.MaxMessageBytes)));
            participant = new CollaborationClient(remote.Origin, room, catalog, allowInsecureLan: options.Has("--allow-lan-http"), recoveryFile: session, networkPolicy: network);
        }
        using var participantLifetime = participant;
        if (participant != null) await participant.AuthenticateForReplacementAsync(options.Get("--name", "Replacement editor"), Password(options), cancellation: stop.Token);
        if (operation == "replacement-target") {
            var target = participant != null ? await participant.InspectRoomReplacementAsync(stop.Token) : await remote.InspectAsync(room, owner.credential, stop.Token);
            var record = new ReplacementTargetFile { origin = remote.Origin, target = target };
            Protobot.Collaboration.Server.RoomRegistry.WriteNew(Path.GetFullPath(options.Required("--output")), record);
            Console.WriteLine(WireJson.Text(record)); return 0;
        }
        if (operation == "replacement-check") {
            string file = Path.GetFullPath(options.Required("--attempt")); RequirePrivateCredential(file); PrivateAttemptDirectory(file);
            var record = RemoteRoomReplacement.ReadAttempt(file);
            DocumentModel.Require(record.target.room == room, "credential-room-mismatch");
            var outcome = participant != null ? await participant.CheckRoomReplacementAsync(file, stop.Token) : await remote.CheckAsync(file, owner.credential, stop.Token);
            Console.WriteLine(WireJson.Text(outcome));
            if (!outcome.found) Console.Error.WriteLine(RemoteRoomReplacement.ReadAttempt(file).notAccepted
                ? "The old target changed; this absent replacement cannot publish."
                : "Outcome remains unconfirmed. Keep this attempt; no replacement was resent.");
            return 0;
        }
        DocumentModel.Require(options.Required("--confirm-room") == room, "confirm-exact-owner-room");
        var prepared = WireJson.Decode<ReplacementTargetFile>(Encoding.UTF8.GetBytes(ReadText(options.Required("--target"), RemoteRoomReplacement.MaxRecordBytes)));
        DocumentModel.Require(prepared != null && prepared.schema == 1 && prepared.format == "protobot-room-replacement-target", "invalid-replacement-target-file");
        DocumentModel.Require(prepared.origin == remote.Origin, "replacement-target-origin-mismatch");
        var expected = prepared.target;
        RoomReplacement.ValidateTarget(expected);
        DocumentModel.Require(expected.room == room, "owner-file-room-mismatch");
        var content = LocalJson.Decode<RoomExport>(Encoding.UTF8.GetBytes(ReadText(options.Required("--snapshot"), LocalJson.MaxBytes)));
        string output = Path.GetFullPath(options.Required("--attempt-output"));
        DocumentModel.Require(!File.Exists(output) && !Directory.Exists(output) && new FileInfo(output).LinkTarget == null, "replacement-attempt-output-exists");
        PrivateAttemptDirectory(output);
        var result = participant != null ? await participant.ReplaceRoomProjectToFileAsync(content, expected, output, stop.Token) :
            await remote.ReplaceAsync(content, expected, owner.credential, output, stop.Token);
        Console.WriteLine(WireJson.Text(result)); return 0;
    }
}
