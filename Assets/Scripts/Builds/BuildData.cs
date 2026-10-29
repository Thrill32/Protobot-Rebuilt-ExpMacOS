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

        public string lastWriteTime;

        public string version = AppData.Version;

        public bool CompareData(BuildData data) {
            if (data.parts.Length != parts.Length) {
                return false;
            }

            foreach (ObjectData part in parts) {
                if (!data.parts.Contains(part))
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
}
