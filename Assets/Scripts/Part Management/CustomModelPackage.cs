using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Protobot.Collaboration;

namespace Protobot {
    public static partial class CustomModelImporter {
        private const int MaxLibraries = 8;
        private const int MaxMtlBytes = 256 * 1024;
        private const int MaxImageBytes = 16 * 1024 * 1024;

        private sealed class Mtl {
            public readonly DiffuseMaterial material = new();
            public string directory;
            public string map;
        }

        private sealed class ObjPackage {
            private readonly string root;
            public readonly List<byte[]> images = new();
            public readonly List<DiffuseTexture> textures = new();
            private readonly Dictionary<string, int> texturePaths = new(StringComparer.Ordinal);
            private long rgbBytes, imageBytes;

            public ObjPackage(string root) => this.root = root;

            public Dictionary<string, Mtl> ReadLibraries(string text) {
                Dictionary<string, Mtl> definitions = null;
                var paths = new HashSet<string>(StringComparer.Ordinal);
                using var obj = new StringReader(text);
                string line;
                while ((line = obj.ReadLine()) != null) {
                    var trimmed = line.TrimStart().TrimStart('\uFEFF').TrimStart();
                    if (!trimmed.StartsWith("mtllib", StringComparison.Ordinal)) continue;
                    var (command, args) = Statement(trimmed);
                    if (command != "mtllib") continue;
                    var path = Resolve(root, args);
                    if (!paths.Add(path)) continue;
                    if (paths.Count > MaxLibraries || !string.Equals(Path.GetExtension(path), ".mtl", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("An OBJ may reference up to eight local MTL files.");
                    definitions ??= new Dictionary<string, Mtl>(StringComparer.Ordinal);
                    ReadLibrary(path, definitions);
                }
                return definitions;
            }

            private static void ReadLibrary(string path, Dictionary<string, Mtl> definitions) {
                using var reader = new StringReader(Encoding.UTF8.GetString(ReadBytes(path, MaxMtlBytes)).TrimStart('\uFEFF'));
                Mtl current = null;
                string line;
                while ((line = reader.ReadLine()) != null) {
                    var (command, args) = Statement(line);
                    switch (command) {
                        case "newmtl":
                            current = new Mtl { directory = Path.GetDirectoryName(path) };
                            if (definitions.Count >= CustomAppearance.MaxMaterials || args.Length == 0 || args.Length > 200 ||
                                definitions.ContainsKey(args))
                                throw new InvalidDataException("MTL material names must be unique. An OBJ may define up to 32 materials.");
                            definitions.Add(args, current);
                            break;
                        case "Kd":
                            var rgb = Words(args);
                            if (current == null || rgb.Length != 3 || current.material.hasColor)
                                throw new InvalidDataException("Each MTL material may specify one RGB Kd color.");
                            var material = current.material;
                            material.r = Number(rgb[0]); material.g = Number(rgb[1]); material.b = Number(rgb[2]);
                            if (material.r < 0 || material.r > 1 || material.g < 0 || material.g > 1 || material.b < 0 || material.b > 1)
                                throw new InvalidDataException("MTL Kd channels must be between 0 and 1.");
                            material.hasColor = true;
                            break;
                        case "map_Kd":
                            if (current == null || current.map != null || args.Length == 0 || args[0] == '-')
                                throw new InvalidDataException("Each MTL material may use one map_Kd path without options.");
                            current.map = args;
                            break;
                    }
                }
            }

            public int AddTexture(Mtl material) {
                var path = Resolve(material.directory, material.map);
                if (texturePaths.TryGetValue(path, out int slot)) return slot;
                if (textures.Count >= CustomAppearance.MaxTextures)
                    throw new InvalidDataException("An OBJ may use up to eight diffuse images.");
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
                    throw new InvalidDataException("Diffuse images must be local PNG or JPEG files.");
                var bytes = ReadBytes(path, (int)(MaxImageBytes - imageBytes));
                var image = extension == ".png" ? PngDimensions(bytes) : JpegDimensions(bytes);
                rgbBytes += image.RgbBytes;
                if (rgbBytes > CustomAppearance.MaxRgbBytes)
                    throw new InvalidDataException("Diffuse images exceed the 16 MiB decoded RGB budget.");
                imageBytes += bytes.Length;
                slot = textures.Count;
                texturePaths.Add(path, slot);
                textures.Add(image);
                images.Add(bytes);
                return slot;
            }

            private static string Resolve(string directory, string path) {
                // Each step stays inside the package and is checked before following it.
                // Use slash-separated paths on both Windows and macOS.
                if (path.Length == 0 || path.Length > 512 || path.IndexOfAny(new[] { '\\', ':', '\0', '"', '?', '*', '<', '>', '|', '%', '~' }) >= 0)
                    throw new InvalidDataException("Model dependencies must use relative paths inside the import directory.");
                foreach (string part in path.Split('/')) {
                    if (part == ".") continue;
                    if (part == ".." || part.Length == 0 || part.Trim() != part || part.EndsWith(".", StringComparison.Ordinal))
                        throw new InvalidDataException("Model dependencies must use relative paths inside the import directory.");
                    directory = Path.Combine(directory, part);
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Model dependencies cannot use symbolic links or junctions.");
                }
                return directory;
            }
        }

        private sealed class ObjFaces {
            private readonly ObjPackage package;
            private readonly Dictionary<string, Mtl> definitions;
            private readonly Dictionary<string, int> slots = new(StringComparer.Ordinal);
            private readonly List<DiffuseMaterial> materials = new();
            private readonly List<int> faceMaterials = new();
            private readonly List<(float u, float v)> coordinates = new();
            private readonly Dictionary<(int vertex, int uv), int> corners = new();
            private readonly List<float> uv = new();
            private string selected = "";
            private int active;
            private int texture = -1;
            private bool hasAppearance;

            public ObjFaces(ObjPackage package, Dictionary<string, Mtl> definitions) {
                this.package = package;
                this.definitions = definitions;
            }

            public void AddUv(string args) {
                var words = Words(args);
                if (coordinates.Count >= MaxVertices || words.Length < 2)
                    throw new InvalidDataException("Invalid OBJ texture coordinates or too many UVs.");
                float u = Number(words[0]), v = Number(words[1]);
                if (Math.Abs(u) > 1000000 || Math.Abs(v) > 1000000)
                    throw new InvalidDataException("OBJ UV coordinates are out of range.");
                coordinates.Add((u, v));
            }

            public void Select(string name) {
                if (!definitions.ContainsKey(name)) throw new InvalidDataException("An OBJ face uses a missing MTL material.");
                selected = name;
            }

            public int Corner(int vertex, string index, int slash, List<float> source, List<float> vertices) {
                if (!slots.TryGetValue(selected, out active)) {
                    if (materials.Count >= CustomAppearance.MaxMaterials)
                        throw new InvalidDataException("An OBJ may use up to 32 material slots, including unassigned faces.");
                    var record = selected == "" ? new Mtl() : definitions[selected];
                    var material = record.material;
                    if (record.map != null) material.texture = package.AddTexture(record);
                    active = materials.Count;
                    slots.Add(selected, active);
                    materials.Add(material);
                    hasAppearance |= material.hasColor || material.texture >= 0;
                }
                texture = materials[active].texture;
                int uvIndex = -1;
                if (texture >= 0) {
                    int next = slash < 0 ? -1 : index.IndexOf('/', slash + 1);
                    if (slash < 0 || slash == index.Length - 1 || next == slash + 1)
                        throw new InvalidDataException("Every corner of a textured OBJ face needs a UV index.");
                    uvIndex = ObjIndex(next < 0 ? index.Substring(slash + 1) : index.Substring(slash + 1, next - slash - 1),
                        coordinates.Count, "texture coordinate");
                }
                if (corners.TryGetValue((vertex, uvIndex), out int result)) return result;
                result = vertices.Count / 3;
                if (result >= MaxVertices) throw new InvalidDataException("OBJ UV seams exceed the 1,500,000 vertex limit.");
                corners.Add((vertex, uvIndex), result);
                int start = vertex * 3;
                vertices.Add(source[start]); vertices.Add(source[start + 1]); vertices.Add(source[start + 2]);
                var value = uvIndex < 0 ? default : coordinates[uvIndex];
                uv.Add(value.u); uv.Add(value.v);
                return result;
            }

            public void AddFace(int count) {
                for (int i = 0; i < count; i++) faceMaterials.Add(active);
            }

            public CustomAppearance Finish(List<float> source, List<float> vertices) {
                // Include unused source positions in the pivot as on a geometry-only import.
                var used = new HashSet<int>(corners.Keys.Select(key => key.vertex));
                for (int i = 0; i < source.Count / 3; i++) {
                    if (used.Contains(i)) continue;
                    if (vertices.Count / 3 >= MaxVertices) throw new InvalidDataException("OBJ UV seams exceed the 1,500,000 vertex limit.");
                    vertices.Add(source[i * 3]); vertices.Add(source[i * 3 + 1]); vertices.Add(source[i * 3 + 2]);
                    uv.Add(0); uv.Add(0);
                }
                return hasAppearance ? new CustomAppearance { materials = materials.ToArray(), faceMaterials = faceMaterials.ToArray(),
                    uv = package.textures.Count == 0 ? Array.Empty<float>() : uv.ToArray(), textures = package.textures.ToArray() } : null;
            }
        }

        private static DiffuseTexture PngDimensions(byte[] bytes) {
            uint U32(int i) => (uint)bytes[i] << 24 | (uint)bytes[i + 1] << 16 | (uint)bytes[i + 2] << 8 | bytes[i + 3];
            if (bytes.Length < 33 || U32(0) != 0x89504e47 || U32(4) != 0x0d0a1a0a || U32(8) != 13 || U32(12) != 0x49484452)
                throw new InvalidDataException("The diffuse PNG has an invalid header.");
            var image = new DiffuseTexture { width = unchecked((int)U32(16)), height = unchecked((int)U32(20)) };
            bool pixels = false;
            for (int offset = 8; offset <= bytes.Length - 12;) {
                uint length = U32(offset);
                uint kind = U32(offset + 4);
                if (length > bytes.Length - offset - 12) break;
                if (kind is 0x6163544c or 0x6663544c or 0x66644154 or 0x69434350 or 0x7a545874 or 0x69545874)
                    throw new InvalidDataException("Diffuse PNGs must be static and omit compressed profiles and text.");
                if (kind == 0x49444154) pixels = true;
                offset += (int)length + 12;
                if (kind == 0x49454e44 && length == 0 && pixels && offset == bytes.Length) return image;
            }
            throw new InvalidDataException("The diffuse PNG is incomplete.");
        }

        private static DiffuseTexture JpegDimensions(byte[] bytes) {
            if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[bytes.Length - 2] != 0xff || bytes[bytes.Length - 1] != 0xd9)
                throw new InvalidDataException("The diffuse JPEG has an invalid header or is incomplete.");
            DiffuseTexture image = null;
            for (int offset = 2; offset <= bytes.Length - 4;) {
                if (bytes[offset++] != 0xff) break;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset + 2 >= bytes.Length) break;
                int marker = bytes[offset++];
                int length = bytes[offset] * 256 + bytes[offset + 1];
                if (length < 2 || length > bytes.Length - offset) break;
                if (marker == 0xda && image != null) return image;
                if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc) {
                    if ((marker != 0xc0 && marker != 0xc2) || image != null || length < 8 || bytes[offset + 2] != 8)
                        throw new InvalidDataException("Diffuse JPEGs must be 8-bit baseline or progressive images.");
                    image = new DiffuseTexture { height = bytes[offset + 3] * 256 + bytes[offset + 4],
                        width = bytes[offset + 5] * 256 + bytes[offset + 6] };
                }
                offset += length;
            }
            throw new InvalidDataException("The diffuse JPEG has an invalid image frame.");
        }
    }
}
