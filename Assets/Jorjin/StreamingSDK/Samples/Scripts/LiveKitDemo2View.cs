using System;
using System.Collections.Generic;
using Jorjin.Streaming;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime-built version of the StreamingSDK Demo 2 screen.  Keeping this UI
/// in code lets the existing sample scene use the new layout without replacing
/// its serialized LiveKit, AR-camera, meeting-session, and transcript setup.
/// </summary>
public sealed class LiveKitDemo2View : MonoBehaviour
{
    public sealed class Callbacks
    {
        public Action Initialize;
        public Action Dispose;
        public Action Join;
        public Action Leave;
        public Action StartPreview;
        public Action StopPreview;
        public Action RenewToken;
        public Action ToggleAudioModule;
        public Action ToggleMicrophoneCapture;
        public Action ToggleLocalAudioPublish;
        public Action ToggleAllRemoteAudio;
        public Action ToggleVideoModule;
        public Action ToggleCameraCapture;
        public Action ToggleLocalVideoPublish;
        public Action ToggleAllRemoteVideo;
        public Action ApplyCameraDirection;
        public Action ApplyEncoderConfiguration;
        public Action ApplyRemoteStreamType;
        public Action ApplyAudioScenario;
        public Action RefreshDevices;
        public Action ApplyDevices;
        public Action ToggleMicrophone;
        public Action Like;
        public Action Clap;
        public Action ToggleTranscript;
    }

    private static readonly Color32 PageColor = new(6, 13, 20, 255);
    private static readonly Color32 PanelColor = new(16, 25, 40, 255);
    private static readonly Color32 InputColor = new(31, 46, 64, 255);
    private static readonly Color32 ButtonColor = new(34, 105, 166, 255);
    private static readonly Color32 CyanColor = new(31, 193, 218, 255);
    private static readonly Color32 TextColor = new(229, 233, 239, 255);
    private static readonly Color32 MutedColor = new(156, 169, 185, 255);

    public TMP_InputField AppIdInput { get; private set; }
    public TMP_InputField ChannelInput { get; private set; }
    public TMP_InputField TokenInput { get; private set; }
    public TMP_InputField UserIdInput { get; private set; }
    public TMP_InputField RenewTokenInput { get; private set; }
    public TMP_InputField BitrateInput { get; private set; }
    public TMP_Text StatusText { get; private set; }
    public TMP_Text StatsText { get; private set; }
    public TMP_Text LogText { get; private set; }
    public RectTransform VideoHost { get; private set; }
    public Toggle MaintainResolutionToggle { get; private set; }
    public LiveKitDemo2Selector CameraDirectionSelector { get; private set; }
    public LiveKitDemo2Selector ResolutionSelector { get; private set; }
    public LiveKitDemo2Selector FrameRateSelector { get; private set; }
    public LiveKitDemo2Selector RemoteStreamSelector { get; private set; }
    public LiveKitDemo2Selector AudioScenarioSelector { get; private set; }
    public LiveKitDemo2Selector RecordingDeviceSelector { get; private set; }
    public LiveKitDemo2Selector PlaybackDeviceSelector { get; private set; }
    public LiveKitDemo2Selector VideoDeviceSelector { get; private set; }

    private Callbacks callbacks;
    private Button microphoneButton;
    private TMP_Text microphoneLabel;
    private Button transcriptButton;
    private TMP_Text transcriptLabel;

    public static LiveKitDemo2View Create(
        Transform canvas,
        Callbacks callbacks,
        string appId,
        string channel,
        string token,
        string userId)
    {
        if (canvas == null) return null;
        GameObject root = NewUiObject(
            "LiveKit Demo 2 UI",
            canvas,
            typeof(CanvasRenderer),
            typeof(Image));
        RectTransform rect = (RectTransform)root.transform;
        Stretch(rect);
        root.GetComponent<Image>().color = PageColor;
        var view = root.AddComponent<LiveKitDemo2View>();
        view.Build(callbacks ?? new Callbacks(), appId, channel, token, userId);
        return view;
    }

