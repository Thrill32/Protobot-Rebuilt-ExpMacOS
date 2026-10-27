using UnityEngine;

namespace Protobot {
    public abstract class ObjectLink : MonoBehaviour {
        public abstract GameObject obj {get;}
        public virtual bool individual => false;
        public bool active => (obj != null && obj.activeInHierarchy);
        public Transform tform => obj.transform;
    }
}
