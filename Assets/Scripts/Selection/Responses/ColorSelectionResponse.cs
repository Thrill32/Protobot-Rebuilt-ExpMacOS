using System.Collections.Generic;
using System.Linq;
using Protobot.Builds;
using Protobot.StateSystems;
using UnityEngine;

namespace Protobot.SelectionSystem {
    public class ColorSelectionResponse : SelectionResponse {
        // Undo/redo and other programmatic selection restoration must never paint.
        public override bool RespondOnlyToSelectors => true;

        public override void OnSet(ISelection selection) {
            if (selection?.gameObject == null) return;
            var body = PartColors.BodyOf(selection.gameObject);
            var scope = body != null ? new ObjectSelection { gameObject = body, individual = selection.individual } : selection;
            ChangeColors(BuildTransfer.SelectedBodies(scope));
        }

        public void ChangeColor(GameObject target) => ChangeColors(new[] { PartColors.BodyOf(target) });

        private static void ChangeColors(IEnumerable<GameObject> bodies) {
            var parts = bodies.Where(p => p != null).Distinct().ToArray();
            ColorTool.Material = parts.Select(PartColors.SampleMaterial).FirstOrDefault(m => m != null);
            if (!ColorToolActiveCheck.colorToolActive) return;
            var changed = parts.Where(p => ColorTool.UseDefaultColor
                ? p.GetComponent<SavedObject>().hasPaint || !PartColors.IsDefault(p)
                : PartColors.Materials(p).Any(m => !PartColors.SameRGB(m.color, ColorTool.ColorToSet))).ToArray();
            if (changed.Length == 0) return;
            if (CollaborationSession.IsShared) { CollaborationSession.Paint(changed, ColorTool.ColorToSet, ColorTool.UseDefaultColor); return; }
            var after = new List<IElement>();
            foreach (var part in changed) {
                OwnedPartMaterials.CaptureGenerated(part);
                StateSystem.AddElement(new ColorElement(part));
                if (ColorTool.UseDefaultColor) PartColors.RestoreDefaults(part);
                else PartColors.ApplyPaint(part, ColorTool.ColorToSet);
                after.Add(new ColorElement(part));
            }
            StateSystem.AddState(new State(after));
            ColorTool.Material = parts.Select(PartColors.SampleMaterial).FirstOrDefault(m => m != null);
        }

        public override void OnClear(ClearInfo info) { }
    }
}
