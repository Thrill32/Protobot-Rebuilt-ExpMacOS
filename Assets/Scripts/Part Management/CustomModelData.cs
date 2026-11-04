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
            SameRotation(other) &&
            diameter == other.diameter && depth == other.depth && threaded == other.threaded;
        // Unity normalizes rotations when assigning a loaded local transform.
        // Ignore that floating-point noise (and the equivalent negated quaternion)
        // so loading a saved point does not make an unchanged build look dirty.
        private bool SameRotation(CustomMountData other) =>
            Math.Abs(qx - other.qx) < .000001f && Math.Abs(qy - other.qy) < .000001f && Math.Abs(qz - other.qz) < .000001f && Math.Abs(qw - other.qw) < .000001f ||
            Math.Abs(qx + other.qx) < .000001f && Math.Abs(qy + other.qy) < .000001f && Math.Abs(qz + other.qz) < .000001f && Math.Abs(qw + other.qw) < .000001f;
        public override bool Equals(object obj) => Equals(obj as CustomMountData);
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode();
    }
}