    private void Build(
        Callbacks value,
        string appId,
        string channel,
        string token,
        string userId)
    {
        callbacks = value;
        var rootLayout = GetComponent<HorizontalLayoutGroup>() ??
            gameObject.AddComponent<HorizontalLayoutGroup>();
        rootLayout.padding = new RectOffset(8, 8, 8, 8);
        rootLayout.spacing = 10f;
        rootLayout.childAlignment = TextAnchor.UpperLeft;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = true;

        RectTransform left = CreateColumn("Session", 30f);
        RectTransform center = CreateColumn("Advanced Controls", 33f);
        RectTransform right = CreateColumn("Video Status Log", 34f);
        BuildLeftColumn(left, appId, channel, token, userId);
        BuildCenterColumn(center);
        BuildRightColumn(right);
    }

    /// <summary>
    /// Recreates the serialized scene preview with live callbacks and runtime
    /// references. Runtime UnityEvent listeners are intentionally not stored in
    /// the scene, so the sample calls this once from Awake.
    /// </summary>
    public void Rebuild(
        Callbacks value,
        string appId,
        string channel,
        string token,
        string userId)
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }
        Build(value ?? new Callbacks(), appId, channel, token, userId);
    }

    private RectTransform CreateColumn(string name, float widthWeight)
    {
        GameObject column = NewUiObject(name, transform, typeof(CanvasRenderer), typeof(Image));
        column.GetComponent<Image>().color = PanelColor;
        LayoutElement element = column.AddComponent<LayoutElement>();
        element.flexibleWidth = widthWeight;
        element.minWidth = 250f;
        var layout = column.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 10, 10);
        layout.spacing = 4f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return (RectTransform)column.transform;
    }

    private void BuildLeftColumn(
        Transform parent,
        string appId,
        string channel,
        string token,
        string userId)
    {
        CreateText(parent, "DEMO 2\nAdvanced Streaming Control", 18f, CyanColor,
            FontStyles.Bold, 44f);
        CreateText(parent, "Required connection credentials", 12f, TextColor,
            FontStyles.Bold, 24f);
        AppIdInput = CreateInputRow(parent, "App ID", "LiveKit / compatible App ID", appId);
        ChannelInput = CreateInputRow(parent, "Channel", "Channel name", channel);
        TokenInput = CreateInputRow(parent, "Token", "Required RTC token", token);
        UserIdInput = CreateInputRow(parent, "UID", "Participant identity / UID", userId);

        CreateSection(parent, "SESSION");
        CreateTwoButtonRow(parent, "Initialize", callbacks.Initialize,
            "Dispose", callbacks.Dispose);
        CreateTwoButtonRow(parent, "Join Channel", callbacks.Join,
            "Leave Channel", callbacks.Leave);
        CreateTwoButtonRow(parent, "Start Preview", callbacks.StartPreview,
            "Stop Preview", callbacks.StopPreview);

        CreateSection(parent, "TOKEN RENEWAL");
        RenewTokenInput = CreateInputRow(parent, "New Token",
            "Required replacement token", string.Empty);
        CreateButton(parent, "Renew Token", callbacks.RenewToken, 29f);

        CreateText(
            parent,
            "Demo 1 APIs are prerequisites only.\n" +
            "Demo 2 validates preview, media layers, devices, encoder quality, " +
            "token renewal, reconnect and stats.",
            11f,
            MutedColor,
            FontStyles.Italic,
            64f);
    }

    private void BuildCenterColumn(Transform parent)
    {
        CreateText(parent, "Advanced API Controls", 18f, CyanColor,
            FontStyles.Bold, 28f);

        CreateSection(parent, "AUDIO LAYERS");
        CreateTwoButtonRow(parent, "Audio Module On / Off", callbacks.ToggleAudioModule,
            "Mic Capture On / Off", callbacks.ToggleMicrophoneCapture);
        CreateTwoButtonRow(parent, "Local Audio Publish", callbacks.ToggleLocalAudioPublish,
            "All Remote Audio", callbacks.ToggleAllRemoteAudio);

        CreateSection(parent, "VIDEO LAYERS");
        CreateTwoButtonRow(parent, "Video Module On / Off", callbacks.ToggleVideoModule,
            "Camera Capture On / Off", callbacks.ToggleCameraCapture);
        CreateTwoButtonRow(parent, "Local Video Publish", callbacks.ToggleLocalVideoPublish,
            "All Remote Video", callbacks.ToggleAllRemoteVideo);

        CreateSection(parent, "CAMERA AND ENCODER");
        CameraDirectionSelector = CreateSelectorRow(parent, "Camera Direction",
            new[] { "Rear / AR", "Front" }, new[] { "rear", "front" }, 0);
        CreateButton(parent, "Apply Camera Direction", callbacks.ApplyCameraDirection, 27f);
        ResolutionSelector = CreateSelectorRow(parent, "Resolution",
            new[] { "1280 x 720", "960 x 540", "640 x 360", "320 x 180" },
            new[] { "1280x720", "960x540", "640x360", "320x180" }, 1);
        FrameRateSelector = CreateSelectorRow(parent, "Frame Rate",
            new[] { "30 FPS", "24 FPS", "15 FPS" },
            new[] { "30", "24", "15" }, 2);
        BitrateInput = CreateInputRow(parent, "Bitrate", "-1", "-1");
        MaintainResolutionToggle = CreateToggleRow(parent, "Maintain Resolution", true);
        CreateButton(parent, "Apply Encoder Configuration",
            callbacks.ApplyEncoderConfiguration, 27f);
        RemoteStreamSelector = CreateSelectorRow(parent, "Remote Stream",
            new[] { "High Stream", "Low Stream" }, new[] { "high", "low" }, 0);
        CreateButton(parent, "Apply Remote Stream Type",
            callbacks.ApplyRemoteStreamType, 27f);
        AudioScenarioSelector = CreateSelectorRow(parent, "Audio Scenario",
            new[] { "Default (0)", "Chatroom (5)", "Game Streaming (3)" },
            new[] { "0", "5", "3" }, 0);
        CreateButton(parent, "Apply Audio Scenario", callbacks.ApplyAudioScenario, 27f);

        CreateSection(parent, "DESKTOP DEVICE SELECTION");
        RecordingDeviceSelector = CreateSelectorRow(parent, "Microphone",
            new[] { "System Default" }, new[] { string.Empty }, 0);
        PlaybackDeviceSelector = CreateSelectorRow(parent, "Playback",
            new[] { "System Default" }, new[] { string.Empty }, 0);
        VideoDeviceSelector = CreateSelectorRow(parent, "Camera Device",
            new[] { "System / AR Camera" }, new[] { string.Empty }, 0);
        CreateTwoButtonRow(parent, "Refresh Devices", callbacks.RefreshDevices,
            "Apply Devices", callbacks.ApplyDevices);

        CreateSection(parent, "MEETING TOOLS");
        RectTransform tools = CreateRow(parent, 29f);
        microphoneButton = CreateButton(tools, "MIC ON", callbacks.ToggleMicrophone, 29f,
            out microphoneLabel);
        CreateButton(tools, "LIKE", callbacks.Like, 29f);
        CreateButton(tools, "CLAP", callbacks.Clap, 29f);
        transcriptButton = CreateButton(tools, "START TXT", callbacks.ToggleTranscript, 29f,
            out transcriptLabel);
    }

    private void BuildRightColumn(Transform parent)
    {
        CreateText(parent, "Video / Status / Callback Log", 18f, CyanColor,
            FontStyles.Bold, 28f);
        RectTransform labels = CreateRow(parent, 18f);
        CreateText(labels, "LOCAL / PREVIEW", 11f, TextColor, FontStyles.Bold, 18f);
        CreateText(labels, "REMOTE / PARTICIPANTS", 11f, TextColor, FontStyles.Bold, 18f);

        GameObject video = NewUiObject(
            "Video Host",
            parent,
            typeof(CanvasRenderer),
            typeof(Image));
        VideoHost = (RectTransform)video.transform;
        video.GetComponent<Image>().color = new Color32(38, 43, 49, 255);
        SetHeight(video, 176f);

        CreateSection(parent, "STATE");
        StatusText = CreateText(
            parent,
            "Initialized: False    Joined: False    Preview: False\n" +
            "Connection: DISCONNECTED    Remote Users: -",
            11f,
            TextColor,
            FontStyles.Normal,
            82f);
        StatusText.textWrappingMode = TextWrappingModes.Normal;

        CreateSection(parent, "STATS / VIDEO SIZE");
        StatsText = CreateText(
            parent,
            "Local Audio Bitrate: 0 kbps\nRemote Audio: uid=-, 0 Hz\n" +
            "Local Video Size: -\nRemote Video Sizes: -",
            11f,
            TextColor,
            FontStyles.Normal,
            68f);

        CreateSection(parent, "CALLBACK / API LOG");
        LogText = CreateText(parent, string.Empty, 10.5f, TextColor,
            FontStyles.Normal, 176f);
        LayoutElement logLayout = LogText.GetComponent<LayoutElement>();
        logLayout.flexibleHeight = 1f;
        LogText.textWrappingMode = TextWrappingModes.Normal;
        LogText.overflowMode = TextOverflowModes.Truncate;
    }

    public void SetMicrophoneMuted(bool muted)
    {
        if (microphoneLabel != null) microphoneLabel.text = muted ? "MIC OFF" : "MIC ON";
        if (microphoneButton != null)
        {
            microphoneButton.GetComponent<Image>().color = muted
                ? new Color32(174, 65, 65, 255)
                : new Color32(30, 132, 82, 255);
        }
    }

    public void SetTranscriptState(bool recording, bool busy)
    {
        if (transcriptLabel != null)
        {
            transcriptLabel.text = busy
                ? (recording ? "SAVING..." : "STARTING...")
                : recording ? "STOP & SAVE" : "START TXT";
        }
        if (transcriptButton != null)
        {
            transcriptButton.GetComponent<Image>().color = recording
                ? new Color32(190, 55, 55, 255)
                : ButtonColor;
        }
    }

    public void SetDeviceOptions(
        JJDeviceInfo[] recording,
        JJDeviceInfo[] playback,
        JJDeviceInfo[] video)
    {
        SetDeviceOptions(RecordingDeviceSelector, recording, "System Default");
        SetDeviceOptions(PlaybackDeviceSelector, playback, "System Default");
        SetDeviceOptions(VideoDeviceSelector, video, "System / AR Camera");
    }

    private static void SetDeviceOptions(
        LiveKitDemo2Selector selector,
        JJDeviceInfo[] devices,
        string fallback)
    {
        if (selector == null) return;
        if (devices == null || devices.Length == 0)
        {
            selector.SetOptions(new[] { fallback }, new[] { string.Empty }, 0);
            return;
        }
        string[] labels = new string[devices.Length];
        string[] values = new string[devices.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            labels[i] = string.IsNullOrWhiteSpace(devices[i].deviceName)
                ? devices[i].deviceId
                : devices[i].deviceName;
            values[i] = devices[i].deviceId;
        }
        selector.SetOptions(labels, values, 0);
    }

    private TMP_InputField CreateInputRow(
        Transform parent,
        string label,
        string placeholder,
        string initial)
    {
        RectTransform row = CreateRow(parent, 27f);
        TMP_Text rowLabel = CreateText(row, label, 11f, TextColor,
            FontStyles.Bold, 27f);
        rowLabel.GetComponent<LayoutElement>().preferredWidth = 92f;
        rowLabel.GetComponent<LayoutElement>().flexibleWidth = 0f;
        return CreateInput(row, placeholder, initial);
    }

    private static TMP_InputField CreateInput(
        Transform parent,
        string placeholderValue,
        string initial)
    {
        GameObject root = NewUiObject(
            "Input",
            parent,
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(TMP_InputField));
        root.GetComponent<Image>().color = InputColor;
        LayoutElement layout = root.AddComponent<LayoutElement>();
        layout.flexibleWidth = 1f;
        layout.minWidth = 40f;

        GameObject area = NewUiObject("Text Area", root.transform, typeof(RectMask2D));
        RectTransform areaRect = (RectTransform)area.transform;
        Stretch(areaRect, 7f, 4f, 7f, 4f);
        TMP_Text placeholder = CreateText(area.transform, placeholderValue, 10.5f,
            MutedColor, FontStyles.Italic, 0f);
        Stretch((RectTransform)placeholder.transform);
        TMP_Text text = CreateText(area.transform, initial ?? string.Empty, 10.5f,
            TextColor, FontStyles.Normal, 0f);
        Stretch((RectTransform)text.transform);

        TMP_InputField input = root.GetComponent<TMP_InputField>();
        input.textViewport = areaRect;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.text = initial ?? string.Empty;
        return input;
    }

    private LiveKitDemo2Selector CreateSelectorRow(
        Transform parent,
        string label,
        string[] labels,
        string[] values,
        int selected)
    {
        RectTransform row = CreateRow(parent, 25f);
        TMP_Text rowLabel = CreateText(row, label, 11f, TextColor,
            FontStyles.Bold, 25f);
        rowLabel.GetComponent<LayoutElement>().preferredWidth = 118f;
        rowLabel.GetComponent<LayoutElement>().flexibleWidth = 0f;
        return LiveKitDemo2Selector.Create(row, labels, values, selected,
            InputColor, TextColor, CyanColor);
    }

    private static Toggle CreateToggleRow(Transform parent, string label, bool initial)
    {
        RectTransform row = CreateRow(parent, 23f);
        GameObject toggleRoot = NewUiObject(
            "Maintain Resolution Toggle",
            row,
            typeof(Toggle));
        LayoutElement layout = toggleRoot.AddComponent<LayoutElement>();
        layout.preferredWidth = 20f;
        layout.preferredHeight = 20f;
        GameObject background = NewUiObject(
            "Background", toggleRoot.transform, typeof(CanvasRenderer), typeof(Image));
        Stretch((RectTransform)background.transform, 2f, 2f, 2f, 2f);
        background.GetComponent<Image>().color = InputColor;
        GameObject checkmark = NewUiObject(
            "Checkmark", background.transform, typeof(CanvasRenderer), typeof(Image));
        Stretch((RectTransform)checkmark.transform, 3f, 3f, 3f, 3f);
        checkmark.GetComponent<Image>().color = CyanColor;
        Toggle toggle = toggleRoot.GetComponent<Toggle>();
        toggle.targetGraphic = background.GetComponent<Image>();
        toggle.graphic = checkmark.GetComponent<Image>();
        toggle.isOn = initial;
        CreateText(row, label, 10.5f, TextColor, FontStyles.Normal, 23f);
        return toggle;
    }

    private static void CreateSection(Transform parent, string title)
    {
        CreateText(parent, title, 11.5f, CyanColor, FontStyles.Bold, 19f);
    }

    private static RectTransform CreateRow(Transform parent, float height)
    {
        GameObject row = NewUiObject("Row", parent);
        SetHeight(row, height);
        var layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;
        return (RectTransform)row.transform;
    }

    private static void CreateTwoButtonRow(
        Transform parent,
        string left,
        Action leftAction,
        string right,
        Action rightAction)
    {
        RectTransform row = CreateRow(parent, 29f);
        CreateButton(row, left, leftAction, 29f);
        CreateButton(row, right, rightAction, 29f);
    }

    private static Button CreateButton(
        Transform parent,
        string label,
        Action action,
        float height)
    {
        return CreateButton(parent, label, action, height, out _);
    }

    private static Button CreateButton(
        Transform parent,
        string label,
        Action action,
        float height,
        out TMP_Text labelText)
    {
        GameObject root = NewUiObject(
            label + " Button",
            parent,
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button));
        root.GetComponent<Image>().color = ButtonColor;
        LayoutElement layout = root.AddComponent<LayoutElement>();
        layout.flexibleWidth = 1f;
        layout.preferredHeight = height;
        labelText = CreateText(root.transform, label, 10.5f, TextColor,
            FontStyles.Bold, height);
        Stretch((RectTransform)labelText.transform, 4f, 1f, 4f, 1f);
        labelText.alignment = TextAlignmentOptions.Center;
        Button button = root.GetComponent<Button>();
        if (action != null) button.onClick.AddListener(() => action());
        return button;
    }

    private static TMP_Text CreateText(
        Transform parent,
        string text,
        float fontSize,
        Color color,
        FontStyles style,
        float height)
    {
        GameObject root = NewUiObject(
            string.IsNullOrWhiteSpace(text) ? "Text" : text.Split('\n')[0],
            parent,
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        TMP_Text label = root.GetComponent<TMP_Text>();
        label.text = text;
        label.fontSize = fontSize;
        label.color = color;
        label.fontStyle = style;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.raycastTarget = false;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        LayoutElement layout = root.AddComponent<LayoutElement>();
        if (height > 0f) layout.preferredHeight = height;
        layout.flexibleWidth = 1f;
        return label;
    }

    private static GameObject NewUiObject(
        string name,
        Transform parent,
        params Type[] components)
    {
        var types = new List<Type> { typeof(RectTransform) };
        if (components != null) types.AddRange(components);
        var result = new GameObject(name, types.ToArray());
        result.transform.SetParent(parent, false);
        return result;
    }

    private static void SetHeight(GameObject target, float height)
    {
        LayoutElement element = target.GetComponent<LayoutElement>() ??
            target.AddComponent<LayoutElement>();
        element.preferredHeight = height;
        element.minHeight = height;
    }

    private static void Stretch(
        RectTransform rect,
        float left = 0f,
        float bottom = 0f,
        float right = 0f,
        float top = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }
}

