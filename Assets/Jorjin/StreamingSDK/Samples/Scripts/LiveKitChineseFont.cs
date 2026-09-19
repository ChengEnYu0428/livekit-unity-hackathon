using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// The bundled LiberationSans SDF font has no Chinese glyphs. This loads a
/// Chinese font installed on the device and adds it as a global TextMeshPro
/// fallback, so Chinese labels render instead of empty boxes.
/// </summary>
internal static class LiveKitChineseFont
{
    private static readonly string[] Families =
    {
        "Microsoft JhengHei", "Noto Sans CJK TC", "Noto Sans TC",
        "PingFang TC", "Noto Sans CJK JP", "Noto Sans CJK SC"
    };

    private static TMP_FontAsset fallback;

    internal static void EnsureFallback()
    {
        if (fallback != null) return;
        foreach (string family in Families)
        {
            try { fallback = TMP_FontAsset.CreateFontAsset(family, "Regular"); }
            catch (System.Exception) { fallback = null; }
            if (fallback != null) break;
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        // Android usually ships one CJK collection; face 3 is Traditional Chinese.
        if (fallback == null) fallback = FromFile("/system/fonts/NotoSansCJK-Regular.ttc", 3);
        if (fallback == null) fallback = FromFile("/system/fonts/NotoSansTC-Regular.otf", 0);
        if (fallback == null) fallback = FromFile("/system/fonts/DroidSansFallback.ttf", 0);
#endif
        if (fallback == null)
        {
            Debug.LogWarning("No Chinese system font was found; Chinese labels may show as boxes.");
            return;
        }
        fallback.name = "Chinese System Font (runtime)";
        var list = TMP_Settings.fallbackFontAssets;
        if (list == null) return;
        list.Add(fallback);
        // TMP Settings is an asset; do not leave a runtime-only font in it after Play mode.
        Application.quitting += () => list.Remove(fallback);
    }

    private static TMP_FontAsset FromFile(string path, int faceIndex)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return TMP_FontAsset.CreateFontAsset(path, faceIndex, 90, 9,
                GlyphRenderMode.SDFAA, 1024, 1024);
        }
        catch (System.Exception) { return null; }
    }
}
