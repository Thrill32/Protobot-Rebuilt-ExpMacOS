using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Protobot.Collaboration;

namespace Protobot {
    public static partial class CustomModelImporter {
        private sealed class Glb {
            private const int MaxMetadataBytes = 4 * 1024 * 1024;
            private const int MaxNodes = 4096;
            private readonly byte[] bytes;
            private readonly Root root;
            private readonly int binStart, binLength;
            private readonly bool[] visited;
            private readonly SourceImages images;
            private readonly List<float> vertices, uv = new();
            private readonly List<int> triangles, faceMaterials = new();
            private readonly List<DiffuseMaterial> materials = new();
            private readonly Dictionary<int, int> materialSlots = new(), imageSlots = new();

            private Glb(byte[] bytes, Root root, int binStart, int binLength, List<float> vertices, List<int> triangles, SourceImages images) {
                this.bytes = bytes;
                this.root = root;
                this.binStart = binStart;
                this.binLength = binLength;
                this.vertices = vertices;
                this.triangles = triangles;
                this.images = images;
                visited = new bool[root.nodes.Length];
            }

            public static CustomAppearance Read(byte[] bytes, List<float> vertices, List<int> triangles, SourceImages images) {
                if (bytes.Length < 28 || ReadUInt(bytes, 0) != 0x46546C67 || ReadUInt(bytes, 4) != 2 || ReadUInt(bytes, 8) != bytes.Length)
                    throw Invalid("Expected a GLB version 2 container.");
                uint jsonLength = ReadUInt(bytes, 12);
                if (ReadUInt(bytes, 16) != 0x4E4F534A || jsonLength == 0 || jsonLength % 4 != 0 ||
                    jsonLength > MaxMetadataBytes || jsonLength > bytes.Length - 28)
                    throw Invalid("The GLB JSON chunk is invalid or too large.");
                int binHeader = 20 + (int)jsonLength;
                uint binSize = ReadUInt(bytes, binHeader);
                int binStart = binHeader + 8;
                if (ReadUInt(bytes, binHeader + 4) != 0x004E4942 || binSize % 4 != 0 || binSize > bytes.Length - binStart)
                    throw Invalid("Expected an embedded GLB binary chunk.");
                for (int next = binStart + (int)binSize; next < bytes.Length;) {
                    if (bytes.Length - next < 8) throw Invalid("The GLB chunk table is truncated.");
                    uint size = ReadUInt(bytes, next);
                    uint kind = ReadUInt(bytes, next + 4);
                    if (size % 4 != 0 || size > bytes.Length - next - 8 || kind is 0x4E4F534A or 0x004E4942)
                        throw Invalid("The GLB chunk table is invalid.");
                    next += 8 + (int)size;
                }

                Root root;
                try {
                    var limits = new XmlDictionaryReaderQuotas { MaxDepth = 64, MaxStringContentLength = MaxMetadataBytes,
                        MaxArrayLength = 200000, MaxNameTableCharCount = 65536 };
                    using var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, 20, (int)jsonLength, Encoding.UTF8, limits, null);
                    var serializer = new DataContractJsonSerializer(typeof(Root), new DataContractJsonSerializerSettings {
                        MaxItemsInObjectGraph = 200000, UseSimpleDictionaryFormat = true
                    });
                    root = (Root)serializer.ReadObject(reader);
                }
                catch (SerializationException) { throw Invalid("The GLB metadata is invalid or exceeds its limits."); }
                catch (XmlException) { throw Invalid("The GLB metadata is invalid or exceeds its limits."); }
                if (root?.asset?.version != "2.0" || (root.asset.minVersion != null && root.asset.minVersion != "2.0"))
                    throw Invalid("Expected glTF version 2.0 metadata.");
                if (root.extensionsRequired?.Length > 0 || root.animations?.Length > 0 || root.skins?.Length > 0)
                    throw Invalid("GLB required extensions, skinning, and animation are unsupported.");
                if (root.buffers?.Length != 1 || root.buffers[0] == null || root.buffers[0].uri != null || root.images?.Any(image => image?.uri != null) == true)
                    throw Invalid("GLB buffers and images must be embedded in the file.");
                int embeddedLength = root.buffers[0].byteLength;
                if (embeddedLength <= 0 || embeddedLength > binSize || binSize - embeddedLength > 3)
                    throw Invalid("The GLB buffer length does not match the binary chunk.");
                if (root.nodes == null || root.nodes.Length > MaxNodes || root.meshes?.Length > MaxNodes)
                    throw Invalid("A GLB scene must contain at most 4,096 nodes and meshes.");

