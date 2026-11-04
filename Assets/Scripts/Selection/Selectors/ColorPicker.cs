using Protobot.SelectionSystem;
using Protobot;
using System;
using UnityEngine;
using Protobot.InputEvents;
using UnityEngine.InputSystem;

public class ColorPicker : Selector {
    public override event Action<ISelection> setEvent;
    public override event Action clearEvent;

    [SerializeField] private MouseCast mouseCast;
    [SerializeField] private InputEvent input;
    [SerializeField] private ColorTool colorToolGameObject;
    public string colorPickerKeybind;

    public void Awake() => input.performed += OnPerformInput;

    private void OnPerformInput() {
        if (Keyboard.current?.ctrlKey.isPressed == true && colorPickerKeybind?.Contains("Ctrl") != true) return;
        if (!ColorToolActiveCheck.colorToolActive || !ColorTool.CustomColor || MouseInput.overUI || !mouseCast.overObj) return;
        var material = PartColors.SampleMaterial(mouseCast.gameObject);
        if (material == null) return;
        ColorTool.Material = material;
        colorToolGameObject.UpdateColorSliders();
    }
}
