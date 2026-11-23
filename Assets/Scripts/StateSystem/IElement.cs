namespace Protobot.StateSystems {
    public interface IElement {
        void Load();
    }
    internal interface IObjectElements {
        System.Collections.Generic.IEnumerable<ObjectElement> Objects { get; }
    }
}
