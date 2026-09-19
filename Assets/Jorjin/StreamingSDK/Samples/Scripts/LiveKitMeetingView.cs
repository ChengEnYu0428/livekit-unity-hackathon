using System;
using System.Collections.Generic;
using Jorjin.Streaming;
using LiveKit;
using LiveKit.Proto;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Responsive Google-Meet-style participant grid created at runtime so the
/// existing sample scene can support any practical number of participants
/// without requiring a fixed set of video surfaces.
/// </summary>
public sealed class LiveKitMeetingView : MonoBehaviour
{
    public enum AdvancedControl
    {
        Preview,
        Camera,
        RemoteAudio,
        RemoteVideo,
        RemoteQuality,
        VideoProfile,
        MicrophoneInput,
        PcmObserver
    }

    private readonly Dictionary<string, LiveKitParticipantTile> tiles = new();
    private readonly Dictionary<string, string> roleLabels = new();
    private readonly Dictionary<string, string> trackTileKeys = new();

    private RectTransform rootRect;
    private Image rootImage;
    private RectTransform headerBar;
    private Image connectionDot;
    private TMP_Text connectionText;
    private Image transcriptStateImage;
    private TMP_Text transcriptStateText;
    private TMP_Text participantCountText;
    private RectTransform emptyState;
    private RectTransform gridRect;
    private GridLayoutGroup gridLayout;
    private RectTransform reactionBar;
    private RectTransform advancedBar;
    private Button microphoneButton;
    private TMP_Text microphoneButtonLabel;
    private Button likeButton;
    private Button clapButton;
    private Button transcriptButton;
    private TMP_Text transcriptButtonLabel;
    private Button previewButton;
    private TMP_Text previewButtonLabel;
    private Button cameraButton;
    private TMP_Text cameraButtonLabel;
    private Button remoteAudioButton;
    private TMP_Text remoteAudioButtonLabel;
    private Button remoteVideoButton;
    private TMP_Text remoteVideoButtonLabel;
    private Button remoteQualityButton;
    private TMP_Text remoteQualityButtonLabel;
    private Button videoProfileButton;
    private TMP_Text videoProfileButtonLabel;
    private Button microphoneInputButton;
    private TMP_Text microphoneInputButtonLabel;
    private Button pcmObserverButton;
    private TMP_Text pcmObserverButtonLabel;
    private Action<JJReactionType> reactionRequested;
    private Action microphoneToggleRequested;
    private Action transcriptRequested;
    private Action<AdvancedControl> advancedControlRequested;
    private string localIdentity;
    private bool localMicrophoneMuted;
    private bool sdkReady;
    private bool connected;
    private bool previewEnabled;
    private bool embeddedMode;
    private Vector2 lastGridSize;
    private bool lastLandscape;
    private int lastTileCount = -1;

