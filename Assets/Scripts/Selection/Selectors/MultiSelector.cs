using System;
using System.Collections.Generic;
using UnityEngine;
using Protobot.InputEvents;
using System.Linq;

namespace Protobot.SelectionSystem {
    public class MultiSelector : SelectionResponse, IResponseSelector {
        public override bool RespondOnlyToSelectors => false;

        private HashSet<GameObject> objs = new();
        [SerializeField] private string ignoredTags;
        [SerializeField] private InputEvent mutliSelectInput;

        public bool IsMultiSelectHeld => mutliSelectInput != null && mutliSelectInput.IsPressed;

        private GameObject lastSelectedObj;
        private bool lastIndividual;

        public override void OnSet(ISelection selection) {
            if (IsBodySelection(selection)) {
                lastSelectedObj = selection is HoleSelection hole ? hole.holeData.part : selection.gameObject;
                lastIndividual = selection.individual;
            }
        }

        private bool IsBodySelection(ISelection sel) {
            return sel is ObjectSelection || sel is HoleSelection || sel is MultiSelection;
        }

        public ISelection GetResponseSelection(ISelection incomingSelection) {

            if (incomingSelection is ObjectSelection || incomingSelection is HoleSelection) {
                if (mutliSelectInput.IsPressed && lastSelectedObj != null) {
                    objs.Clear();
                    var hole = incomingSelection as HoleSelection;
                    var body = hole != null ? hole.holeData.part : incomingSelection.gameObject;
                    AddBodies(body, !incomingSelection.individual);
                    AddBodies(lastSelectedObj, !lastIndividual);

                    return new MultiSelection(incomingSelection.selector, objs.ToList()) {
                        individual = incomingSelection.individual || lastIndividual,
                        placementHole = hole != null ? SelectedHoleAnchor.From(hole) : null
                    };
                }
            }

            return null;
        }

        private void AddBodies(GameObject body, bool includeConnections) {
            foreach (var connected in body.GetConnectedObjects(true, true, includeConnections))
                foreach (var saved in connected.GetComponentsInChildren<SavedObject>())
                    if (!saved.gameObject.IsDeleted()) objs.Add(saved.gameObject);
        }

        public override void OnClear(ClearInfo info) {
            if (!mutliSelectInput.IsPressed) {
                lastSelectedObj = null;
                objs.Clear();
            }
        }
    }
}
