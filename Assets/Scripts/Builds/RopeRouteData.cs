using System;
using System.Collections.Generic;
using System.Linq;
using Protobot.Simulation;
using UnityEngine;

namespace Protobot.Builds {
    [Serializable]
    public struct RopeAttachment {
        public string part;
        public float x, y, z;
        public Vector3 LocalPoint => new(x, y, z);
        public Vector3 WorldPoint(SavedObject body) => body.transform.TransformPoint(body.GeometryLocal(LocalPoint));

        public static RopeAttachment At(SavedObject body, Vector3 worldPoint) {
            var point = body.GeometryLocal(body.transform.InverseTransformPoint(worldPoint));
            return new RopeAttachment { part = body.ConnectionId, x = point.x, y = point.y, z = point.z };
        }
    }

    [Serializable]
    public sealed class RopeRouteData {
        public const float KitLength = 48f;
        public const int MaxPulleys = Collaboration.DocumentModel.MaxRouteMembers - 2;
        public const string DrumId = Collaboration.RopeRecord.DrumId;
        public const string PulleyId = Collaboration.RopeRecord.PulleyId;
        public string id;
        public RopeAttachment start, end;
        public string[] pulleys;
        // Drum followed by ordered pulleys. 0 = outer, 1 = inner.
        public int[] wraps;
        public float availableLength = KitLength;

        // Signed sweeps use the drum's live axis. Reflection reverses their sense.
        internal int[] WorldWraps(SavedObject drum) => drum.Mirrored ? wraps.Select(wrap => 1 - wrap).ToArray() : wraps;

        public IEnumerable<string> MemberIds {
            get {
                yield return start.part;
                foreach (var pulley in pulleys) yield return pulley;
                yield return end.part;
            }
        }

        internal bool ValidMetadata() =>
            pulleys != null && pulleys.Length >= 1 && pulleys.Length <= MaxPulleys &&
            wraps != null && wraps.Length == pulleys.Length + 1 && wraps.All(wrap => wrap == 0 || wrap == 1) &&
            MemberIds.All(part => !string.IsNullOrEmpty(part)) && MemberIds.Distinct(StringComparer.Ordinal).Count() == pulleys.Length + 2 &&
            ChainRouteGeometry.Finite(start.LocalPoint) && ChainRouteGeometry.Finite(end.LocalPoint) &&
            ChainRouteGeometry.Finite(availableLength) && availableLength > 0;

        public RopeRouteData Remap(Func<string, string> map) {
            var copy = new RopeRouteData {
                start = start, end = end, pulleys = pulleys.Select(map).ToArray(), wraps = wraps.ToArray(), availableLength = availableLength
            };
            copy.start.part = map(start.part);
            copy.end.part = map(end.part);
            return copy;
        }

        internal RopeRouteData Snapshot() {
            var copy = Remap(part => part);
            copy.id = id;
            return copy;
        }

        internal bool SamePath(RopeRouteData other) => other != null && start.Equals(other.start) && end.Equals(other.end) &&
            availableLength.Equals(other.availableLength) && pulleys.SequenceEqual(other.pulleys) && wraps.SequenceEqual(other.wraps);
    }
}