    public static LiveKitMeetingView Create(
        Transform canvasTransform,
        Action<JJReactionType> reactionRequested,
        Action microphoneToggleRequested,
        Action transcriptRequested,
        Action<AdvancedControl> advancedControlRequested)
    {
        if (canvasTransform == null) return null;

        var root = new GameObject(
            "LiveKit Meeting View",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        root.transform.SetParent(canvasTransform, false);
        var view = root.AddComponent<LiveKitMeetingView>();
        view.Initialize(
            reactionRequested,
            microphoneToggleRequested,
            transcriptRequested,
            advancedControlRequested);
        return view;
    }

    private void Initialize(
        Action<JJReactionType> onReactionRequested,
        Action onMicrophoneToggleRequested,
        Action onTranscriptRequested,
        Action<AdvancedControl> onAdvancedControlRequested)
    {
        reactionRequested = onReactionRequested;
        microphoneToggleRequested = onMicrophoneToggleRequested;
        transcriptRequested = onTranscriptRequested;
        advancedControlRequested = onAdvancedControlRequested;
        rootRect = (RectTransform)transform;
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootImage = GetComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(rootImage, LiveKitMeetingStyle.Background);
        rootImage.raycastTarget = false;
        LiveKitMeetingStyle.AddSoftShadow(gameObject, 0.48f);
        LiveKitMeetingStyle.AddBorder(gameObject, 0.48f);

        BuildHeader();

        GameObject gridObject = CreateRect("Participant Grid", transform);
        gridRect = (RectTransform)gridObject.transform;
        gridRect.anchorMin = new Vector2(0f, 0.27f);
        gridRect.anchorMax = new Vector2(1f, 0.91f);
        gridRect.offsetMin = new Vector2(14f, 8f);
        gridRect.offsetMax = new Vector2(-14f, -4f);
        gridLayout = gridObject.AddComponent<GridLayoutGroup>();
        gridLayout.padding = new RectOffset(6, 6, 6, 6);
        gridLayout.spacing = new Vector2(12f, 12f);
        gridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
        gridLayout.startAxis = GridLayoutGroup.Axis.Horizontal;
        gridLayout.childAlignment = TextAnchor.MiddleCenter;
        gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        gridLayout.constraintCount = 1;

        BuildEmptyState();

        GameObject barObject = CreateRect("Reaction Bar", transform);
        reactionBar = (RectTransform)barObject.transform;
        reactionBar.anchorMin = new Vector2(0.5f, 0.015f);
        reactionBar.anchorMax = new Vector2(0.5f, 0.125f);
        reactionBar.pivot = new Vector2(0.5f, 0.5f);
        reactionBar.sizeDelta = new Vector2(624f, 0f);
        reactionBar.anchoredPosition = Vector2.zero;
        Image reactionBarImage = barObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            reactionBarImage,
            new Color32(16, 25, 40, 248),
            true);
        reactionBarImage.raycastTarget = false;
        LiveKitMeetingStyle.AddSoftShadow(barObject, 0.42f);
        var barLayout = barObject.AddComponent<HorizontalLayoutGroup>();
        barLayout.spacing = 8f;
        barLayout.padding = new RectOffset(10, 10, 8, 8);
        barLayout.childAlignment = TextAnchor.MiddleCenter;
        barLayout.childControlWidth = true;
        barLayout.childControlHeight = true;
        barLayout.childForceExpandWidth = false;
        barLayout.childForceExpandHeight = true;

        microphoneButton = CreateMicrophoneButton(reactionBar);
        likeButton = CreateReactionButton(reactionBar, "LIKE", JJReactionType.LIKE);
        clapButton = CreateReactionButton(reactionBar, "CLAP", JJReactionType.CLAP);
        transcriptButton = CreateTranscriptButton(reactionBar);

        GameObject advancedBarObject = CreateRect("Advanced Controls", transform);
        advancedBar = (RectTransform)advancedBarObject.transform;
        advancedBar.anchorMin = new Vector2(0.5f, 0.135f);
        advancedBar.anchorMax = new Vector2(0.5f, 0.255f);
        advancedBar.pivot = new Vector2(0.5f, 0.5f);
        advancedBar.sizeDelta = new Vector2(768f, 0f);
        advancedBar.anchoredPosition = Vector2.zero;
        Image advancedBarImage = advancedBarObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            advancedBarImage,
            new Color32(18, 29, 46, 235));
        advancedBarImage.raycastTarget = false;
        var advancedLayout = advancedBarObject.AddComponent<HorizontalLayoutGroup>();
        advancedLayout.spacing = 7f;
        advancedLayout.padding = new RectOffset(10, 10, 7, 7);
        advancedLayout.childAlignment = TextAnchor.MiddleCenter;
        advancedLayout.childControlWidth = true;
        advancedLayout.childControlHeight = true;
        advancedLayout.childForceExpandWidth = false;
        advancedLayout.childForceExpandHeight = true;

