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
                if (IsDuplicateHeld && !CollaborationSession.IsShared) {
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
            movementManager.OnCancelMoving += () => {
                if (!duplicatedOnMove) return;
                duplicatedOnMove = false;
                DiscardCopies(prevDuplicatedObjs);
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
        public List<GameObject> CreateCopies(IEnumerable<GameObject> objects, Transform parent = null, bool preserveConnectionIds = false, bool copyChains = true) {
            var parts = objects.Where(obj => obj != null)
                .SelectMany(obj => obj.GetComponentsInChildren<SavedObject>(true))
                .Select(saved => saved.gameObject).Distinct().ToList();
            var sourceRoots = CopyRoots(parts);
            CustomModel.RequireAppearanceCapacity(parts.Sum(part => part.GetComponent<CustomModel>()?.CurrentAppearanceCost ?? 0));
            var copies = new List<GameObject>();
            try {
                foreach (var part in sourceRoots) {
                    var copy = Instantiate(part, part.transform.position, part.transform.rotation, parent);
                    copies.Add(copy);
                    var cloned = copy.GetComponentsInChildren<SavedObject>(true);
                    foreach (var saved in cloned) saved.InitializeMirrorMeshes();
                    if (preserveConnectionIds) {
                        var originals = part.GetComponentsInChildren<SavedObject>(true);
                        for (int i = 0; i < originals.Length; i++) cloned[i].ConnectionId = originals[i].ConnectionId;
                    }
                    if (parent != null && !parent.gameObject.activeInHierarchy && copy.TryGetComponent<CustomModel>(out var custom))
                        custom.InitializeTemplate();
                    copy.DisableOutline();
                    OwnedPartMaterials.CloneForCopy(copy);
                }
                if (copyChains && parent == null && !preserveConnectionIds && !CollaborationSession.IsShared)
                    CopyRoutes(sourceRoots, copies, new List<GameObject>());
                return copies;
            }
            catch {
                DiscardCopies(copies);
                throw;
            }
        }

        internal static List<GameObject> CopyRoots(List<GameObject> parts) => parts.Where(part => !parts.Any(other =>
            other != part && part.transform.IsChildOf(other.transform))).ToList();

        internal static void CopyRoutes(IReadOnlyList<GameObject> sources, IReadOnlyList<GameObject> copies, List<GameObject> routes) {
            var ids = new Dictionary<string, string>();
            for (int i = 0; i < sources.Count; i++) {
                var original = sources[i].GetComponentsInChildren<SavedObject>(true);
                var cloned = copies[i].GetComponentsInChildren<SavedObject>(true);
                for (int j = 0; j < original.Length; j++) ids[original[j].ConnectionId] = cloned[j].ConnectionId;
            }
            foreach (var chain in Simulation.ChainLoop.SaveAll().Where(c => c.members.All(ids.ContainsKey))) {
                var loop = Simulation.ChainLoop.FromData(chain.Remap(id => ids[id]));
                if (loop != null) routes.Add(loop.gameObject);
            }
            foreach (var rope in Simulation.RopeRoute.Capture(ids.Keys.ToHashSet()))
                routes.Add(Simulation.RopeRoute.FromData(rope.Remap(id => ids[id])).gameObject);
        }

        public static void DiscardCopies(List<GameObject> copies) {
            var copiedParts = copies.Where(c => c != null).SelectMany(c => c.GetComponentsInChildren<SavedObject>(true)).ToHashSet();
            var ids = copiedParts.Select(s => s.ConnectionId).ToHashSet();
            // Frozen templates and load-validation bodies deliberately retain
            // source IDs. They never own the source's logical chain.
            ids.ExceptWith(Object.FindObjectsByType<SavedObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(s => !copiedParts.Contains(s)).Select(s => s.ConnectionId));
            foreach (var chain in Object.FindObjectsByType<Simulation.ChainLoop>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(c => c.MemberIds != null && c.MemberIds.All(ids.Contains))) {
                chain.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(chain.gameObject); else DestroyImmediate(chain.gameObject);
            }
            foreach (var rope in Object.FindObjectsByType<Simulation.RopeRoute>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(route => route.MemberIds.All(ids.Contains))) rope.Discard();
            foreach (var copy in copies.Where(copy => copy != null)) {
                copy.SetActive(false);
                foreach (var saved in copy.GetComponentsInChildren<SavedObject>(true)) saved.ReleaseMirrorMeshes();
                if (copy.TryGetComponent<CustomModel>(out var custom)) custom.ReleaseGeometry();
                copy.GetComponent<OwnedPartMaterials>()?.Release();
                if (Application.isPlaying) Destroy(copy);
                else DestroyImmediate(copy);
            }
            copies.Clear();
        }
    }
}
