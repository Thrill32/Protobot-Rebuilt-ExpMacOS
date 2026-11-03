using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Protobot {
    /// <summary>File parsing only; safe to run away from Unity's thread.</summary>
    public static class CustomModelImporter {
        public const int MaxFileBytes = 64 * 1024 * 1024;
        public const int MaxTriangles = 500000;
        public const int MaxVertices = MaxTriangles * 3;

        public static CustomModelData Read(string path, float inchesPerUnit) {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxFileBytes) throw new InvalidDataException("Model files must be smaller than 64 MB.");
            var bytes = new byte[(int)file.Length];
            int offset = 0, count;
            while (offset < bytes.Length && (count = file.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
            if (offset != bytes.Length) throw new EndOfStreamException("Could not read the complete model.");
            return Parse(bytes, Path.GetExtension(path), Path.GetFileNameWithoutExtension(path), inchesPerUnit);
        }

        public static CustomModelData Parse(byte[] bytes, string extension, string name, float inchesPerUnit) {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxFileBytes)
                throw new InvalidDataException("The model is empty or larger than 64 MB.");
            if (!Finite(inchesPerUnit) || inchesPerUnit <= 0) throw new InvalidDataException("Invalid model units.");
            var vertices = new List<float>();
            var triangles = new List<int>();
            switch (extension.ToLowerInvariant()) {
                case ".obj": ParseObj(Encoding.UTF8.GetString(bytes), vertices, triangles); break;
                case ".stl": ParseStl(bytes, vertices, triangles); break;
                default: throw new InvalidDataException("Choose an OBJ or STL model.");
            }
            if (vertices.Count == 0 || triangles.Count == 0) throw new InvalidDataException("The model contains no triangle faces.");
            // Center the editing pivot and bake units into geometry, keeping ordinary transforms at scale one.
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < vertices.Count; i++) { int axis = i % 3; min[axis] = Math.Min(min[axis], vertices[i]); max[axis] = Math.Max(max[axis], vertices[i]); }
            for (int i = 0; i < vertices.Count; i++) {
                int axis = i % 3;
                vertices[i] = (float)(((double)vertices[i] - ((double)min[axis] + max[axis]) * .5) * inchesPerUnit);
                if (!Finite(vertices[i]) || Math.Abs(vertices[i]) > 100000)
                    throw new InvalidDataException("The model dimensions are too large. Check its units.");
            }
            return new CustomModelData { name = name, vertices = vertices.ToArray(), triangles = triangles.ToArray() };
        }

        private static void ParseObj(string text, List<float> vertices, List<int> triangles) {
            using var reader = new StringReader(text.TrimStart('\uFEFF'));
            string line;
            while ((line = reader.ReadLine()) != null) {
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                var words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                if (words[0] == "v") {
                    if (words.Length < 4) throw new InvalidDataException("An OBJ vertex needs three coordinates.");
                    for (int i = 1; i <= 3; i++) vertices.Add(Number(words[i]));
                    CheckLimits(vertices, triangles);
                }
                else if (words[0] == "f") {
                    if (words.Length < 4 || words.Length > 4097) throw new InvalidDataException("An OBJ face needs 3–4096 vertices.");
                    var face = new int[words.Length - 1];
                    for (int i = 1; i < words.Length; i++) {
                        string indexText = words[i].Split('/')[0];
                        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index == 0)
                            throw new InvalidDataException("Invalid OBJ vertex index.");
                        index = index < 0 ? vertices.Count / 3 + index : index - 1;
                        if (index < 0 || index >= vertices.Count / 3) throw new InvalidDataException("An OBJ face references a missing vertex.");
                        face[i - 1] = index;
                    }
                    Triangulate(face, vertices, triangles);
                    CheckLimits(vertices, triangles);
                }
            }
        }

        // Ear clipping also handles concave planar polygons exported by CAD tools.
        private static void Triangulate(int[] face, List<float> v, List<int> triangles) {
            if (face.Length == 3) { triangles.AddRange(face); return; }
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < face.Length; i++) {
                int a = face[i] * 3, b = face[(i + 1) % face.Length] * 3;
                nx += ((double)v[a + 1] - v[b + 1]) * ((double)v[a + 2] + v[b + 2]);
                ny += ((double)v[a + 2] - v[b + 2]) * ((double)v[a] + v[b]);
                nz += ((double)v[a] - v[b]) * ((double)v[a + 1] + v[b + 1]);
            }
            int drop = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
            int u = (drop + 1) % 3, w = (drop + 2) % 3;
            double Cross(int a, int b, int c) => ((double)v[b * 3 + u] - v[a * 3 + u]) * ((double)v[c * 3 + w] - v[a * 3 + w]) -
                ((double)v[b * 3 + w] - v[a * 3 + w]) * ((double)v[c * 3 + u] - v[a * 3 + u]);
            double area = 0;
            for (int i = 1; i < face.Length - 1; i++) area += Cross(face[0], face[i], face[i + 1]);
            double sign = Math.Sign(area);
            var remaining = new List<int>(face);
            while (remaining.Count > 3) {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++) {
                    int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                    if (sign * Cross(a, b, c) <= 0) continue;
                    bool contains = false;
                    foreach (int p in remaining) {
                        if (p == a || p == b || p == c) continue;
                        if (sign * Cross(a, b, p) >= 0 && sign * Cross(b, c, p) >= 0 && sign * Cross(c, a, p) >= 0) { contains = true; break; }
                    }
                    if (contains) continue;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    remaining.RemoveAt(i); clipped = true; break;
                }
                if (!clipped) throw new InvalidDataException("An OBJ polygon is degenerate or self-intersecting. Export triangulated faces.");
            }
            triangles.AddRange(remaining);
        }

        private static void ParseStl(byte[] bytes, List<float> vertices, List<int> triangles) {
            // Binary STL headers may begin with 'solid'; determine the format from its exact record length.
            uint count = bytes.Length >= 84 ? BitConverter.ToUInt32(bytes, 80) : 0;
            if (bytes.Length >= 84 && 84L + 50L * count == bytes.Length) {
                if (count > MaxTriangles) throw new InvalidDataException("Models may contain at most 500,000 triangles.");
                using var reader = new BinaryReader(new MemoryStream(bytes));
                reader.BaseStream.Position = 84;
                for (int i = 0; i < count; i++) {
                    reader.BaseStream.Position += 12; // Authored normal; Unity recalculates mesh normals.
                    for (int j = 0; j < 9; j++) {
                        float value = reader.ReadSingle();
                        if (!Finite(value)) throw new InvalidDataException("An STL vertex is not a finite number.");
                        vertices.Add(value);
                    }
                    reader.ReadUInt16();
                }
            }
            else {
                using var reader = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                string line;
                int facetVertices = -1, facets = 0;
                while ((line = reader.ReadLine()) != null) {
                    var words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 0) continue;
                    if (words[0] == "facet") {
                        if (facetVertices != -1) throw new InvalidDataException("Malformed ASCII STL facet.");
                        facetVertices = 0;
                    }
                    else if (words[0] == "vertex") {
                        if (facetVertices < 0 || facetVertices >= 3 || words.Length != 4) throw new InvalidDataException("Malformed ASCII STL vertex.");
                        for (int i = 1; i <= 3; i++) vertices.Add(Number(words[i]));
                        facetVertices++;
                        CheckLimits(vertices, triangles);
                    }
                    else if (words[0] == "endfacet") {
                        if (facetVertices != 3) throw new InvalidDataException("An STL facet needs exactly three vertices.");
                        facetVertices = -1; facets++;
                    }
                }
                if (facetVertices != -1 || facets == 0) throw new InvalidDataException("The STL file is incomplete or invalid.");
            }
            for (int i = 0; i < vertices.Count / 3; i++) triangles.Add(i);
            CheckLimits(vertices, triangles);
        }

        private static float Number(string text) {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !Finite(value))
                throw new InvalidDataException("A model coordinate is not a finite number.");
            return value;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void CheckLimits(List<float> vertices, List<int> triangles) {
            if (vertices.Count / 3 > MaxVertices || triangles.Count / 3 > MaxTriangles)
                throw new InvalidDataException("Models may contain at most 500,000 triangles and 1,500,000 vertices.");
        }
    }
}