        previewButton = CreateAdvancedButton(
            advancedBar,
            "Preview Button",
            "預覽\n關閉",
            new Color32(76, 86, 106, 255),
            AdvancedControl.Preview,
            out previewButtonLabel);
        cameraButton = CreateAdvancedButton(
            advancedBar,
            "Camera Button",
            "鏡頭\n後鏡頭/AR",
            new Color32(62, 92, 135, 255),
            AdvancedControl.Camera,
            out cameraButtonLabel);
        remoteAudioButton = CreateAdvancedButton(
            advancedBar,
            "Remote Audio Button",
            "對方麥克風\n開啟",
            new Color32(32, 128, 72, 255),
            AdvancedControl.RemoteAudio,
            out remoteAudioButtonLabel);
        remoteVideoButton = CreateAdvancedButton(
            advancedBar,
            "Remote Video Button",
            "對方視訊\n開啟",
            new Color32(32, 112, 156, 255),
            AdvancedControl.RemoteVideo,
            out remoteVideoButtonLabel);
        remoteQualityButton = CreateAdvancedButton(
            advancedBar,
            "Remote Quality Button",
            "畫質\n高",
            new Color32(93, 80, 160, 255),
            AdvancedControl.RemoteQuality,
            out remoteQualityButtonLabel);
        videoProfileButton = CreateAdvancedButton(
            advancedBar,
            "Video Profile Button",
            "視訊\n720P",
            new Color32(28, 105, 128, 255),
            AdvancedControl.VideoProfile,
            out videoProfileButtonLabel);
        microphoneInputButton = CreateAdvancedButton(
            advancedBar,
            "Microphone Input Button",
            "麥克風來源\n系統預設",
            new Color32(41, 98, 122, 255),
            AdvancedControl.MicrophoneInput,
            out microphoneInputButtonLabel);
        pcmObserverButton = CreateAdvancedButton(
            advancedBar,
            "PCM Observer Button",
            "PCM\n關閉",
            new Color32(76, 86, 106, 255),
            AdvancedControl.PcmObserver,
            out pcmObserverButtonLabel);
        SetMicrophoneMuted(false);
        SetSdkReady(false);
        SetAdvancedControlState(false, false, false, false, false, "720P", false);
        SetConnected(false);
        UpdateResponsiveRect(true);
    }

    private void BuildHeader()
    {
        GameObject headerObject = CreateRect("Meeting Header", transform);
        headerBar = (RectTransform)headerObject.transform;
        headerBar.anchorMin = new Vector2(0f, 0.91f);
        headerBar.anchorMax = Vector2.one;
        headerBar.offsetMin = new Vector2(22f, 0f);
        headerBar.offsetMax = new Vector2(-22f, -2f);

        TMP_Text title = CreateText("Title", headerBar, 22f, FontStyles.Bold);
        title.text = "LIVE MEETING";
        title.color = LiveKitMeetingStyle.TextPrimary;
        title.alignment = TextAlignmentOptions.MidlineLeft;
        title.characterSpacing = 4f;
        SetAnchoredRect(title.rectTransform, 0f, 0f, 0.31f, 1f, 0f, 0f, 0f, 0f);

        GameObject statusObject = CreateRect("Connection Status", headerBar);
        RectTransform statusRect = (RectTransform)statusObject.transform;
        statusRect.anchorMin = new Vector2(0.31f, 0.2f);
        statusRect.anchorMax = new Vector2(0.56f, 0.8f);
        statusRect.offsetMin = Vector2.zero;
        statusRect.offsetMax = Vector2.zero;
        Image statusImage = statusObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            statusImage,
            new Color32(31, 43, 64, 235),
            true);
        statusImage.raycastTarget = false;

        GameObject dotObject = CreateRect("Status Dot", statusObject.transform);
        RectTransform dotRect = (RectTransform)dotObject.transform;
        dotRect.anchorMin = new Vector2(0f, 0.5f);
        dotRect.anchorMax = new Vector2(0f, 0.5f);
        dotRect.pivot = new Vector2(0.5f, 0.5f);
        dotRect.anchoredPosition = new Vector2(17f, 0f);
        dotRect.sizeDelta = new Vector2(10f, 10f);
        connectionDot = dotObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            connectionDot,
            LiveKitMeetingStyle.TextSecondary,
            true);
        connectionDot.raycastTarget = false;

        connectionText = CreateText(
            "Status Label",
            statusObject.transform,
            14f,
            FontStyles.Bold);
        connectionText.color = LiveKitMeetingStyle.TextSecondary;
        connectionText.alignment = TextAlignmentOptions.MidlineLeft;
        SetAnchoredRect(
            connectionText.rectTransform,
            0f,
            0f,
            1f,
            1f,
            31f,
            0f,
            -8f,
            0f);

        GameObject transcriptObject = CreateRect("Transcript Status", headerBar);
        RectTransform transcriptRect = (RectTransform)transcriptObject.transform;
        transcriptRect.anchorMin = new Vector2(0.57f, 0.2f);
        transcriptRect.anchorMax = new Vector2(0.82f, 0.8f);
        transcriptRect.offsetMin = Vector2.zero;
        transcriptRect.offsetMax = Vector2.zero;
        transcriptStateImage = transcriptObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            transcriptStateImage,
            LiveKitMeetingStyle.SurfaceRaised,
            true);
        transcriptStateImage.raycastTarget = false;

        transcriptStateText = CreateText(
            "Transcript Status Label",
            transcriptObject.transform,
            13f,
            FontStyles.Bold);
        transcriptStateText.text = "AGENT OFFLINE";
        transcriptStateText.color = LiveKitMeetingStyle.TextSecondary;
        transcriptStateText.alignment = TextAlignmentOptions.Center;
        SetAnchoredRect(
            transcriptStateText.rectTransform,
            0f,
            0f,
            1f,
            1f,
            6f,
            0f,
            -6f,
            0f);

        participantCountText = CreateText(
            "Participant Count",
            headerBar,
            15f,
            FontStyles.Normal);
        participantCountText.color = LiveKitMeetingStyle.TextSecondary;
        participantCountText.alignment = TextAlignmentOptions.MidlineRight;
        SetAnchoredRect(
            participantCountText.rectTransform,
            0.71f,
            0f,
            0.82f,
            1f,
            0f,
            0f,
            0f,
            0f);
        RefreshHeader();
    }

    public void SetTranscriptInfo(
        bool agentReady,
        bool recording,
        bool pending,
        int characterCount)
    {
        if (transcriptStateText == null || transcriptStateImage == null) return;

        if (pending)
        {
            transcriptStateText.text = recording ? "SAVING..." : "STARTING...";
            transcriptStateText.color = LiveKitMeetingStyle.TextPrimary;
            LiveKitMeetingStyle.ApplyRounded(
                transcriptStateImage,
                new Color32(146, 94, 12, 255),
                true);
        }
        else if (recording)
        {
            transcriptStateText.text = characterCount > 0
                ? $"RECORDING  {characterCount}"
                : "RECORDING";
            transcriptStateText.color = LiveKitMeetingStyle.TextPrimary;
            LiveKitMeetingStyle.ApplyRounded(
                transcriptStateImage,
                new Color32(185, 45, 58, 255),
                true);
        }
        else if (agentReady)
        {
            transcriptStateText.text = "AGENT READY";
            transcriptStateText.color = LiveKitMeetingStyle.TextPrimary;
            LiveKitMeetingStyle.ApplyRounded(
                transcriptStateImage,
                new Color32(20, 112, 82, 255),
                true);
        }
        else
        {
            transcriptStateText.text = "AGENT OFFLINE";
            transcriptStateText.color = LiveKitMeetingStyle.TextSecondary;
            LiveKitMeetingStyle.ApplyRounded(
                transcriptStateImage,
                LiveKitMeetingStyle.SurfaceRaised,
                true);
        }
    }

    private void BuildEmptyState()
    {
        GameObject emptyObject = CreateRect("Meeting Empty State", transform);
        emptyState = (RectTransform)emptyObject.transform;
        emptyState.anchorMin = new Vector2(0.16f, 0.36f);
        emptyState.anchorMax = new Vector2(0.84f, 0.76f);
        emptyState.offsetMin = Vector2.zero;
        emptyState.offsetMax = Vector2.zero;

        Image emptyImage = emptyObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            emptyImage,
            new Color32(17, 27, 43, 220));
        emptyImage.raycastTarget = false;
        LiveKitMeetingStyle.AddBorder(emptyObject, 0.36f);

        TMP_Text heading = CreateText(
            "Empty State Title",
            emptyState,
            25f,
            FontStyles.Bold);
        heading.text = "READY WHEN YOU ARE";
        heading.color = LiveKitMeetingStyle.TextPrimary;
        heading.alignment = TextAlignmentOptions.Bottom;
        heading.characterSpacing = 2f;
        SetAnchoredRect(
            heading.rectTransform,
            0.08f,
            0.48f,
            0.92f,
            0.78f,
            0f,
            0f,
            0f,
            0f);

        TMP_Text hint = CreateText(
            "Empty State Hint",
            emptyState,
            16f,
            FontStyles.Normal);
        hint.text = "Initialize and join a channel to start your meeting";
        hint.color = LiveKitMeetingStyle.TextSecondary;
        hint.alignment = TextAlignmentOptions.Top;
        SetAnchoredRect(
            hint.rectTransform,
            0.08f,
            0.22f,
            0.92f,
            0.5f,
            0f,
            0f,
            0f,
            0f);
    }

    public void SetConnected(bool connected)
    {
        this.connected = connected;
        RefreshHeader();
        RefreshViewVisibility();
        // Transcription can be used before joining a room, so keep the
        // controls bar visible while the participant grid remains hidden.
        if (reactionBar != null) reactionBar.gameObject.SetActive(!embeddedMode);
        if (microphoneButton != null) microphoneButton.interactable = connected;
        if (likeButton != null) likeButton.interactable = connected;
        if (clapButton != null) clapButton.interactable = connected;
        if (transcriptButton != null) transcriptButton.interactable = true;
        RefreshAdvancedInteractable();
        if (!connected) ClearAll();
    }

    public void SetSdkReady(bool ready)
    {
        sdkReady = ready;
        RefreshHeader();
        RefreshAdvancedInteractable();
    }

    public void SetAdvancedControlState(
        bool previewOn,
        bool frontCamera,
        bool remoteAudioMuted,
        bool remoteVideoMuted,
        bool lowQuality,
        string videoProfile,
        bool pcmEnabled)
    {
        previewEnabled = previewOn;
        SetAdvancedButtonState(
            previewButton,
            previewButtonLabel,
            previewOn ? "預覽\n開啟" : "預覽\n關閉",
            previewOn,
            new Color32(32, 128, 72, 255));
        SetAdvancedButtonState(
            cameraButton,
            cameraButtonLabel,
            frontCamera ? "鏡頭\n前鏡頭" : "鏡頭\n後鏡頭/AR",
            frontCamera,
            new Color32(30, 125, 180, 255));
        SetAdvancedButtonState(
            remoteAudioButton,
            remoteAudioButtonLabel,
            remoteAudioMuted ? "對方麥克風\n關閉" : "對方麥克風\n開啟",
            !remoteAudioMuted,
            new Color32(32, 128, 72, 255));
        SetAdvancedButtonState(
            remoteVideoButton,
            remoteVideoButtonLabel,
            remoteVideoMuted ? "對方視訊\n關閉" : "對方視訊\n開啟",
            !remoteVideoMuted,
            new Color32(32, 112, 156, 255));
        SetAdvancedButtonState(
            remoteQualityButton,
            remoteQualityButtonLabel,
            lowQuality ? "畫質\n低" : "畫質\n高",
            !lowQuality,
            new Color32(93, 80, 160, 255));
        SetAdvancedButtonState(
            videoProfileButton,
            videoProfileButtonLabel,
            "視訊\n" + (string.IsNullOrWhiteSpace(videoProfile)
                ? "720P"
                : videoProfile),
            true,
            new Color32(28, 105, 128, 255));
        SetAdvancedButtonState(
            pcmObserverButton,
            pcmObserverButtonLabel,
            pcmEnabled ? "PCM\n開啟" : "PCM\n關閉",
            pcmEnabled,
            new Color32(168, 96, 32, 255));
        RefreshViewVisibility();
    }

    private void RefreshViewVisibility()
    {
        bool showGrid = connected || (sdkReady && previewEnabled);
        if (rootImage != null) rootImage.enabled = !embeddedMode;
        if (headerBar != null) headerBar.gameObject.SetActive(!embeddedMode);
        if (gridRect != null) gridRect.gameObject.SetActive(showGrid);
        if (emptyState != null)
        {
            emptyState.gameObject.SetActive(!embeddedMode && !showGrid);
        }
        if (reactionBar != null) reactionBar.gameObject.SetActive(!embeddedMode);
        if (advancedBar != null) advancedBar.gameObject.SetActive(!embeddedMode);
    }

    /// <summary>
    /// Places only the participant grid inside another runtime UI.  The
    /// original meeting toolbar is hidden because Demo 2 exposes the same
    /// actions in its Advanced API Controls column.
    /// </summary>
    public void SetEmbeddedMode(bool embedded)
    {
        embeddedMode = embedded;
        if (!embedded || rootRect == null) return;

        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;
        if (rootImage != null) rootImage.enabled = false;
        if (headerBar != null) headerBar.gameObject.SetActive(false);
        if (emptyState != null) emptyState.gameObject.SetActive(false);
        if (gridRect != null)
        {
            gridRect.anchorMin = Vector2.zero;
            gridRect.anchorMax = Vector2.one;
            gridRect.offsetMin = new Vector2(2f, 2f);
            gridRect.offsetMax = new Vector2(-2f, -2f);
        }
        if (reactionBar != null) reactionBar.gameObject.SetActive(false);
        if (advancedBar != null) advancedBar.gameObject.SetActive(false);
        lastGridSize = Vector2.zero;
        RefreshViewVisibility();
    }

    private void RefreshAdvancedInteractable()
    {
        if (previewButton != null) previewButton.interactable = sdkReady;
        if (cameraButton != null) cameraButton.interactable = sdkReady;
        if (videoProfileButton != null) videoProfileButton.interactable = sdkReady;
        if (microphoneInputButton != null) microphoneInputButton.interactable = sdkReady;
        if (remoteAudioButton != null) remoteAudioButton.interactable = connected;
        if (remoteVideoButton != null) remoteVideoButton.interactable = connected;
        if (remoteQualityButton != null) remoteQualityButton.interactable = connected;
        if (pcmObserverButton != null) pcmObserverButton.interactable = connected;
    }

    public void SetTranscriptState(bool recording, bool startingOrSaving = false)
    {
        if (transcriptButton == null || transcriptButtonLabel == null) return;

        transcriptButtonLabel.text = recording
            ? (startingOrSaving ? "儲存中…" : "停止並儲存")
            : (startingOrSaving ? "啟動中…" : "開始逐字稿");
        LiveKitMeetingStyle.ApplyRounded(
            transcriptButton.GetComponent<Image>(),
            recording ? LiveKitMeetingStyle.Danger : new Color32(13, 148, 136, 255),
            true);
    }

    public void SetLocalParticipant(string identity)
    {
        string normalizedIdentity = string.IsNullOrWhiteSpace(identity)
            ? "Me"
            : identity.Trim();

        // Initialize() can create the tile from the UID input before LiveKit
        // reports the identity embedded in the token. Remove that provisional
        // tile when the authoritative identity arrives so one device is never
        // shown twice (for example, "1 (You)" and "user2 (You)").
        string previousLocalIdentity = localIdentity;
        if (!string.IsNullOrWhiteSpace(previousLocalIdentity) &&
            !string.Equals(
                previousLocalIdentity,
                normalizedIdentity,
                StringComparison.Ordinal))
        {
            RemoveTile(previousLocalIdentity);
        }

        localIdentity = normalizedIdentity;
        LiveKitParticipantTile tile = EnsureParticipant(localIdentity, DisplayNameFor(localIdentity));
        tile.SetDisplayName(DisplayNameFor(localIdentity));
        tile.SetMicrophoneMuted(localMicrophoneMuted);
    }

    public void SetMicrophoneMuted(bool muted)
    {
        localMicrophoneMuted = muted;
        if (microphoneButton != null && microphoneButtonLabel != null)
        {
            microphoneButtonLabel.text = muted ? "麥克風\n關閉" : "麥克風\n開啟";
            LiveKitMeetingStyle.ApplyRounded(
                microphoneButton.GetComponent<Image>(),
                muted ? LiveKitMeetingStyle.Danger : LiveKitMeetingStyle.Success,
                true);
        }

        if (!string.IsNullOrWhiteSpace(localIdentity) &&
            tiles.TryGetValue(localIdentity, out LiveKitParticipantTile tile))
        {
            tile.SetMicrophoneMuted(muted);
        }
    }

    public void AddRemoteParticipant(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return;
        if (string.Equals(identity, localIdentity, StringComparison.Ordinal)) return;
        EnsureParticipant(identity, DisplayNameFor(identity));
    }

    public void SetMicrophoneInput(string deviceName)
    {
        string compactName = CompactDeviceName(deviceName);
        SetAdvancedButtonState(
            microphoneInputButton,
            microphoneInputButtonLabel,
            "麥克風來源\n" + compactName,
            true,
            new Color32(41, 98, 122, 255));
    }

    private static string CompactDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "系統預設";

        string value = deviceName.Trim();
        const int maximumLength = 15;
        return value.Length <= maximumLength
            ? value.ToUpperInvariant()
            : value.Substring(0, maximumLength - 1).ToUpperInvariant() + "…";
    }

    public void RemoveParticipant(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return;
        var keys = new List<string>();
        foreach (KeyValuePair<string, LiveKitParticipantTile> entry in tiles)
        {
            if (entry.Value != null &&
                string.Equals(entry.Value.OwnerIdentity, identity, StringComparison.Ordinal))
            {
                keys.Add(entry.Key);
            }
        }
        foreach (string key in keys) RemoveTile(key);

        var trackIds = new List<string>();
        foreach (KeyValuePair<string, string> entry in trackTileKeys)
        {
            if (!tiles.ContainsKey(entry.Value)) trackIds.Add(entry.Key);
        }
        foreach (string sid in trackIds) trackTileKeys.Remove(sid);
    }

    public void BindLocalTexture(Texture texture)
    {
        if (string.IsNullOrWhiteSpace(localIdentity)) return;
        EnsureParticipant(localIdentity, DisplayNameFor(localIdentity)).BindLocalTexture(texture);
    }

    public void GetCollaborationSources(List<LiveKitCollaborationPanel.VideoSource> sources)
    {
        sources.Clear();
        foreach (var pair in tiles)
        {
            var tile = pair.Value;
            if (tile == null) continue;
            sources.Add(new LiveKitCollaborationPanel.VideoSource {
                Key = pair.Key, Identity = tile.OwnerIdentity,
                IsLocal = tile.OwnerIdentity == localIdentity, IsScreenShare = tile.IsScreenShare,
                Texture = tile.DisplayTexture, Uv = tile.DisplayUvRect
            });
        }
        sources.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
    }

    public void SetLocalVideoVisible(bool visible)
    {
        if (!string.IsNullOrWhiteSpace(localIdentity) &&
            tiles.TryGetValue(localIdentity, out LiveKitParticipantTile tile))
        {
            tile.SetVideoVisible(visible);
        }
    }

    public void BindRemoteTrack(
        RemoteVideoTrack track,
        string identity,
        TrackSource source)
    {
        if (track == null || string.IsNullOrWhiteSpace(identity)) return;

        string key;
        string label;
        if (source == TrackSource.SourceCamera)
        {
            key = identity;
            label = identity;
        }
        else
        {
            key = $"{identity}|{track.Sid}";
            label = source == TrackSource.SourceScreenshare
                ? identity + " (Screen)"
                : identity + " (Video)";
        }

        LiveKitParticipantTile tile = EnsureTile(key, identity, label);
        tile.IsScreenShare = source == TrackSource.SourceScreenshare;
        tile.BindRemoteTrack(track);
        trackTileKeys[track.Sid] = key;
    }

    public void UnbindRemoteTrack(
        RemoteVideoTrack track,
        string identity,
        TrackSource source)
    {
        if (track == null) return;
        if (!trackTileKeys.TryGetValue(track.Sid, out string key))
        {
            key = source == TrackSource.SourceCamera ? identity : $"{identity}|{track.Sid}";
        }
        trackTileKeys.Remove(track.Sid);

        if (source == TrackSource.SourceCamera)
        {
            if (tiles.TryGetValue(key, out LiveKitParticipantTile participantTile))
            {
                participantTile.ClearVideo();
            }
        }
        else
        {
            RemoveTile(key);
        }
    }

    public void ShowReaction(string identity, JJReactionType reaction)
    {
        if (string.IsNullOrWhiteSpace(identity)) identity = "Guest";
        EnsureParticipant(identity, DisplayNameFor(identity)).PlayReaction(reaction);
    }

    public void ClearAll()
    {
        foreach (LiveKitParticipantTile tile in tiles.Values)
        {
            if (tile == null) continue;
            tile.gameObject.SetActive(false);
            Destroy(tile.gameObject);
        }
        tiles.Clear();
        trackTileKeys.Clear();
        localIdentity = null;
        lastTileCount = -1;
        RefreshHeader();
    }

    /// <summary>Shows 場域端 / 專家端 next to a participant's name.</summary>
    public void SetParticipantRole(string identity, string role)
    {
        if (string.IsNullOrWhiteSpace(identity)) return;
        roleLabels[identity] = role == "expert" ? "專家端" : "場域端";
        if (tiles.TryGetValue(identity, out LiveKitParticipantTile tile) && tile != null)
            tile.SetDisplayName(DisplayNameFor(identity));
    }

    private string DisplayNameFor(string identity)
    {
        string name = string.Equals(identity, localIdentity, StringComparison.Ordinal)
            ? identity + " (You)"
            : identity;
        return roleLabels.TryGetValue(identity, out string role) ? name + "・" + role : name;
    }

    private LiveKitParticipantTile EnsureParticipant(string identity, string displayName)
    {
        return EnsureTile(identity, identity, displayName);
    }

    private LiveKitParticipantTile EnsureTile(
        string key,
        string ownerIdentity,
        string displayName)
    {
        if (tiles.TryGetValue(key, out LiveKitParticipantTile existing) && existing != null)
        {
            return existing;
        }

        LiveKitParticipantTile tile = LiveKitParticipantTile.Create(
            gridRect,
            key,
            ownerIdentity,
            displayName);
        tiles[key] = tile;
        lastTileCount = -1;
        RefreshHeader();
        return tile;
    }

    private void RemoveTile(string key)
    {
        if (!tiles.TryGetValue(key, out LiveKitParticipantTile tile)) return;
        tiles.Remove(key);
        if (tile != null)
        {
            tile.gameObject.SetActive(false);
            Destroy(tile.gameObject);
        }
        lastTileCount = -1;
        RefreshHeader();
    }

    private Button CreateReactionButton(
        Transform parent,
        string label,
        JJReactionType reaction)
    {
        var buttonObject = new GameObject(
            label + " Button",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button),
            typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        var image = buttonObject.GetComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            image,
            reaction == JJReactionType.LIKE
                ? new Color32(37, 99, 235, 255)
                : LiveKitMeetingStyle.Warning,
            true);

        var button = buttonObject.GetComponent<Button>();
        LiveKitMeetingStyle.ConfigureButton(button);
        button.onClick.AddListener(() => reactionRequested?.Invoke(reaction));

        var layout = buttonObject.GetComponent<LayoutElement>();
        layout.preferredWidth = 118f;
        layout.preferredHeight = 50f;

        GameObject labelObject = CreateRect("Label", buttonObject.transform);
        RectTransform labelRect = (RectTransform)labelObject.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(6f, 3f);
        labelRect.offsetMax = new Vector2(-6f, -3f);
        var text = labelObject.AddComponent<TextMeshProUGUI>();
        // The bundled LiberationSans SDF font has no emoji glyphs. Keeping
        // these labels text-only avoids the square placeholder seen in builds.
        text.text = reaction == JJReactionType.LIKE ? "讚" : "拍手";
        text.fontSize = 20f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        text.enableAutoSizing = true;
        text.fontSizeMin = 12f;
        text.fontSizeMax = 20f;
        return button;
    }

    private Button CreateAdvancedButton(
        Transform parent,
        string objectName,
        string label,
        Color32 color,
        AdvancedControl control,
        out TMP_Text labelText)
    {
        var buttonObject = new GameObject(
            objectName,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button),
            typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        LiveKitMeetingStyle.ApplyRounded(
            buttonObject.GetComponent<Image>(),
            color,
            true);

        var button = buttonObject.GetComponent<Button>();
        LiveKitMeetingStyle.ConfigureButton(button);
        button.onClick.AddListener(
            () => advancedControlRequested?.Invoke(control));

        var layout = buttonObject.GetComponent<LayoutElement>();
        layout.preferredWidth = 87f;
        layout.preferredHeight = 42f;

        GameObject labelObject = CreateRect("Label", buttonObject.transform);
        RectTransform labelRect = (RectTransform)labelObject.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(3f, 2f);
        labelRect.offsetMax = new Vector2(-3f, -2f);
        labelText = labelObject.AddComponent<TextMeshProUGUI>();
        labelText.text = label;
        labelText.fontSize = 12f;
        labelText.fontStyle = FontStyles.Bold;
        labelText.alignment = TextAlignmentOptions.Center;
        labelText.color = Color.white;
        labelText.raycastTarget = false;
        labelText.enableAutoSizing = true;
        labelText.fontSizeMin = 7f;
        labelText.fontSizeMax = 12f;
        return button;
    }

    private static void SetAdvancedButtonState(
        Button button,
        TMP_Text label,
        string text,
        bool active,
        Color32 activeColor)
    {
        if (label != null)
        {
            label.text = text;
        }
        if (button != null)
        {
            LiveKitMeetingStyle.ApplyRounded(
                button.GetComponent<Image>(),
                active ? activeColor : LiveKitMeetingStyle.SurfaceRaised,
                true);
        }
    }

    private Button CreateMicrophoneButton(Transform parent)
    {
        var buttonObject = new GameObject(
            "Microphone Button",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button),
            typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);

        var image = buttonObject.GetComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            image,
            LiveKitMeetingStyle.Success,
            true);

        var button = buttonObject.GetComponent<Button>();
        LiveKitMeetingStyle.ConfigureButton(button);
        button.onClick.AddListener(() => microphoneToggleRequested?.Invoke());

        var layout = buttonObject.GetComponent<LayoutElement>();
        layout.preferredWidth = 92f;
        layout.preferredHeight = 50f;

        GameObject labelObject = CreateRect("Label", buttonObject.transform);
        RectTransform labelRect = (RectTransform)labelObject.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(5f, 2f);
        labelRect.offsetMax = new Vector2(-5f, -2f);
        microphoneButtonLabel = labelObject.AddComponent<TextMeshProUGUI>();
        microphoneButtonLabel.text = "麥克風\n開啟";
        microphoneButtonLabel.fontSize = 17f;
        microphoneButtonLabel.fontStyle = FontStyles.Bold;
        microphoneButtonLabel.alignment = TextAlignmentOptions.Center;
        microphoneButtonLabel.color = Color.white;
        microphoneButtonLabel.raycastTarget = false;
        microphoneButtonLabel.enableAutoSizing = true;
        microphoneButtonLabel.fontSizeMin = 10f;
        microphoneButtonLabel.fontSizeMax = 17f;
        return button;
    }

    private Button CreateTranscriptButton(Transform parent)
    {
        var buttonObject = new GameObject(
            "Transcript Button",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button),
            typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);

        var image = buttonObject.GetComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            image,
            new Color32(13, 148, 136, 255),
            true);

        var button = buttonObject.GetComponent<Button>();
        LiveKitMeetingStyle.ConfigureButton(button);
        button.onClick.AddListener(() => transcriptRequested?.Invoke());

        var layout = buttonObject.GetComponent<LayoutElement>();
        layout.preferredWidth = 244f;
        layout.preferredHeight = 50f;

        GameObject labelObject = CreateRect("Label", buttonObject.transform);
        RectTransform labelRect = (RectTransform)labelObject.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(6f, 3f);
        labelRect.offsetMax = new Vector2(-6f, -3f);
        transcriptButtonLabel = labelObject.AddComponent<TextMeshProUGUI>();
        transcriptButtonLabel.text = "開始逐字稿";
        transcriptButtonLabel.fontSize = 18f;
        transcriptButtonLabel.fontStyle = FontStyles.Bold;
        transcriptButtonLabel.alignment = TextAlignmentOptions.Center;
        transcriptButtonLabel.color = Color.white;
        transcriptButtonLabel.raycastTarget = false;
        transcriptButtonLabel.enableAutoSizing = true;
        transcriptButtonLabel.fontSizeMin = 10f;
        transcriptButtonLabel.fontSizeMax = 18f;
        return button;
    }

    private void LateUpdate()
    {
        UpdateResponsiveRect(false);
        UpdateGridLayout();
    }

    private void UpdateResponsiveRect(bool force)
    {
        if (embeddedMode) return;
        bool landscape = Screen.width >= Screen.height;
        if (!force && landscape == lastLandscape) return;
        lastLandscape = landscape;

        if (landscape)
        {
            rootRect.anchorMin = new Vector2(0.33f, 0.05f);
            rootRect.anchorMax = new Vector2(0.98f, 0.95f);
        }
        else
        {
            // Keep the existing connection controls at the top and place the
            // responsive participant grid in the large area below them.
            rootRect.anchorMin = new Vector2(0.04f, 0.04f);
            rootRect.anchorMax = new Vector2(0.96f, 0.68f);
        }
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;
        lastGridSize = Vector2.zero;
    }

    private void UpdateGridLayout()
    {
        if (gridRect == null || gridLayout == null || tiles.Count == 0) return;
        Vector2 size = gridRect.rect.size;
        if (size.x < 1f || size.y < 1f) return;
        if (lastTileCount == tiles.Count && (lastGridSize - size).sqrMagnitude < 1f) return;

        lastTileCount = tiles.Count;
        lastGridSize = size;
        int count = tiles.Count;
        int bestColumns = 1;
        Vector2 bestCell = new Vector2(size.x, size.y);
        float bestArea = -1f;
        const float aspect = 16f / 9f;
        const float spacing = 12f;

        for (int columns = 1; columns <= count; columns++)
        {
            int rows = Mathf.CeilToInt((float)count / columns);
            float slotWidth = (size.x - 16f - spacing * (columns - 1)) / columns;
            float slotHeight = (size.y - 16f - spacing * (rows - 1)) / rows;
            if (slotWidth <= 0f || slotHeight <= 0f) continue;

            float cellWidth = Mathf.Min(slotWidth, slotHeight * aspect);
            float cellHeight = cellWidth / aspect;
            float area = cellWidth * cellHeight;
            if (area > bestArea)
            {
                bestArea = area;
                bestColumns = columns;
                bestCell = new Vector2(cellWidth, cellHeight);
            }
        }

        gridLayout.constraintCount = bestColumns;
        gridLayout.cellSize = bestCell;
    }

    private static GameObject CreateRect(string name, Transform parent)
    {
        var result = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
        result.transform.SetParent(parent, false);
        return result;
    }

    private void RefreshHeader()
    {
        if (connectionText != null)
        {
            connectionText.text = connected
                ? "CONNECTED"
                : sdkReady ? "READY TO JOIN" : "NOT INITIALIZED";
            connectionText.color = connected
                ? LiveKitMeetingStyle.TextPrimary
                : LiveKitMeetingStyle.TextSecondary;
        }
        if (connectionDot != null)
        {
            connectionDot.color = connected
                ? LiveKitMeetingStyle.Success
                : sdkReady ? LiveKitMeetingStyle.Warning : LiveKitMeetingStyle.TextSecondary;
        }
        if (participantCountText != null)
        {
            int count = tiles.Count;
            participantCountText.text = count == 1
                ? "1 PARTICIPANT"
                : $"{count} PARTICIPANTS";
        }
    }

    private static TMP_Text CreateText(
        string name,
        Transform parent,
        float fontSize,
        FontStyles style)
    {
        GameObject textObject = CreateRect(name, parent);
        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.raycastTarget = false;
        text.enableAutoSizing = true;
        text.fontSizeMin = Mathf.Max(9f, fontSize * 0.58f);
        text.fontSizeMax = fontSize;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    private static void SetAnchoredRect(
        RectTransform rect,
        float minX,
        float minY,
        float maxX,
        float maxY,
        float left,
        float bottom,
        float right,
        float top)
    {
        rect.anchorMin = new Vector2(minX, minY);
        rect.anchorMax = new Vector2(maxX, maxY);
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(right, top);
    }
}
