using System;
using UnityEngine;
using Protobot.InputEvents;
using Protobot.Tools;

namespace Protobot.SelectionSystem {
    public class ClickSelector : Selector {
        public override event Action<ISelection> setEvent;
        public override event Action clearEvent;
        
        [SerializeField] private MouseCast mouseCast = null;
        [SerializeField] private InputEvent input;
        [SerializeField] private ToolToggle moveTool;
        [SerializeField] private MoveToolManager moveToolManager;

        public void Awake() {
            input.performed += () => OnPerformInput();
        }

        private void OnPerformInput() {
            if (ConnectTool.IsCapturingPoints || Protobot.Simulation.SimulationTool.CapturesSelection)
                return;

            if (!MouseInput.overUI) {
                if (mouseCast.overObj) {
                    var selection = new ObjectSelection {
                        gameObject = mouseCast.gameObject,
                        individual = GroupingBypassInput.IsHeld,
                        selector = this
                    };

                    var selectionManager = GetComponent<SelectionManager>();
                    var previousSelection = selectionManager?.current;
                    setEvent?.Invoke(selection);

                    // Selection must finish first so Move uses the chosen face
                    // and the grouping scope captured by this click.
                    if (moveTool != null && moveTool.active && moveToolManager != null && selectionManager != null &&
                        selection.gameObject.TryGetComponent<HoleCollider>(out var clickedHole) &&
                        selectionManager.current is HoleSelection holeSelection &&
                        holeSelection != previousSelection && holeSelection.holeData == clickedHole.holeData)
                        moveToolManager.StartMoving();
                }
                else {
                    clearEvent?.Invoke();
                }
            }
        }
    }
}
