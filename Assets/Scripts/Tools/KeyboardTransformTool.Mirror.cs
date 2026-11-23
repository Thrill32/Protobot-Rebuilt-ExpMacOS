using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.StateSystems;
using Protobot.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Protobot.Tools {
    public sealed partial class KeyboardTransformTool {
        private const string MirrorPrompt = "Mirror across X, Y, or Z · Esc to cancel.";
        [SerializeField] private InputEvent mirrorInput;
        private ISelection mirrorSelection;
        private GameObject[] mirrorBodies;
        private bool mirrorDuplicate;
        private bool mirrorReserving;
        private KeyControl mirrorAxis;

        private bool HoldsMirrorAxis(InputEvent input) {
            if (mirrorAxis == null) return false;
            if (!mirrorAxis.isPressed || mirrorAxis.wasReleasedThisFrame) { mirrorAxis = null; return false; }
            return input.UsesControl(mirrorAxis);
        }

        private void BeginMirror() {
            if (!isActiveAndEnabled || IsCapturingInput || placement.placing || IsBorrowingMove ||
                NativeFileDialogs.IsOpen || ConnectTool.IsCapturingPoints || CustomModelTool.IsCapturingInput ||
                Simulation.SimulationTool.HasPreview || IsEditingText() || !hasFocus ||
                InputEvent.IsNoclipMovementCaptured || CollaborationSession.HasReservation) return;
            var current = selectionManager.current;
            if (!HasMirrorPivot(current)) return;
            var selected = BuildTransfer.SelectedBodies(current).ToArray();
            if (selected.Length == 0) return;
            mirrorDuplicate = duplicator != null && duplicator.IsDuplicateHeld;
            mirrorBodies = selected;
            mirrorSelection = current;
        }

        private static bool HasMirrorPivot(ISelection selection) {
            if (selection?.gameObject == null || !selection.gameObject.activeInHierarchy || selection.gameObject.IsDeleted()) return false;
            return selection is not HoleSelection hole || hole.holeData?.part != null &&
                !hole.holeData.part.IsDeleted() && hole.holeData.source != null && hole.holeData.source.gameObject.activeInHierarchy;
        }

        private bool ValidMirrorSelection() => selectionManager.current == mirrorSelection && HasMirrorPivot(mirrorSelection) &&
            mirrorBodies.ToHashSet().SetEquals(BuildTransfer.SelectedBodies(mirrorSelection));

        private void UpdateMirror() {
            if (!hasFocus || Keyboard.current == null || RebindAction.Rebinding || IsEditingText() ||
                placement.placing || !ValidMirrorSelection()) {
                CancelMirror();
                return;
            }
            if (Keyboard.current.escapeKey.wasPressedThisFrame) {
                if (!EscapeDeselect.TryHandleEscape()) CancelMirror();
                return;
            }
            if (mirrorReserving || !TransformGesture.TryReadAxis(out var normal)) return;
            var keyboard = Keyboard.current;
            mirrorAxis = normal == Vector3.right ? keyboard.xKey : normal == Vector3.up ? keyboard.yKey : keyboard.zKey;
            if (mirrorSelection is HoleSelection hole) hole.holeFace.Refresh();
            var pivot = mirrorSelection.gameObject.transform.position;
            if (CollaborationSession.IsShared) {
                if (!CollaborationSession.CanEdit || CollaborationSession.HasReservation) { CancelMirror(); return; }
                mirrorReserving = true;
                var requested = mirrorBodies;
                CollaborationSession.ReservePose(requested, () => {
                    if (mirrorBodies != requested) return;
                    if (!hasFocus || !ValidMirrorSelection() || !CollaborationSession.HasPoseReservation(requested)) {
                        CancelMirror();
                        return;
                    }
                    var selectCopies = mirrorDuplicate ? selectionManager : null;
                    EndMirror();
                    CollaborationSession.ConfirmMirror(requested, pivot, normal, selectCopies);
                }, () => { if (mirrorBodies == requested) CancelMirror(); }, sendPreviews: false);
            }
            else {
                var selected = mirrorBodies;
                var copy = mirrorDuplicate;
                EndMirror();
                CommitLocalMirror(selected, pivot, normal, copy);
            }
        }

        private void OnApplicationFocus(bool focused) {
            hasFocus = focused;
            if (focused) return;
            CancelMirror();
            mirrorAxis = null;
        }

        internal static bool CancelPendingMirror() => instance != null && instance.CancelMirror();

        private bool CancelMirror() {
            if (mirrorSelection == null) return false;
            if (mirrorReserving) CollaborationSession.CancelPose();
            if (selectionManager != null && selectionManager.current == mirrorSelection && (!HasMirrorPivot(mirrorSelection) ||
                mirrorBodies.Any(body => body == null || !body.activeInHierarchy || body.IsDeleted())))
                selectionManager.ClearCurrent(false, true, true);
            EndMirror();
            return true;
        }

        private void EndMirror() {
            mirrorSelection = null;
            mirrorBodies = null;
            mirrorReserving = false;
            CaptureCurrentFrame();
        }

        private void CommitLocalMirror(GameObject[] sources, Vector3 pivot, Vector3 normal, bool copy) {
            var copies = new List<GameObject>();
            var routes = new List<GameObject>();
            var previous = selectionManager.current;
            var subjects = sources;
            ObjectElement[] before = null;
            ObjectElement[] after;
            ISelection result;
            try {
                if (copy) {
                    copies = duplicator.CreateCopies(sources, copyChains: false);
                    subjects = CollaborationScene.Bodies(copies).ToArray();
                }
                var detectors = HoleDetector.GetAffectedDetectors(subjects);
                before = subjects.Select(body => new ObjectElement(body) { existing = !copy }).ToArray();
                var poses = before.Select(beforePart => PartReflection.Across(new Pose(beforePart.position, beforePart.rotation), pivot, normal)).ToArray();
                for (int i = 0; i < subjects.Length; i++) {
                    var body = subjects[i].GetComponent<SavedObject>();
                    body.transform.DOKill();
                    body.SetMirrored(!body.Mirrored);
                    body.transform.SetPositionAndRotation(poses[i].position, poses[i].rotation);
                }
                if (copy) ObjectDuplicator.CopyRoutes(ObjectDuplicator.CopyRoots(sources.ToList()), copies, routes);
                HoleDetector.RefreshMirroredParts(subjects, detectors);
                result = copy ? selectionManager.SelectExact(subjects.ToList()) : null;
                RefreshMirrorPivot(selectionManager.current);
                after = subjects.Select(body => new ObjectElement(body)).ToArray();
            }
            catch (Exception error) {
                if (copy) {
                    RestoreMirrorSelection(selectionManager, previous);
                    ObjectDuplicator.DiscardCopies(copies);
                }
                else if (before != null) {
                    var detectors = HoleDetector.GetAffectedDetectors(subjects);
                    foreach (var part in before) part.LoadImmediately();
                    HoleDetector.RefreshMirroredParts(subjects, detectors);
                }
                InteractionFeedback.Notify("Could not mirror the selection. " + error.Message);
                return;
            }
            StateSystem.AddElement(new MirrorState(this, before, routes, previous, copy, false));
            StateSystem.AddState(new MirrorState(this, after, routes, result, copy, true));
            if (copy) FindFirstObjectByType<PartListOutput>()?.CalculatePartsList();
        }

        private static void RestoreMirrorSelection(SelectionManager manager, ISelection selection) {
            if (manager.current == selection) return;
            manager.ClearCurrent(false, true, true);
            if (HasMirrorPivot(selection)) {
                selection.selector = null;
                manager.SetCurrent(selection, true, bypassResponseSelectors: true);
            }
        }

        private static void RefreshMirrorPivot(ISelection current) {
            if (current is MultiSelection multi && multi.pivot != null && !multi.pivot.IsEmpty) {
                multi.pivot.Center();
                multi.placementHole?.Show();
            }
            else if (current is HoleSelection hole) hole.holeFace.Refresh();
        }

        private sealed class MirrorState : IElement, IObjectElements {
            private readonly KeyboardTransformTool tool;
            private readonly ObjectElement[] parts;
            private readonly GameObject[] routes;
            private readonly ISelection selection;
            private readonly bool copy, applied;
            public IEnumerable<ObjectElement> Objects => parts;

            internal MirrorState(KeyboardTransformTool tool, ObjectElement[] parts, List<GameObject> routes,
                ISelection selection, bool copy, bool applied) {
                this.tool = tool;
                this.parts = parts;
                this.routes = routes.ToArray();
                this.selection = selection;
                this.copy = copy;
                this.applied = applied;
            }

            public void Load() {
                var manager = tool.selectionManager;
                var bodies = parts.Select(part => part.obj).ToArray();
                var detectors = HoleDetector.GetAffectedDetectors(bodies);
                if (copy) manager.ClearCurrent(false, true, true);
                foreach (var part in parts) part.LoadImmediately();
                foreach (var route in routes) if (route != null) route.SetActive(applied);
                HoleDetector.RefreshMirroredParts(bodies, detectors);
                if (copy) RestoreMirrorSelection(manager, selection);
                RefreshMirrorPivot(manager.current);
            }
        }
    }
}
