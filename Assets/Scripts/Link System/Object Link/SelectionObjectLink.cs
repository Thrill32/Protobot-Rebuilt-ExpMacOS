using UnityEngine;
using Protobot.SelectionSystem;

namespace Protobot {
    public class SelectionObjectLink : ObjectLink {
        public override GameObject obj => selectionManager.current?.gameObject;
        public override bool individual => selectionManager.current?.individual ?? false;
        [SerializeField] private SelectionManager selectionManager;
    }
}