                var import = new Glb(bytes, root, binStart, embeddedLength, vertices, triangles, images);
                var scene = Element(root.scenes, root.scene ?? 0, "scene");
                foreach (int node in scene.nodes ?? Array.Empty<int>()) import.Visit(node, Matrix4x4.Identity, 0);
                return import.materials.Count == 0 ? null : new CustomAppearance {
                    materials = import.materials.ToArray(), faceMaterials = import.faceMaterials.ToArray(),
                    uv = images.textures.Count == 0 ? Array.Empty<float>() : import.uv.ToArray(), textures = images.textures.ToArray()
                };
            }

            private void Visit(int index, Matrix4x4 parent, int depth) {
                var node = Element(root.nodes, index, "node");
                if (depth >= 64 || visited[index]) throw Invalid("The GLB scene contains a repeated node or exceeds the nesting limit.");
                visited[index] = true;
                if (node.skin.HasValue || node.weights != null || node.extensions?.ContainsKey("EXT_mesh_gpu_instancing") == true)
                    throw Invalid("GLB skinning, morphing, and GPU instancing are unsupported.");
                var world = NodeTransform(node) * parent;
                float determinant = world.GetDeterminant();
                if (!Finite(determinant) || determinant == 0) throw Invalid("A GLB node has an invalid or singular transform.");
                if (node.mesh.HasValue) {
                    var mesh = Element(root.meshes, node.mesh.Value, "mesh");
                    if (mesh.weights != null || mesh.primitives == null) throw Invalid("The GLB mesh is invalid or contains morph targets.");
                    foreach (var primitive in mesh.primitives) AddPrimitive(primitive, world, determinant > 0);
                }
                foreach (int child in node.children ?? Array.Empty<int>()) Visit(child, world, depth + 1);
            }

            private static Matrix4x4 NodeTransform(Node node) {
                if (node.matrix != null) {
                    var m = node.matrix;
                    if (node.translation != null || node.rotation != null || node.scale != null || m.Length != 16 || !m.All(Finite) ||
                        m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1) throw Invalid("A GLB node matrix must be affine.");
                    // System.Numerics uses row vectors, so the serialized column-major order transposes directly.
                    return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                }
                var scale = Vector(node.scale, Vector3.One);
                var translation = Vector(node.translation, Vector3.Zero);
                var rotation = Quaternion.Identity;
                if (node.rotation != null) {
                    var q = node.rotation;
                    if (q.Length != 4 || !q.All(Finite)) throw Invalid("A GLB node rotation is invalid.");
                    rotation = new Quaternion(q[0], q[1], q[2], q[3]);
                    if (Math.Abs(rotation.LengthSquared() - 1) > .001f) throw Invalid("A GLB node rotation must be normalized.");
                    rotation = Quaternion.Normalize(rotation);
                }
                return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            }

            private static Vector3 Vector(float[] values, Vector3 fallback) {
                if (values == null) return fallback;
                if (values.Length != 3 || !values.All(Finite)) throw Invalid("A GLB node transform contains invalid coordinates.");
                return new Vector3(values[0], values[1], values[2]);
            }

