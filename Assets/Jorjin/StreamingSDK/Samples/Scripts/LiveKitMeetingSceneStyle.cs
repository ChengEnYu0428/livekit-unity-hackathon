using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Styles the legacy connection controls used by the custom meeting scene.
/// It is installed only by JorjinStreamingSimpleSample; the vendor Demo 2
/// scene and LiveKitDemo2View are deliberately left untouched.
/// </summary>
internal static class LiveKitMeetingSceneStyle
{
    internal static void Apply(
        Canvas canvas,
        TMP_InputField[] inputs,
        Button[] buttons,
        TMP_Text statusText,
        TMP_Text logText)
    {
        if (canvas == null) return;

        Camera targetCamera = canvas.worldCamera != null
            ? canvas.worldCamera
            : Camera.main;
        if (targetCamera != null)
        {
            targetCamera.clearFlags = CameraClearFlags.SolidColor;
            targetCamera.backgroundColor = new Color32(7, 14, 27, 255);
        }

        EnsureBackdrop(canvas.transform);
        EnsureConnectionPanel(canvas.transform);

        if (inputs != null)
        {
            foreach (TMP_InputField input in inputs) StyleInput(input);
        }
        if (buttons != null)
        {
            foreach (Button button in buttons) StyleButton(button);
        }

        ArrangeConnectionControls(canvas, inputs, buttons);

        StyleOutput(statusText, 17f, LiveKitMeetingStyle.TextPrimary);
        StyleOutput(logText, 15f, LiveKitMeetingStyle.TextSecondary);
    }

