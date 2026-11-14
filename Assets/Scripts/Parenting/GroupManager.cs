using System;
using System.Linq;
using System.Collections;
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
            var pending = new Stack<GameObject>();
            pending.Push(obj);
            while (pending.Count > 0) {
                var current = pending.Pop();
                if (!Live(current) || !visited.Add(current)) continue;
                if (Graph.TryGetValue(current, out var neighbours))
                    foreach (var next in neighbours) if (Live(next)) pending.Push(next);
            }
            if (visited.Count < 2) return false;
            objs = visited.ToList();
            return true;
        }
    }
}
