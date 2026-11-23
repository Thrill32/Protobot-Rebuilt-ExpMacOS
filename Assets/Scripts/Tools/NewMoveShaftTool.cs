using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using System.Linq;

namespace Protobot.Tools {
    public class NewMoveShaftTool : MonoBehaviour {
        [SerializeField] private ObjectLink shaftObjLink;
        private Vector3 shaftObjForward => shaftObjLink.tform.forward;

        private GameObject curShaft;

        [SerializeField] private ObjectLink targetObjLink;
        [SerializeField] private HoleFace targetHoleFace;

        [SerializeField] private Transform buttonCanvas;

        [SerializeField] private Transform positiveArrow;
        [SerializeField] private Transform negativeArrow;

        [SerializeField] private Transform positiveBracket;
        [SerializeField] private Transform negativeBracket;

        [SerializeField] private Pivot moveShaftPivot;
        
        public void OnSetShaftObj() {
            float shaftLength = GetShaftLength();

            buttonCanvas.forward = shaftObjForward;

            positiveArrow.transform.localPosition = Vector3.up * shaftLength;
            negativeArrow.transform.localPosition = Vector3.down * shaftLength;

            ResetPivot();
            moveShaftPivot.transform.position = shaftObjLink.tform.position;
            moveShaftPivot.transform.forward = shaftObjForward;
            moveShaftPivot.AddObject(shaftObjLink.obj);
        }

        public void SetCurShaft(GameObject newShaft) {
            curShaft = newShaft;
        }

        public float GetShaftLength() => shaftObjLink.obj.GetComponent<MeshFilter>().mesh.bounds.extents.z;
        public Vector3 GetShaftExtents() => shaftObjLink.obj.GetComponent<MeshCollider>().bounds.extents;

        //<Summary> Returns the next object on the shaft in a certain direction from a starting position </Summary>
        public GameObject GetNextObjOnShaft(Vector3 startPos, Vector3 dir) {
            var holeColliders = Physics.RaycastAll(startPos, dir)
                                .Where(x => x.collider.tag == "HoleCollider")
                                .OrderBy(o => Vector3.Distance(o.point, startPos))
                                .Select(x => x.collider.gameObject)
                                .ToList();

            foreach (GameObject holeCollider in holeColliders) {
                GameObject obj = holeCollider.transform.parent.gameObject;

                return obj;
            }

            print("No next object on shaft found or next object was already in pivot!");
            return null;
        }

        public void SetPivotObjects(Vector3 startPos, Vector3 endPos) {
            ResetPivot();
            moveShaftPivot.transform.position = shaftObjLink.tform.position;
            moveShaftPivot.transform.forward = shaftObjForward;

            Vector3 center = (endPos + startPos) / 2;

            Vector3 shaftExtents = GetShaftExtents();

            Vector3 forwardExtents = Vector3.Project(shaftExtents, shaftObjForward);

            Vector3 extents = (shaftExtents - forwardExtents) + (Vector3.Distance(endPos, startPos) * shaftObjForward) / 2;

            var objectsWithin = Physics.OverlapBox(center, extents)
                                .Where(x => x.CompareTag("HoleCollider"))
                                .Select(x => x.transform.parent.gameObject)
                                .ToList();

            moveShaftPivot.AddObjects(objectsWithin);
            moveShaftPivot.AddObject(shaftObjLink.obj);

            AddPivotOutline();
        }

        private void ClearPivotOutline() {
            moveShaftPivot.objects.
                Where(x => x != shaftObjLink.obj).
                ToList();
        }

        private void AddPivotOutline() {
            moveShaftPivot.objects.
                Where(x => x != shaftObjLink.obj).
                ToList();
        }

        public void ResetPivot() {
            ClearPivotOutline();
            moveShaftPivot.SetEmpty();
        }

        public void AddNextUp() {
            var nextObjPos = GetNextObjOnShaft(positiveBracket.position, shaftObjForward).transform.position;
            positiveBracket.DOMove(nextObjPos, 0.25f);

            SetPivotObjects(nextObjPos, negativeBracket.position);
        }