    private static void ArrangeConnectionControls(
        Canvas canvas,
        TMP_InputField[] inputs,
        Button[] buttons)
    {
        TMP_InputField firstInput = FirstValid(inputs);
        if (firstInput == null || firstInput.transform.parent == null) return;

        Transform firstRow = firstInput.transform.parent;
        Transform controlsRoot = firstRow.parent;
        if (controlsRoot == null || controlsRoot == canvas.transform) return;

        RectTransform rootRect = controlsRoot as RectTransform;
        if (rootRect == null) return;

        rootRect.anchorMin = new Vector2(0.018f, 0.075f);
        rootRect.anchorMax = new Vector2(0.305f, 0.925f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.anchoredPosition = Vector2.zero;
        rootRect.sizeDelta = Vector2.zero;

        VerticalLayoutGroup vertical = controlsRoot.GetComponent<VerticalLayoutGroup>();
        if (vertical != null)
        {
            vertical.padding = new RectOffset(18, 18, 18, 18);
            vertical.spacing = 8f;
            vertical.childAlignment = TextAnchor.UpperCenter;
            vertical.childControlWidth = true;
            vertical.childControlHeight = true;
            vertical.childForceExpandWidth = true;
            vertical.childForceExpandHeight = false;
        }

        if (inputs != null)
        {
            foreach (TMP_InputField input in inputs)
            {
                ArrangeInputRow(input);
            }
        }

        Transform buttonRoot = FindCommonButtonParent(buttons);
        if (buttonRoot == null) return;

        LayoutElement buttonLayout = GetOrAddLayoutElement(buttonRoot.gameObject);
        buttonLayout.preferredHeight = 290f;
        buttonLayout.flexibleHeight = 0f;
        buttonLayout.flexibleWidth = 1f;

        GridLayoutGroup grid = buttonRoot.GetComponent<GridLayoutGroup>();
        if (grid != null)
        {
            grid.padding = new RectOffset(0, 0, 2, 2);
            grid.spacing = new Vector2(12f, 12f);
            grid.cellSize = new Vector2(250f, 84f);
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 2;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(rootRect);
        Canvas.ForceUpdateCanvases();
    }

    private static void ArrangeInputRow(TMP_InputField input)
    {
        if (input == null || input.transform.parent == null) return;

        RectTransform row = input.transform.parent as RectTransform;
        RectTransform inputRect = input.transform as RectTransform;
        if (row == null || inputRect == null) return;

        LayoutElement rowLayout = GetOrAddLayoutElement(row.gameObject);
        rowLayout.preferredHeight = 80f;
        rowLayout.flexibleHeight = 0f;
        rowLayout.flexibleWidth = 1f;

        inputRect.anchorMin = new Vector2(0.24f, 0.18f);
        inputRect.anchorMax = new Vector2(1f, 0.82f);
        inputRect.pivot = new Vector2(0.5f, 0.5f);
        inputRect.offsetMin = new Vector2(8f, 0f);
        inputRect.offsetMax = new Vector2(-2f, 0f);

        foreach (TMP_Text label in row.GetComponentsInChildren<TMP_Text>(true))
        {
            if (label == input.textComponent || label.transform.IsChildOf(input.transform))
            {
                continue;
            }

            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(0.24f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = new Vector2(-4f, 0f);
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableAutoSizing = true;
            label.fontSizeMin = 13f;
            label.fontSizeMax = 24f;
            label.textWrappingMode = TextWrappingModes.NoWrap;
        }
    }

    private static TMP_InputField FirstValid(TMP_InputField[] inputs)
    {
        if (inputs == null) return null;
        foreach (TMP_InputField input in inputs)
        {
            if (input != null) return input;
        }
        return null;
    }

    private static Transform FindCommonButtonParent(Button[] buttons)
    {
        if (buttons == null) return null;
        foreach (Button button in buttons)
        {
            if (button != null && button.transform.parent != null)
            {
                return button.transform.parent;
            }
        }
        return null;
    }

    private static LayoutElement GetOrAddLayoutElement(GameObject target)
    {
        LayoutElement layout = target.GetComponent<LayoutElement>();
        return layout != null ? layout : target.AddComponent<LayoutElement>();
    }

    private static void EnsureBackdrop(Transform canvasTransform)
    {
        if (canvasTransform.Find("Custom Meeting Backdrop") != null) return;

        GameObject backdrop = CreateImage(
            "Custom Meeting Backdrop",
            canvasTransform,
            new Color32(7, 14, 27, 255));
        Stretch((RectTransform)backdrop.transform);
        backdrop.transform.SetAsFirstSibling();

        GameObject glow = CreateImage(
            "Meeting Accent Glow",
            backdrop.transform,
            new Color32(18, 105, 135, 42));
        RectTransform glowRect = (RectTransform)glow.transform;
        glowRect.anchorMin = new Vector2(0.52f, 0.42f);
        glowRect.anchorMax = new Vector2(1.08f, 1.16f);
        glowRect.offsetMin = Vector2.zero;
        glowRect.offsetMax = Vector2.zero;
        LiveKitMeetingStyle.ApplyRounded(
            glow.GetComponent<Image>(),
            new Color32(18, 105, 135, 42),
            true);
    }

    private static void EnsureConnectionPanel(Transform canvasTransform)
    {
        if (canvasTransform.Find("Custom Connection Panel") != null) return;

        GameObject panel = CreateImage(
            "Custom Connection Panel",
            canvasTransform,
            new Color32(14, 25, 42, 245));
        RectTransform rect = (RectTransform)panel.transform;
        rect.anchorMin = new Vector2(0.012f, 0.05f);
        rect.anchorMax = new Vector2(0.315f, 0.95f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        LiveKitMeetingStyle.ApplyRounded(
            panel.GetComponent<Image>(),
            new Color32(14, 25, 42, 245));
        LiveKitMeetingStyle.AddSoftShadow(panel, 0.42f);
        LiveKitMeetingStyle.AddBorder(panel, 0.42f);
        panel.transform.SetSiblingIndex(Mathf.Min(1, canvasTransform.childCount - 1));

        GameObject accent = CreateImage(
            "Connection Panel Accent",
            panel.transform,
            LiveKitMeetingStyle.Accent);
        RectTransform accentRect = (RectTransform)accent.transform;
        accentRect.anchorMin = new Vector2(0f, 1f);
        accentRect.anchorMax = new Vector2(1f, 1f);
        accentRect.pivot = new Vector2(0.5f, 1f);
        accentRect.sizeDelta = new Vector2(0f, 4f);
        accentRect.anchoredPosition = new Vector2(0f, -1f);
        LiveKitMeetingStyle.ApplyRounded(
            accent.GetComponent<Image>(),
            LiveKitMeetingStyle.Accent,
            true);
    }

    private static void StyleInput(TMP_InputField input)
    {
        if (input == null) return;
        Image image = input.GetComponent<Image>();
        if (image != null)
        {
            LiveKitMeetingStyle.ApplyRounded(
                image,
                new Color32(27, 40, 60, 255));
            LiveKitMeetingStyle.AddBorder(input.gameObject, 0.46f);
        }

        if (input.textComponent != null)
        {
            input.textComponent.color = LiveKitMeetingStyle.TextPrimary;
            input.textComponent.fontStyle = FontStyles.Normal;
        }
        if (input.placeholder is TMP_Text placeholder)
        {
            placeholder.color = new Color32(132, 149, 174, 210);
            placeholder.fontStyle = FontStyles.Italic;
        }
        input.caretColor = LiveKitMeetingStyle.Accent;
        input.selectionColor = new Color32(35, 181, 211, 90);
    }

    private static void StyleButton(Button button)
    {
        if (button == null) return;
        Image image = button.GetComponent<Image>();
        if (image != null)
        {
            LiveKitMeetingStyle.ApplyRounded(
                image,
                new Color32(32, 50, 75, 255));
            LiveKitMeetingStyle.AddBorder(button.gameObject, 0.38f);
        }
        LiveKitMeetingStyle.ConfigureButton(button);

        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            label.color = LiveKitMeetingStyle.TextPrimary;
            label.fontStyle = FontStyles.Bold;
        }
    }

    private static void StyleOutput(TMP_Text text, float fontSize, Color color)
    {
        if (text == null) return;
        text.color = color;
        text.fontSize = fontSize;
        text.enableAutoSizing = true;
        text.fontSizeMin = 10f;
        text.fontSizeMax = fontSize;
    }

    private static GameObject CreateImage(
        string name,
        Transform parent,
        Color color)
    {
        var result = new GameObject(
            name,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        result.transform.SetParent(parent, false);
        Image image = result.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return result;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

}
