using System;
using System.Collections.Generic;
using System.Linq;
using EPOOutline;
using UnityEngine;

namespace Protobot {
    /// <summary>Temporarily replaces the solid moving geometry in either movement workflow.</summary>
    internal sealed class MovePreviewVisibility : IDisposable {
        private readonly Dictionary<Renderer, bool> renderers = new();
        private readonly Dictionary<Outlinable, bool> outlines = new();

        public MovePreviewVisibility(IEnumerable<GameObject> bodies, IEnumerable<MeshRenderer> geometry) {
            foreach (var renderer in geometry) {
                renderers.Add(renderer, renderer.enabled);
                renderer.enabled = false;
            }
            foreach (var outline in bodies.Where(body => body != null)
                .SelectMany(body => body.GetComponentsInChildren<Outlinable>()).Distinct()) {
                outlines.Add(outline, outline.enabled);
                outline.enabled = false;
            }
        }

        public void Dispose() {
            foreach (var entry in renderers)
                if (entry.Key != null) entry.Key.enabled = entry.Value;
            foreach (var entry in outlines)
                if (entry.Key != null) entry.Key.enabled = entry.Value;
            renderers.Clear();
            outlines.Clear();
        }
    }
}
