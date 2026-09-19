using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visual helpers used only by the custom meeting UI.  The vendor Demo 2 UI
/// intentionally does not reference this class, so its appearance stays
/// identical to the supplied sample.
/// </summary>
internal static class LiveKitMeetingStyle
{
    internal static readonly Color32 Background = new(10, 18, 32, 250);
    internal static readonly Color32 Surface = new(22, 32, 49, 245);
    internal static readonly Color32 SurfaceRaised = new(31, 43, 64, 255);
    internal static readonly Color32 SurfaceHover = new(48, 65, 91, 255);
    internal static readonly Color32 Border = new(61, 79, 105, 220);
    internal static readonly Color32 TextPrimary = new(241, 245, 249, 255);
    internal static readonly Color32 TextSecondary = new(157, 170, 190, 255);
    internal static readonly Color32 Accent = new(35, 181, 211, 255);
    internal static readonly Color32 Success = new(34, 197, 94, 255);
    internal static readonly Color32 Danger = new(239, 68, 68, 255);
    internal static readonly Color32 Warning = new(245, 158, 11, 255);

    private static Sprite roundedSprite;
    private static Sprite pillSprite;

    internal static void ApplyRounded(Image image, Color color, bool pill = false)
    {
        if (image == null) return;
        image.sprite = pill ? PillSprite : RoundedSprite;
        image.type = Image.Type.Sliced;
        image.color = color;
    }

    internal static void ConfigureButton(Button button)
    {
        if (button == null) return;
        button.transition = Selectable.Transition.ColorTint;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color32(226, 236, 248, 255);
        colors.pressedColor = new Color32(180, 198, 220, 255);
        colors.selectedColor = Color.white;
        colors.disabledColor = new Color32(115, 126, 145, 115);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.09f;
        button.colors = colors;
    }

    internal static void AddSoftShadow(GameObject target, float alpha = 0.35f)
    {
        if (target == null || target.GetComponent<Shadow>() != null) return;
        var shadow = target.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, alpha);
        shadow.effectDistance = new Vector2(0f, -4f);
        shadow.useGraphicAlpha = true;
    }

    internal static void AddBorder(GameObject target, float alpha = 0.75f)
    {
        if (target == null || target.GetComponent<Outline>() != null) return;
        var outline = target.AddComponent<Outline>();
        Color color = Border;
        color.a = alpha;
        outline.effectColor = color;
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = true;
    }

    private static Sprite RoundedSprite =>
        roundedSprite != null ? roundedSprite : roundedSprite = CreateRoundedSprite(18);

    private static Sprite PillSprite =>
        pillSprite != null ? pillSprite : pillSprite = CreateRoundedSprite(30);

    private static Sprite CreateRoundedSprite(int radius)
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = $"Meeting UI Rounded {radius}",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        var pixels = new Color32[size * size];
        float innerMin = radius - 0.5f;
        float innerMax = size - radius - 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nearestX = Mathf.Clamp(x, innerMin, innerMax);
                float nearestY = Mathf.Clamp(y, innerMin, innerMax);
                float distance = Vector2.Distance(
                    new Vector2(x, y),
                    new Vector2(nearestX, nearestY));
                byte alpha = (byte)Mathf.RoundToInt(
                    Mathf.Clamp01(radius + 0.5f - distance) * 255f);
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        var border = new Vector4(radius + 1f, radius + 1f, radius + 1f, radius + 1f);
        var sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            border);
        sprite.name = $"Meeting UI Rounded {radius}";
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
