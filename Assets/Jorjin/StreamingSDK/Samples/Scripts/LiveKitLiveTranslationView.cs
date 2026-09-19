using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Live Chinese ⇄ English captions. A "即時翻譯" switch under the AI button turns
/// them on for this participant only; the Agent then sends each spoken sentence
/// with its translation, shown in a subtitle box at the bottom of the call.
/// </summary>
public sealed class LiveKitLiveTranslationView : MonoBehaviour
{
    [Serializable]
    private sealed class Packet
    {
        public string type, speaker, source_language, text, translation, message;
        public bool enabled;
        public int sequence;
    }

    private sealed class Caption
    {
        public string Speaker, Text, Translation, Language;
    }

    private const int VisibleCaptions = 3;
    private const float ResendSeconds = 30f;
    private static readonly Color32 PanelColor = new(10, 18, 32, 225);
    private static readonly Color32 On = new(35, 181, 211, 255);
    private static readonly Color32 Off = new(31, 43, 64, 255);

    private Func<string, int> sendSwitch;   // "on" / "off" → 0 when sent
    private Func<bool> isConnected, isRecording;
    private readonly ConcurrentQueue<string> inbox = new();
    private readonly List<Caption> captions = new();
    private readonly HashSet<int> seen = new();
    private GameObject canvasObject, panel;
    private Button toggle;
    private Text toggleLabel, captionText, statusText;
    private Font font;
    private bool enabledByUser, wasConnected, inWorkspace;
    private Canvas canvas;

    public bool Enabled => enabledByUser;

    /// <summary>
    /// While the full-screen AI workspace is open, show the captions on top of it as
    /// subtitles over the main video; its own 即時翻譯 button replaces the pill here.
    /// </summary>
    public void SetWorkspaceMode(bool visible)
    {
        inWorkspace = visible;
        if (canvas == null) return;
        canvas.sortingOrder = visible ? 510 : 450;
        toggle.gameObject.SetActive(!visible);
    }
    private float lastSent = -999f;

    public void Configure(Func<string, int> sendSwitch, Func<bool> isConnected, Func<bool> isRecording)
    {
        this.sendSwitch = sendSwitch;
        this.isConnected = isConnected;
        this.isRecording = isRecording;
        if (canvasObject == null) Build();
    }

    /// <summary>Collaboration packets of type "translation" / "translation_state" from the Agent.</summary>
    public void Receive(string json)
    {
        if (json != null && json.Length <= 15000 && inbox.Count < 60) inbox.Enqueue(json);
    }

    public static bool IsTranslationPacket(string json) =>
        json != null && (json.Contains("\"type\": \"translation") || json.Contains("\"type\":\"translation"));

    private void Update()
    {
        if (canvasObject == null) return;
        bool online = isConnected != null && isConnected();
        if (online && !wasConnected) lastSent = -999f;         // new connection: announce again
        if (!online && wasConnected) { captions.Clear(); seen.Clear(); }
        wasConnected = online;

        // Re-send "on" now and then: the Agent may have restarted and forgotten us.
        if (enabledByUser && online && Time.realtimeSinceStartup - lastSent > ResendSeconds)
        {
            if (sendSwitch("on") == 0) lastSent = Time.realtimeSinceStartup;
        }

        while (inbox.TryDequeue(out string json))
        {
            Packet packet;
            try { packet = JsonUtility.FromJson<Packet>(json); }
            catch (Exception) { continue; }
            if (packet == null) continue;
            if (packet.type == "translation_state")
            {
                if (!string.IsNullOrEmpty(packet.message)) statusText.text = packet.message;
            }
            else if (packet.type == "translation" && enabledByUser && seen.Add(packet.sequence))
            {
                captions.Add(new Caption { Speaker = packet.speaker, Text = packet.text,
                    Translation = packet.translation, Language = packet.source_language });
                if (captions.Count > VisibleCaptions) captions.RemoveAt(0);
                if (seen.Count > 500) seen.Clear();
                Render();
            }
        }

        toggle.interactable = online;
        panel.SetActive(enabledByUser && online);
        if (enabledByUser && online && captions.Count == 0)
            statusText.text = isRecording != null && isRecording()
                ? "Waiting for speech..." : "Start recording or the transcript to see live translation";
        else if (captions.Count > 0)
            statusText.text = "";
        LayoutCaptions();
    }

    public void Toggle()
    {
        enabledByUser = !enabledByUser;
        if (sendSwitch(enabledByUser ? "on" : "off") == 0) lastSent = Time.realtimeSinceStartup;
        if (!enabledByUser) { captions.Clear(); seen.Clear(); Render(); }
        toggleLabel.text = enabledByUser ? "Live Translation: On" : "Live Translation: Off";
        LiveKitMeetingStyle.ApplyRounded(toggle.GetComponent<Image>(), enabledByUser ? On : Off, true);
    }

    private void Render()
    {
        var text = new StringBuilder();
        foreach (Caption c in captions)
        {
            if (text.Length > 0) text.Append('\n');
            string flow = c.Language == "en" ? "EN→ZH" : "ZH→EN";
            text.Append("<size=15><color=#9DAABE>").Append(Safe(c.Speaker)).Append("  ").Append(flow).Append("</color></size>\n");
            text.Append("<color=#D0D7E2>").Append(Safe(c.Text)).Append("</color>\n");
            text.Append("<size=24><color=#7FD8EC>")
                .Append(string.IsNullOrEmpty(c.Translation) ? "(Translation unavailable)" : Safe(c.Translation))
                .Append("</color></size>");
        }
        captionText.text = text.ToString();
    }

