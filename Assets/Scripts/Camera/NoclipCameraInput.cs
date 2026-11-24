using DG.Tweening;
using Protobot.InputEvents;
using Protobot.Simulation;
using Protobot.Tools;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Protobot {
    [DefaultExecutionOrder(-1000)]
    [RequireComponent(typeof(PivotCamera))]
    public class NoclipCameraInput : MonoBehaviour {
        [SerializeField] private InputEvent activationInput;
        [SerializeField] private float movementSpeed = 15f;
        [SerializeField] private float mouseSensitivity = 0.1f;

        private PivotCamera pivotCamera;
        private bool active;
        private float yaw;
        private float pitch;
        private Vector3 cameraWorldPosition;
        private CursorLockMode previousLockMode;
        private bool previousCursorVisibility;

        private void Awake() {
            pivotCamera = GetComponent<PivotCamera>();

            if (activationInput == null) {
                var inputObject = GameObject.Find("Noclip Camera");
                if (inputObject != null)
                    activationInput = inputObject.GetComponent<InputEvent>();
            }
        }

        private void Update() {
            var shouldBeActive = activationInput != null &&
                                 activationInput.IsCurrentlyPressed &&
                                 CanCaptureInput();

            if (shouldBeActive && !active)
                BeginNoclip();
            else if (!shouldBeActive && active)
                EndNoclip();

            InputEvent.SetNoclipMovementCapture(active);

            if (active)
                UpdateNoclip();
        }

        private bool CanCaptureInput() {
            if (RebindAction.Rebinding || !Application.isFocused || CollaborationSession.BlocksUIInput ||
                (!active && MouseInput.BlocksCameraPointer) || !KeyboardTransformTool.AllowsNoclipCamera(activationInput) ||
                SimulationTool.BlocksCamera || CustomModelTool.BlocksCamera || KeyboardTransformTool.IsEditingText())
                return false;

            // A free-fly binding that includes the pick button needs motion
            // before locking the cursor so a stationary viewport click can pick.
            var mouse = Mouse.current;
            return active || !SimulationTool.HasPendingRoutePick || !activationInput.UsesControl(mouse?.leftButton) ||
                mouse.delta.ReadValue() != Vector2.zero || Keyboard.current != null && ReadMovement(Keyboard.current) != Vector3.zero;
        }

        private void BeginNoclip() {
            active = true;
            transform.DOKill();
            pivotCamera.camera.transform.DOKill();

            cameraWorldPosition = pivotCamera.camera.transform.position;
            var angles = pivotCamera.camera.transform.eulerAngles;
            yaw = angles.y;
            pitch = NormalizeAngle(angles.x);

            previousLockMode = Cursor.lockState;
            previousCursorVisibility = Cursor.visible;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void EndNoclip() {
            active = false;
            InputEvent.SetNoclipMovementCapture(false);
            Cursor.lockState = previousLockMode;
            Cursor.visible = previousCursorVisibility;
        }

        private void UpdateNoclip() {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (mouse == null || keyboard == null)
                return;

            var mouseDelta = mouse.delta.ReadValue();
            yaw += mouseDelta.x * mouseSensitivity;
            pitch = Mathf.Clamp(pitch - mouseDelta.y * mouseSensitivity, -89f, 89f);

            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            var movement = ReadMovement(keyboard);
            if (movement.sqrMagnitude > 1f)
                movement.Normalize();

            cameraWorldPosition += rotation * movement * movementSpeed * Time.unscaledDeltaTime;

            var focusDistance = Mathf.Max(0.5f, -pivotCamera.camera.transform.localPosition.z);
            var focusPosition = cameraWorldPosition + rotation * Vector3.forward * focusDistance;
            pivotCamera.SetTransform(focusPosition, rotation.eulerAngles, -focusDistance);
        }

        private static Vector3 ReadMovement(Keyboard keyboard) {
            var movement = Vector3.zero;
            if (keyboard.wKey.isPressed) movement += Vector3.forward;
            if (keyboard.sKey.isPressed) movement -= Vector3.forward;
            if (keyboard.dKey.isPressed) movement += Vector3.right;
            if (keyboard.aKey.isPressed) movement -= Vector3.right;
            if (keyboard.eKey.isPressed) movement += Vector3.up;
            if (keyboard.qKey.isPressed) movement -= Vector3.up;
            return movement;
        }

        private static float NormalizeAngle(float angle) {
            return angle > 180f ? angle - 360f : angle;
        }

        public void SetMovementSpeed(float value) => movementSpeed = value;
        public void SetMouseSensitivity(float value) => mouseSensitivity = value;
        public float MovementSpeed => movementSpeed;
        public float MouseSensitivity => mouseSensitivity;

        private void OnApplicationFocus(bool hasFocus) {
            if (!hasFocus && active)
                EndNoclip();
        }

        private void OnDisable() {
            if (active)
                EndNoclip();
            else
                InputEvent.SetNoclipMovementCapture(false);
        }
    }
}
