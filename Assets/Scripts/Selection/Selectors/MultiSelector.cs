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
            if (IsObjectSelection(selection)) {
                lastSelectedObj = selection.gameObject;
                lastIndividual = selection.individual;
            }
        }

        private bool IsObjectSelection(ISelection sel) {
            return (sel as ObjectSelection != null);
        }

        public ISelection GetResponseSelection(ISelection incomingSelection) {

            if (IsObjectSelection(incomingSelection)) {
                if (mutliSelectInput.IsPressed && lastSelectedObj != null) {
                    incomingSelection.gameObject.GetConnectedObjects(true, false, !incomingSelection.individual).ForEach(x => objs.Add(x));
                    lastSelectedObj.GetConnectedObjects(true, false, !lastIndividual).ForEach(x => objs.Add(x));

                    return new MultiSelection(incomingSelection.selector, objs.ToList()) { individual = incomingSelection.individual || lastIndividual };
                }
            }

            return null;
        }

        public override void OnClear(ClearInfo info) {
            if (!mutliSelectInput.IsPressed) {
                lastSelectedObj = null;
                objs.Clear();
            }
        }
    }
}