            private void AddPrimitive(Primitive p, Matrix4x4 world, bool reverse) {
                if (p == null || (p.mode ?? 4) != 4 || p.targets != null || p.attributes == null ||
                    !p.attributes.TryGetValue("POSITION", out int positions) || p.attributes.ContainsKey("COLOR_0"))
                    throw Invalid("GLB requires static triangle meshes with POSITION and without vertex colors.");
                var position = ReadAccessor(positions, "VEC3", 3, false);
                if (position.component != 5126 || position.normalized) throw Invalid("GLB positions must use floating-point coordinates.");
                var indices = p.indices.HasValue ? ReadAccessor(p.indices.Value, "SCALAR", 1, true) : default;
                int indexCount = p.indices.HasValue ? indices.count : position.count;
                if (p.indices.HasValue && (indices.normalized || indices.component == 5126) || indexCount < 3 || indexCount % 3 != 0)
                    throw Invalid("GLB triangle indices are invalid.");
                if (position.count > MaxVertices - vertices.Count / 3 || indexCount / 3 > MaxTriangles - triangles.Count / 3)
                    throw Invalid("Models may contain at most 500,000 triangles and 1,500,000 vertices.");

                if (p.material < 0) throw Invalid("A GLB material index cannot be negative.");
                int slot = MaterialSlot(p.material ?? -1);
                bool textured = materials[slot].texture >= 0;
                Access uvAccessor = default;
                if (textured) {
                    var material = root.materials[p.material.Value];
                    int set = material.pbrMetallicRoughness.baseColorTexture.texCoord ?? 0;
                    if (set < 0 || !p.attributes.TryGetValue("TEXCOORD_" + set, out int texcoord))
                        throw Invalid("A textured GLB primitive needs its assigned UV set.");
                    uvAccessor = ReadAccessor(texcoord, "VEC2", 2, false);
                    if (uvAccessor.count != position.count || !(uvAccessor.component == 5126 && !uvAccessor.normalized ||
                        uvAccessor.component is 5121 or 5123 && uvAccessor.normalized))
                        throw Invalid("GLB texture coordinates are invalid.");
                }
                int first = vertices.Count / 3;
                bool hasUvs = images.textures.Count > 0;
                if (hasUvs && uv.Count < first * 2) uv.AddRange(new float[first * 2]);
                for (int i = 0; i < position.count; i++) {
                    int offset = position.start + i * position.stride;
                    var v = Vector3.Transform(new Vector3(Single(offset), Single(offset + 4), Single(offset + 8)), world);
                    if (!Finite(v.X) || !Finite(v.Y) || !Finite(v.Z)) throw Invalid("A GLB vertex is not finite.");
                    // Reflect Z when converting from glTF to Unity. The winding below accounts for this reflection.
                    vertices.Add(v.X); vertices.Add(v.Y); vertices.Add(-v.Z);
                    if (textured) {
                        int uvOffset = uvAccessor.start + i * uvAccessor.stride;
                        float x = UvComponent(uvAccessor, uvOffset), y = 1f - UvComponent(uvAccessor, uvOffset + uvAccessor.size);
                        if (!Finite(x) || !Finite(y) || Math.Abs(x) > 1000000 || Math.Abs(y) > 1000000)
                            throw Invalid("GLB UV coordinates are out of range.");
                        uv.Add(x); uv.Add(y);
                    }
                    else if (hasUvs) { uv.Add(0); uv.Add(0); }
                }
                int Index(int i) {
                    uint value = p.indices.HasValue ? Unsigned(indices, indices.start + indices.stride * i) : (uint)i;
                    if (value >= position.count) throw Invalid("A GLB triangle references a missing vertex.");
                    return first + (int)value;
                }
                for (int i = 0; i < indexCount; i += 3) {
                    triangles.Add(Index(i)); triangles.Add(Index(i + (reverse ? 2 : 1))); triangles.Add(Index(i + (reverse ? 1 : 2)));
                    faceMaterials.Add(slot);
                }
            }

