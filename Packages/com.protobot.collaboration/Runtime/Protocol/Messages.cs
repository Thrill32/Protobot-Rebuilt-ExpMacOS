using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Protobot.Collaboration {
    public static class CollaborationProtocol {
        public const string Version = "0.9.0-alpha";
        public const int ShortPollingVersion = 1;
    }
    // Public fields and arrays are deliberate: this schema is also consumed by Unity.
    [Serializable] public sealed class Part {
        public string id;
        public long version;
        public string catalogId;
        public string state;
        public double[] position = new double[3];
        public double[] rotation = new double[] { 0, 0, 0, 1 };
        public bool hasPaint;
        public double[] color = new double[] { 1, 1, 1 };
        // CUSTOM parts reference immutable document geometry and own an ordered
        // mounting layout. Catalog parts leave both fields empty.
        public string assetHash;
        public CustomMount[] mounts = Array.Empty<CustomMount>();
    }
    [Serializable] public sealed class Document {
        public int schema = 2;
        public string generation = Guid.NewGuid().ToString("N");
        public string catalogHash;
        public long revision;
        public Part[] parts = Array.Empty<Part>();
        public VersionRef[] tombstones = Array.Empty<VersionRef>();
        public ChainRecord[] chains = Array.Empty<ChainRecord>();
        public VersionRef[] chainTombstones = Array.Empty<VersionRef>();
        public GeometryAsset[] assets = Array.Empty<GeometryAsset>();
    }
    [Serializable] public sealed class GeometryAsset {
        public string hash;
        public string name;
        public double[] vertices = Array.Empty<double>();
        public int[] triangles = Array.Empty<int>();
    }
    [Serializable] public sealed class CustomMount {
        public double[] position = new double[3];
        public double[] rotation = new double[] { 0, 0, 0, 1 };
        public double diameter, depth;
        public bool threaded;
    }
    [Serializable] public sealed class ChainRecord {
        public string id;
        public long version;
        public string[] members = Array.Empty<string>();
        public int routeVersion;
        public int[] wraps;
    }
    [Serializable] public sealed class VersionRef { public string id; public long version; }
    [Serializable] public sealed class ParameterRule {
        public bool used;
        public bool custom;
        public bool integer;
        public double min, max;
        public string[] options = Array.Empty<string>();
    }
    [Serializable] public sealed class CatalogVariant {
        public string first;
        public ParameterRule second = new ParameterRule();
    }
    [Serializable] public sealed class CatalogEntry {
        public string id;
        public ParameterRule first = new ParameterRule();
        public ParameterRule second = new ParameterRule();
        public CatalogVariant[] variants = Array.Empty<CatalogVariant>();
    }
    [Serializable] public sealed class CatalogManifest {
        public int protocol = 1;
        public string hash;
        public CatalogEntry[] entries = Array.Empty<CatalogEntry>();
    }
    [Serializable] public sealed class Transaction {
        public int protocol = 1;
        public string generation;
        public string operationId = Guid.NewGuid().ToString("N");
        public long sequence;
        public string kind;
        public string lease;
        // Optional for results whose dependency is the complete mechanism graph.
        public long? expectedRevision;
        public VersionRef[] expected = Array.Empty<VersionRef>();
        public Part[] writes = Array.Empty<Part>();
        public string[] deletes = Array.Empty<string>();
        public VersionRef[] expectedChains = Array.Empty<VersionRef>();
        public ChainRecord[] chainWrites = Array.Empty<ChainRecord>();
        public string[] chainDeletes = Array.Empty<string>();
        public GeometryAsset[] assetWrites = Array.Empty<GeometryAsset>();
    }
    [Serializable] public sealed class Receipt {
        public string operationId;
        public bool accepted;
        public string error;
        public long revision;
    }
    [Serializable] public sealed class AcceptedEdit {
        public long revision;
        public string author;
        public Transaction transaction;
        public string hash;
    }
    [Serializable] public sealed class JoinRequest {
        [System.Runtime.Serialization.OptionalField] public int geometryLimitsVersion = GeometryTransfer.LimitsVersion;
        [System.Runtime.Serialization.OptionalField] public string expectedGeneration, roomIdentity, resynchronizeFromGeneration;
        public int protocol = 1;
        public string version = CollaborationProtocol.Version;
        public string room;
        public string catalogHash;
        public string name;
        public string password;
        public string resumeToken;
        public string ownerCredential;
        public bool viewer;
    }
    [Serializable] public sealed class Participant { public string id, name, role; }
    [Serializable] public sealed class Membership {
        public Participant participant;
        public bool active, revoked;
    }
    [Serializable] public sealed class MembershipList { public Membership[] memberships = Array.Empty<Membership>(); }
    [Serializable] public sealed class RemoveParticipant { public string participantId; }
    [Serializable] public sealed class RemovalResult { public string participantId; public bool alreadyRevoked; }
    [Serializable] public sealed class JoinResponse {
        [System.Runtime.Serialization.OptionalField] public string room;
        public string token;
        public string resumeToken;
        public long sequence;
        public Participant participant;
        public string incarnation;
        public Document document;
    }
    [Serializable] public sealed class LeaseRequest { public string token; public string[] ids = Array.Empty<string>(); }
    [Serializable] public sealed class LeaseResponse { public string token; public int lifetimeMilliseconds; }
    [Serializable] public sealed class Preview {
        public string lease;
        public string author;
        public long sequence;
        public Part[] parts = Array.Empty<Part>();
    }
    [Serializable] public sealed class EventBatch {
        public AcceptedEdit[] edits = Array.Empty<AcceptedEdit>();
        public Preview[] previews = Array.Empty<Preview>();
        public Participant[] participants = Array.Empty<Participant>();
        public long transientVersion;
    }
    [Serializable] public sealed class Capabilities {
        public int protocol = 1;
        public string version = CollaborationProtocol.Version;
        public string[] transports = new[] { "websocket", "long-polling" };
        public bool durable = false;
        // Optional additive capability; absent on earlier 0.7 helpers.
        [System.Runtime.Serialization.OptionalField] public bool participantAdministration;
        [System.Runtime.Serialization.OptionalField] public bool geometryTransfer = true;
        [System.Runtime.Serialization.OptionalField] public int geometryLimitsVersion, geometryAssetBytes;
        [System.Runtime.Serialization.OptionalField] public bool multiRoom;
        [System.Runtime.Serialization.OptionalField] public int registryVersion;
        [System.Runtime.Serialization.OptionalField] public int diagnosticsVersion;
        [System.Runtime.Serialization.OptionalField] public int shortPollingVersion;
        [System.Runtime.Serialization.OptionalField] public int roomCreationVersion;
        [System.Runtime.Serialization.OptionalField] public int roomReplacementVersion;
        [System.Runtime.Serialization.OptionalField] public int editorRoomReplacementVersion;
        [System.Runtime.Serialization.OptionalField] public bool allowEditorProjectReplacement;
        [System.Runtime.Serialization.OptionalField] public string roomIdentity;
        [System.Runtime.Serialization.OptionalField] public string roomCreationPolicy;
        [System.Runtime.Serialization.OptionalField] public string serverId;
        public string room, generation, catalogHash;
    }
    [Serializable] public sealed class Bootstrap {
        [System.Runtime.Serialization.OptionalField] public bool allowEditorProjectReplacement;
        public string room = "team";
        public string password;
        public string ownerCredential;
        public CatalogManifest catalog;
        public Document document;
    }
    [Serializable] public sealed class Ready {
        [System.Runtime.Serialization.OptionalField] public int editorRoomReplacementVersion;
        public string type = "ready";
        public string url, room, version = CollaborationProtocol.Version;
        [System.Runtime.Serialization.OptionalField] public bool multiRoom;
        [System.Runtime.Serialization.OptionalField] public int registryVersion;
        public bool durable, recovered;
        public long revision;
    }
    [Serializable] public sealed class WireError { public string error; }

    public sealed class CollaborationException : Exception {
        public string Code { get; }
        // Local exception metadata only; HTTP guidance is optional Retry-After.
        public int RetryAfterMilliseconds { get; }
        public int HttpStatusCode { get; }
        public CollaborationException(string code, int retryAfterMilliseconds = 0, int httpStatusCode = 0) : base(code) {
            Code = code; RetryAfterMilliseconds = Math.Max(0, Math.Min(60000, retryAfterMilliseconds));
            HttpStatusCode = httpStatusCode >= 100 && httpStatusCode <= 599 ? httpStatusCode : 0;
        }
    }

    public static class WireJson {
        public const int MaxMessageBytes = 4 * 1024 * 1024;
        public static byte[] Encode<T>(T value) {
            using (var stream = new MemoryStream()) {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }
        public static T Decode<T>(byte[] bytes) {
            if (bytes == null || bytes.Length > MaxMessageBytes) throw new CollaborationException("message-too-large");
            using (var stream = new MemoryStream(bytes))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        public static T Clone<T>(T value) => Decode<T>(Encode(value));
        public static string Text<T>(T value) => Encoding.UTF8.GetString(Encode(value));
    }
}
