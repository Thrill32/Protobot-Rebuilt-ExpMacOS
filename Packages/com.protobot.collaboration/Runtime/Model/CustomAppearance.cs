using System;
using System.IO;
using System.Linq;

namespace Protobot.Collaboration {
    [Serializable] public sealed class CustomAppearance {
        public const int Version = 1;
        public const int MaxMaterials = 32;
        public const int MaxTextures = 8;
        public const int MaxDimension = 2048;
        public const int MaxRgbBytes = 16 * 1024 * 1024;

        public int version = Version;
        public DiffuseMaterial[] materials;
        public int[] faceMaterials;
        public float[] uv;
        public DiffuseTexture[] textures;

        public void Validate(int vertexCount, int triangleCount, bool pendingPixels = false) {
            DocumentModel.Require(version == Version, "appearance-version-unsupported");
            DocumentModel.Require(materials != null && materials.Length is >= 1 and <= MaxMaterials &&
                faceMaterials != null && faceMaterials.Length == triangleCount &&
                textures != null && textures.Length <= MaxTextures &&
                uv != null && uv.Length == (textures.Length == 0 ? 0 : vertexCount * 2), "invalid-appearance");
            DocumentModel.Require(faceMaterials.All(m => m >= 0 && m < materials.Length) &&
                uv.All(v => Math.Abs(v) <= 1000000), "invalid-appearance");
            var used = new bool[materials.Length];
            foreach (int slot in faceMaterials) used[slot] = true;
            DocumentModel.Require(used.All(value => value), "invalid-appearance");
            var referenced = new bool[textures.Length];
            foreach (var material in materials) {
                DocumentModel.Require(material != null && Unit(material.r) && Unit(material.g) && Unit(material.b) &&
                    material.texture >= -1 && material.texture < textures.Length, "invalid-appearance");
                if (material.texture >= 0) referenced[material.texture] = true;
            }
            DocumentModel.Require(referenced.All(value => value), "invalid-appearance");
            long total = 0;
            foreach (var texture in textures) {
                DocumentModel.Require(texture != null, "invalid-appearance-texture");
                total += texture.RgbBytes;
            }
            DocumentModel.Require(total <= MaxRgbBytes, "appearance-texture-budget");
            foreach (var texture in textures) {
                if (!pendingPixels) texture.ValidatePixels();
            }
        }

        private static bool Unit(float value) => value >= 0 && value <= 1;

        // Charges retained UTF-16 pixels, geometry metadata, native texture backing and material slots.
        public long ResourceBytes(int vertexCoordinates, int triangleIndices) =>
            (long)vertexCoordinates * 16 + (long)triangleIndices * 12 +
            (long)faceMaterials.Length * sizeof(int) + (long)uv.Length * sizeof(float) * 2 +
            materials.Length * 4096L + textures.Sum(texture => 2L * texture.EncodedLength + 4L * texture.width * texture.height);

        public void WriteHash(BinaryWriter writer) {
            writer.Write(version);
            writer.Write(materials.Length);
            foreach (var material in materials) {
                writer.Write(material.hasColor);
                WriteFloat(writer, material.r); WriteFloat(writer, material.g); WriteFloat(writer, material.b);
                writer.Write(material.texture);
            }
            writer.Write(faceMaterials.Length);
            foreach (int material in faceMaterials) writer.Write(material);
            writer.Write(uv.Length);
            foreach (float coordinate in uv) WriteFloat(writer, coordinate);
            writer.Write(textures.Length);
            foreach (var texture in textures) {
                writer.Write(texture.width); writer.Write(texture.height); writer.Write(texture.pixels);
            }
        }

        private static void WriteFloat(BinaryWriter writer, float value) => writer.Write(value == 0 ? 0f : value);

        public bool SameAs(CustomAppearance other) => ReferenceEquals(this, other) || other != null && version == other.version &&
            faceMaterials.SequenceEqual(other.faceMaterials) && uv.SequenceEqual(other.uv) &&
            materials.Length == other.materials.Length && materials.Zip(other.materials, (a, b) =>
                a.hasColor == b.hasColor && a.r == b.r && a.g == b.g && a.b == b.b && a.texture == b.texture).All(equal => equal) &&
            textures.Length == other.textures.Length && textures.Zip(other.textures, (a, b) =>
                a.width == b.width && a.height == b.height && a.pixels == b.pixels).All(equal => equal);

        internal CustomAppearance Copy() => new CustomAppearance {
            version = version, faceMaterials = faceMaterials.ToArray(), uv = uv.ToArray(),
            materials = materials.Select(m => new DiffuseMaterial { hasColor = m.hasColor, r = m.r, g = m.g, b = m.b, texture = m.texture }).ToArray(),
            textures = textures.Select(t => new DiffuseTexture { width = t.width, height = t.height, pixels = t.pixels }).ToArray()
        };
    }

    [Serializable] public sealed class DiffuseMaterial {
        public bool hasColor;
        public float r = 1, g = 1, b = 1;
        public int texture = -1;
    }

    [Serializable] public sealed class DiffuseTexture {
        public int width, height;
        // Canonical padded base64url keeps the encoded size predictable on both JSON runtimes.
        public string pixels;

        public int RgbBytes {
            get {
                DocumentModel.Require(width is >= 1 and <= CustomAppearance.MaxDimension &&
                    height is >= 1 and <= CustomAppearance.MaxDimension, "appearance-texture-dimensions");
                return width * height * 3;
            }
        }

        // RGB24 always has a whole number of three-byte base64 groups.
        public int EncodedLength => RgbBytes / 3 * 4;

        internal void ValidatePixels() {
            DocumentModel.Require(pixels != null && pixels.Length == EncodedLength &&
                pixels.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'), "invalid-appearance-texture");
        }

        public void SetPixels(byte[] rgb) {
            DocumentModel.Require(rgb != null && rgb.Length == RgbBytes, "invalid-appearance-texture");
            pixels = Convert.ToBase64String(rgb).Replace('+', '-').Replace('/', '_');
        }

        public byte[] GetPixels() => Convert.FromBase64String(pixels.Replace('-', '+').Replace('_', '/'));
    }
}