            private int MaterialSlot(int index) {
                if (materialSlots.TryGetValue(index, out int slot)) return slot;
                if (materials.Count >= CustomAppearance.MaxMaterials) throw Invalid("A model may use up to 32 material slots.");
                var data = new DiffuseMaterial { hasColor = true };
                if (index != -1) {
                    var material = Element(root.materials, index, "material");
                    if (material.alphaMode != null && material.alphaMode != "OPAQUE")
                        throw Invalid("GLB material transparency is unsupported.");
                    var pbr = material.pbrMetallicRoughness;
                    if (pbr?.baseColorFactor != null) {
                        var rgba = pbr.baseColorFactor;
                        if (rgba.Length != 4 || !rgba.All(channel => channel >= 0 && channel <= 1))
                            throw Invalid("GLB base color factors must be between 0 and 1.");
                        data.r = DisplayColor(rgba[0]); data.g = DisplayColor(rgba[1]); data.b = DisplayColor(rgba[2]);
                    }
                    if (pbr?.baseColorTexture != null) {
                        if (pbr.baseColorTexture.extensions?.ContainsKey("KHR_texture_transform") == true)
                            throw Invalid("GLB texture transforms are unsupported. Bake them into the UVs before importing.");
                        var texture = Element(root.textures, pbr.baseColorTexture.index, "texture");
                        if (texture.sampler.HasValue) {
                            var sampler = Element(root.samplers, texture.sampler.Value, "sampler");
                            if ((sampler.wrapS ?? 10497) != 10497 || (sampler.wrapT ?? 10497) != 10497)
                                throw Invalid("GLB diffuse textures must use Repeat wrapping.");
                        }
                        if (!texture.source.HasValue) throw Invalid("A GLB diffuse texture needs an embedded PNG or JPEG.");
                        int source = texture.source.Value;
                        if (!imageSlots.TryGetValue(source, out int imageSlot)) {
                            var image = Element(root.images, source, "image");
                            if (!image.bufferView.HasValue || image.mimeType != "image/png" && image.mimeType != "image/jpeg")
                                throw Invalid("A GLB diffuse texture needs an embedded PNG or JPEG.");
                            var (start, length, stride) = Buffer(image.bufferView.Value);
                            if (length > images.AvailableBytes || stride != 0 || images.textures.Count >= CustomAppearance.MaxTextures)
                                throw Invalid("GLB diffuse images exceed the source budget or use an invalid buffer view.");
                            var sourceBytes = new byte[length];
                            Array.Copy(bytes, start, sourceBytes, 0, length);
                            imageSlot = images.Add(sourceBytes, image.mimeType == "image/png");
                            imageSlots.Add(source, imageSlot);
                        }
                        data.texture = imageSlot;
                    }
                }
                slot = materials.Count;
                materialSlots.Add(index, slot);
                materials.Add(data);
                return slot;
            }

            // Store factors in the same display-RGB convention as paint and OBJ materials.
            private static float DisplayColor(float linear) => linear <= .0031308f ? 12.92f * linear :
                Math.Min(1, (float)(1.055 * Math.Pow(linear, 1.0 / 2.4) - .055));

            private (int start, int length, int stride) Buffer(int index) {
                var view = Element(root.bufferViews, index, "buffer view");
                int relative = view.byteOffset ?? 0;
                int stride = view.byteStride ?? 0;
                if (view.buffer != 0 || relative < 0 || view.byteLength <= 0 || view.byteLength > binLength - relative ||
                    view.byteStride.HasValue && (stride < 4 || stride > 252 || stride % 4 != 0) ||
                    view.extensions?.ContainsKey("EXT_meshopt_compression") == true)
                    throw Invalid("A GLB buffer view is invalid or compressed.");
                return (binStart + relative, view.byteLength, stride);
            }

            private Access ReadAccessor(int index, string type, int components, bool indexBuffer) {
                var a = Element(root.accessors, index, "accessor");
                if (a.sparse != null || !a.bufferView.HasValue || a.type != type || a.count <= 0)
                    throw Invalid("GLB requires dense, embedded mesh accessors.");
                var (start, length, stride) = Buffer(a.bufferView.Value);
                int size = a.componentType switch { 5121 => 1, 5123 => 2, 5125 or 5126 => 4, _ => 0 };
                int offset = a.byteOffset ?? 0;
                int element = size * components;
                if (size == 0 || offset < 0 || indexBuffer && stride != 0 || offset % size != 0 || start % size != 0 ||
                    stride != 0 && (stride < element || stride % size != 0))
                    throw Invalid("A GLB accessor has an invalid component type, offset, or stride.");
                if (stride == 0) stride = element;
                if ((long)offset + (long)(a.count - 1) * stride + element > length)
                    throw Invalid("A GLB accessor exceeds its binary buffer view.");
                return new Access(start + offset, stride, a.count, a.componentType, size, a.normalized);
            }

