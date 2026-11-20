using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Protobot;
using Protobot.Collaboration;

internal static partial class Program {
    private static void GlbChecks(string directory) {
        byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        using var binary = new MemoryStream();
        using (var writer = new BinaryWriter(binary, Encoding.UTF8, true)) {
            for (int i = 0; i < 3; i++) {
                writer.Write(i == 1 ? 1f : 0f);
                writer.Write(i == 2 ? 1f : 0f);
                writer.Write(i == 1 ? 1f : 0f);
                writer.Write(i == 1 ? ushort.MaxValue : (ushort)0);
                writer.Write(i == 2 ? ushort.MaxValue : (ushort)0);
            }
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write((ushort)0);
            writer.Write(image);
        }
        string metadata = @"{
              ""asset"": {""version"": ""2.0""}, ""scene"": 1,
              ""scenes"": [{""nodes"": []}, {""nodes"": [0, 2]}],
              ""nodes"": [
                {""children"": [1], ""translation"": [1, 2, 0]},
                {""mesh"": 0, ""scale"": [-1, 1, 1]},
                {""mesh"": 0, ""matrix"": [1,0,0,0, 0,1,0,0, 0,0,1,0, 3,2,1,1]}
              ],
              ""meshes"": [{""primitives"": [{""attributes"": {""POSITION"": 0, ""TEXCOORD_0"": 1}, ""indices"": 2, ""material"": 0}]}],
              ""accessors"": [
                {""bufferView"": 0, ""componentType"": 5126, ""count"": 3, ""type"": ""VEC3""},
                {""bufferView"": 0, ""byteOffset"": 12, ""componentType"": 5123, ""normalized"": true, ""count"": 3, ""type"": ""VEC2""},
                {""bufferView"": 1, ""componentType"": 5123, ""count"": 3, ""type"": ""SCALAR""}
              ],
              ""buffers"": [{""byteLength"": %BIN_LENGTH%}],
              ""bufferViews"": [
                {""buffer"": 0, ""byteLength"": 48, ""byteStride"": 16},
                {""buffer"": 0, ""byteOffset"": 48, ""byteLength"": 6},
                {""buffer"": 0, ""byteOffset"": 56, ""byteLength"": %IMAGE_LENGTH%}
              ],
              ""images"": [{""bufferView"": 2, ""mimeType"": ""image/png""}],
              ""textures"": [{""source"": 0}],
              ""materials"": [{""pbrMetallicRoughness"": {""baseColorFactor"": [0.25, 0.5, 0.75, 1], ""baseColorTexture"": {""index"": 0} } }]
            }"
            .Replace("%BIN_LENGTH%", binary.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("%IMAGE_LENGTH%", image.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string path = Path.Combine(directory, "appearance.glb");
        var bin = binary.ToArray();
        File.WriteAllBytes(path, MakeGlb(metadata, bin));
        var data = CustomModelImporter.ReadPackage(path, 1, out var images);
        Check(data.vertices.Length == 18 && data.triangles.SequenceEqual(new[] { 0, 1, 2, 3, 5, 4 }),
            "GLB selects its default scene and flattens transformed instances, correcting handedness and mirrored nodes");
        Check(Math.Abs(data.vertices.Where((_, index) => index % 3 == 0).Max() - 2 / .0254f) < .001f &&
            Math.Abs(data.vertices[2] - 1 / .0254f) < .001f && Math.Abs(data.vertices[14] + 1 / .0254f) < .001f,
            "GLB centering and scale use its declared meter convention and reverse the Z axis");
        Check(data.appearance.faceMaterials.SequenceEqual(new[] { 0, 0 }) && images.Count == 1 && images[0].SequenceEqual(image),
            "GLB instances reuse one material and an embedded image without sidecars");
        Check(data.appearance.uv.SequenceEqual(new float[] { 0, 1, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0 }),
            "GLB reads interleaved normalized unsigned UVs and converts the image origin");
        Check(Math.Abs(data.appearance.materials[0].g - .735357f) < .00001f,
            "GLB linear base-color factors convert to display RGB");
        data.appearance.textures[0].SetPixels(new byte[] { 255, 0, 0 });
        var asset = new GeometryAsset { name = data.name, vertices = data.vertices.Select(v => (double)v).ToArray(),
            triangles = data.triangles, appearance = data.appearance };
        asset.hash = DocumentModel.GeometryHash(asset);
        File.Delete(path);
        var embedded = GeometryTransfer.Decode(GeometryTransfer.Encode(asset));
        Check(embedded.hash == asset.hash && embedded.appearance.SameAs(data.appearance),
            "normalized GLB appearance survives the existing embedded geometry codec after source deletion");

        var plain = JsonNode.Parse(metadata);
        plain["meshes"][0]["primitives"][0].AsObject().Remove("indices");
        plain["materials"][0]["pbrMetallicRoughness"].AsObject().Remove("baseColorTexture");
        var untextured = CustomModelImporter.Parse(MakeGlb(plain.ToJsonString(), bin), ".glb", "solid-glb", 1);
        Check(untextured.triangles.Length == 6 && untextured.appearance.textures.Length == 0 &&
            untextured.appearance.materials[0].g > .7f, "GLB non-indexed triangles preserve a diffuse color without an image");

        void Refuse(Action<JsonNode> change) {
            var edited = JsonNode.Parse(metadata);
            change(edited);
            File.WriteAllBytes(path, MakeGlb(edited.ToJsonString(), bin));
            try { CustomModelImporter.ReadPackage(path, 1, out _); throw new Exception("Invalid or unsupported GLB was accepted."); }
            catch (InvalidDataException) { Check(true, "invalid or unsupported GLB is refused before materialization"); }
        }
        Refuse(json => json["asset"]["version"] = "1.0");
        Refuse(json => json["images"][0]["uri"] = "https://example.invalid/map.png");
        Refuse(json => json["buffers"][0]["uri"] = "../mesh.bin");
        Refuse(json => json["accessors"][0]["count"] = int.MaxValue);
        Refuse(json => json["bufferViews"][0]["byteStride"] = 4);
        Refuse(json => json["bufferViews"][2]["byteLength"] = int.MaxValue);
        Refuse(json => json["nodes"][1]["children"] = new JsonArray(JsonValue.Create(0)));
        Refuse(json => json["extensionsRequired"] = new JsonArray(JsonValue.Create("KHR_draco_mesh_compression")));
        Refuse(json => json["materials"][0]["alphaMode"] = "BLEND");
        Refuse(json => json["meshes"][0]["primitives"][0]["mode"] = 1);
    }

    private static byte[] MakeGlb(string metadata, byte[] binary) {
        byte[] json = Encoding.UTF8.GetBytes(metadata);
        int jsonLength = (json.Length + 3) & ~3;
        int binLength = (binary.Length + 3) & ~3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67); writer.Write(2); writer.Write(28 + jsonLength + binLength);
        writer.Write(jsonLength); writer.Write(0x4E4F534A); writer.Write(json);
        for (int i = json.Length; i < jsonLength; i++) writer.Write((byte)' ');
        writer.Write(binLength); writer.Write(0x004E4942); writer.Write(binary);
        for (int i = binary.Length; i < binLength; i++) writer.Write((byte)0);
        return stream.ToArray();
    }
}