    // Captions are rich text; keep speech from being read as markup.
    private static string Safe(string value) => (value ?? "").Replace("<", "＜").Replace(">", "＞");

    private void Build()
    {
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft JhengHei", "Noto Sans CJK TC", "Noto Sans CJK SC", "Arial" }, 24);
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        canvasObject = new GameObject("Live Translation Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 450; // above the call, below the full-screen AI workspace (500)
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600, 900);
        scaler.matchWidthOrHeight = .5f;

        // Switch, right under the "AI 協作" button.
        var toggleObject = new GameObject("Live Translation Switch", typeof(RectTransform), typeof(Image), typeof(Button));
        toggleObject.transform.SetParent(canvasObject.transform, false);
        var toggleRect = (RectTransform)toggleObject.transform;
        toggleRect.anchorMin = toggleRect.anchorMax = Vector2.one;
        toggleRect.pivot = new Vector2(0, 1);
        toggleRect.anchoredPosition = new Vector2(-205, -124);
        toggleRect.sizeDelta = new Vector2(185, 40);
        LiveKitMeetingStyle.ApplyRounded(toggleObject.GetComponent<Image>(), Off, true);
        toggle = toggleObject.GetComponent<Button>();
        toggle.onClick.AddListener(Toggle);
        toggleLabel = Label(toggleObject.transform, "Live Translation: Off", 19, TextAnchor.MiddleCenter);
        Stretch(toggleLabel.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        // Subtitle box at the bottom of the call.
        panel = new GameObject("Live Captions", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
        panel.transform.SetParent(canvasObject.transform, false);
        var panelRect = (RectTransform)panel.transform;
        panelRect.anchorMin = new Vector2(.18f, 0);
        panelRect.anchorMax = new Vector2(.82f, 0);
        panelRect.pivot = new Vector2(.5f, 0);
        panelRect.anchoredPosition = new Vector2(0, 118);
        panelRect.sizeDelta = new Vector2(0, 230);
        var background = panel.GetComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(background, PanelColor);
        background.raycastTarget = false;
        Text title = Label(panel.transform, "Live Translation  ZH ⇄ EN", 16, TextAnchor.UpperLeft);
        title.color = new Color32(157, 170, 190, 255);
        Stretch(title.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(18, -32), new Vector2(-18, -8));
        statusText = Label(panel.transform, "", 16, TextAnchor.UpperRight);
        statusText.color = new Color32(157, 170, 190, 255);
        Stretch(statusText.rectTransform, new Vector2(.3f, 1), Vector2.one, new Vector2(0, -32), new Vector2(-18, -8));
        captionText = Label(panel.transform, "", 19, TextAnchor.LowerLeft);
        captionText.supportRichText = true;
        captionText.verticalOverflow = VerticalWrapMode.Overflow; // newest at the bottom; older lines clip at the top
        Stretch(captionText.rectTransform, Vector2.zero, Vector2.one, new Vector2(18, 12), new Vector2(-18, -36));
        panel.SetActive(false);
    }

    private void LayoutCaptions()
    {
        bool portrait = Screen.height > Screen.width;
        var rect = (RectTransform)panel.transform;
        float height = ((RectTransform)canvasObject.transform).rect.height;
        if (!inWorkspace)
        {
            // Call screen: centred above the reaction bar.
            rect.anchorMin = new Vector2(portrait ? .03f : .18f, 0);
            rect.anchorMax = new Vector2(portrait ? .97f : .82f, 0);
            rect.offsetMin = new Vector2(0, 118);
            rect.offsetMax = new Vector2(0, 118 + 230);
            return;
        }
        // AI workspace: bottom of the main video, matching LiveKitCollaborationPanel's columns.
        if (portrait)
        {
            float columnBottom = .52f * height + 10f;
            float videoBottom = columnBottom + .36f * ((height - 109f) - columnBottom) + 8f;
            rect.anchorMin = new Vector2(0, 0); rect.anchorMax = new Vector2(1, 0);
            rect.offsetMin = new Vector2(30, videoBottom);
            rect.offsetMax = new Vector2(-30, videoBottom + 210);
        }
        else
        {
            float videoBottom = 75f + .36f * (height - 174f) + 6f;
            rect.anchorMin = new Vector2(0, 0); rect.anchorMax = new Vector2(.60f, 0);
            rect.offsetMin = new Vector2(30, videoBottom);
            rect.offsetMax = new Vector2(-20, videoBottom + 200);
        }
    }

    private Text Label(Transform parent, string value, int size, TextAnchor anchor)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var text = go.GetComponent<Text>();
        text.font = font; text.fontSize = size; text.color = Color.white; text.alignment = anchor; text.text = value;
        text.supportRichText = false; text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private static void Stretch(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
    { rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = offsetMin; rect.offsetMax = offsetMax; }

    private void OnDestroy() { if (canvasObject != null) Destroy(canvasObject); }
}
