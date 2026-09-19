using System;
using System.Collections.Generic;
using Jorjin.Streaming;
using LiveKit;
using LiveKit.Proto;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class JorjinStreamingAdvancedSample : MonoBehaviour
{
    [Header("SDK")]
    public JorjinStreamingSDK streamingSDK;
    [SerializeField] private string liveKitUrl = "wss://bb-afbn6bvt.livekit.cloud";

    [Header("Connection")]
    public TMP_InputField appIdInput;
    public TMP_InputField channelNameInput;
    public TMP_InputField tokenInput;
    public TMP_InputField userIdInput;
    public TMP_InputField renewTokenInput;

    [Header("Video Configuration")]
    public TMP_Dropdown cameraDirectionDropdown;
    public TMP_Dropdown resolutionDropdown;
    public TMP_Dropdown frameRateDropdown;
    public TMP_InputField bitrateInput;
    public Toggle maintainResolutionToggle;
    public TMP_Dropdown remoteStreamDropdown;

    [Header("Audio Configuration")]
    public TMP_Dropdown audioScenarioDropdown;

    [Header("Devices")]
    public TMP_Dropdown recordingDeviceDropdown;
    public TMP_Dropdown playbackDeviceDropdown;
    public TMP_Dropdown videoDeviceDropdown;

    [Header("Video Surfaces")]
    public JJVideoSurface localStreamSurface;
    public JJVideoSurface remoteStreamSurface;
    public RectTransform remoteScrollContent;

    [Header("Output")]
    public TMP_Text statusText;
    public TMP_Text statsText;
    public TMP_Text logText;

    private readonly Queue<string> logs = new();
    private readonly List<JJDeviceInfo> recordingDevices = new();
    private readonly List<JJDeviceInfo> playbackDevices = new();
    private readonly List<JJDeviceInfo> videoDevices = new();
    private readonly Dictionary<uint, JJVideoSurface> remoteSurfaces = new();
    private readonly Dictionary<uint, Vector2Int> remoteVideoSizes = new();
    private readonly Dictionary<string, uint> liveKitIdentityUids = new();

    private static readonly Vector2Int[] Resolutions =
    {
        new(640, 360),
        new(960, 540),
        new(1280, 720),
        new(1920, 1080)
    };

    private static readonly int[] FrameRates = { 15, 24, 30 };
    private static readonly int[] AudioScenarios = { 0, 3, 5, 7, 8 };

    private string appId = "";
    private string channelName = "";
    private string token = "";
    private int userId;
    private uint remoteUid;

    private bool initialized;
    private bool joined;
    private bool previewing;
    private bool audioModuleEnabled = true;
    private bool localAudioEnabled = true;
    private bool localAudioMuted;
    private bool videoModuleEnabled = true;
    private bool localVideoEnabled = true;
    private bool localVideoMuted;
    private bool allRemoteAudioMuted;
    private bool allRemoteVideoMuted;

    private JJConnectionState connectionState = JJConnectionState.DISCONNECTED;
    private int localAudioBitrate;
    private uint remoteAudioStatsUid;
    private int remoteAudioSampleRate;
    private Vector2Int localVideoSize;
    private Vector2Int remoteVideoSize;
    private string pendingLiveKitIdentity;

    private JorjinStreamingSDK StreamingSDK
    {
        get
        {
            if (streamingSDK != null) return streamingSDK;

            streamingSDK = GetComponent<JorjinStreamingSDK>();
            if (streamingSDK == null)
            {
                streamingSDK = gameObject.AddComponent<JorjinStreamingSDK>();
            }

            return streamingSDK;
        }
    }

    private void Awake()
    {
        SetupDropdowns();
        SetupRemoteScrollView();
        BindSurfaceEvents();
        ConfigureLogText();
        RefreshStatus();
        RefreshStats();
    }

    private void Start()
    {
        AddLog("Demo 2 LiveKit ready. Enter Channel / Token / UID, then Initialize. App ID is optional.");
    }

    private void OnDestroy()
    {
        UnbindSurfaceEvents();
        if (initialized)
        {
            StreamingSDK.Dispose();
        }
    }

    public void InitializeSDK()
    {
        ReadConnectionInputs();
        if (!ValidateRequiredInputs()) return;

        if (initialized)
        {
            AddLog("Initialize skipped: SDK is already initialized.");
            return;
        }

        string participantIdentity = userIdInput != null
            ? userIdInput.text.Trim()
            : userId.ToString();
        JJStreamingProfile profile = JJStreamingProfile.CreateLiveKit(
            liveKitUrl,
            channelName,
            participantIdentity,
            token,
            appId);
        var eventHandler = new AdvancedStreamingEventHandler(this);

        StreamingSDK.Initialize(profile, eventHandler);
        StreamingSDK.EnableAudio();
        StreamingSDK.EnableLocalAudio(true);
        StreamingSDK.MuteLocalAudioStream(false);
        StreamingSDK.EnableVideo();
        StreamingSDK.EnableLocalVideo(true);

        initialized = StreamingSDK.HasEngine && StreamingSDK.Initialized;
        audioModuleEnabled = true;
        localAudioEnabled = true;
        localAudioMuted = false;
        videoModuleEnabled = true;
        localVideoEnabled = true;
        localVideoMuted = false;
        connectionState = JJConnectionState.DISCONNECTED;

        AddLog(initialized ? "Initialize success." : "Initialize failed.");
        RefreshDevices();
        RefreshStatus();
    }

    public void DisposeSDK()
    {
        if (previewing) StopPreview();
        StreamingSDK.Dispose();

        initialized = false;
        joined = false;
        previewing = false;
        remoteUid = 0;
        pendingLiveKitIdentity = null;
        liveKitIdentityUids.Clear();
        connectionState = JJConnectionState.DISCONNECTED;
        ResetMediaState();
        SetSurfaceEnabled(localStreamSurface, false);
        ClearRemoteSurfaces();
        AddLog("Dispose SDK.");
        RefreshStatus();
    }

    public void JoinChannel()
    {
        // Keep the advanced sample convenient to use: joining is allowed to
        // perform the prerequisite initialization, just like the meeting UI.
        // This also prevents the screen from getting stuck on
        // "SDK is not initialized" when Join Channel is pressed first.
        if (!initialized || !StreamingSDK.HasEngine || !StreamingSDK.Initialized)
        {
            AddLog("SDK is not initialized; initializing automatically...");
            InitializeSDK();
        }
        if (!EnsureInitialized()) return;
        if (joined)
        {
            AddLog("Join skipped: already joined.");
            return;
        }

        if (previewing)
        {
            StopPreview();
        }

        int result = StreamingSDK.JoinChannel();
        AddResult("JoinChannel", result);
    }

    public void LeaveChannel()
    {
        if (!EnsureInitialized()) return;

        int result = StreamingSDK.LeaveChannel();
        joined = false;
        remoteUid = 0;
        SetSurfaceEnabled(localStreamSurface, false);
        ClearRemoteSurfaces();
        AddResult("LeaveChannel", result);
        RefreshStatus();
    }

    public void StartPreview()
    {
        if (!EnsureInitialized()) return;
        if (joined)
        {
            AddLog("StartPreview is intended for the pre-join workflow. Leave the channel first.");
            return;
        }

        StreamingSDK.EnableVideo();
        StreamingSDK.EnableLocalVideo(true);
        BindLocalSurface("");
        StreamingSDK.StartPreview();

        videoModuleEnabled = true;
        localVideoEnabled = true;
        previewing = true;
        SetSurfaceEnabled(localStreamSurface, true);
        AddLog("StartPreview called.");
        RefreshStatus();
    }

    public void StopPreview()
    {
        if (!EnsureInitialized()) return;

        StreamingSDK.StopPreview();
        previewing = false;
        if (!joined)
        {
            SetSurfaceEnabled(localStreamSurface, false);
        }
        AddLog("StopPreview called.");
        RefreshStatus();
    }

    public void ToggleAudioModule()
    {
        if (!EnsureInitialized()) return;

        audioModuleEnabled = !audioModuleEnabled;
        if (audioModuleEnabled) StreamingSDK.EnableAudio();
        else StreamingSDK.DisableAudio();

        AddLog($"Audio module: {OnOff(audioModuleEnabled)}.");
        RefreshStatus();
    }

    public void ToggleLocalAudioCapture()
    {
        if (!EnsureInitialized()) return;

        localAudioEnabled = !localAudioEnabled;
        StreamingSDK.EnableLocalAudio(localAudioEnabled);
        AddLog($"Local audio capture: {OnOff(localAudioEnabled)}.");
        RefreshStatus();
    }

    public void ToggleLocalAudioPublish()
    {
        if (!EnsureInitialized()) return;

        localAudioMuted = !localAudioMuted;
        StreamingSDK.MuteLocalAudioStream(localAudioMuted);
        AddLog($"Local audio publish: {(localAudioMuted ? "Muted" : "Publishing")}.");
        RefreshStatus();
    }

    public void ToggleVideoModule()
    {
        if (!EnsureInitialized()) return;

        videoModuleEnabled = !videoModuleEnabled;
        if (videoModuleEnabled) StreamingSDK.EnableVideo();
        else StreamingSDK.DisableVideo();

        SetSurfaceEnabled(localStreamSurface, videoModuleEnabled && localVideoEnabled && (joined || previewing));
        AddLog($"Video module: {OnOff(videoModuleEnabled)}.");
        RefreshStatus();
    }

    public void ToggleLocalVideoCapture()
    {
        if (!EnsureInitialized()) return;

        localVideoEnabled = !localVideoEnabled;
        StreamingSDK.EnableLocalVideo(localVideoEnabled);
        SetSurfaceEnabled(localStreamSurface, localVideoEnabled && videoModuleEnabled && (joined || previewing));
        AddLog($"Local camera capture: {OnOff(localVideoEnabled)}.");
        RefreshStatus();
    }

    public void ToggleLocalVideoPublish()
    {
        if (!EnsureInitialized()) return;

        localVideoMuted = !localVideoMuted;
        StreamingSDK.MuteLocalVideoStream(localVideoMuted);
        AddLog($"Local video publish: {(localVideoMuted ? "Muted" : "Publishing")}.");
        RefreshStatus();
    }

    public void ToggleAllRemoteAudio()
    {
        if (!EnsureInitialized()) return;

        allRemoteAudioMuted = !allRemoteAudioMuted;
        StreamingSDK.MuteAllRemoteAudioStreams(allRemoteAudioMuted);
        AddLog($"All remote audio: {(allRemoteAudioMuted ? "Muted" : "Receiving")}.");
        RefreshStatus();
    }

    public void ToggleAllRemoteVideo()
    {
        if (!EnsureInitialized()) return;

        allRemoteVideoMuted = !allRemoteVideoMuted;
        StreamingSDK.MuteAllRemoteVideoStreams(allRemoteVideoMuted);
        foreach (JJVideoSurface surface in remoteSurfaces.Values)
        {
            SetSurfaceEnabled(surface, !allRemoteVideoMuted);
        }
        AddLog($"All remote video: {(allRemoteVideoMuted ? "Muted" : "Receiving")}.");
        RefreshStatus();
    }

    public void ApplyCameraDirection()
    {
        if (!EnsureInitialized()) return;

        var config = new JJCameraCapturerConfiguration();
        var direction = cameraDirectionDropdown != null && cameraDirectionDropdown.value == 1
            ? JJCameraDirection.REAR
            : JJCameraDirection.FRONT;
        config.cameraDirection.SetValue(direction);

        // Recreate the active capturer so Android applies the new front/rear
        // direction immediately without requiring the user to leave the channel.
        bool restartPreview = previewing;
        bool restartCapture = videoModuleEnabled && localVideoEnabled;

        if (restartPreview)
        {
            StreamingSDK.StopPreview();
        }
        if (restartCapture)
        {
            StreamingSDK.EnableLocalVideo(false);
        }

        int result = StreamingSDK.SetCameraCapturerConfiguration(config);

        if (restartCapture)
        {
            StreamingSDK.EnableLocalVideo(true);
        }
        if (restartPreview)
        {
            BindLocalSurface("");
            StreamingSDK.StartPreview();
        }

        AddResult($"SetCameraCapturerConfiguration({direction})", result);
        if (result == 0 && restartCapture)
        {
            AddLog($"Camera capture restarted with {direction} camera{(joined ? " while streaming" : " for preview")}.");
        }
        RefreshStatus();
    }

    public void ApplyVideoEncoderConfiguration()
    {
        if (!EnsureInitialized()) return;

        int resolutionIndex = Mathf.Clamp(resolutionDropdown != null ? resolutionDropdown.value : 1, 0, Resolutions.Length - 1);
        int frameRateIndex = Mathf.Clamp(frameRateDropdown != null ? frameRateDropdown.value : 0, 0, FrameRates.Length - 1);
        int bitrate = -1;
        if (bitrateInput != null && !string.IsNullOrWhiteSpace(bitrateInput.text) &&
            !int.TryParse(bitrateInput.text, out bitrate))
        {
            AddLog("Encoder config failed: bitrate must be an integer.");
            return;
        }

        Vector2Int resolution = Resolutions[resolutionIndex];
        var config = new JJVideoEncoderConfiguration
        {
            width = resolution.x,
            height = resolution.y,
            frameRate = FrameRates[frameRateIndex],
            bitrate = bitrate,
            maintainResolution = maintainResolutionToggle != null && maintainResolutionToggle.isOn
        };

        int result = StreamingSDK.SetVideoEncoderConfiguration(config);
        AddResult($"SetVideoEncoderConfiguration({config.width}x{config.height}, {config.frameRate}fps, bitrate={config.bitrate})", result);
    }

    public void ApplyRemoteStreamType()
    {
        if (!EnsureInitialized()) return;
        if (remoteSurfaces.Count == 0)
        {
            AddLog("Remote stream type failed: no remote user is connected.");
            return;
        }

        JJVideoStreamType streamType = remoteStreamDropdown != null && remoteStreamDropdown.value == 1
            ? JJVideoStreamType.LOW
            : JJVideoStreamType.HIGH;
        foreach (uint uid in remoteSurfaces.Keys)
        {
            int result = StreamingSDK.SetRemoteVideoStreamType(uid, streamType);
            AddResult($"SetRemoteVideoStreamType(uid={uid}, {streamType})", result);
        }
    }

    public void ApplyAudioScenario()
    {
        if (!EnsureInitialized()) return;

        int index = Mathf.Clamp(audioScenarioDropdown != null ? audioScenarioDropdown.value : 0, 0, AudioScenarios.Length - 1);
        int scenario = AudioScenarios[index];
        int result = StreamingSDK.SetAudioScenario(scenario);
        AddResult($"SetAudioScenario({scenario})", result);
    }

    public void RefreshDevices()
    {
        if (!EnsureInitialized()) return;

        ReplaceDevices(recordingDevices, StreamingSDK.GetRecordingDevices());
        ReplaceDevices(playbackDevices, StreamingSDK.GetPlaybackDevices());
        ReplaceDevices(videoDevices, StreamingSDK.GetVideoDevices());

        PopulateDeviceDropdown(recordingDeviceDropdown, recordingDevices, "No recording device");
        PopulateDeviceDropdown(playbackDeviceDropdown, playbackDevices, "No playback device");
        PopulateDeviceDropdown(videoDeviceDropdown, videoDevices, "No camera device");

        AddLog($"Devices refreshed: recording={recordingDevices.Count}, playback={playbackDevices.Count}, video={videoDevices.Count}.");
    }

    public void ApplySelectedDevices()
    {
        if (!EnsureInitialized()) return;

        ApplyDevice("SetRecordingDevice", recordingDeviceDropdown, recordingDevices, StreamingSDK.SetRecordingDevice);
        ApplyDevice("SetPlaybackDevice", playbackDeviceDropdown, playbackDevices, StreamingSDK.SetPlaybackDevice);
        ApplyDevice("SetVideoDevice", videoDeviceDropdown, videoDevices, StreamingSDK.SetVideoDevice);
    }

    public void RenewToken()
    {
        if (!EnsureInitialized()) return;

        string newToken = renewTokenInput != null ? renewTokenInput.text.Trim() : "";
        if (string.IsNullOrWhiteSpace(newToken))
        {
            AddLog("RenewToken failed: new token is required.");
            return;
        }

        int result = StreamingSDK.RenewToken(newToken);
        AddResult("RenewToken", result);
    }

    private void SetupDropdowns()
    {
        SetOptions(cameraDirectionDropdown, "Front", "Rear");
        SetOptions(resolutionDropdown, "640 x 360", "960 x 540", "1280 x 720", "1920 x 1080");
        SetOptions(frameRateDropdown, "15 FPS", "24 FPS", "30 FPS");
        SetOptions(remoteStreamDropdown, "High Stream", "Low Stream");
        SetOptions(audioScenarioDropdown, "Default (0)", "Game Streaming (3)", "Chatroom (5)", "Chorus (7)", "Meeting (8)");

        if (resolutionDropdown != null) resolutionDropdown.value = 1;
        if (bitrateInput != null && string.IsNullOrWhiteSpace(bitrateInput.text)) bitrateInput.text = "-1";
    }

    private void ReadConnectionInputs()
    {
        appId = appIdInput != null ? appIdInput.text.Trim() : "";
        channelName = channelNameInput != null ? channelNameInput.text.Trim() : "";
        token = tokenInput != null ? tokenInput.text.Trim() : "";

        if (userIdInput == null || !int.TryParse(userIdInput.text.Trim(), out userId))
        {
            userId = -1;
        }
    }

    private bool ValidateRequiredInputs()
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            AddLog("Initialize failed: Channel is required.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            AddLog("Initialize failed: Token is required.");
            return false;
        }
        if (userId < 0)
        {
            AddLog("Initialize failed: UID must be a non-negative integer.");
            return false;
        }

        return true;
    }

    private void SetupRemoteScrollView()
    {
        if (remoteStreamSurface == null || remoteScrollContent == null)
        {
            Debug.LogError("[StreamingAdvancedSample] Remote ScrollView Content or Remote Stream Template is not assigned.");
            return;
        }

        remoteStreamSurface.gameObject.SetActive(false);
    }

    private static void AddRemoteUidLabel(Transform item, uint uid)
    {
        var labelObject = new GameObject("UID Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(item, false);
        RectTransform rect = labelObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -6f);
        rect.sizeDelta = new Vector2(-12f, 28f);

        TMP_Text label = labelObject.GetComponent<TMP_Text>();
        label.text = $"UID {uid}";
        label.fontSize = 16f;
        label.fontStyle = FontStyles.Bold;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.raycastTarget = false;
    }

    private bool EnsureInitialized()
    {
        if (initialized && StreamingSDK.HasEngine) return true;
        AddLog("SDK is not initialized.");
        return false;
    }

    private void BindSurfaceEvents()
    {
        if (localStreamSurface != null) localStreamSurface.OnTextureSizeModify += OnLocalTextureSizeChanged;
    }

    private void UnbindSurfaceEvents()
    {
        if (localStreamSurface != null) localStreamSurface.OnTextureSizeModify -= OnLocalTextureSizeChanged;
    }

    private void BindLocalSurface(string surfaceChannel)
    {
        if (localStreamSurface == null)
        {
            AddLog("Local stream surface is not assigned.");
            return;
        }

        if (StreamingSDK.Backend == JJStreamingBackend.LiveKit)
        {
            localStreamSurface.SetEnable(true);
            return;
        }

        if (string.IsNullOrEmpty(surfaceChannel))
        {
            localStreamSurface.SetForUser(0);
        }
        else
        {
            localStreamSurface.SetForUser(0, surfaceChannel);
        }
        localStreamSurface.SetEnable(true);
    }

    private void BindRemoteSurface(JJRtcConnection connection, uint uid)
    {
        remoteUid = uid;
        if (remoteSurfaces.TryGetValue(uid, out JJVideoSurface existingSurface))
        {
            if (StreamingSDK.Backend != JJStreamingBackend.LiveKit)
            {
                existingSurface.SetForRemoteUser(uid, connection.ChannelId);
            }
            existingSurface.SetEnable(!allRemoteVideoMuted);
            return;
        }

        if (remoteStreamSurface == null || remoteScrollContent == null)
        {
            AddLog("Remote stream template or ScrollView content is not assigned.");
            return;
        }

        GameObject item = Instantiate(remoteStreamSurface.gameObject, remoteScrollContent);
        item.name = $"Remote Stream {uid}";
        item.SetActive(true);

        JJVideoSurface surface = item.GetComponent<JJVideoSurface>();
        if (StreamingSDK.Backend != JJStreamingBackend.LiveKit)
        {
            surface.SetForRemoteUser(uid, connection.ChannelId);
        }
        surface.OnTextureSizeModify += (width, height) => OnRemoteTextureSizeChanged(uid, width, height);
        surface.SetEnable(!allRemoteVideoMuted);

        AddRemoteUidLabel(item.transform, uid);
        remoteSurfaces.Add(uid, surface);
        remoteVideoSizes[uid] = Vector2Int.zero;
        RefreshStatus();
        RefreshStats();
    }

    private void BindLiveKitRemoteSurface(
        string participantIdentity,
        RemoteVideoTrack track)
    {
        if (track == null || string.IsNullOrWhiteSpace(participantIdentity)) return;
        if (!liveKitIdentityUids.TryGetValue(participantIdentity, out uint uid))
        {
            AddLog($"LiveKit video waiting for participant UID: {participantIdentity}.");
            return;
        }
        if (!remoteSurfaces.TryGetValue(uid, out JJVideoSurface surface))
        {
            BindRemoteSurface(
                new JJRtcConnection(channelName, (uint)Mathf.Max(0, userId)),
                uid);
            remoteSurfaces.TryGetValue(uid, out surface);
        }
        if (surface == null) return;
        surface.SetForLiveKitRemote(track);
        surface.SetEnable(!allRemoteVideoMuted);
    }

    private void OnLocalTextureSizeChanged(int width, int height)
    {
        localVideoSize = new Vector2Int(width, height);
        RefreshStats();
    }

    private void OnRemoteTextureSizeChanged(uint uid, int width, int height)
    {
        remoteUid = uid;
        remoteVideoSize = new Vector2Int(width, height);
        remoteVideoSizes[uid] = remoteVideoSize;
        RefreshStats();
    }

    private void RemoveRemoteSurface(uint uid)
    {
        if (remoteSurfaces.TryGetValue(uid, out JJVideoSurface surface))
        {
            surface.SetEnable(false);
            Destroy(surface.gameObject);
            remoteSurfaces.Remove(uid);
        }

        remoteVideoSizes.Remove(uid);
        remoteUid = 0;
        foreach (uint remainingUid in remoteSurfaces.Keys)
        {
            remoteUid = remainingUid;
            break;
        }
        RefreshStatus();
        RefreshStats();
    }

    private void ClearRemoteSurfaces()
    {
        foreach (JJVideoSurface surface in remoteSurfaces.Values)
        {
            if (surface == null) continue;
            surface.SetEnable(false);
            Destroy(surface.gameObject);
        }
        remoteSurfaces.Clear();
        remoteVideoSizes.Clear();
        liveKitIdentityUids.Clear();
        pendingLiveKitIdentity = null;
        remoteUid = 0;
        remoteVideoSize = Vector2Int.zero;
    }

    private void ResetMediaState()
    {
        audioModuleEnabled = true;
        localAudioEnabled = true;
        localAudioMuted = false;
        videoModuleEnabled = true;
        localVideoEnabled = true;
        localVideoMuted = false;
        allRemoteAudioMuted = false;
        allRemoteVideoMuted = false;
    }

    private void RefreshStatus()
    {
        if (statusText == null) return;

        statusText.text =
            $"Initialized: {initialized}    Joined: {joined}    Preview: {previewing}\n" +
            $"Connection: {connectionState}    Remote Users: {FormatRemoteUsers()}\n" +
            $"Audio Module: {OnOff(audioModuleEnabled)}    Mic: {OnOff(localAudioEnabled)}    Publish: {(localAudioMuted ? "Muted" : "On")}\n" +
            $"Video Module: {OnOff(videoModuleEnabled)}    Camera: {OnOff(localVideoEnabled)}    Publish: {(localVideoMuted ? "Muted" : "On")}\n" +
            $"Remote Audio: {(allRemoteAudioMuted ? "Muted" : "Receiving")}    Remote Video: {(allRemoteVideoMuted ? "Muted" : "Receiving")}";
    }

    private void RefreshStats()
    {
        if (statsText == null) return;

        statsText.text =
            $"Local Audio Bitrate: {localAudioBitrate} kbps\n" +
            $"Remote Audio: uid={(remoteAudioStatsUid == 0 ? "-" : remoteAudioStatsUid.ToString())}, {remoteAudioSampleRate} Hz\n" +
            $"Local Video Size: {FormatSize(localVideoSize)}\n" +
            $"Remote Video Sizes: {FormatRemoteVideoSizes()}";
    }

    private void AddResult(string api, int result)
    {
        AddLog($"{api} result: {result}");
    }

    private void AddLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Debug.Log($"[StreamingAdvancedSample] {message}");
        logs.Enqueue(line);
        while (logs.Count > 24) logs.Dequeue();
        RefreshLog();
    }

    private void ConfigureLogText()
    {
        if (logText == null) return;

        logText.alignment = TextAlignmentOptions.TopLeft;
        logText.textWrappingMode = TextWrappingModes.Normal;
        logText.overflowMode = TextOverflowModes.Truncate;
    }

    private void RefreshLog()
    {
        if (logText == null) return;

        // Keep chronological order, but only show the newest entries that fit in the box.
        string[] lines = logs.ToArray();
        string visibleText = "";
        float availableWidth = logText.rectTransform.rect.width;
        float availableHeight = logText.rectTransform.rect.height;

        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string candidate = string.IsNullOrEmpty(visibleText)
                ? lines[index]
                : lines[index] + "\n" + visibleText;

            if (logText.GetPreferredValues(candidate, availableWidth, 0).y > availableHeight)
            {
                break;
            }

            visibleText = candidate;
        }

        logText.text = visibleText;
    }

    private static void ReplaceDevices(List<JJDeviceInfo> target, JJDeviceInfo[] source)
    {
        target.Clear();
        if (source != null) target.AddRange(source);
    }

    private static void PopulateDeviceDropdown(TMP_Dropdown dropdown, List<JJDeviceInfo> devices, string emptyText)
    {
        if (dropdown == null) return;

        dropdown.ClearOptions();
        var options = new List<string>();
        if (devices.Count == 0)
        {
            options.Add(emptyText);
        }
        else
        {
            foreach (JJDeviceInfo device in devices)
            {
                options.Add(string.IsNullOrWhiteSpace(device.deviceName) ? device.deviceId : device.deviceName);
            }
        }

        dropdown.AddOptions(options);
        dropdown.value = 0;
        dropdown.RefreshShownValue();
    }

    private void ApplyDevice(string api, TMP_Dropdown dropdown, List<JJDeviceInfo> devices, Func<string, int> setter)
    {
        if (dropdown == null || devices.Count == 0)
        {
            AddLog($"{api} skipped: no device is available on this platform.");
            return;
        }

        int index = Mathf.Clamp(dropdown.value, 0, devices.Count - 1);
        JJDeviceInfo device = devices[index];
        int result = setter(device.deviceId);
        AddResult($"{api}({device.deviceName})", result);
    }

    private static void SetOptions(TMP_Dropdown dropdown, params string[] options)
    {
        if (dropdown == null) return;
        dropdown.ClearOptions();
        dropdown.AddOptions(new List<string>(options));
        dropdown.RefreshShownValue();
    }

    private static void SetSurfaceEnabled(JJVideoSurface surface, bool enabled)
    {
        if (surface != null) surface.SetEnable(enabled);
    }

    private static string OnOff(bool value) => value ? "On" : "Off";
    private static string FormatSize(Vector2Int value) => value.x > 0 && value.y > 0 ? $"{value.x} x {value.y}" : "-";

    private string FormatRemoteUsers()
    {
        if (remoteSurfaces.Count == 0) return "-";
        return string.Join(", ", remoteSurfaces.Keys);
    }

    private string FormatRemoteVideoSizes()
    {
        if (remoteVideoSizes.Count == 0) return "-";

        var values = new List<string>();
        foreach (KeyValuePair<uint, Vector2Int> entry in remoteVideoSizes)
        {
            values.Add($"{entry.Key}={FormatSize(entry.Value)}");
        }
        return string.Join(", ", values);
    }

    private sealed class AdvancedStreamingEventHandler : StreamingEventHandler
    {
        private readonly JorjinStreamingAdvancedSample sample;

        public AdvancedStreamingEventHandler(JorjinStreamingAdvancedSample sample)
        {
            this.sample = sample;
        }

        public override void OnError(int err, string msg)
        {
            sample.AddLog($"OnError err={err}, msg={msg}");
        }

        public override void OnJoinChannelSuccess(JJRtcConnection connection, int elapsed)
        {
            sample.joined = true;
            sample.connectionState = JJConnectionState.CONNECTED;
            sample.BindLocalSurface(connection.ChannelId);
            sample.AddLog($"OnJoinChannelSuccess channel={connection.ChannelId}, uid={connection.LocalUid}, elapsed={elapsed}");
            sample.RefreshStatus();
        }

        public override void OnRejoinChannelSuccess(JJRtcConnection connection, int elapsed)
        {
            sample.joined = true;
            sample.connectionState = JJConnectionState.CONNECTED;
            sample.AddLog($"OnRejoinChannelSuccess channel={connection.ChannelId}, uid={connection.LocalUid}, elapsed={elapsed}");
            sample.RefreshStatus();
        }

        public override void OnLeaveChannel(JJRtcConnection connection, JJRtcStats stats)
        {
            sample.joined = false;
            sample.connectionState = JJConnectionState.DISCONNECTED;
            sample.AddLog($"OnLeaveChannel channel={connection.ChannelId}, uid={connection.LocalUid}");
            sample.RefreshStatus();
        }

        public override void OnConnectionStateChanged(JJRtcConnection connection, JJConnectionState state, JJConnectionChangedReason reason)
        {
            sample.connectionState = state;
            sample.AddLog($"OnConnectionStateChanged state={state}, reason={reason}");
            sample.RefreshStatus();
        }

        public override void OnConnectionLost(JJRtcConnection connection)
        {
            sample.connectionState = JJConnectionState.RECONNECTING;
            sample.AddLog($"OnConnectionLost channel={connection.ChannelId}, uid={connection.LocalUid}");
            sample.RefreshStatus();
        }

        public override void OnLiveKitConnected(
            string roomName,
            string localIdentity)
        {
            sample.joined = true;
            sample.connectionState = JJConnectionState.CONNECTED;
            sample.AddLog(
                $"LiveKit connected room={roomName}, identity={localIdentity}");
            sample.RefreshStatus();
        }

        public override void OnLiveKitDisconnected(string roomName)
        {
            sample.joined = false;
            sample.connectionState = JJConnectionState.DISCONNECTED;
            sample.ClearRemoteSurfaces();
            sample.AddLog($"LiveKit disconnected room={roomName}");
            sample.RefreshStatus();
        }

        public override void OnLiveKitParticipantConnected(string identity)
        {
            sample.pendingLiveKitIdentity = identity;
            sample.AddLog($"LiveKit participant joined: {identity}");
        }

        public override void OnLiveKitParticipantDisconnected(string identity)
        {
            if (sample.liveKitIdentityUids.TryGetValue(identity, out uint uid))
            {
                sample.liveKitIdentityUids.Remove(identity);
                sample.RemoveRemoteSurface(uid);
            }
            sample.AddLog($"LiveKit participant left: {identity}");
        }

        public override void OnLiveKitLocalVideoTexture(Texture texture)
        {
            if (sample.localStreamSurface == null || texture == null) return;
            sample.localStreamSurface.SetForLiveKitLocal(texture);
            sample.localStreamSurface.SetEnable(
                sample.videoModuleEnabled && sample.localVideoEnabled);
            sample.OnLocalTextureSizeChanged(texture.width, texture.height);
        }

        public override void OnLiveKitVideoTrackSubscribed(
            RemoteVideoTrack track,
            string participantIdentity,
            TrackSource source)
        {
            sample.BindLiveKitRemoteSurface(participantIdentity, track);
            sample.AddLog(
                $"LiveKit video subscribed: {participantIdentity}, source={source}");
        }

        public override void OnLiveKitVideoTrackUnsubscribed(
            RemoteVideoTrack track,
            string participantIdentity,
            TrackSource source)
        {
            if (sample.liveKitIdentityUids.TryGetValue(
                    participantIdentity,
                    out uint uid) &&
                sample.remoteSurfaces.TryGetValue(uid, out JJVideoSurface surface))
            {
                surface.ClearLiveKitVideo();
                surface.SetEnable(false);
            }
        }

        public override void OnUserJoined(JJRtcConnection connection, uint uid, int elapsed)
        {
            if (!string.IsNullOrWhiteSpace(sample.pendingLiveKitIdentity))
            {
                sample.liveKitIdentityUids[sample.pendingLiveKitIdentity] = uid;
                sample.pendingLiveKitIdentity = null;
            }
            sample.BindRemoteSurface(connection, uid);
            sample.AddLog($"OnUserJoined uid={uid}, elapsed={elapsed}");
            sample.RefreshStatus();
        }

        public override void OnUserOffline(JJRtcConnection connection, uint uid, JJUserOfflineReason reason)
        {
            sample.AddLog($"OnUserOffline uid={uid}, reason={reason}");
            sample.RemoveRemoteSurface(uid);
        }

        public override void OnUserMuteAudio(JJRtcConnection connection, uint remoteUid, bool muted)
        {
            sample.AddLog($"OnUserMuteAudio uid={remoteUid}, muted={muted}");
        }

        public override void OnUserMuteVideo(JJRtcConnection connection, uint remoteUid, bool muted)
        {
            if (sample.remoteSurfaces.TryGetValue(remoteUid, out JJVideoSurface surface))
            {
                SetSurfaceEnabled(surface, !muted && !sample.allRemoteVideoMuted);
            }
            sample.AddLog($"OnUserMuteVideo uid={remoteUid}, muted={muted}");
        }

        public override void OnRemoteVideoStateChanged(
            JJRtcConnection connection,
            uint remoteUid,
            JJRemoteVideoState state,
            JJRemoteVideoStateReason reason,
            int elapsed)
        {
            if (sample.remoteSurfaces.TryGetValue(remoteUid, out JJVideoSurface surface))
            {
                if (reason == JJRemoteVideoStateReason.REMOTE_MUTED ||
                    reason == JJRemoteVideoStateReason.REMOTE_OFFLINE)
                {
                    SetSurfaceEnabled(surface, false);
                }
                else if ((state == JJRemoteVideoState.STARTING || state == JJRemoteVideoState.DECODING) &&
                         !sample.allRemoteVideoMuted)
                {
                    SetSurfaceEnabled(surface, true);
                }
            }
            sample.AddLog($"OnRemoteVideoStateChanged uid={remoteUid}, state={state}, reason={reason}");
        }

        public override void OnLocalVideoStateChanged(
            JJVideoSourceType source,
            JJLocalVideoStreamState state,
            JJLocalVideoStreamReason reason)
        {
            sample.AddLog($"OnLocalVideoStateChanged source={source}, state={state}, reason={reason}");
        }

        public override void OnRemoteAudioStats(JJRtcConnection connection, JJRemoteAudioStats stats)
        {
            sample.remoteAudioStatsUid = stats.uid;
            sample.remoteAudioSampleRate = stats.receivedSampleRate;
            sample.RefreshStats();
        }

        public override void OnLocalAudioStats(JJRtcConnection connection, JJLocalAudioStats stats)
        {
            sample.localAudioBitrate = stats.sentBitrate;
            sample.RefreshStats();
        }

        public override void OnTokenPrivilegeWillExpire(JJRtcConnection connection, string expiringToken)
        {
            sample.AddLog("OnTokenPrivilegeWillExpire: enter a new token and press Renew Token.");
        }

        public override void OnRequestToken(JJRtcConnection connection)
        {
            sample.AddLog("OnRequestToken: token expired. Enter a new token and press Renew Token.");
        }
    }
}