        public void RemoveNextDown() {
            var nextObjPos = GetNextObjOnShaft(positiveBracket.position, -shaftObjForward).transform.position;
            positiveBracket.DOMove(nextObjPos, 0.25f);

            SetPivotObjects(nextObjPos, negativeBracket.position);
        }

        public void AddNextDown() {
            var nextObjPos = GetNextObjOnShaft(negativeBracket.position, -shaftObjForward).transform.position;
            negativeBracket.DOMove(nextObjPos, 0.25f);

            SetPivotObjects(nextObjPos, positiveBracket.position);
        }

        public void RemoveNextUp() {
            var nextObjPos = GetNextObjOnShaft(negativeBracket.position, shaftObjForward).transform.position;
            negativeBracket.DOMove(nextObjPos, 0.25f);

            SetPivotObjects(nextObjPos, positiveBracket.position);
        }


        public void SetShaftPosition(bool reverseDir) {
            if (CollaborationSession.IsShared) { SetSharedShaftPosition(reverseDir); return; }
            SavedObject savedObj = targetObjLink?.obj?.GetComponent<SavedObject>();

            if (savedObj != null && savedObj.nameId == "MOTR") {
                Transform motrTransform = targetObjLink.tform;

                curShaft.transform.forward = motrTransform.forward;

                float shaftLength = GetShaftLength();
                
                Vector3 newPos = motrTransform.position + motrTransform.right * -0.5f + (motrTransform.forward * (shaftLength + 0.85f));

                curShaft.transform.DOMove(newPos, 0.25f);
            }
            else if (targetHoleFace.gameObject.activeInHierarchy) {
                curShaft.transform.DOMove(targetHoleFace.position, 0.25f);
                curShaft.transform.forward = targetHoleFace.direction;
            }

            if (reverseDir)
                curShaft.transform.forward = -curShaft.transform.forward;
        }
        private void SetSharedShaftPosition(bool reverseDir) {
            if (!CollaborationSession.CanEdit || CollaborationSession.HasActivePreview || curShaft == null) return;
            var position = curShaft.transform.position;
            var rotation = curShaft.transform.rotation;
            var target = targetObjLink?.obj?.GetComponent<SavedObject>();
            if (target != null && target.nameId == "MOTR") {
                position = target.transform.position + target.transform.right * -.5f + target.transform.forward * (GetShaftLength() + .85f);
                rotation = Quaternion.LookRotation(target.transform.forward);
            }
            else if (targetHoleFace.gameObject.activeInHierarchy && targetHoleFace.hole?.part != null) {
                position = targetHoleFace.position;
                rotation = Quaternion.LookRotation(targetHoleFace.direction);
                target = targetHoleFace.hole.part.GetComponent<SavedObject>();
            }
            else return;
            if (reverseDir) rotation = Quaternion.LookRotation(-(rotation * Vector3.forward));
            var bodies = Protobot.Builds.BuildTransfer.SelectedBodies(new Protobot.SelectionSystem.ObjectSelection { gameObject = curShaft, individual = true });
            if (bodies.Count == 0) return;
            var origin = curShaft.transform.position;
            var delta = rotation * Quaternion.Inverse(curShaft.transform.rotation);
            var writes = bodies.Select(body => {
                var part = CollaborationScene.Capture(body);
                var point = position + delta * (body.transform.position - origin);
                var orientation = delta * body.transform.rotation;
                part.position = new double[] { point.x, point.y, point.z };
                part.rotation = new double[] { orientation.x, orientation.y, orientation.z, orientation.w }; return part;
            }).ToArray();
            var dependencies = CollaborationSession.Versions(writes.Select(p => p.id).Concat(new[] { target.ConnectionId }));
            _ = CollaborationSession.CommitResolvedPosesAsync(writes, dependencies, CollaborationSession.CommittedRevision);
        }
    }
}
