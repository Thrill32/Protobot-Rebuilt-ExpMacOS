using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace Protobot {
    public class Shaft : ConnectingPart {
        public float ShaftLength => transform.localScale.z;
        public override bool CanFormConnection => holeDetector.GetOrderedHoles()
            .Where(holeDetector.IsTargetHole).Select(hole => hole.holeData.part).Distinct().Count() >= 2;

        public override HoleDetector CreateHoleDetector() {
            return HoleDetector.Create(transform, ShaftLength, HoleCollider.HoleType.Clamp);
        }

        public override List<GameObject> GetAttachedParts() {
            if (CanFormConnection) {
                var holes = holeDetector.GetOrderedHoles();
                float Position(HoleCollider hole) => Vector3.Dot(hole.transform.position - transform.position, transform.forward);
                var clamps = holes.Where(holeDetector.IsTargetHole).Select(Position).ToArray();
                float min = clamps.Min(), max = clamps.Max();
                return holes.Where(hole => Position(hole) >= min - .001f && Position(hole) <= max + .001f)
                    .Select(hole => hole.holeData.part).Distinct().ToList();
            }

            return null;
        }
    }
}