/// <summary>
/// Compact selector used by the runtime screen.  A click cycles through the
/// available entries; this avoids a large pop-up covering the video on glasses
/// while retaining the same dropdown appearance and selected value semantics.
/// </summary>
public sealed class LiveKitDemo2Selector : MonoBehaviour
{
    private string[] labels = Array.Empty<string>();
    private string[] values = Array.Empty<string>();
    private int selectedIndex;
    private TMP_Text label;

    public int SelectedIndex => selectedIndex;
    public string SelectedValue => values.Length == 0
        ? string.Empty
        : values[Mathf.Clamp(selectedIndex, 0, values.Length - 1)];

    public static LiveKitDemo2Selector Create(
        Transform parent,
        string[] labels,
        string[] values,
        int selected,
        Color background,
        Color textColor,
        Color arrowColor)
    {
        GameObject root = new(
            "Selector",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button),
            typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        root.GetComponent<Image>().color = background;
        LayoutElement layout = root.GetComponent<LayoutElement>();
        layout.flexibleWidth = 1f;
        layout.minWidth = 40f;

        var selector = root.AddComponent<LiveKitDemo2Selector>();
        GameObject text = new(
            "Value",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        text.transform.SetParent(root.transform, false);
        RectTransform textRect = (RectTransform)text.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(7f, 1f);
        textRect.offsetMax = new Vector2(-22f, -1f);
        selector.label = text.GetComponent<TMP_Text>();
        selector.label.fontSize = 10.5f;
        selector.label.color = textColor;
        selector.label.alignment = TextAlignmentOptions.MidlineLeft;
        selector.label.raycastTarget = false;

        GameObject arrow = new(
            "Arrow",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        arrow.transform.SetParent(root.transform, false);
        RectTransform arrowRect = (RectTransform)arrow.transform;
        arrowRect.anchorMin = new Vector2(1f, 0f);
        arrowRect.anchorMax = Vector2.one;
        arrowRect.pivot = new Vector2(1f, 0.5f);
        arrowRect.sizeDelta = new Vector2(22f, 0f);
        TMP_Text arrowText = arrow.GetComponent<TMP_Text>();
        arrowText.text = "▼";
        arrowText.fontSize = 10f;
        arrowText.color = arrowColor;
        arrowText.alignment = TextAlignmentOptions.Center;
        arrowText.raycastTarget = false;

        selector.SetOptions(labels, values, selected);
        root.GetComponent<Button>().onClick.AddListener(selector.Next);
        return selector;
    }

    public void SetOptions(string[] optionLabels, string[] optionValues, int selected)
    {
        labels = optionLabels == null || optionLabels.Length == 0
            ? new[] { "-" }
            : optionLabels;
        values = optionValues == null || optionValues.Length != labels.Length
            ? labels
            : optionValues;
        selectedIndex = Mathf.Clamp(selected, 0, labels.Length - 1);
        RefreshLabel();
    }

    private void Next()
    {
        if (labels.Length == 0) return;
        selectedIndex = (selectedIndex + 1) % labels.Length;
        RefreshLabel();
    }

    private void RefreshLabel()
    {
        if (label != null && labels.Length > 0)
        {
            label.text = labels[Mathf.Clamp(selectedIndex, 0, labels.Length - 1)];
        }
    }
}
