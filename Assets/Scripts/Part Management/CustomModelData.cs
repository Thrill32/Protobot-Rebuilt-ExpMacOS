using System;
using System.Linq;

namespace Protobot {
    // Primitive arrays keep embedded geometry compatible with both build serializers.
    [Serializable]
    public sealed class CustomModelData {
        public string name;
        public float[] vertices;
        public int[] triangles;
        public CustomMountData[] mounts = Array.Empty<CustomMountData>();

        public bool SameAs(CustomModelData other) => other != null && name == other.name &&
            (ReferenceEquals(vertices, other.vertices) || vertices.SequenceEqual(other.vertices)) &&
            (ReferenceEquals(triangles, other.triangles) || triangles.SequenceEqual(other.triangles)) &&
            (mounts ?? Array.Empty<CustomMountData>()).SequenceEqual(other.mounts ?? Array.Empty<CustomMountData>());
    }

    [Serializable]
    public sealed class CustomMountData : IEquatable<CustomMountData> {
        public float x, y, z;
        public float qx, qy, qz, qw;
        public float diameter = .169f, depth = .1f;
        public bool threaded = true;

        public bool Equals(CustomMountData other) => other != null && x == other.x && y == other.y && z == other.z &&
            qx == other.qx && qy == other.qy && qz == other.qz && qw == other.qw &&
            diameter == other.diameter && depth == other.depth && threaded == other.threaded;
        public override bool Equals(object obj) => Equals(obj as CustomMountData);
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode();
    }
}
