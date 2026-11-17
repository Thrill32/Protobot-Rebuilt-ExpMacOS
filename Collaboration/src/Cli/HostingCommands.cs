using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;
using Protobot.Collaboration.Server;

internal static partial class Program {
    private static int InitializeServer(Options options) {
        string target = Path.GetFullPath(options.Required("--data-dir"));
        RoomRegistry.Initialize(target);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { config = Path.Combine(target, "server.json"), adminFile = Path.Combine(target, "admin.json"), registryVersion = RoomRegistry.Version }));
        return 0;
    }
    private static ServerAdministration ReadServerAdmin(string path) {
        RequirePrivateCredential(path);
        var admin = RoomRegistry.Read<ServerAdministration>(path);
        DocumentModel.Require(admin != null && admin.schema == 1 && DocumentModel.ValidId(admin.serverId) && DocumentModel.ValidAssetHash(admin.credential), "invalid-server-admin-file");
        return admin;
    }
    private static async Task<int> Hosting(string operation, Options options) {
        var admin = ReadServerAdmin(options.Required("--admin-file"));
        if (operation == "restore") {
            // Offline registration is a restore into private registry staging,
            // preserving the archived room/generation/credentials/history.
            using var registry = new RoomRegistry(options.Required("--config"), recover: false);
            registry.Authenticate(admin);
            Console.WriteLine(WireJson.Text(registry.Restore(options.Required("--input")))); return 0;
        }
        string origin = ConnectionDoctor.Origin(options.Required("--url"), options.Has("--allow-lan-http"));
        using var stop = CommandCancellation(); var network = await Network(options, stop.Token);
        using var http = network.CreateHttpClient();
        Bootstrap seed = null;
        if (operation == "create") {
            DocumentModel.Require(options.Has("--catalog") != options.Has("--snapshot"), "use-catalog-or-snapshot");
            RoomExport content;
            if (options.Has("--snapshot")) content = LocalJson.Decode<RoomExport>(Encoding.UTF8.GetBytes(ReadText(options.Get("--snapshot"), LocalJson.MaxBytes)));
            else {
                var catalog = WireJson.Decode<CatalogManifest>(Encoding.UTF8.GetBytes(ReadText(options.Get("--catalog"), WireJson.MaxMessageBytes)));
                content = new RoomExport { catalog = catalog, document = new Document { catalogHash = catalog.hash } };
            }
            content.Validate(); string room = options.Required("--room"); DocumentModel.Require(DocumentModel.ValidId(room), "invalid-room");
            var owner = new OwnerFile { room = room, credential = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
            seed = content.CreateRoom(room, Password(options), owner.credential);
            // Retain this private capability even if a reply is lost. List the room
            // before retrying; duplicates never overwrite or create a second copy.
            string output = Path.GetFullPath(options.Required("--owner-output"));
            RoomRegistry.WriteNew(output, owner);
        }
        using var request = new HttpRequestMessage(seed == null ? HttpMethod.Get : HttpMethod.Post, origin + "/admin/rooms");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.credential);
        if (seed != null) { request.Content = new ByteArrayContent(LocalJson.Encode(seed)); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); request.Headers.ExpectContinue = true; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        NetworkPolicy.CheckProxyResponse(response);
        using var stream = await response.Content.ReadAsStreamAsync(); using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) { DocumentModel.Require(bytes.Length + read <= 65536, "invalid-hosting-response"); bytes.Write(buffer, 0, read); }
        if (!response.IsSuccessStatusCode) {
            string reason = null; try { reason = WireJson.Decode<WireError>(bytes.ToArray())?.error; } catch { }
            var codes = new[] { "server-admin-required", "server-creation-busy", "server-room-limit", "room-already-registered", "duplicate-room-identity", "server-geometry-quota", "registry-destination-exists", "invalid-room-bootstrap" };
            throw new CollaborationException("hosting-http-" + (int)response.StatusCode + (codes.Contains(reason) ? ": " + reason : ""));
        }
        if (seed == null) Console.WriteLine(WireJson.Text(WireJson.Decode<HostedRoomList>(bytes.ToArray())));
        else Console.WriteLine(WireJson.Text(WireJson.Decode<HostedRoomStatus>(bytes.ToArray())));
        return 0;
    }
}
