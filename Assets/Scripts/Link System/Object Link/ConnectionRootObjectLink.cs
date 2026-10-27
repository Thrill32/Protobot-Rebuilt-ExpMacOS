using UnityEngine;
using Protobot.SelectionSystem;

namespace Protobot {
    public class ConnectionRootObjectLink : ObjectLink {
        [SerializeField] private ObjectLink link;
        public GameObject originalObj => link.obj;
        public override bool individual => link.individual;

        public override GameObject obj {
            get {
                if (individual) return originalObj;
                Transform root = link.tform.root;

                if (root != null) {
                    return root.gameObject;
                }

                return originalObj;
            }
        }
    }
}
