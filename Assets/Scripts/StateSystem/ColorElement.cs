using System.Linq;
using UnityEngine;

namespace Protobot.StateSystems {
    /// <summary>Exact material colors without changing selection, existence or pose.</summary>
    public sealed class ColorElement : IElement {
        private readonly SavedObject part;
        private readonly bool hasPaint;
        private readonly (Material material, Color color)[] colors;

        public ColorElement(GameObject body) {
            part = body.GetComponent<SavedObject>();
            hasPaint = part.hasPaint;
            colors = PartColors.Materials(body).Select(m => (m, m.color)).ToArray();
        }

        public void Load() {
            if (part == null) return;
            part.hasPaint = hasPaint;
            foreach (var entry in colors)
                if (entry.material != null) entry.material.color = entry.color;
        }
    }
}
