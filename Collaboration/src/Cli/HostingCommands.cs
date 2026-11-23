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
        string policy = options.Get("--creation-policy", "admin-only");
        RoomRegistry.Initialize(target, policy, options.Get("--directory-policy", "hidden"));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { config = Path.Combine(target, "server.json"), adminFile = Path.Combine(target, "admin.json"),
            creationPolicy = policy, creatorFile = policy == "key" ? Path.Combine(target, "creator.json") : null, registryVersion = RoomRegistry.Version }));
        return 0;
    }
    private static ServerAdministration ReadServerAdmin(string path) {
        RequirePrivateCredential(path);
        var admin = RoomRegistry.Read<ServerAdministration>(path);
        DocumentModel.Require(admin != null && admin.schema == 1 && DocumentModel.ValidId(admin.serverId) && DocumentModel.ValidAssetHash(admin.credential), "invalid-server-admin-file");
        return admin;
    }
    private static int ConfigureServerCreation(Options options) {
        var admin = ReadServerAdmin(options.Required("--admin-file"));
        using var registry = new RoomRegistry(options.Required("--config"), recover: false);
        string policy = options.Required("--creation-policy");
        registry.ConfigureCreation(admin, policy, options.Get("--creator-output"));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { creationPolicy = policy, creatorFile = options.Get("--creator-output") }));
        return 0;
    }
    private static int ConfigureServerDirectory(Options options) {
        var admin = ReadServerAdmin(options.Required("--admin-file"));
        using var registry = new RoomRegistry(options.Required("--config"), recover: false);
        string policy = options.Required("--directory-policy"); registry.ConfigureDirectory(admin, policy);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { directoryPolicy = policy })); return 0;
    }
    private static async Task<int> Hosting(string operation, Options options) {
        bool delegated = options.Has("--creation-key-file") || options.Has("--open-creation");
        DocumentModel.Require(operation == "create" || !delegated, "server-admin-required");
        DocumentModel.Require(!delegated || !options.Has("--admin-file") && !(options.Has("--creation-key-file") && options.Has("--open-creation")), "use-one-creation-authority");
        var admin = delegated ? null : ReadServerAdmin(options.Required("--admin-file"));
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
        string credential = admin?.credential;
        if (delegated) {
            var capabilities = await ConnectionRetry.GetAsync<Capabilities>(http, origin + "/capabilities", stop.Token);
            ServerRooms.RequireCreation(capabilities);
            if (options.Has("--creation-key-file")) {
                string path = options.Get("--creation-key-file"); RequirePrivateCredential(path);
                var key = RoomRegistry.Read<RoomCreationKey>(path);
                DocumentModel.Require(key != null && key.format == "protobot-room-creator" && key.schema == 1 && key.serverId == capabilities.serverId && DocumentModel.ValidAssetHash(key.credential), "invalid-room-creation-key-file");
                credential = key.credential;
                DocumentModel.Require(capabilities.roomCreationPolicy == "key", "room-creation-key-required");
            }
            else DocumentModel.Require(capabilities.roomCreationPolicy == "open", "room-creation-key-required");
        }
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
            bool allowEditorReplacement = EditorReplacementPolicy(options);
            if (allowEditorReplacement) {
                var caps = await ConnectionRetry.GetAsync<Capabilities>(http, origin + "/capabilities", stop.Token);
                DocumentModel.Require(caps.editorRoomReplacementVersion == RoomReplacement.EditorPolicyVersion, "editor-room-replacement-unavailable");
            }
            seed = content.CreateRoom(room, Password(options), owner.credential, allowEditorReplacement);
            // Retain this private capability even if a reply is lost. List the room
            // before retrying; duplicates never overwrite or create a second copy.
            string output = Path.GetFullPath(options.Required("--owner-output"));
            RoomRegistry.WriteNew(output, owner);
        }
        if (seed == null) {
            try {
                var rooms = await ConnectionRetry.GetAsync<HostedRoomList>(http, origin + "/admin/rooms", stop.Token, admin.credential, progress: retryProgress);
                Console.WriteLine(WireJson.Text(rooms)); return 0;
            }
            catch (CollaborationException error) when (error.HttpStatusCode > 0) {
                throw new CollaborationException("hosting-http-" + error.HttpStatusCode + (error.Code == "server-admin-required" ? ": " + error.Code : ""));
            }
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, origin + (delegated ? "/rooms" : "/admin/rooms"));
        if (credential != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (seed != null) { request.Content = new ByteArrayContent(LocalJson.Encode(seed)); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); request.Headers.ExpectContinue = true; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        NetworkPolicy.CheckProxyResponse(response);
        using var stream = await response.Content.ReadAsStreamAsync(); using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) { DocumentModel.Require(bytes.Length + read <= 65536, "invalid-hosting-response"); bytes.Write(buffer, 0, read); }
        if (!response.IsSuccessStatusCode) {
            string reason = null; try { reason = WireJson.Decode<WireError>(bytes.ToArray())?.error; } catch { }
            var codes = new[] { "server-admin-required", "room-creation-key-required", "server-creation-busy", "server-room-limit", "room-already-registered", "duplicate-room-identity", "server-geometry-quota", "registry-destination-exists", "invalid-room-bootstrap" };
            throw new CollaborationException("hosting-http-" + (int)response.StatusCode + (codes.Contains(reason) ? ": " + reason : ""));
        }
        if (seed == null) Console.WriteLine(WireJson.Text(WireJson.Decode<HostedRoomList>(bytes.ToArray())));
        else Console.WriteLine(WireJson.Text(WireJson.Decode<HostedRoomStatus>(bytes.ToArray())));
        return 0;
    }
}
