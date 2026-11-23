using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Protobot.Outlining;
using EPOOutline;
using Protobot.Transformations;
using Protobot.SelectionSystem;

namespace Protobot {
    public class GameObjectPlacementData : PlacementData {
        public override Transform placementTransform { get; set; }

        private GameObject gameObject;
        public bool individual { get; }
        public SelectedHoleAnchor sourceHole { get; }

        public override string objectId {
            get {
                if (gameObject.TryGetComponent(out SavedObject savedObject))
                    return savedObject.id;
                else
                    return gameObject.tag;
            }
        }

        public GameObjectPlacementData(GameObject gameObject, Transform placementTransform, bool individual = false,
            SelectedHoleAnchor sourceHole = null) {
            this.gameObject = gameObject;
            this.placementTransform = placementTransform;
            this.individual = individual;
            this.sourceHole = sourceHole;
        }

        public override Mesh GetDisplayMesh() {
            var connectedObjs = gameObject.GetConnectedObjects(true, false, !individual);
            return MeshCombiner.CombineMeshes(connectedObjs, gameObject.transform.position, gameObject.transform.rotation);
        }

        public GameObject GetGameObject() => gameObject;
    }
}
