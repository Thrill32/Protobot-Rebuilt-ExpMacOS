using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Protobot.StateSystems;
using Protobot.InputEvents;
using Protobot.Outlining;
using UnityEngine.InputSystem;

namespace Protobot {
    public class ObjectDuplicator : MonoBehaviour {
        [SerializeField] private MovementManager movementManager;
        [SerializeField] private InputEvent input;

        private List<GameObject> prevDuplicatedObjs = new List<GameObject>();
        private bool duplicatedOnMove;

        // Poll the binding itself so G/R works even when its InputEvent updates
        // earlier than the Duplicate modifier or modal capture is active.
        public bool IsDuplicateHeld => input != null && input.IsCurrentlyPressed &&
            !(Keyboard.current != null && Keyboard.current.tabKey.isPressed && input.GetCurrentKeybind() == "Alt");

        public bool IsDuplicateFeedbackHeld => IsDuplicateHeld && Protobot.Tools.KeyboardTransformTool.AllowsInput(input);

        public void Awake() {
            var feedback = GetComponent<Protobot.UI.InteractionFeedback>();
            if (feedback == null) feedback = gameObject.AddComponent<Protobot.UI.InteractionFeedback>();
            feedback.Initialize(this);
            movementManager.OnStartMoving += () => {
                if (IsDuplicateHeld) {
                    DuplicateObject();
                    
                    foreach (GameObject obj in prevDuplicatedObjs) {
                        ObjectElement objElement = new ObjectElement(obj);
                        objElement.existing = false;

                        StateSystem.AddElement(objElement);
                    }

                    duplicatedOnMove = true;
                }
            };

            StateSystem.OnChangeState += () => {
                if (duplicatedOnMove) {
                    ObjectElement.AddObjectElements(prevDuplicatedObjs);
                    duplicatedOnMove = false;
                }
            };
        }

        private void DuplicateObject() {
            if (movementManager.MovingObj != null) {
                var movingObjs = movementManager.GetMovingConnectedObjs();
                prevDuplicatedObjs = CreateCopies(movingObjs);
            }
        }

        /// <summary>Shared by axis dragging and keyboard transforms. Copy each
        /// part once, leaving selection pivots out of the cloned hierarchy.</summary>
        public List<GameObject> CreateCopies(IEnumerable<GameObject> objects) {
            var parts = objects.Where(obj => obj != null)
                .SelectMany(obj => obj.GetComponentsInChildren<SavedObject>())
                .Select(saved => saved.gameObject).Distinct().ToList();
            var sourceRoots = parts.Where(part => !parts.Any(other =>
                other != part && part.transform.IsChildOf(other.transform))).ToList();
            var copies = new List<GameObject>();
            foreach (var part in sourceRoots) {
                var copy = Instantiate(part, part.transform.position, part.transform.rotation);
                copy.DisableOutline();
                foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(material =>
                        material != null ? new Material(material) : null).ToArray();
                copies.Add(copy);
            }
            return copies;
        }

        public static void DiscardCopies(List<GameObject> copies) {
            foreach (var copy in copies.Where(copy => copy != null)) {
                copy.SetActive(false);
                foreach (var material in copy.GetComponentsInChildren<Renderer>(true)
                             .SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null).Distinct()) {
                    if (Application.isPlaying) Destroy(material);
                    else DestroyImmediate(material);
                }
                if (Application.isPlaying) Destroy(copy);
                else DestroyImmediate(copy);
            }
            copies.Clear();
        }
    }
}
