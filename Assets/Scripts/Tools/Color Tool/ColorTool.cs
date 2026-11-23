using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;
using Protobot.SelectionSystem;

public class ColorTool : MonoBehaviour
{
    [SerializeField] public static Material Material;
    [SerializeField] public static List<Material> Materials = new();
    [SerializeField] List<Color> color;
    [SerializeField] GameObject custom;
    [SerializeField] Slider red,green,blue;
    [SerializeField] Image preview;
    private static Color colorToSet;
    public static bool UseDefaultColor { get; private set; }
    public static Color ColorToSet {
        get => colorToSet;
        set { colorToSet = value; UseDefaultColor = false; }
    }
    public static bool CustomColor;

    private void Awake()
    {
        var panel = custom.transform as RectTransform;
        ColorPickerPointerOwnership.Configure(panel);
        if (custom.TryGetComponent<Graphic>(out var background)) background.raycastTarget = true;
        // Include the palette/header/background as well as the nested RGB panel.
        var options = custom.transform.parent.gameObject;
        if (options.TryGetComponent<Graphic>(out var optionsBackground)) optionsBackground.raycastTarget = true;
        if (options.GetComponent<ColorPickerPointerOwnership>() == null)
            options.AddComponent<ColorPickerPointerOwnership>();
        foreach (var slider in custom.GetComponentsInChildren<Slider>(true))
            if (slider.GetComponent<ColorPickerPointerOwnership>() == null)
                slider.gameObject.AddComponent<ColorPickerPointerOwnership>();
    }

    public void EnableUI(GameObject ui)
    {
        ui.SetActive(!ui.gameObject.activeSelf);
        red.value = 242;
        green.value = 242;
        blue.value = 242;
        // Slider callbacks refresh their labels and temporary custom color.
        // Apply the selected palette mode after those callbacks finish.
        HandleInput(ui.GetComponentInChildren<Dropdown>(true)?.value ?? 0);
        UpdatePreview();
    }
    public void HandleInput(int index)
    {
        if (index < color.Count)
        {
            ColorToSet = color[index];
            UseDefaultColor = index == 0;
            custom.SetActive(false);
            CustomColor = false;
        }
        else
        {
            UpdateCustomColor();
            custom.SetActive(true);
            CustomColor = true;
        }
    }

    public void UpdateCustomColor()
    {
        ColorToSet = new Color(red.value / 255, green.value / 255, blue.value / 255, 1);
    }

    public void UpdatePreview()
    {
        preview.color = new Color(red.value/255,green.value/255,blue.value/255,1);
    }

    public void UpdateColorSliders()
    {
        red.value = Material.color.r * 255;
        green.value = Material.color.g * 255;
        blue.value = Material.color.b * 255;
    }
}