            private float Single(int offset) => BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt(bytes, offset)));
            private float UvComponent(Access a, int offset) => a.component == 5126 ? Single(offset) :
                Unsigned(a, offset) / (a.size == 1 ? 255f : 65535f);
            private uint Unsigned(Access a, int offset) => a.size switch {
                1 => bytes[offset], 2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)), _ => ReadUInt(bytes, offset)
            };
            private static uint ReadUInt(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            private static T Element<T>(T[] values, int index, string label) where T : class {
                if (values == null || index < 0 || index >= values.Length || values[index] == null)
                    throw Invalid("The GLB references a missing " + label + ".");
                return values[index];
            }
            private static InvalidDataException Invalid(string message) => new(message);

            private readonly struct Access {
                public readonly int start, stride, count, component, size;
                public readonly bool normalized;
                public Access(int start, int stride, int count, int component, int size, bool normalized) {
                    this.start = start; this.stride = stride; this.count = count;
                    this.component = component; this.size = size; this.normalized = normalized;
                }
            }

            // These fields are assigned by DataContractJsonSerializer.
#pragma warning disable CS0649
            [DataContract] private sealed class Root {
                [DataMember] public Asset asset;
                [DataMember] public string[] extensionsRequired;
                [DataMember] public object[] animations, skins;
                [DataMember] public int? scene;
                [DataMember] public Scene[] scenes;
                [DataMember] public Node[] nodes;
                [DataMember] public Mesh[] meshes;
                [DataMember] public Accessor[] accessors;
                [DataMember] public BufferView[] bufferViews;
                [DataMember] public BinBuffer[] buffers;
                [DataMember] public Image[] images;
                [DataMember] public Material[] materials;
                [DataMember] public Texture[] textures;
                [DataMember] public Sampler[] samplers;
            }
            [DataContract] private sealed class Asset { [DataMember] public string version, minVersion; }
            [DataContract] private sealed class Scene { [DataMember] public int[] nodes; }
            [DataContract] private sealed class Node {
                [DataMember] public int? mesh, skin;
                [DataMember] public int[] children;
                [DataMember] public float[] matrix, translation, rotation, scale, weights;
                [DataMember] public Dictionary<string, object> extensions;
            }
            [DataContract] private sealed class Mesh { [DataMember] public Primitive[] primitives; [DataMember] public float[] weights; }
            [DataContract] private sealed class Primitive {
                [DataMember] public int? mode, indices, material;
                [DataMember] public Dictionary<string, int> attributes;
                [DataMember] public object[] targets;
            }
            [DataContract] private sealed class Accessor {
                [DataMember] public int? bufferView, byteOffset;
                [DataMember] public int componentType, count;
                [DataMember] public string type;
                [DataMember] public bool normalized;
                [DataMember] public object sparse;
            }
            [DataContract] private sealed class BufferView {
                [DataMember(IsRequired = true)] public int buffer, byteLength;
                [DataMember] public int? byteOffset, byteStride;
                [DataMember] public Dictionary<string, object> extensions;
            }
            [DataContract] private sealed class BinBuffer { [DataMember] public int byteLength; [DataMember] public string uri; }
            [DataContract] private sealed class Image { [DataMember] public string uri, mimeType; [DataMember] public int? bufferView; }
            [DataContract] private sealed class Material { [DataMember] public string alphaMode; [DataMember] public Pbr pbrMetallicRoughness; }
            [DataContract] private sealed class Pbr { [DataMember] public float[] baseColorFactor; [DataMember] public TextureInfo baseColorTexture; }
            [DataContract] private sealed class TextureInfo {
                [DataMember(IsRequired = true)] public int index;
                [DataMember] public int? texCoord;
                [DataMember] public Dictionary<string, object> extensions;
            }
            [DataContract] private sealed class Texture { [DataMember] public int? source, sampler; }
            [DataContract] private sealed class Sampler { [DataMember] public int? wrapS, wrapT; }
#pragma warning restore CS0649
        }
    }
}
