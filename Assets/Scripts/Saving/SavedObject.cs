using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Protobot {
    public class SavedObject : MonoBehaviour {
        public string id;
        public string nameId => id.Split('-')[0];
        public string state;
        [NonSerialized] private string connectionId;
        public string ConnectionId {
            get => connectionId ??= Guid.NewGuid().ToString("N");
            set => connectionId = string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString("N") : value;
        }
    }
}
