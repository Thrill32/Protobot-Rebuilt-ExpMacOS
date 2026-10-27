using UnityEngine;

namespace Protobot.Tools {
    /// <summary>Wrap a modal drag's visible cursor within the editing viewport.</summary>
    public static class TransformCursor {
        public static bool TryWrap(Vector2 position, Rect viewport, out Vector2 wrapped) {
            wrapped = position;
            if (viewport.width < 32f || viewport.height < 32f) return false;
            const float edge = 4f, inset = 12f;
            if (position.x <= viewport.xMin + edge) wrapped.x = viewport.xMax - inset;
            else if (position.x >= viewport.xMax - edge) wrapped.x = viewport.xMin + inset;
            if (position.y <= viewport.yMin + edge) wrapped.y = viewport.yMax - inset;
            else if (position.y >= viewport.yMax - edge) wrapped.y = viewport.yMin + inset;
            return wrapped != position;
        }
    }
}
