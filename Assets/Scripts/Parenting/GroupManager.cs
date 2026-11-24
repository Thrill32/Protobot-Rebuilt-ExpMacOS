using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace Protobot {
    public static class GroupManager {
        private static Dictionary<GameObject, HashSet<GameObject>> Graph = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => Graph.Clear();

        private static bool Live(GameObject obj) => obj != null && obj.activeInHierarchy && !obj.IsDeleted();

        /// <summary>
        /// Forms a connection between a connecting part and a list of given parts
        /// </summary>
        /// <param name="connectingPart"></param>
        /// <param name="objs"></param>
        public static void FormConnection(ConnectingPart connectingPart, List<GameObject> objs) {
            if (connectingPart == null) return;
            GameObject connectingObj = connectingPart.gameObject;
            var attached = Live(connectingObj) && connectingPart.isActiveAndEnabled && objs != null
                ? new HashSet<GameObject>(objs.Where(obj => Live(obj) && obj != connectingObj)) : new HashSet<GameObject>();
            if (Graph.TryGetValue(connectingObj, out var previous))
                foreach (var removed in previous.Except(attached).ToArray()) BreakConnection(connectingPart, removed);
            if (attached.Count == 0) { Remove(connectingObj); return; }
            Graph.TryAdd(connectingObj, new HashSet<GameObject>());
            foreach (GameObject obj in attached) {
                Graph[connectingObj].Add(obj);
                Graph.TryAdd(obj, new HashSet<GameObject>());
                Graph[obj].Add(connectingObj);
            }
        }
        
        /// <summary>
        /// Destroys the connection formed between these two objects
        /// </summary>
        public static void BreakConnection(ConnectingPart connectingPart, GameObject obj) {
            GameObject connectingObj = connectingPart.gameObject;

            RemoveEdge(connectingObj, obj);
            RemoveEdge(obj, connectingObj);
        }
        
        private static void RemoveEdge(GameObject from, GameObject to) {
            if (!Graph.TryGetValue(from, out var edges)) return;
            edges.Remove(to);
            if (edges.Count == 0) Graph.Remove(from);
        }

        /// <summary>
        /// Removes a given object and all of its connections from the group graph
        /// </summary>
        public static void Remove(GameObject obj) {
            if (ReferenceEquals(obj, null)) return;
            if (Graph.ContainsKey(obj)) {
                foreach (GameObject c in Graph[obj].ToArray()) RemoveEdge(c, obj);

                Graph.Remove(obj);
            }
        }

        public static bool TryGetGroup(this GameObject obj, out List<GameObject> objs) {
            objs = null;
            if (!Live(obj)) return false;
            var visited = new HashSet<GameObject>();
            var seats = new Dictionary<Shaft, (GameObject left, GameObject right)>();
            var pending = new Stack<GameObject>();
            pending.Push(obj);
            while (pending.Count > 0) {
                var current = pending.Pop();
                if (!Live(current) || !visited.Add(current)) continue;
                if (Graph.TryGetValue(current, out var neighbours))
                    foreach (var next in neighbours) if (Live(next)) pending.Push(next);

                foreach (var shaft in NearbyShafts(current)) {
                    if (!Live(shaft.gameObject) || !Graph.ContainsKey(shaft.gameObject)) continue;
                    if (!seats.TryGetValue(shaft, out var walls)) {
                        walls = shaft.GetSeatedWalls();
                        if (!Live(walls.left) || !Live(walls.right) || !ShareFixedFrame(walls.left, walls.right)) walls = default;
                        seats.Add(shaft, walls);
                    }
                    if (walls.left == null) continue;
                    if (current == shaft.gameObject) {
                        pending.Push(walls.left);
                        pending.Push(walls.right);
                    } else if (current == walls.left || current == walls.right) pending.Push(shaft.gameObject);
                }
            }
            if (visited.Count < 2) return false;
            objs = visited.ToList();
            return true;
        }

        private static IEnumerable<Shaft> NearbyShafts(GameObject body) {
            if (body.TryGetComponent(out Shaft shaft)) yield return shaft;
            foreach (var hole in body.GetComponentsInChildren<HoleCollider>()) {
                if (hole.holeType != HoleCollider.HoleType.Normal || hole.holeData?.part != body) continue;
                foreach (var detector in hole.detectors)
                    if (detector != null && detector.Owner is Shaft owner && owner != null) yield return owner;
            }
        }

        // Resolve seating against the live fastener graph. Shaft edges cannot establish a frame,
        // and a change in face contact need not produce a trigger enter or exit.
        private static bool ShareFixedFrame(GameObject left, GameObject right) {
            var visited = new HashSet<GameObject>();
            var pending = new Stack<GameObject>();
            pending.Push(left);
            while (pending.Count > 0) {
                var current = pending.Pop();
                if (!Live(current) || current.TryGetComponent<Shaft>(out _) || !visited.Add(current)) continue;
                if (current == right) return true;
                if (Graph.TryGetValue(current, out var neighbours))
                    foreach (var next in neighbours) pending.Push(next);
            }
            return false;
        }
    }
}
