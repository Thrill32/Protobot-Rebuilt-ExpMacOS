using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Linq;

namespace Protobot.Builds {
    [Serializable]
    public class BuildData {
        public string name;
        public string fileName; //the name used to determine path

        public ObjectData[] parts = Array.Empty<ObjectData>();
        public CameraData camera;
        [System.Runtime.Serialization.OptionalField] public ChainData[] chains;
        [System.Runtime.Serialization.OptionalField] public PlacementAnchorData placementAnchor;

        public string lastWriteTime;

        public string version = AppData.Version;

        private static readonly HashSet<string> VersionsWithoutColor = new() {
            "1.0", "1.1", "1.1.1", "Beta 1.2", "Beta 1.3", "Beta 1.3.1", "1.3.2", "1.3.3", "1.3.4"
        };
        public bool HasColors => version != null && !VersionsWithoutColor.Contains(version);

        public bool CompareData(BuildData data) {
            if (data.parts.Length != parts.Length) {
                return false;
            }

            bool compareColors = HasColors && data.HasColors;
            foreach (ObjectData part in parts) {
                if (!data.parts.Any(candidate => candidate.Equals(part) &&
                    (!compareColors || candidate.GetColor() == part.GetColor())))
                    return false;
            }

            return ChainKeys(chains).SequenceEqual(ChainKeys(data.chains));
        }

        private static IEnumerable<string> ChainKeys(ChainData[] values) => (values ?? Array.Empty<ChainData>())
            .Where(c => c?.members != null)
            .Select(c => string.Join("|", (c.members ?? Array.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal)))
            .OrderBy(key => key, StringComparer.Ordinal);
    }

    [Serializable]
    public class ChainData {
        public string[] members;
    }

    [Serializable]
    public sealed class PlacementAnchorData {
        public float x, y, z, qx, qy, qz, qw;
        public bool hole;
        public Pose GetPose() => new(new Vector3(x, y, z), new Quaternion(qx, qy, qz, qw));
        public static PlacementAnchorData From(Pose pose, bool hole) => new() {
            x = pose.position.x, y = pose.position.y, z = pose.position.z,
            qx = pose.rotation.x, qy = pose.rotation.y, qz = pose.rotation.z, qw = pose.rotation.w, hole = hole
        };
    }
}
