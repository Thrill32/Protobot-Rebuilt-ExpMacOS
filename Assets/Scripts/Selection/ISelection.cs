using UnityEngine;
using System;
using System.Collections.Generic;

namespace Protobot.SelectionSystem {
    public interface ISelection {
        GameObject gameObject { get; set; }
        Selector selector { get; set; }
        bool individual { get; set; }

        void Select();
        void Deselect();
    }

    public class ObjectSelection : ISelection {
        public bool individual { get; set; }
        public GameObject gameObject { get; set; }
        public Selector selector { get; set; }
        public void Select() { }
        public void Deselect() { }
    }

    public class MultiSelection : ISelection {
        public bool individual { get; set; }
        public GameObject gameObject { get; set; }
        public Selector selector { get; set; }

        public Pivot pivot;
        public SelectedHoleAnchor placementHole;

        public List<GameObject> objs = new List<GameObject>();

        public MultiSelection(Selector selector, List<GameObject> objs) {
            this.objs = objs;
            CreatePivot();
            this.selector = selector;
        }

        public void CreatePivot() {
            pivot = Pivot.Create("MultiPivot");
            gameObject = pivot.gameObject;
        }

        public void Select() {
            pivot.AddObjects(objs, !individual);
            pivot.Center();
            placementHole?.Show();
        }

        public void Deselect() {
            placementHole?.Hide();
            if (gameObject.activeInHierarchy)
                pivot.DetachObjects();
        }
    }

    public class HoleSelection : ISelection {
        public bool individual { get; set; }
        public GameObject gameObject { get; set; }
        public Selector selector { get; set; }

        public HoleFace holeFace;
        public HoleData holeData;
        public Vector3 faceDirection;
        private Vector3? localFaceDirection;
        public void Select() {
            if (holeData.part != null) {
                localFaceDirection ??= holeData.part.transform.InverseTransformDirection(faceDirection);
                faceDirection = holeData.part.transform.TransformDirection(localFaceDirection.Value);
            }
            holeFace.Set(holeData, faceDirection);
            holeFace.gameObject.SetActive(true);
        }

        public void Deselect() {
            holeFace.gameObject.SetActive(false);
        }
    }

    // Keep the chosen face relative to its body; the shared HoleFace marker can be reused by later clicks.
    public sealed class SelectedHoleAnchor {
        public HoleData Hole { get; }
        private readonly Vector3 localDirection;
        private readonly HoleFace marker;
        public Vector3 Direction => Hole.part.transform.TransformDirection(localDirection).normalized;
        public Pose Face => MoveTargeting.HoleFace(Hole, Direction);

        private SelectedHoleAnchor(HoleData hole, Vector3 direction, HoleFace marker) {
            Hole = hole;
            localDirection = hole.part.transform.InverseTransformDirection(direction);
            this.marker = marker;
        }

        public static SelectedHoleAnchor From(HoleSelection selection) {
            if (selection.holeData?.part == null) return null;
            selection.holeFace?.Refresh();
            var direction = selection.holeFace != null && selection.holeFace.hole == selection.holeData
                ? selection.holeFace.direction : selection.faceDirection;
            return new SelectedHoleAnchor(selection.holeData, direction, selection.holeFace);
        }

        public void Show() {
            if (marker == null || Hole.part == null) return;
            marker.Set(Hole, Direction); marker.gameObject.SetActive(true);
        }
        public void Hide() { if (marker != null && marker.hole == Hole) marker.gameObject.SetActive(false); }
    }
}
