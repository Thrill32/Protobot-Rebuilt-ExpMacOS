using UnityEngine;

namespace Protobot.Simulation {
    /// <summary>Defines the circular rope path at the authored wrap plane of a part.</summary>
    public sealed class RopeGroove : MonoBehaviour {
        public const float KitRopeDiameter = .125f;

        [SerializeField] private Vector3 localCenter;
        [SerializeField] private Vector3 localAxis = Vector3.forward;
        [SerializeField] private float surfaceRadius;

        public Vector3 Center => transform.TransformPoint(localCenter);
        public Vector3 Axis => transform.TransformDirection(localAxis).normalized;
        public float SurfaceRadius => surfaceRadius;

        // The tangent path follows the rope's centerline, half a diameter off the bare surface.
        public float EffectiveRadius(float ropeDiameter = KitRopeDiameter) => surfaceRadius + ropeDiameter * .5f;
    }
}
