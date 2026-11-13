using UnityEngine;
using DG.Tweening;

namespace Protobot {
    public class PivotCamera : MonoBehaviour {
        public static PivotCamera Main;
        [SerializeField] public new Camera camera;

        //Moving values
        public bool moving => DOTween.IsTweening(transform);

        private int lastOrbitFrame = -1;
        private int lastZoomFrame = -1;
        public bool orbiting => lastOrbitFrame == Time.frameCount;
        public bool zooming => lastZoomFrame == Time.frameCount;

        public Vector3 cameraPosition => camera.transform.localPosition;
        public Vector3 focusPosition => transform.position;
        public float focusDistance => cameraPosition.z;
        public Vector3 lookAngle => transform.eulerAngles;
        public Vector3 orbitRot;

        private Vector3 panPos = Vector3.zero;


        [CacheComponent] private ProjectionSwitcher projectionSwitcher;
        private IPivotCameraInput[] inputs;
        private float zoomDistance;
        public bool invertZoom = false;
        public float snapSensitivity;

        void Start() {
            orbitRot = transform.eulerAngles;

            var mainCams = GameObject.FindGameObjectsWithTag("MainCamera");

            foreach (GameObject cam in mainCams) {
                if (cam.GetComponent<PivotCamera>() != null) {
                    Main = cam.GetComponent<PivotCamera>();
                    break;
                }
            }

            if (Main == null) Debug.LogError("MAIN PIVOT CAMERA REFERENCE MISSING");

            SetupInputs();

            zoomDistance = -cameraPosition.z;
        }

        void Update() {
            if (projectionSwitcher != null && projectionSwitcher.isOrtho) {
                camera.orthographicSize = -cameraPosition.z / 2;
                OrbitSnap();
            }
        }

        public void SetupInputs() {
            inputs = GetComponents<IPivotCameraInput>();

            foreach (IPivotCameraInput input in inputs) {
                input.updateOrbit += OrbitControl;
                input.updatePan += PanControl;
                input.updateZoom += ZoomControl;
            }
        }

        public void SetTransform(Vector3 newPos, Vector3 newAngle, float newDistance) {
            transform.position = newPos;

            transform.eulerAngles = newAngle;
            orbitRot = transform.eulerAngles;

            Vector3 newCamPos = camera.transform.localPosition;
            newCamPos.z = newDistance;
            zoomDistance = -newDistance;

            camera.transform.localPosition = newCamPos;

            panPos = transform.position;
        }

        public void MoveFocusPosition(Vector3 newFocusPos) {
            panPos = newFocusPos;
            transform.DOMove(newFocusPos, 0.4f);
        }

        //ZOOM
        public void ZoomControl(float input) {
            zoomDistance = CalculateZoomDistance(zoomDistance, input, 1f, invertZoom);
            camera.transform.DOKill();
            Vector3 newPos = cameraPosition;
            newPos.z = -zoomDistance;
            camera.transform.localPosition = newPos;
            lastZoomFrame = Time.frameCount;
        }

        public static float CalculateZoomDistance(float distance, float scrollDelta, float sensitivity, bool inverted) {
            if (!float.IsFinite(distance) || !float.IsFinite(scrollDelta) || !float.IsFinite(sensitivity))
                return Mathf.Clamp(float.IsFinite(distance) ? distance : 5f, 0.5f, 1000f);
            // Input System wheel deltas use 120 units per detent. Trackpads
            // contribute fractional deltas, so exponential scaling gives both
            // devices the same signed, frame-rate-independent behavior.
            float detents = scrollDelta / 120f;
            float direction = inverted ? 1f : -1f;
            return Mathf.Clamp(distance * Mathf.Exp(direction * detents * sensitivity * 0.12f), 0.5f, 1000f);
        }
        
        public void SetInvertZoom(bool value) {
            invertZoom = value;
        }


        //ORBIT
        public void OrbitControl(Vector2 orbitValue) {
            if (DOTween.IsTweening(transform)) {
                transform.DOKill();
                orbitRot = transform.eulerAngles;
                panPos = transform.position;
            }
            orbitRot += new Vector3(orbitValue.x, orbitValue.y, 0);
            transform.rotation = Quaternion.Euler(orbitRot);
            lastOrbitFrame = Time.frameCount;
        }

        public void OrbitSnap() {
            Vector3 snapVector = lookAngle.Round(90);
            if (lastOrbitFrame != Time.frameCount && Vector3.Distance(snapVector, lookAngle) < snapSensitivity && !moving)
                transform.DOLocalRotate(snapVector, 0.25f).OnComplete(() => orbitRot = transform.eulerAngles);
        }

        //PAN
        public void PanControl(Vector2 panValue) {
            if (DOTween.IsTweening(transform)) {
                transform.DOKill();
                panPos = transform.position;
                orbitRot = transform.eulerAngles;
            }
            float zoomFactor = zoomDistance / 5; // makes panning adjust by zoom distance, closer zoom should be less pan distance covered
            panPos += ((transform.right * -panValue.x) + (transform.up * -panValue.y)) * zoomFactor;
            transform.position = panPos;
        }
    }
}
