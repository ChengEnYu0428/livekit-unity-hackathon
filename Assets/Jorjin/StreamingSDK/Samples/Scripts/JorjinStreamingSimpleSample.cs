using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jorjin.Streaming;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class JorjinStreamingSimpleSample : MonoBehaviour
{
    [Header("Streaming Settings")]
    [SerializeField] private JorjinStreamingSDK streamingSDK;
    [SerializeField] private string liveKitUrl = "wss://bb-afbn6bvt.livekit.cloud";

    [Header("Company Meeting Bootstrap")]
    [Tooltip("Fetch a company/device meeting assignment and short-lived token at startup.")]
    [SerializeField] private bool enableCompanyMeetingBootstrap;
    [SerializeField] private string meetingBootstrapEndpoint;
    [SerializeField] private string companyId = "jorjin";
    [Tooltip("Optional. Leave empty to use the server-side device assignment.")]
    [SerializeField] private string requestedMeetingGroupId;
    [Tooltip("Optional. SystemInfo.deviceUniqueIdentifier is used when empty.")]
    [SerializeField] private string provisionedDeviceId;
    [SerializeField] private string provisionedDeviceName = "Jorjin AR Glasses";
    [Tooltip("A rotatable device credential. Never put the LiveKit API secret here.")]
    [SerializeField] private string companyDeviceKey;
    [SerializeField] private bool autoJoinAssignedMeeting = true;
    [SerializeField] private bool allowManualJoinFallback = true;
    [SerializeField, Range(5, 60)] private int bootstrapTimeoutSeconds = 15;

    [Header("Advanced Meeting Sessions")]
    [Tooltip(
        "Enable direct expert/group/QR/scheduled meeting routing through the " +
        "application server. This takes priority over the Editor-only token.")]
    [SerializeField] private bool enableAdvancedMeetingSessions;
    [Tooltip(
        "Service root such as https://meeting.example.com. When empty, the " +
        "Company Meeting Bootstrap endpoint above is used as the service root.")]
    [SerializeField] private string meetingSessionApiBaseUrl;
    [Tooltip(
        "Set this only when this client represents a signed-in expert/member. " +
        "Leave empty for an AR device client.")]
    [SerializeField] private string localMemberId;
    [SerializeField] private JJMeetingSessionController meetingSessionController;

    [Header("Unity Editor Automatic Token")]
    [Tooltip("In Unity Editor, create a short-lived token from local Editor preferences when Play starts.")]
    [SerializeField] private bool enableEditorAutomaticToken = true;

    [Header("Input")]
    [SerializeField] private TMP_InputField appIdInput;
    [SerializeField] private TMP_InputField channelNameInput;
    [SerializeField] private TMP_InputField tokenInput;
    [SerializeField] private TMP_InputField userIdInput;

    [Header("Buttons")]
    [SerializeField] private Button initializeButton;
    [SerializeField] private Button disposeButton;
    [SerializeField] private Button joinChannelButton;
    [SerializeField] private Button leaveChannelButton;
    [SerializeField] private Button toggleAudioButton;
    [SerializeField] private Button toggleVideoButton;
    [SerializeField] private Button shareScreenButton;

    [Header("Video Surfaces")]
    [SerializeField] private JJVideoSurface localStreamSurface;
    [SerializeField] private JJVideoSurface remoteStreamSurface;

    [Header("Output")]
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text logText;

    private readonly Queue<string> logs = new();

    private string appId = "";
    private string channelName = "test";
    private string token = "";
    private int userId;
    private bool initialized;
    private bool joined;
    private bool audioMuted;
    private bool localVideoEnabled = true;
    private bool screenSharing;
    private bool previewEnabled;
    private bool frontCameraSelected;
    private bool remoteAudioMuted;
    private bool remoteVideoMuted;
    private bool remoteLowQuality;
    private bool pcmObserverEnabled;
    private string selectedMeetingMicrophoneDevice;
    private bool audioModuleEnabled = true;
    private bool microphoneCaptureEnabled = true;
    private bool videoModuleEnabled = true;
    private bool localVideoPublishMuted;
    private int videoProfileIndex;
    private int receivedPcmFrameCount;
    private int localAudioBitrate;
    private uint remoteAudioStatsUid;
    private int remoteAudioSampleRate;
    private Vector2Int localVideoSize;
    private readonly HashSet<uint> remoteUids = new();
    private static readonly JJVideoEncoderConfiguration[] VideoProfiles =
    {
        new()
        {
            width = 1280,
            height = 720,
            frameRate = 30,
            bitrate = 1500,
            maintainResolution = true
        },
        new()
        {
            width = 960,
            height = 540,
            frameRate = 24,
            bitrate = 1000,
            maintainResolution = true
        },
        new()
        {
            width = 640,
            height = 360,
            frameRate = 15,
            bitrate = 600,
            maintainResolution = true
        }
    };
    private static readonly string[] VideoProfileLabels =
        { "720P", "540P", "360P" };
    private string localIdentity;
    private LiveKitMeetingView meetingView;
    private LiveKitDemo2View demo2View;
    private LiveKitCollaborationPanel collaborationPanel;
    private LiveKitLiveTranslationView liveTranslation;
    private const string MeetingRoleKey = "jorjin.meeting.role";
    private string meetingRole = "field";
    private readonly Dictionary<string, string> participantRoles = new();
    private Button fieldRoleButton, expertRoleButton;
    // Join-screen UI (token inputs, buttons, log) hidden while in a meeting.
    private readonly List<GameObject> joinScreenObjects = new();
    private readonly List<TranscriptEntry> transcriptEntries = new();
    private readonly HashSet<int> transcriptSequences = new();
    private bool transcriptAgentReady;
    private bool transcriptRecording;
    private bool transcriptRequestPending;
    private string transcriptSessionId;
    private int transcriptCharacterCount;
    private FileStream recordingTransferStream;
    private string recordingTransferSessionId;
    private string recordingTempPath;
    private string recordingFinalPath;
    private string recordingExpectedSha256;
    private long recordingExpectedBytes;
    private long recordingReceivedBytes;
    private int recordingExpectedChunks;
    private int recordingReceivedChunks;
    private Coroutine companyBootstrapCoroutine;
    private string assignedCompanyName;
    private string assignedGroupName;
    private bool meetingSessionEventsBound;
    private string incomingSessionFingerprint;
    private Coroutine advancedMeetingInitializationCoroutine;
    private bool advancedMeetingSessionsInitialized;
    private bool startHasRun;

    private JorjinStreamingSDK StreamingSDK
    {
        get
        {
            if (streamingSDK != null)
            {
                return streamingSDK;
            }

            streamingSDK = GetComponent<JorjinStreamingSDK>();
            if (streamingSDK == null)
            {
                streamingSDK = gameObject.AddComponent<JorjinStreamingSDK>();
            }

            return streamingSDK;
        }
    }

    public JJMeetingSessionController MeetingSessionController
    {
        get
        {
            if (!enableAdvancedMeetingSessions)
            {
                return null;
            }
            if (meetingSessionController == null)
            {
                SetupMeetingSessionController();
            }
            return meetingSessionController;
        }
    }

    private void Awake()
    {
        LiveKitChineseFont.EnsureFallback();
        meetingRole = PlayerPrefs.GetString(MeetingRoleKey, "field") == "expert" ? "expert" : "field";
        SetupLiveKitMeetingView();
        collaborationPanel = gameObject.AddComponent<LiveKitCollaborationPanel>();
        collaborationPanel.Configure(
            (action, requestId, question) => StreamingSDK.SendCollaborationRequest(action, transcriptSessionId, requestId, question),
            () => transcriptSessionId,
            () => joined && StreamingSDK.IsConnected);
        liveTranslation = gameObject.AddComponent<LiveKitLiveTranslationView>();
        liveTranslation.Configure(
            state => StreamingSDK.SendCollaborationRequest("translate", "", Guid.NewGuid().ToString("N"), state),
            () => joined && StreamingSDK.IsConnected,
            () => transcriptRecording);
        collaborationPanel.ConfigureTranslation(
            () => liveTranslation.Enabled,
            liveTranslation.Toggle,
            liveTranslation.SetWorkspaceMode);
        collaborationPanel.ConfigurePhoto(
            (jpeg, requestId, sent, instruction) => StreamingSDK.SendCollaborationImage(jpeg, requestId, sent, instruction));
        collaborationPanel.ConfigureMeeting(
            sources => meetingView?.GetCollaborationSources(sources),
            ReadCollaborationMeetingState,
            ToggleAudio,
            ToggleTranscript);
        if (enableAdvancedMeetingSessions)
        {
            SetupMeetingSessionController();
        }
        else if (meetingSessionController != null)
        {
            meetingSessionController.enabled = false;
        }
        ReadInputFields();
        RefreshStatus();
    }

    private void Start()
    {
        startHasRun = true;
        if (enableAdvancedMeetingSessions)
        {
            AddLog("Loading the company expert directory and scheduled meetings...");
            BeginAdvancedMeetingInitialization();
            return;
        }

#if UNITY_EDITOR
        if (enableEditorAutomaticToken &&
            TryApplyEditorAutomaticToken())
        {
            return;
        }
#endif
        if (enableCompanyMeetingBootstrap)
        {
            companyBootstrapCoroutine =
                StartCoroutine(BootstrapCompanyMeeting());
        }
    }

    private void BeginAdvancedMeetingInitialization()
    {
        if (!isActiveAndEnabled ||
            !enableAdvancedMeetingSessions ||
            advancedMeetingInitializationCoroutine != null)
        {
            return;
        }
        JJMeetingSessionController controller = MeetingSessionController;
        if (controller == null) return;
        advancedMeetingInitializationCoroutine =
            StartCoroutine(InitializeAdvancedMeetingSessions(controller));
    }

    private System.Collections.IEnumerator InitializeAdvancedMeetingSessions(
        JJMeetingSessionController controller)
    {
        try
        {
            controller.RefreshDirectory();
            while (isActiveAndEnabled && controller.RequestInProgress)
            {
                yield return null;
            }
            if (!isActiveAndEnabled) yield break;

            controller.RefreshScheduledMeetings();
            while (isActiveAndEnabled && controller.RequestInProgress)
            {
                yield return null;
            }
            if (!isActiveAndEnabled) yield break;

            advancedMeetingSessionsInitialized = true;
            controller.StartIncomingCallPolling();
        }
        finally
        {
            advancedMeetingInitializationCoroutine = null;
        }
    }

#if UNITY_EDITOR
    private bool TryApplyEditorAutomaticToken()
    {
        if (!LiveKitEditorTokenGenerator.TryCreateConnection(
                liveKitUrl,
                channelNameInput != null
                    ? channelNameInput.text
                    : channelName,
                out LiveKitEditorTokenGenerator.Connection connection,
                out string error))
        {
            AddLog(error);
            AddLog("Manual Channel / Token join remains available.");
            return false;
        }

        liveKitUrl = connection.serverUrl;
        channelName = connection.roomName;
        token = connection.participantToken;
        localIdentity = connection.participantIdentity;
        assignedCompanyName = "Unity Editor";
        assignedGroupName = connection.roomName;

        if (channelNameInput != null)
        {
            channelNameInput.text = channelName;
        }
        if (tokenInput != null)
        {
            tokenInput.text = token;
        }
        if (userIdInput != null)
        {
            userIdInput.text = localIdentity;
        }

        AddLog(
            $"Created a short-lived Unity Token for room {channelName}.");
        RefreshStatus();
        if (connection.autoJoin)
        {
            AddLog("Auto-joining from Unity Play mode...");
            JoinChannel();
        }
        return true;
    }
#endif

    private void OnEnable()
    {
        AddButtonListeners();
        if (enableAdvancedMeetingSessions && meetingSessionController != null)
        {
            BindMeetingSessionEvents();
            meetingSessionController.enabled = true;
            if (startHasRun)
            {
                if (advancedMeetingSessionsInitialized)
                {
                    meetingSessionController.StartIncomingCallPolling();
                }
                else
                {
                    BeginAdvancedMeetingInitialization();
                }
            }
        }
        else if (!enableAdvancedMeetingSessions &&
                 meetingSessionController != null)
        {
            meetingSessionController.enabled = false;
        }
        AddLog("LiveKit ready. Fill Channel / Token, then Join.");
    }

    private void OnDisable()
    {
        RemoveButtonListeners();
        DisablePcmObserver();
        if (advancedMeetingInitializationCoroutine != null)
        {
            StopCoroutine(advancedMeetingInitializationCoroutine);
            advancedMeetingInitializationCoroutine = null;
        }
        if (meetingSessionController != null)
        {
            meetingSessionController.StopIncomingCallPolling();
            meetingSessionController.CancelPendingRequests();
            UnbindMeetingSessionEvents();
            meetingSessionController.enabled = false;
        }
    }

    private void OnDestroy()
    {
        DisablePcmObserver();
        AbortRecordingTransfer(true);
        if (companyBootstrapCoroutine != null)
        {
            StopCoroutine(companyBootstrapCoroutine);
            companyBootstrapCoroutine = null;
        }
        if (advancedMeetingInitializationCoroutine != null)
        {
            StopCoroutine(advancedMeetingInitializationCoroutine);
            advancedMeetingInitializationCoroutine = null;
        }
        UnbindMeetingSessionEvents();
        if (initialized)
        {
            StreamingSDK.Dispose();
        }
        if (meetingView != null)
        {
            Destroy(meetingView.gameObject);
            meetingView = null;
        }
    }

    private void SetupMeetingSessionController()
    {
        if (!enableAdvancedMeetingSessions)
        {
            return;
        }
        if (meetingSessionController == null)
        {
            meetingSessionController = GetComponent<JJMeetingSessionController>();
        }
        if (meetingSessionController == null)
        {
            meetingSessionController =
                gameObject.AddComponent<JJMeetingSessionController>();
        }

        string serviceUrl = string.IsNullOrWhiteSpace(meetingSessionApiBaseUrl)
            ? meetingBootstrapEndpoint
            : meetingSessionApiBaseUrl;
        meetingSessionController.Configure(
            serviceUrl,
            companyId,
            companyDeviceKey,
            provisionedDeviceId,
            provisionedDeviceName,
            bootstrapTimeoutSeconds,
            localMemberId);
        BindMeetingSessionEvents();
    }

    private bool TryGetMeetingSessionController(
        out JJMeetingSessionController controller)
    {
        controller = MeetingSessionController;
        if (controller != null)
        {
            return true;
        }
        AddLog(
            "Advanced Meeting Sessions is disabled. Enable it before using " +
            "meeting routing features.");
        return false;
    }

    private void BindMeetingSessionEvents()
    {
        if (meetingSessionController == null || meetingSessionEventsBound) return;
        meetingSessionController.SessionUpdated += HandleMeetingSessionUpdated;
        meetingSessionController.ConnectionReady += HandleMeetingConnectionReady;
        meetingSessionController.ScheduledMeetingsUpdated +=
            HandleScheduledMeetingsUpdated;
        meetingSessionController.IncomingSessionsUpdated +=
            HandleIncomingSessionsUpdated;
        meetingSessionController.DirectoryUpdated += HandleMeetingDirectoryUpdated;
        meetingSessionController.InternalInviteCreated +=
            HandleInternalInviteCreated;
        meetingSessionController.GuestInviteCreated += HandleGuestInviteCreated;
        meetingSessionController.RequestFailed += HandleMeetingSessionError;
        meetingSessionEventsBound = true;
    }

    private void UnbindMeetingSessionEvents()
    {
        if (meetingSessionController == null || !meetingSessionEventsBound) return;
        meetingSessionController.SessionUpdated -= HandleMeetingSessionUpdated;
        meetingSessionController.ConnectionReady -= HandleMeetingConnectionReady;
        meetingSessionController.ScheduledMeetingsUpdated -=
            HandleScheduledMeetingsUpdated;
        meetingSessionController.IncomingSessionsUpdated -=
            HandleIncomingSessionsUpdated;
        meetingSessionController.DirectoryUpdated -= HandleMeetingDirectoryUpdated;
        meetingSessionController.InternalInviteCreated -=
            HandleInternalInviteCreated;
        meetingSessionController.GuestInviteCreated -= HandleGuestInviteCreated;
        meetingSessionController.RequestFailed -= HandleMeetingSessionError;
        meetingSessionEventsBound = false;
    }

    private void HandleMeetingSessionUpdated(JJMeetingSession session)
    {
        if (session == null) return;
        assignedCompanyName = session.company_name;
        assignedGroupName = session.group_name;
        AddLog(
            $"Session {session.session_id} ready: route={session.route_type}, " +
            $"status={session.status}, room={session.room_name}");
        RefreshStatus();
    }

    private void HandleMeetingConnectionReady(JJMeetingSession session)
    {
        if (session == null || !session.HasConnectionData)
        {
            AddLog("Meeting Session did not return valid LiveKit connection data.");
            return;
        }

        if (initialized)
        {
            StreamingSDK.Dispose();
            initialized = false;
            joined = false;
            meetingView?.SetConnected(false);
            SetMeetingLayout(false);
        }

        liveKitUrl = session.server_url;
        channelName = session.room_name;
        token = session.participant_token;
        localIdentity = session.participant_identity;
        if (channelNameInput != null) channelNameInput.text = channelName;
        if (tokenInput != null) tokenInput.text = token;
        if (userIdInput != null) userIdInput.text = localIdentity;

        AddLog(
            $"Joining Session {session.session_id} as {localIdentity}...");
        JoinChannel();
    }

    private void HandleScheduledMeetingsUpdated(JJMeetingSession[] sessions)
    {
        int count = sessions?.Length ?? 0;
        AddLog($"Scheduled meetings available: {count}");
        for (int index = 0; index < count && index < 5; index++)
        {
            JJMeetingSession session = sessions[index];
            AddLog(
                $"  {session.scheduled_start_utc} | {session.title} | " +
                $"{session.session_id}");
        }
    }

    private void HandleIncomingSessionsUpdated(JJMeetingSession[] sessions)
    {
        int count = sessions?.Length ?? 0;
        var fingerprint = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            fingerprint.Append(sessions[index]?.session_id).Append('|');
        }
        string nextFingerprint = fingerprint.ToString();
        if (string.Equals(
                incomingSessionFingerprint,
                nextFingerprint,
                StringComparison.Ordinal))
        {
            return;
        }
        incomingSessionFingerprint = nextFingerprint;
        AddLog($"Incoming meeting calls: {count}");
        for (int index = 0; index < count && index < 5; index++)
        {
            JJMeetingSession session = sessions[index];
            AddLog(
                $"  {session.title} | from={session.caller_member_id} | " +
                $"{session.session_id}");
        }
    }

    private void HandleMeetingDirectoryUpdated(
        JJMeetingDirectoryResponse directory)
    {
        AddLog(
            $"Meeting directory loaded: " +
            $"{directory?.members?.Length ?? 0} members, " +
            $"{directory?.groups?.Length ?? 0} groups.");
    }

    private void HandleInternalInviteCreated(
        JJMeetingInternalInviteResponse invite)
    {
        AddLog(
            $"Internal invitation created for " +
            $"{invite?.invited_member_ids?.Length ?? 0} member(s); " +
            $"notification={invite?.notification_status}.");
    }

    private void HandleGuestInviteCreated(JJMeetingGuestInviteResponse invite)
    {
        if (invite == null || string.IsNullOrWhiteSpace(invite.guest_url))
        {
            AddLog("Guest invitation response did not include a link.");
            return;
        }
        GUIUtility.systemCopyBuffer = invite.guest_url;
        AddLog(
            $"Guest link copied to clipboard; expires {invite.expires_at_utc}.");
    }

    private void HandleMeetingSessionError(string message)
    {
        AddLog("Meeting Session error: " + message);
    }

    private void RefreshLiveKitToken()
    {
        if (meetingSessionController != null &&
            meetingSessionController.ActiveSession != null)
        {
            AddLog("Requesting a fresh Session token from the application server...");
            meetingSessionController.RefreshActiveSessionConnection();
            return;
        }

        if (enableCompanyMeetingBootstrap &&
            companyBootstrapCoroutine == null)
        {
            // The old bootstrap endpoint also issues a new short-lived token.
            // Recreate the LiveKit client so the new token is used to connect.
            if (initialized)
            {
                StreamingSDK.Dispose();
                initialized = false;
                joined = false;
                meetingView?.SetConnected(false);
                SetMeetingLayout(false);
            }
            companyBootstrapCoroutine =
                StartCoroutine(BootstrapCompanyMeeting());
            return;
        }

        AddLog(
            "Automatic token refresh is unavailable. Configure the Meeting " +
            "Session service or Company Meeting Bootstrap endpoint.");
    }

    /// <summary>
    /// Starts a new one-to-one Session and asks the application server to
    /// notify the selected expert. The returned short-lived LiveKit token is
    /// applied automatically.
    /// </summary>
    public void StartDirectExpertCall(string expertId)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.StartDirectExpertCall(expertId);
    }

    public void StartDirectDeviceCall(
        string targetDeviceId,
        string callerMemberId)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.StartDirectDeviceCall(
            targetDeviceId,
            callerMemberId);
    }

    /// <summary>
    /// Starts a unique Session routed to the configured expert/support group.
    /// This is different from keeping every field device in one permanent room.
    /// </summary>
    public void StartExpertGroupCall(string groupId)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.StartExpertGroupCall(groupId);
    }

    /// <summary>
    /// Entry point for the AR-glasses QR scanner. Pass the complete scanned
    /// opaque/signed payload here; never encode a LiveKit secret in the QR.
    /// </summary>
    public void StartEquipmentSessionFromQr(string qrPayload)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.StartEquipmentSessionFromQr(qrPayload);
    }

    public void ScheduleMeeting(
        string title,
        string scheduledStartUtc,
        string scheduledEndUtc,
        string commaSeparatedParticipantIds,
        string supportGroupId = null)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.ScheduleMeeting(
            title,
            scheduledStartUtc,
            scheduledEndUtc,
            SplitIdentifiers(commaSeparatedParticipantIds),
            supportGroupId);
    }

    public void ScheduleMeetingForDevices(
        string title,
        string scheduledStartUtc,
        string scheduledEndUtc,
        string commaSeparatedMemberIds,
        string commaSeparatedDeviceIds,
        string supportGroupId = null)
    {
        string[] memberIds = SplitIdentifiers(commaSeparatedMemberIds);
        string[] deviceIds = SplitIdentifiers(commaSeparatedDeviceIds);
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.ScheduleMeeting(
            title,
            scheduledStartUtc,
            scheduledEndUtc,
            memberIds,
            supportGroupId,
            deviceIds);
    }

    private static string[] SplitIdentifiers(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        string[] identifiers = value.Split(
            new[] { ',', ';' },
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < identifiers.Length; index++)
        {
            identifiers[index] = identifiers[index].Trim();
        }
        return identifiers;
    }

    public void RefreshScheduledMeetings()
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.RefreshScheduledMeetings();
    }

    public void RefreshIncomingSessions()
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.RefreshIncomingSessions();
    }

    public void AcceptIncomingSession(string sessionId)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.AcceptIncomingSession(sessionId);
    }

    public void JoinScheduledMeeting(string sessionId)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.JoinScheduledMeeting(sessionId);
    }

    public void InviteInternalMember(string memberId)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.InviteInternalMember(memberId);
    }

    public void CreateExternalGuestLink(int validMinutes = 30)
    {
        if (!TryGetMeetingSessionController(out var controller)) return;
        controller.CreateExternalGuestLink(validMinutes);
    }

    private System.Collections.IEnumerator BootstrapCompanyMeeting()
    {
        if (string.IsNullOrWhiteSpace(meetingBootstrapEndpoint))
        {
            AddLog(
                "Company auto-join is enabled, but the bootstrap endpoint is empty.");
            companyBootstrapCoroutine = null;
            yield break;
        }
        if (string.IsNullOrWhiteSpace(companyId))
        {
            AddLog("Company auto-join failed: Company ID is empty.");
            companyBootstrapCoroutine = null;
            yield break;
        }

        string deviceId = string.IsNullOrWhiteSpace(provisionedDeviceId)
            ? SystemInfo.deviceUniqueIdentifier
            : provisionedDeviceId.Trim();
        AddLog(
            $"Resolving assigned meeting for company={companyId}, device={deviceId}...");

        CompanyMeetingBootstrapClient.Response response = null;
        string error = null;
        yield return CompanyMeetingBootstrapClient.Fetch(
            meetingBootstrapEndpoint,
            companyId,
            deviceId,
            provisionedDeviceName,
            requestedMeetingGroupId,
            companyDeviceKey,
            bootstrapTimeoutSeconds,
            result => response = result,
            message => error = message);

        companyBootstrapCoroutine = null;
        if (response == null)
        {
            AddLog(
                string.IsNullOrWhiteSpace(error)
                    ? "Company meeting assignment failed."
                    : error);
            if (allowManualJoinFallback)
            {
                AddLog("Manual Channel / Token join remains available.");
            }
            RefreshStatus();
            yield break;
        }

        liveKitUrl = response.server_url;
        channelName = response.room_name;
        token = response.participant_token;
        assignedCompanyName = response.company_name;
        assignedGroupName = response.group_name;

        if (channelNameInput != null)
        {
            channelNameInput.text = channelName;
        }
        if (tokenInput != null)
        {
            tokenInput.text = token;
        }
        if (userIdInput != null)
        {
            userIdInput.text = response.participant_identity;
        }

        AddLog(
            $"Assigned meeting: {assignedCompanyName} / " +
            $"{assignedGroupName} ({channelName})");
        RefreshStatus();

        if (autoJoinAssignedMeeting && response.auto_join)
        {
            AddLog("Auto-joining the assigned company meeting...");
            JoinChannel();
        }
    }

    private void AddButtonListeners()
    {
        if (initializeButton != null) initializeButton.onClick.AddListener(Initialize);
        if (disposeButton != null) disposeButton.onClick.AddListener(DisposeSDK);
        if (joinChannelButton != null) joinChannelButton.onClick.AddListener(JoinChannel);
        if (leaveChannelButton != null) leaveChannelButton.onClick.AddListener(LeaveChannel);
        if (toggleAudioButton != null) toggleAudioButton.onClick.AddListener(ToggleAudio);
        if (toggleVideoButton != null) toggleVideoButton.onClick.AddListener(ToggleVideo);
        if (shareScreenButton != null) shareScreenButton.onClick.AddListener(ToggleScreenShare);
    }

    private void RemoveButtonListeners()
    {
        if (initializeButton != null) initializeButton.onClick.RemoveListener(Initialize);
        if (disposeButton != null) disposeButton.onClick.RemoveListener(DisposeSDK);
        if (joinChannelButton != null) joinChannelButton.onClick.RemoveListener(JoinChannel);
        if (leaveChannelButton != null) leaveChannelButton.onClick.RemoveListener(LeaveChannel);
        if (toggleAudioButton != null) toggleAudioButton.onClick.RemoveListener(ToggleAudio);
        if (toggleVideoButton != null) toggleVideoButton.onClick.RemoveListener(ToggleVideo);
        if (shareScreenButton != null) shareScreenButton.onClick.RemoveListener(ToggleScreenShare);
    }

    private void ReadInputFields()
    {
        appId = appIdInput != null ? appIdInput.text : appId;
        channelName = channelNameInput != null ? channelNameInput.text : channelName;
        token = tokenInput != null ? tokenInput.text : token;

        if (userIdInput != null && int.TryParse(userIdInput.text, out int parsedUid))
        {
            userId = parsedUid;
        }
    }

    private void Initialize()
    {
        ReadInputFields();

        if (string.IsNullOrWhiteSpace(channelName))
        {
            AddLog("Initialize failed: Channel is empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            AddLog("Initialize failed: LiveKit token is empty.");
            return;
        }

        string participantIdentity = userIdInput != null ? userIdInput.text : userId.ToString();
        localIdentity = participantIdentity;
        JJStreamingProfile profile = JJStreamingProfile.CreateLiveKit(
            liveKitUrl,
            channelName,
            participantIdentity,
            token,
            appId);
        SimpleStreamingEventHandler eventHandler = new(this);

        StreamingSDK.Initialize(profile, eventHandler);
        StreamingSDK.SetLiveKitMeetingRole(meetingRole);
        participantRoles.Clear();
        StreamingSDK.EnableAudio();
        StreamingSDK.EnableLocalAudio(true);
        StreamingSDK.MuteLocalAudioStream(false);
        StreamingSDK.EnableVideo();
        StreamingSDK.EnableLocalVideo(true);

        initialized = StreamingSDK.HasEngine;
        audioMuted = false;
        audioModuleEnabled = true;
        microphoneCaptureEnabled = true;
        SetMicrophoneUi(false);
        localVideoEnabled = true;
        videoModuleEnabled = true;
        localVideoPublishMuted = false;
        previewEnabled = initialized;
        frontCameraSelected = false;
        remoteAudioMuted = false;
        remoteVideoMuted = false;
        remoteLowQuality = false;
        pcmObserverEnabled = false;
        selectedMeetingMicrophoneDevice = null;
        meetingView?.SetMicrophoneInput(null);
        receivedPcmFrameCount = 0;
        videoProfileIndex = 0;
        remoteUids.Clear();
        meetingView?.SetSdkReady(initialized);
        meetingView?.SetLocalParticipant(participantIdentity);
        meetingView?.SetParticipantRole(participantIdentity, meetingRole);
        if (initialized)
        {
            StreamingSDK.SetVideoEncoderConfiguration(
                VideoProfiles[videoProfileIndex]);
            StreamingSDK.StartPreview();
            RefreshDeviceSelectors();
            // 專家端 usually talks to the front camera; 場域端 shows the scene (rear/AR glasses).
            if (meetingRole == "expert") ApplyCamera(true);
        }
        RefreshAdvancedControls();
        AddLog(initialized ? "Initialize success." : "Initialize failed.");
        RefreshStatus();
    }

    private void DisposeSDK()
    {
        AbortRecordingTransfer(true);
        DisablePcmObserver();
        StreamingSDK.Dispose();
        initialized = false;
        joined = false;
        audioMuted = false;
        audioModuleEnabled = false;
        microphoneCaptureEnabled = false;
        SetMicrophoneUi(false);
        localVideoEnabled = true;
        videoModuleEnabled = false;
        localVideoPublishMuted = false;
        previewEnabled = false;
        frontCameraSelected = false;
        remoteAudioMuted = false;
        remoteVideoMuted = false;
        remoteLowQuality = false;
        remoteUids.Clear();
        meetingView?.SetSdkReady(false);
        meetingView?.SetConnected(false);
        SetMeetingLayout(false);
        RefreshAdvancedControls();
        AddLog("Dispose SDK.");
        RefreshStatus();
    }

    private void JoinChannel()
    {
        if (!initialized || !StreamingSDK.HasEngine)
        {
            Initialize();
        }

        if (!EnsureInitialized()) return;

        int result = StreamingSDK.JoinChannel();
        joined = result == 0;
        AddLog($"JoinChannel result: {result}");
        RefreshStatus();
    }

    private void LeaveChannel()
    {
        if (!EnsureInitialized()) return;

        AbortRecordingTransfer(true);
        int result = StreamingSDK.LeaveChannel();
        joined = false;
        previewEnabled = false;
        remoteUids.Clear();
        DisablePcmObserver();
        meetingView?.SetConnected(false);
        SetMeetingLayout(false);
        RefreshAdvancedControls();
        AddLog($"LeaveChannel result: {result}");
        RefreshStatus();
    }

    private void ToggleAudio()
    {
        if (!EnsureInitialized()) return;

        audioMuted = !audioMuted;
        StreamingSDK.MuteLocalAudioStream(audioMuted);
        SetMicrophoneUi(audioMuted);
        AddLog($"{(audioMuted ? "Mute" : "Unmute")} local audio.");
        RefreshStatus();
    }

    private void ToggleVideo()
    {
        if (!EnsureInitialized()) return;

        localVideoEnabled = !localVideoEnabled;
        meetingView?.SetCameraOn(localVideoEnabled);
        StreamingSDK.EnableLocalVideo(localVideoEnabled);
        meetingView?.SetLocalVideoVisible(localVideoEnabled && joined);
        AddLog($"{(localVideoEnabled ? "Enable" : "Disable")} local video.");
        RefreshStatus();
    }

    private void ToggleScreenShare()
    {
        if (!EnsureInitialized()) return;
        int result = screenSharing
            ? StreamingSDK.StopShareScreen()
            : StreamingSDK.ShareScreen(new JJChannelMediaOptions());
        if (result == 0) screenSharing = !screenSharing;
        meetingView?.SetSharing(screenSharing);
        AddLog($"{(screenSharing ? "Start" : "Stop")} screen share result: {result}");
    }

    private void HandleAdvancedControl(
        LiveKitMeetingView.AdvancedControl control)
    {
        switch (control)
        {
            case LiveKitMeetingView.AdvancedControl.Preview:
                TogglePreview();
                break;
            case LiveKitMeetingView.AdvancedControl.Camera:
                ToggleCameraDirection();
                break;
            case LiveKitMeetingView.AdvancedControl.RemoteAudio:
                ToggleRemoteAudio();
                break;
            case LiveKitMeetingView.AdvancedControl.RemoteVideo:
                ToggleRemoteVideo();
                break;
            case LiveKitMeetingView.AdvancedControl.RemoteQuality:
                ToggleRemoteQuality();
                break;
            case LiveKitMeetingView.AdvancedControl.VideoProfile:
                CycleVideoProfile();
                break;
            case LiveKitMeetingView.AdvancedControl.MicrophoneInput:
                CycleMicrophoneInput();
                break;
            case LiveKitMeetingView.AdvancedControl.PcmObserver:
                TogglePcmObserver();
                break;
        }
    }

    private void TogglePreview()
    {
        if (!EnsureInitialized()) return;
        previewEnabled = !previewEnabled;
        if (previewEnabled)
        {
            if (string.IsNullOrWhiteSpace(localIdentity))
            {
                localIdentity = userIdInput != null
                    ? userIdInput.text
                    : userId.ToString();
            }
            meetingView?.SetLocalParticipant(localIdentity);
            StreamingSDK.StartPreview();
        }
        else
        {
            StreamingSDK.StopPreview();
        }
        RefreshAdvancedControls();
        AddLog($"Local camera preview {(previewEnabled ? "started" : "stopped")}.");
    }

    private void ToggleCameraDirection()
    {
        if (!EnsureInitialized()) return;
        bool useFront = !frontCameraSelected;
        var config = new JJCameraCapturerConfiguration();
        config.cameraDirection.SetValue(
            useFront ? JJCameraDirection.FRONT : JJCameraDirection.REAR);
        int result = StreamingSDK.SetCameraCapturerConfiguration(config);
        if (result == 0)
        {
            frontCameraSelected = useFront;
        }
        RefreshAdvancedControls();
        AddLog(
            $"Select {(useFront ? "front" : "rear/AR")} camera result: {result}");
    }

    private void ToggleRemoteAudio()
    {
        if (!EnsureConnectedForAdvancedControl()) return;
        remoteAudioMuted = !remoteAudioMuted;
        StreamingSDK.MuteAllRemoteAudioStreams(remoteAudioMuted);
        RefreshAdvancedControls();
        AddLog(
            $"Remote audio reception {(remoteAudioMuted ? "stopped" : "resumed")}.");
    }

    private void ToggleRemoteVideo()
    {
        if (!EnsureConnectedForAdvancedControl()) return;
        remoteVideoMuted = !remoteVideoMuted;
        StreamingSDK.MuteAllRemoteVideoStreams(remoteVideoMuted);
        RefreshAdvancedControls();
        AddLog(
            $"Remote video reception {(remoteVideoMuted ? "stopped" : "resumed")}.");
    }

    private void ToggleRemoteQuality()
    {
        if (!EnsureConnectedForAdvancedControl()) return;
        if (remoteUids.Count == 0)
        {
            AddLog("No remote participant is available for quality switching.");
            return;
        }

        bool useLow = !remoteLowQuality;
        int successCount = 0;
        foreach (uint remoteUid in remoteUids)
        {
            if (StreamingSDK.SetRemoteVideoStreamType(
                    remoteUid,
                    useLow ? JJVideoStreamType.LOW : JJVideoStreamType.HIGH) == 0)
            {
                successCount++;
            }
        }
        if (successCount > 0)
        {
            remoteLowQuality = useLow;
        }
        RefreshAdvancedControls();
        AddLog(
            $"Remote quality {(useLow ? "LOW" : "HIGH")}: " +
            $"{successCount}/{remoteUids.Count} participants updated.");
    }

    private void CycleVideoProfile()
    {
        if (!EnsureInitialized()) return;
        int nextIndex = (videoProfileIndex + 1) % VideoProfiles.Length;
        int result = StreamingSDK.SetVideoEncoderConfiguration(
            VideoProfiles[nextIndex]);
        if (result == 0)
        {
            videoProfileIndex = nextIndex;
        }
        RefreshAdvancedControls();
        AddLog(
            $"Video profile {VideoProfileLabels[nextIndex]} result: {result}");
    }

    private void CycleMicrophoneInput()
    {
        if (!EnsureInitialized()) return;

        JJDeviceInfo[] devices = StreamingSDK.GetRecordingDevices() ??
                                 Array.Empty<JJDeviceInfo>();
        if (devices.Length == 0)
        {
            int defaultResult = StreamingSDK.SetRecordingDevice(null);
            if (defaultResult == 0)
            {
                selectedMeetingMicrophoneDevice = null;
                meetingView?.SetMicrophoneInput(null);
            }
            AddLog(
                "No named microphone was detected; using the system default " +
                $"input. Result: {defaultResult}.");
            return;
        }

        int currentIndex = -1;
        if (!string.IsNullOrWhiteSpace(selectedMeetingMicrophoneDevice))
        {
            for (int index = 0; index < devices.Length; index++)
            {
                if (string.Equals(
                        devices[index].deviceId,
                        selectedMeetingMicrophoneDevice,
                        StringComparison.Ordinal))
                {
                    currentIndex = index;
                    break;
                }
            }
        }

        // System default -> device 1 -> device 2 -> ... -> system default.
        int nextIndex = currentIndex + 1;
        string nextDeviceId = nextIndex < devices.Length
            ? devices[nextIndex].deviceId
            : null;
        string nextDeviceName = nextIndex < devices.Length
            ? devices[nextIndex].deviceName
            : "System Default";
        int result = StreamingSDK.SetRecordingDevice(nextDeviceId);
        if (result == 0)
        {
            selectedMeetingMicrophoneDevice = nextDeviceId;
            meetingView?.SetMicrophoneInput(nextDeviceName);
        }
        AddLog(
            $"Microphone input: {nextDeviceName}. Result: {result}." +
            (joined
                ? " The microphone track is restarting; audio may pause briefly."
                : string.Empty));
    }

    private void TogglePcmObserver()
    {
        if (!EnsureConnectedForAdvancedControl()) return;
        if (pcmObserverEnabled)
        {
            int frameCount = System.Threading.Interlocked.Exchange(
                ref receivedPcmFrameCount,
                0);
            DisablePcmObserver();
            AddLog($"Remote PCM observer stopped. Frames received: {frameCount}.");
            RefreshAdvancedControls();
            return;
        }

        StreamingSDK.RemoteAudioFrameReceived -= HandleRemoteAudioFrame;
        StreamingSDK.RemoteAudioFrameReceived += HandleRemoteAudioFrame;
        int result = StreamingSDK.EnableRemoteAudioFrameObserver(
            new JJRemoteAudioFrameConfig
            {
                sampleRate = 16000,
                channels = 1
            });
        pcmObserverEnabled = result == 0;
        if (!pcmObserverEnabled)
        {
            StreamingSDK.RemoteAudioFrameReceived -= HandleRemoteAudioFrame;
        }
        RefreshAdvancedControls();
        AddLog($"Remote PCM observer start result: {result}.");
    }

    private void DisablePcmObserver()
    {
        if (streamingSDK == null)
        {
            pcmObserverEnabled = false;
            return;
        }
        StreamingSDK.RemoteAudioFrameReceived -= HandleRemoteAudioFrame;
        if (StreamingSDK.RemoteAudioFrameObserverEnabled)
        {
            StreamingSDK.DisableRemoteAudioFrameObserver();
        }
        pcmObserverEnabled = false;
    }

    private void HandleRemoteAudioFrame(JJRemoteAudioFrame frame)
    {
        // LiveKit/Agora invokes this callback outside Unity's main thread.
        // Only update an atomic counter here; never touch UI/GameObjects.
        System.Threading.Interlocked.Increment(ref receivedPcmFrameCount);
    }

    private bool EnsureConnectedForAdvancedControl()
    {
        if (EnsureInitialized() && joined && StreamingSDK.IsConnected)
        {
            return true;
        }
        AddLog("Join the LiveKit room before using this control.");
        return false;
    }

    private void RefreshAdvancedControls()
    {
        meetingView?.SetAdvancedControlState(
            previewEnabled,
            frontCameraSelected,
            remoteAudioMuted,
            remoteVideoMuted,
            remoteLowQuality,
            VideoProfileLabels[Mathf.Clamp(
                videoProfileIndex,
                0,
                VideoProfileLabels.Length - 1)],
            pcmObserverEnabled);
    }

    private bool EnsureInitialized()
    {
        if (initialized && StreamingSDK.HasEngine)
        {
            return true;
        }

        AddLog("SDK is not initialized.");
        return false;
    }

    private void StartPreview()
    {
        if (!EnsureInitialized() || previewEnabled) return;
        previewEnabled = true;
        if (string.IsNullOrWhiteSpace(localIdentity))
        {
            localIdentity = userIdInput != null ? userIdInput.text : userId.ToString();
        }
        meetingView?.SetLocalParticipant(localIdentity);
        StreamingSDK.StartPreview();
        RefreshAdvancedControls();
        RefreshStatus();
        AddLog("Local camera preview started.");
    }

    private void StopPreview()
    {
        if (!EnsureInitialized() || !previewEnabled) return;
        StreamingSDK.StopPreview();
        previewEnabled = false;
        RefreshAdvancedControls();
        RefreshStatus();
        AddLog("Local camera preview stopped.");
    }

    private void ToggleAudioModule()
    {
        if (!EnsureInitialized()) return;
        audioModuleEnabled = !audioModuleEnabled;
        if (audioModuleEnabled) StreamingSDK.EnableAudio();
        else StreamingSDK.DisableAudio();
        AddLog($"Audio module {(audioModuleEnabled ? "enabled" : "disabled")}.");
        RefreshStatus();
    }

    private void ToggleMicrophoneCapture()
    {
        if (!EnsureInitialized()) return;
        microphoneCaptureEnabled = !microphoneCaptureEnabled;
        StreamingSDK.EnableLocalAudio(microphoneCaptureEnabled);
        AddLog($"Microphone capture {(microphoneCaptureEnabled ? "enabled" : "disabled")}.");
        RefreshStatus();
    }

    private void ToggleVideoModule()
    {
        if (!EnsureInitialized()) return;
        videoModuleEnabled = !videoModuleEnabled;
        if (videoModuleEnabled) StreamingSDK.EnableVideo();
        else StreamingSDK.DisableVideo();
        AddLog($"Video module {(videoModuleEnabled ? "enabled" : "disabled")}.");
        RefreshStatus();
    }

    private void ToggleCameraCapture()
    {
        ToggleVideo();
    }

    private void ToggleLocalVideoPublish()
    {
        if (!EnsureInitialized()) return;
        localVideoPublishMuted = !localVideoPublishMuted;
        StreamingSDK.MuteLocalVideoStream(localVideoPublishMuted);
        AddLog(
            $"Local video publishing {(localVideoPublishMuted ? "stopped" : "resumed")}.");
        RefreshStatus();
    }

    private void ApplyCameraDirection()
    {
        if (!EnsureInitialized() || demo2View?.CameraDirectionSelector == null) return;
        bool useFront = demo2View.CameraDirectionSelector.SelectedValue == "front";
        var config = new JJCameraCapturerConfiguration();
        config.cameraDirection.SetValue(
            useFront ? JJCameraDirection.FRONT : JJCameraDirection.REAR);
        int result = StreamingSDK.SetCameraCapturerConfiguration(config);
        if (result == 0) frontCameraSelected = useFront;
        RefreshAdvancedControls();
        AddLog(
            $"Apply {(useFront ? "front" : "rear/AR")} camera result: {result}.");
    }

    private void ApplyEncoderConfiguration()
    {
        if (!EnsureInitialized() || demo2View == null) return;
        string[] sizeParts = demo2View.ResolutionSelector.SelectedValue.Split('x');
        if (sizeParts.Length != 2 ||
            !int.TryParse(sizeParts[0], out int width) ||
            !int.TryParse(sizeParts[1], out int height))
        {
            AddLog("Encoder configuration failed: invalid resolution.");
            return;
        }
        int.TryParse(demo2View.FrameRateSelector.SelectedValue, out int frameRate);
        if (!int.TryParse(demo2View.BitrateInput.text, out int bitrate)) bitrate = -1;
        var config = new JJVideoEncoderConfiguration
        {
            width = width,
            height = height,
            frameRate = frameRate > 0 ? frameRate : 15,
            bitrate = bitrate,
            maintainResolution = demo2View.MaintainResolutionToggle.isOn
        };
        int result = StreamingSDK.SetVideoEncoderConfiguration(config);
        for (int i = 0; i < VideoProfiles.Length; i++)
        {
            if (VideoProfiles[i].width == width && VideoProfiles[i].height == height)
            {
                videoProfileIndex = i;
                break;
            }
        }
        RefreshAdvancedControls();
        AddLog(
            $"Encoder {width}x{height} {config.frameRate}fps {bitrate}kbps result: {result}.");
    }

    private void ApplyRemoteStreamSelection()
    {
        if (!EnsureConnectedForAdvancedControl() || demo2View?.RemoteStreamSelector == null)
            return;
        bool useLow = demo2View.RemoteStreamSelector.SelectedValue == "low";
        int successCount = 0;
        foreach (uint uid in remoteUids)
        {
            if (StreamingSDK.SetRemoteVideoStreamType(
                    uid,
                    useLow ? JJVideoStreamType.LOW : JJVideoStreamType.HIGH) == 0)
            {
                successCount++;
            }
        }
        remoteLowQuality = useLow;
        RefreshAdvancedControls();
        AddLog(
            $"Remote stream {(useLow ? "LOW" : "HIGH")}: " +
            $"{successCount}/{remoteUids.Count} participants updated.");
    }

    private void ApplyAudioScenario()
    {
        if (!EnsureInitialized() || demo2View?.AudioScenarioSelector == null) return;
        int.TryParse(demo2View.AudioScenarioSelector.SelectedValue, out int scenario);
        int result = StreamingSDK.SetAudioScenario(scenario);
        AddLog($"Audio scenario {scenario} result: {result}.");
    }

    private void RefreshDeviceSelectors()
    {
        if (!EnsureInitialized() || demo2View == null) return;
        JJDeviceInfo[] recording = StreamingSDK.GetRecordingDevices();
        JJDeviceInfo[] playback = StreamingSDK.GetPlaybackDevices();
        JJDeviceInfo[] video = StreamingSDK.GetVideoDevices();
        demo2View.SetDeviceOptions(recording, playback, video);
        AddLog(
            $"Devices refreshed: microphone={recording?.Length ?? 0}, " +
            $"playback={playback?.Length ?? 0}, camera={video?.Length ?? 0}.");
    }

    private void ApplySelectedDevices()
    {
        if (!EnsureInitialized() || demo2View == null) return;
        string microphone = demo2View.RecordingDeviceSelector.SelectedValue;
        string playback = demo2View.PlaybackDeviceSelector.SelectedValue;
        string camera = demo2View.VideoDeviceSelector.SelectedValue;
        int micResult = string.IsNullOrWhiteSpace(microphone)
            ? 0
            : StreamingSDK.SetRecordingDevice(microphone);
        int playbackResult = string.IsNullOrWhiteSpace(playback)
            ? 0
            : StreamingSDK.SetPlaybackDevice(playback);
        int cameraResult = string.IsNullOrWhiteSpace(camera)
            ? 0
            : StreamingSDK.SetVideoDevice(camera);
        AddLog(
            $"Apply devices: microphone={micResult}, playback={playbackResult}, " +
            $"camera={cameraResult}.");
    }

    private void RenewTokenFromUi()
    {
        if (!EnsureInitialized() || demo2View?.RenewTokenInput == null) return;
        string replacement = demo2View.RenewTokenInput.text?.Trim();
        if (string.IsNullOrWhiteSpace(replacement))
        {
            AddLog("Renew token failed: replacement token is empty.");
            return;
        }
        int result = StreamingSDK.RenewToken(replacement);
        if (result == 0)
        {
            token = replacement;
            if (tokenInput != null) tokenInput.text = replacement;
            demo2View.RenewTokenInput.text = string.Empty;
        }
        AddLog($"Renew token result: {result}.");
    }

    private void SetMicrophoneUi(bool muted)
    {
        meetingView?.SetMicrophoneMuted(muted);
        demo2View?.SetMicrophoneMuted(muted);
    }

    private void SetTranscriptUi(bool recording, bool busy = false)
    {
        meetingView?.SetTranscriptState(recording, busy);
        demo2View?.SetTranscriptState(recording, busy);
    }

    private void RefreshStats()
    {
        if (demo2View?.StatsText == null) return;
        string localSize = localVideoSize.x > 0
            ? $"{localVideoSize.x} x {localVideoSize.y}"
            : "-";
        string remoteAudio = remoteAudioStatsUid > 0
            ? $"uid={remoteAudioStatsUid}, {remoteAudioSampleRate} Hz"
            : "uid=-, 0 Hz";
        demo2View.StatsText.text =
            $"Local Audio Bitrate: {localAudioBitrate} kbps\n" +
            $"Remote Audio: {remoteAudio}\n" +
            $"Local Video Size: {localSize}\n" +
            $"Remote Video / PCM: {remoteUids.Count} user(s), " +
            $"{System.Threading.Volatile.Read(ref receivedPcmFrameCount)} frame(s)";
    }

    /// <summary>Top-level canvas children that make up the join screen (left panel).</summary>
    private void CollectJoinScreenObjects(Canvas canvas)
    {
        joinScreenObjects.Clear();
        void Add(Component part)
        {
            if (part == null) return;
            Transform top = part.transform;
            while (top.parent != null && top.parent != canvas.transform) top = top.parent;
            if (top.parent != canvas.transform || meetingView != null && top == meetingView.transform) return;
            if (!joinScreenObjects.Contains(top.gameObject)) joinScreenObjects.Add(top.gameObject);
        }
        Add(channelNameInput); Add(tokenInput); Add(userIdInput);
        Add(initializeButton); Add(joinChannelButton); Add(statusText); Add(logText);
        Transform backdrop = canvas.transform.Find("Custom Connection Panel");
        if (backdrop != null) Add(backdrop);
    }

    /// <summary>In a meeting the call fills the screen; the join panel returns after leaving.</summary>
    private void SetMeetingLayout(bool inMeeting)
    {
        foreach (GameObject part in joinScreenObjects) if (part != null) part.SetActive(!inMeeting);
        meetingView?.SetFullScreen(inMeeting);
        meetingView?.SetCameraOn(localVideoEnabled);
        meetingView?.SetSharing(screenSharing);
    }

    private void ApplyCamera(bool front)
    {
        var config = new JJCameraCapturerConfiguration();
        config.cameraDirection.SetValue(front ? JJCameraDirection.FRONT : JJCameraDirection.REAR);
        if (StreamingSDK.SetCameraCapturerConfiguration(config) == 0) frontCameraSelected = front;
        RefreshAdvancedControls();
    }

    /// <summary>Two-button Field / Expert selector above the Channel field.</summary>
    private void CreateRoleSelector()
    {
        Transform row = channelNameInput != null ? channelNameInput.transform.parent : null;
        Transform column = row != null ? row.parent : null;
        if (column == null || column.GetComponent<VerticalLayoutGroup>() == null)
        {
            AddLog("Role selector could not find the connection panel.");
            return;
        }
        var selector = new GameObject("Role Selector", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        selector.transform.SetParent(column, false);
        selector.transform.SetSiblingIndex(row.GetSiblingIndex());
        var layout = selector.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = true;
        var element = selector.GetComponent<LayoutElement>();
        element.preferredHeight = 64f; element.flexibleHeight = 0f; element.flexibleWidth = 1f;
        fieldRoleButton = CreateRoleButton(selector.transform, "I'm Field Side", "field");
        expertRoleButton = CreateRoleButton(selector.transform, "I'm the Expert", "expert");
        RefreshRoleButtons();
    }

    private Button CreateRoleButton(Transform parent, string label, string role)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var button = go.GetComponent<Button>();
        LiveKitMeetingStyle.ConfigureButton(button);
        button.onClick.AddListener(() => SetMeetingRole(role));
        var text = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        text.transform.SetParent(go.transform, false);
        var rect = text.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        text.text = label; text.fontSize = 22f; text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center; text.color = Color.white; text.raycastTarget = false;
        return button;
    }

    private void RefreshRoleButtons()
    {
        if (fieldRoleButton != null)
            LiveKitMeetingStyle.ApplyRounded(fieldRoleButton.GetComponent<Image>(),
                meetingRole == "field" ? LiveKitMeetingStyle.Accent : LiveKitMeetingStyle.SurfaceRaised, true);
        if (expertRoleButton != null)
            LiveKitMeetingStyle.ApplyRounded(expertRoleButton.GetComponent<Image>(),
                meetingRole == "expert" ? LiveKitMeetingStyle.Accent : LiveKitMeetingStyle.SurfaceRaised, true);
    }

    private void SetMeetingRole(string role)
    {
        role = role == "expert" ? "expert" : "field";
        if (role == meetingRole) return;
        meetingRole = role;
        PlayerPrefs.SetString(MeetingRoleKey, role);
        PlayerPrefs.Save();
        RefreshRoleButtons();
        if (StreamingSDK != null && StreamingSDK.HasEngine)
        {
            StreamingSDK.SetLiveKitMeetingRole(role);
            if (initialized) ApplyCamera(role == "expert");
        }
        if (!string.IsNullOrWhiteSpace(localIdentity)) meetingView?.SetParticipantRole(localIdentity, role);
        AddLog(role == "expert" ? "Role: Expert" : "Role: Field");
    }

    private void HandleParticipantRole(string identity, string role)
    {
        participantRoles[identity] = role;
        meetingView?.SetParticipantRole(identity, role);
    }

    private static void SetButtonLabel(Button button, string label)
    {
        if (button == null) return;
        TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
        if (text != null) text.text = label;
    }

    private static void HideConnectionInputRow(TMP_InputField input)
    {
        if (input == null) return;

        // Each field sits in its own row together with its label. Hide the
        // whole row unless that parent also holds other controls (buttons).
        Transform row = input.transform.parent;
        bool rowIsDedicated = row != null
            && row.GetComponentsInChildren<TMP_InputField>(true).Length == 1
            && row.GetComponentsInChildren<Button>(true).Length == 0;
        (rowIsDedicated ? row.gameObject : input.gameObject).SetActive(false);
    }

    private void SetupLiveKitMeetingView()
    {
        // Keep the original sample controls and place the responsive meeting
        // grid above the legacy fixed local/remote video surfaces.
        if (localStreamSurface != null) localStreamSurface.gameObject.SetActive(false);
        if (remoteStreamSurface != null) remoteStreamSurface.gameObject.SetActive(false);

        Canvas canvas = ResolveSampleCanvas();
        if (canvas == null)
        {
            AddLog("LiveKit meeting grid could not find a Canvas.");
            return;
        }

        LiveKitMeetingSceneStyle.Apply(
            canvas,
            new[]
            {
                appIdInput,
                channelNameInput,
                tokenInput,
                userIdInput
            },
            new[]
            {
                initializeButton,
                disposeButton,
                joinChannelButton,
                leaveChannelButton,
                toggleAudioButton,
                toggleVideoButton,
                shareScreenButton
            },
            statusText,
            logText);

        // App ID is Agora-only; LiveKit authenticates with its URL and token,
        // so the App ID field is hidden. Channel / Token / User ID stay visible
        // because LiveKit uses them as room name, token and participant identity.
        HideConnectionInputRow(appIdInput);

        SetButtonLabel(initializeButton, "Initialize");
        SetButtonLabel(disposeButton, "Reset");
        SetButtonLabel(joinChannelButton, "Join Meeting");
        SetButtonLabel(leaveChannelButton, "Leave Meeting");
        SetButtonLabel(toggleAudioButton, "Microphone");
        SetButtonLabel(toggleVideoButton, "Camera");
        SetButtonLabel(shareScreenButton, "Share Screen");
        CreateRoleSelector();

        // Participant tiles contain their own names, so the old fixed video
        // headings are the only legacy elements hidden at runtime.
        foreach (TMP_Text label in canvas.GetComponentsInChildren<TMP_Text>(true))
        {
            string text = label.text?.Trim();
            if (text == "Local Stream" || text == "Remote Stream")
            {
                label.gameObject.SetActive(false);
            }
        }

        meetingView = LiveKitMeetingView.Create(
            canvas.transform,
            SendReaction,
            ToggleAudio,
            ToggleTranscript,
            HandleAdvancedControl);
        meetingView?.ConfigureCallControls(ToggleVideo, ToggleScreenShare, LeaveChannel);
        CollectJoinScreenObjects(canvas);
        SetMicrophoneUi(audioMuted);
        SetTranscriptUi(false);
        meetingView?.SetSdkReady(initialized);
        RefreshAdvancedControls();
    }

    private int collaborationTranscriptCount = -1;
    private string collaborationTranscriptSession;
    private string collaborationTranscriptPreview;
    private readonly LiveKitCollaborationPanel.MeetingState collaborationMeetingState = new();

    private LiveKitCollaborationPanel.MeetingState ReadCollaborationMeetingState()
    {
        if (collaborationTranscriptCount != transcriptEntries.Count || collaborationTranscriptSession != transcriptSessionId)
        {
            collaborationTranscriptCount = transcriptEntries.Count;
            collaborationTranscriptSession = transcriptSessionId;
            var text = new StringBuilder();
            // Newest first keeps the current exchange visible without stealing the user's scroll position.
            for (int i = transcriptEntries.Count - 1; i >= Math.Max(0, transcriptEntries.Count - 6); i--)
            {
                var entry = transcriptEntries[i];
                string speaker = string.IsNullOrWhiteSpace(entry.participantName) ? entry.participantIdentity : entry.participantName;
                string time = DateTimeOffset.TryParse(entry.timestamp, out var parsed) ? parsed.ToLocalTime().ToString("HH:mm:ss") : "";
                text.Append(time).Append("  ").Append(speaker).Append('\n').Append(entry.text).Append("\n\n");
            }
            collaborationTranscriptPreview = text.ToString();
        }
        collaborationMeetingState.Room = channelName;
        collaborationMeetingState.LocalIdentity = localIdentity;
        collaborationMeetingState.LocalRole = meetingRole;
        collaborationMeetingState.Roles = participantRoles;
        collaborationMeetingState.Transcript = collaborationTranscriptPreview;
        collaborationMeetingState.Recording = transcriptRecording;
        collaborationMeetingState.AgentReady = transcriptAgentReady;
        collaborationMeetingState.Pending = transcriptRequestPending;
        collaborationMeetingState.MicrophoneMuted = audioMuted;
        return collaborationMeetingState;
    }

    private Canvas ResolveSampleCanvas()
    {
        // The persistent UI switch button owns a DontDestroyOnLoad Canvas.
        // FindFirstObjectByType<Canvas>() could select that Canvas and attach
        // the meeting panels to it, making them survive the scene switch and
        // cover the vendor UI. Resolve from serialized scene controls first.
        Component[] sceneControls =
        {
            appIdInput,
            channelNameInput,
            tokenInput,
            userIdInput,
            initializeButton,
            statusText
        };
        foreach (Component control in sceneControls)
        {
            if (control == null) continue;
            Canvas ownerCanvas = control.GetComponentInParent<Canvas>(true);
            if (ownerCanvas != null && ownerCanvas.gameObject.scene == gameObject.scene)
            {
                return ownerCanvas;
            }
        }

        Canvas[] canvases = FindObjectsByType<Canvas>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        foreach (Canvas candidate in canvases)
        {
            if (candidate != null && candidate.gameObject.scene == gameObject.scene)
            {
                return candidate;
            }
        }
        return null;
    }

    private void ToggleTranscript()
    {
        if (!joined || !StreamingSDK.IsConnected)
        {
            AddLog("Join the LiveKit room before controlling transcription.");
            return;
        }

        if (!transcriptAgentReady)
        {
            AddLog("Transcript Agent is not online yet. Wait for Agent ready.");
            return;
        }

        if (transcriptRequestPending)
        {
            AddLog("Waiting for Transcript Agent response.");
            return;
        }

        if (transcriptRecording)
        {
            transcriptRequestPending = true;
            SetTranscriptUi(true, true);
            int result = StreamingSDK.SendTranscriptControl(
                false,
                transcriptSessionId);
            if (result != 0)
            {
                transcriptRequestPending = false;
                SetTranscriptUi(true);
                AddLog("Unable to send Transcript Agent stop request.");
            }
            else
            {
                AddLog("Stopping meeting transcription and requesting TXT + MP3...");
            }
            RefreshStatus();
            return;
        }

        AbortRecordingTransfer(true);
        transcriptEntries.Clear();
        transcriptSequences.Clear();
        transcriptCharacterCount = 0;
        transcriptSessionId = Guid.NewGuid().ToString("N");
        transcriptRequestPending = true;
        SetTranscriptUi(false, true);
        int startResult = StreamingSDK.SendTranscriptControl(
            true,
            transcriptSessionId);
        if (startResult != 0)
        {
            transcriptRequestPending = false;
            SetTranscriptUi(false);
            AddLog("Unable to send Transcript Agent start request.");
        }
        else
        {
            AddLog("Requesting whole-room transcription and MP3 recording...");
        }
        RefreshStatus();
    }

    private void HandleTranscriptAgentStatus(
        string eventType,
        string state,
        string sessionId,
        string message)
    {
        transcriptAgentReady = true;
        bool isRecording = string.Equals(
            state,
            "recording",
            StringComparison.OrdinalIgnoreCase);

        if (isRecording)
        {
            if (!string.Equals(
                    transcriptSessionId,
                    sessionId,
                    StringComparison.Ordinal))
            {
                transcriptEntries.Clear();
                transcriptSequences.Clear();
                transcriptCharacterCount = 0;
                transcriptSessionId = sessionId;
            }
            transcriptRecording = true;
            transcriptRequestPending = false;
            SetTranscriptUi(true);
        }
        else if (string.Equals(eventType, "error", StringComparison.Ordinal))
        {
            transcriptRecording = false;
            transcriptRequestPending = false;
            SetTranscriptUi(false);
        }

        AddLog(
            string.IsNullOrWhiteSpace(message)
                ? $"Transcript Agent: {state}"
                : message);
        RefreshStatus();
    }

    private void HandleTranscriptSegment(
        string sessionId,
        string timestamp,
        string participantIdentity,
        string participantName,
        string text,
        int sequence)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!string.Equals(
                transcriptSessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            transcriptEntries.Clear();
            transcriptSequences.Clear();
            transcriptCharacterCount = 0;
            transcriptSessionId = sessionId;
        }
        if (sequence > 0 && !transcriptSequences.Add(sequence)) return;

        transcriptRecording = true;
        transcriptRequestPending = false;
        transcriptCharacterCount += text.Length;
        transcriptEntries.Add(new TranscriptEntry
        {
            timestamp = timestamp,
            participantIdentity = participantIdentity,
            participantName = participantName,
            text = text,
            sequence = sequence
        });
        SetTranscriptUi(true);
        RefreshStatus();
    }

    private void HandleTranscriptCompleted(
        string sessionId,
        int segmentCount,
        string message)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            !string.Equals(
                transcriptSessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        transcriptRecording = false;
        transcriptRequestPending = false;
        SetTranscriptUi(false);
        string path = SaveMeetingTranscript();
        AddLog(
            string.IsNullOrWhiteSpace(path)
                ? "Transcript completed but TXT save failed."
                : $"Transcript TXT saved ({segmentCount} segments): {path}");
        RefreshStatus();
    }

    private void HandleTranscriptRecordingStarted(
        string sessionId,
        string fileName,
        long totalBytes,
        int chunkCount,
        float durationSeconds,
        bool truncated)
    {
        if (!string.IsNullOrWhiteSpace(transcriptSessionId) &&
            !string.Equals(
                transcriptSessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        AbortRecordingTransfer(true);
        try
        {
            string directory = Path.Combine(
                Application.persistentDataPath,
                "Transcripts");
            Directory.CreateDirectory(directory);
            string safeFileName = SanitizeRecordingFileName(fileName);

            recordingTransferSessionId = sessionId;
            recordingFinalPath = Path.Combine(directory, safeFileName);
            recordingTempPath = recordingFinalPath + ".part";
            recordingExpectedBytes = totalBytes;
            recordingExpectedChunks = chunkCount;
            recordingReceivedBytes = 0;
            recordingReceivedChunks = 0;
            recordingExpectedSha256 = string.Empty;
            recordingTransferStream = new FileStream(
                recordingTempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            AddLog(
                $"Receiving meeting MP3 ({durationSeconds:0.0}s, " +
                $"{totalBytes} bytes, {chunkCount} chunks)" +
                (truncated ? " [recording limit reached]" : string.Empty));
        }
        catch (Exception exception)
        {
            AbortRecordingTransfer(true);
            AddLog("Unable to create MP3 file: " + exception.Message);
        }
    }

    private void HandleTranscriptRecordingChunk(
        string sessionId,
        int chunkIndex,
        string base64Data)
    {
        if (recordingTransferStream == null ||
            !string.Equals(
                recordingTransferSessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (chunkIndex != recordingReceivedChunks)
        {
            AbortRecordingTransfer(true);
            AddLog(
                "MP3 transfer stopped: expected chunk " +
                $"{recordingReceivedChunks}, received {chunkIndex}.");
            return;
        }

        try
        {
            byte[] data = Convert.FromBase64String(base64Data ?? string.Empty);
            long nextSize = recordingReceivedBytes + data.Length;
            if (recordingExpectedBytes >= 0 &&
                nextSize > recordingExpectedBytes)
            {
                throw new InvalidDataException(
                    "Received more MP3 data than declared by the Agent.");
            }

            recordingTransferStream.Write(data, 0, data.Length);
            recordingReceivedBytes = nextSize;
            recordingReceivedChunks++;
        }
        catch (Exception exception)
        {
            AbortRecordingTransfer(true);
            AddLog("MP3 transfer failed: " + exception.Message);
        }
    }

    private void HandleTranscriptRecordingCompleted(
        string sessionId,
        string fileName,
        long totalBytes,
        int chunkCount,
        string sha256,
        float durationSeconds,
        bool truncated)
    {
        if (recordingTransferStream == null ||
            !string.Equals(
                recordingTransferSessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        string tempPath = recordingTempPath;
        string finalPath = recordingFinalPath;
        try
        {
            recordingTransferStream.Flush();
            recordingTransferStream.Dispose();
            recordingTransferStream = null;

            long declaredBytes = totalBytes > 0
                ? totalBytes
                : recordingExpectedBytes;
            int declaredChunks = chunkCount > 0
                ? chunkCount
                : recordingExpectedChunks;
            if (recordingReceivedBytes != declaredBytes ||
                recordingReceivedChunks != declaredChunks)
            {
                throw new InvalidDataException(
                    "MP3 transfer is incomplete " +
                    $"({recordingReceivedBytes}/{declaredBytes} bytes, " +
                    $"{recordingReceivedChunks}/{declaredChunks} chunks).");
            }

            recordingExpectedSha256 = sha256 ?? string.Empty;
            string actualSha256 = ComputeSha256(tempPath);
            if (string.IsNullOrWhiteSpace(recordingExpectedSha256) ||
                !string.Equals(
                    actualSha256,
                    recordingExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "MP3 SHA-256 verification failed.");
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
            File.Move(tempPath, finalPath);
            ResetRecordingTransferState();

            AddLog(
                $"Meeting MP3 saved ({durationSeconds:0.0}s): {finalPath}" +
                (truncated ? " [recording limit reached]" : string.Empty));
        }
        catch (Exception exception)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tempPath) &&
                    File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupException)
            {
                Debug.LogWarning(
                    "[StreamingSample] Unable to delete invalid MP3: " +
                    cleanupException.Message);
            }
            ResetRecordingTransferState();
            AddLog("Unable to save meeting MP3: " + exception.Message);
        }
    }

    private void HandleTranscriptRecordingError(
        string sessionId,
        string message)
    {
        if (!string.IsNullOrWhiteSpace(recordingTransferSessionId) &&
            !string.Equals(
                recordingTransferSessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        AbortRecordingTransfer(true);
        AddLog(
            string.IsNullOrWhiteSpace(message)
                ? "Meeting MP3 recording failed."
                : message);
    }

    private void AbortRecordingTransfer(bool deletePartialFile)
    {
        try
        {
            recordingTransferStream?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[StreamingSample] Unable to close partial MP3: " +
                exception.Message);
        }
        recordingTransferStream = null;

        if (deletePartialFile &&
            !string.IsNullOrWhiteSpace(recordingTempPath))
        {
            try
            {
                if (File.Exists(recordingTempPath))
                {
                    File.Delete(recordingTempPath);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[StreamingSample] Unable to delete partial MP3: " +
                    exception.Message);
            }
        }
        ResetRecordingTransferState();
    }

    private void ResetRecordingTransferState()
    {
        recordingTransferStream = null;
        recordingTransferSessionId = null;
        recordingTempPath = null;
        recordingFinalPath = null;
        recordingExpectedSha256 = null;
        recordingExpectedBytes = 0;
        recordingReceivedBytes = 0;
        recordingExpectedChunks = 0;
        recordingReceivedChunks = 0;
    }

    private static string SanitizeRecordingFileName(string fileName)
    {
        string safeName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = $"MeetingRecording_{DateTime.Now:yyyyMMdd_HHmmss}.mp3";
        }

        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidCharacter, '_');
        }
        if (!string.Equals(
                Path.GetExtension(safeName),
                ".mp3",
                StringComparison.OrdinalIgnoreCase))
        {
            safeName = Path.ChangeExtension(safeName, ".mp3");
        }
        return safeName;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream input = File.OpenRead(path);
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(input);
        return BitConverter.ToString(hash)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private string SaveMeetingTranscript()
    {
        try
        {
            string directory = Path.Combine(
                Application.persistentDataPath,
                "Transcripts");
            Directory.CreateDirectory(directory);
            string session = string.IsNullOrWhiteSpace(transcriptSessionId)
                ? DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : transcriptSessionId;
            string path = Path.Combine(
                directory,
                $"MeetingTranscript_{DateTime.Now:yyyyMMdd_HHmmss}_{session}.txt");

            var output = new StringBuilder();
            output.AppendLine("LiveKit 會議逐字稿");
            output.AppendLine($"房間：{channelName}");
            output.AppendLine($"匯出時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            output.AppendLine($"辨識語言：繁體中文（zh-TW）");
            output.AppendLine();

            if (transcriptEntries.Count == 0)
            {
                output.AppendLine("（這段期間沒有收到可辨識的語音）");
            }
            else
            {
                transcriptEntries.Sort((left, right) =>
                    left.sequence.CompareTo(right.sequence));
                foreach (TranscriptEntry entry in transcriptEntries)
                {
                    string time = entry.timestamp;
                    if (DateTimeOffset.TryParse(
                            entry.timestamp,
                            out DateTimeOffset parsed))
                    {
                        time = parsed.ToLocalTime().ToString("HH:mm:ss");
                    }
                    string speaker = string.IsNullOrWhiteSpace(entry.participantName)
                        ? entry.participantIdentity
                        : entry.participantName;
                    output.AppendLine($"[{time}] {speaker}：{entry.text}");
                }
            }

            File.WriteAllText(path, output.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch (Exception exception)
        {
            AddLog("Unable to save meeting transcript: " + exception.Message);
            return null;
        }
    }

    private void SendReaction(JJReactionType reaction)
    {
        if (!joined || !StreamingSDK.IsConnected)
        {
            AddLog("Join a LiveKit room before sending a reaction.");
            return;
        }

        int result = StreamingSDK.SendReaction(reaction);
        AddLog($"Send reaction {reaction}: {result}");
    }

    private void BindLocalStreamSurface(JJRtcConnection connection)
    {
        if (localStreamSurface == null)
        {
            AddLog("Local stream surface is not assigned.");
            return;
        }

        localStreamSurface.SetForUser(0, connection.ChannelId);
        localStreamSurface.SetEnable(localVideoEnabled);
        AddLog("Local stream surface enabled.");
    }

    private void BindRemoteStreamSurface(JJRtcConnection connection, uint remoteUid)
    {
        if (remoteStreamSurface == null)
        {
            AddLog("Remote stream surface is not assigned.");
            return;
        }

        remoteStreamSurface.SetForRemoteUser(remoteUid, connection.ChannelId);
        remoteStreamSurface.SetEnable(true);
        AddLog($"Remote stream surface enabled. uid={remoteUid}");
    }

    private static void SetSurfaceEnabled(JJVideoSurface surface, bool enabled)
    {
        if (surface != null)
        {
            surface.SetEnable(enabled);
        }
    }

    private void AddLog(string message)
    {
        string line = $"[{System.DateTime.Now:HH:mm:ss}] {message}";
        Debug.Log($"[StreamingSample] {message}");
        logs.Enqueue(line);
        while (logs.Count > 12)
        {
            logs.Dequeue();
        }

        if (logText != null)
        {
            logText.text = string.Join("\n", logs);
        }
    }

    private void RefreshStatus()
    {
        meetingView?.SetTranscriptInfo(
            transcriptAgentReady,
            transcriptRecording,
            transcriptRequestPending,
            transcriptCharacterCount);

        string transcriptStatus = transcriptRequestPending
            ? "Waiting Agent"
            : transcriptRecording
                ? $"Recording ({transcriptCharacterCount} chars)"
                : transcriptAgentReady ? "Agent Ready" : "Agent Offline";

        if (statusText == null) return;

        statusText.text =
            $"Initialized: {initialized}\n" +
            $"Joined: {joined}\n" +
            $"Audio Muted: {audioMuted}\n" +
            $"Local Video: {localVideoEnabled}\n" +
            $"Transcript: {transcriptStatus}" +
            (string.IsNullOrWhiteSpace(assignedGroupName)
                ? string.Empty
                : $"\nMeeting Group: {assignedCompanyName} / {assignedGroupName}");
    }

    private sealed class TranscriptEntry
    {
        public string timestamp;
        public string participantIdentity;
        public string participantName;
        public string text;
        public int sequence;
    }

    private sealed class SimpleStreamingEventHandler : StreamingEventHandler
    {
        private readonly JorjinStreamingSimpleSample sample;

        public SimpleStreamingEventHandler(JorjinStreamingSimpleSample sample)
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
            sample.AddLog($"OnJoinChannelSuccess channel={connection.ChannelId}, uid={connection.LocalUid}, elapsed={elapsed}");
            sample.RefreshStatus();
        }

        public override void OnLeaveChannel(JJRtcConnection connection, JJRtcStats stats)
        {
            sample.joined = false;
            sample.AddLog($"OnLeaveChannel channel={connection.ChannelId}, uid={connection.LocalUid}");
            sample.RefreshStatus();
        }

        public override void OnConnectionStateChanged(JJRtcConnection connection, JJConnectionState state, JJConnectionChangedReason reason)
        {
            sample.AddLog($"OnConnectionStateChanged state={state}, reason={reason}");
        }

        public override void OnConnectionLost(JJRtcConnection connection)
        {
            sample.AddLog("OnConnectionLost");
        }

        public override void OnUserJoined(JJRtcConnection connection, uint uid, int elapsed)
        {
            sample.remoteUids.Add(uid);
            if (sample.remoteLowQuality &&
                sample.StreamingSDK.Backend == JJStreamingBackend.LiveKit)
            {
                sample.StreamingSDK.SetRemoteVideoStreamType(
                    uid,
                    JJVideoStreamType.LOW);
            }
            sample.AddLog($"OnUserJoined uid={uid}, elapsed={elapsed}");
            sample.RefreshStatus();
            if (sample.StreamingSDK.Backend != JJStreamingBackend.LiveKit)
            {
                sample.BindRemoteStreamSurface(connection, uid);
            }
        }

        public override void OnLiveKitConnected(string roomName, string localIdentity)
        {
            sample.joined = true;
            sample.channelName = roomName;
            sample.localIdentity = localIdentity;
            sample.transcriptAgentReady = false;
            sample.meetingView?.SetSdkReady(sample.initialized);
            sample.meetingView?.SetConnected(true);
            sample.meetingView?.SetLocalParticipant(localIdentity);
            sample.SetMeetingLayout(true);
            sample.SetMicrophoneUi(sample.audioMuted);
            sample.AddLog($"LiveKit connected room={roomName}, identity={localIdentity}");
            sample.RefreshStatus();
        }

        public override void OnLiveKitDisconnected(string roomName)
        {
            sample.AbortRecordingTransfer(true);
            sample.joined = false;
            sample.transcriptAgentReady = false;
            sample.transcriptRecording = false;
            sample.transcriptRequestPending = false;
            sample.previewEnabled = false;
            sample.remoteUids.Clear();
            sample.DisablePcmObserver();
            sample.AddLog($"LiveKit disconnected room={roomName}");
            sample.meetingView?.SetConnected(false);
            sample.SetMeetingLayout(false);
            sample.RefreshAdvancedControls();
            sample.RefreshStatus();
        }

        public override void OnLiveKitParticipantConnected(string identity)
        {
            sample.AddLog($"LiveKit participant joined: {identity}");
            sample.meetingView?.AddRemoteParticipant(identity);
            sample.RefreshStatus();
        }

        public override void OnLiveKitParticipantDisconnected(string identity)
        {
            sample.AddLog($"LiveKit participant left: {identity}");
            sample.meetingView?.RemoveParticipant(identity);
            sample.RefreshStatus();
        }

        public override void OnLiveKitLocalVideoTexture(Texture texture)
        {
            sample.meetingView?.BindLocalTexture(texture);
            sample.meetingView?.SetLocalVideoVisible(sample.localVideoEnabled);
            if (texture != null)
            {
                sample.localVideoSize = new Vector2Int(texture.width, texture.height);
                sample.RefreshStats();
            }
        }

        public override void OnLiveKitVideoTrackSubscribed(
            LiveKit.RemoteVideoTrack track,
            string participantIdentity,
            LiveKit.Proto.TrackSource source)
        {
            sample.AddLog($"LiveKit video subscribed: {participantIdentity}, source={source}");
            sample.meetingView?.BindRemoteTrack(track, participantIdentity, source);
        }

        public override void OnLiveKitVideoTrackUnsubscribed(
            LiveKit.RemoteVideoTrack track,
            string participantIdentity,
            LiveKit.Proto.TrackSource source)
        {
            sample.meetingView?.UnbindRemoteTrack(track, participantIdentity, source);
        }

        public override void OnLiveKitScreenShareChanged(bool sharing)
        {
            sample.screenSharing = sharing;
            sample.meetingView?.SetSharing(sharing);
            sample.AddLog($"LiveKit screen sharing: {sharing}");
        }

        public override void OnLiveKitReactionReceived(
            string participantIdentity,
            JJReactionType reaction)
        {
            sample.meetingView?.ShowReaction(participantIdentity, reaction);
            sample.AddLog($"{participantIdentity} reacted: {reaction}");
        }

        public override void OnLiveKitParticipantRole(string participantIdentity, string role)
        {
            sample.HandleParticipantRole(participantIdentity, role);
        }

        public override void OnLiveKitCollaborationPacket(string participantIdentity, string json)
        {
            if (LiveKitLiveTranslationView.IsTranslationPacket(json))
                sample.liveTranslation?.Receive(json);
            else
                sample.collaborationPanel?.Receive(participantIdentity, json);
        }

        public override void OnLiveKitTranscriptAgentStatus(
            string eventType,
            string state,
            string sessionId,
            string message)
        {
            sample.HandleTranscriptAgentStatus(
                eventType,
                state,
                sessionId,
                message);
        }

        public override void OnLiveKitTranscriptSegment(
            string sessionId,
            string timestamp,
            string participantIdentity,
            string participantName,
            string text,
            int sequence)
        {
            sample.HandleTranscriptSegment(
                sessionId,
                timestamp,
                participantIdentity,
                participantName,
                text,
                sequence);
        }

        public override void OnLiveKitTranscriptCompleted(
            string sessionId,
            int segmentCount,
            string message)
        {
            sample.HandleTranscriptCompleted(
                sessionId,
                segmentCount,
                message);
        }

        public override void OnLiveKitTranscriptRecordingStarted(
            string sessionId,
            string fileName,
            long totalBytes,
            int chunkCount,
            float durationSeconds,
            bool truncated)
        {
            sample.HandleTranscriptRecordingStarted(
                sessionId,
                fileName,
                totalBytes,
                chunkCount,
                durationSeconds,
                truncated);
        }

        public override void OnLiveKitTranscriptRecordingChunk(
            string sessionId,
            int chunkIndex,
            string base64Data)
        {
            sample.HandleTranscriptRecordingChunk(
                sessionId,
                chunkIndex,
                base64Data);
        }

        public override void OnLiveKitTranscriptRecordingCompleted(
            string sessionId,
            string fileName,
            long totalBytes,
            int chunkCount,
            string sha256,
            float durationSeconds,
            bool truncated)
        {
            sample.HandleTranscriptRecordingCompleted(
                sessionId,
                fileName,
                totalBytes,
                chunkCount,
                sha256,
                durationSeconds,
                truncated);
        }

        public override void OnLiveKitTranscriptRecordingError(
            string sessionId,
            string message)
        {
            sample.HandleTranscriptRecordingError(sessionId, message);
        }

        public override void OnUserOffline(JJRtcConnection connection, uint uid, JJUserOfflineReason reason)
        {
            sample.remoteUids.Remove(uid);
            sample.AddLog($"OnUserOffline uid={uid}, reason={reason}");
            sample.RefreshStatus();
            if (sample.StreamingSDK.Backend != JJStreamingBackend.LiveKit)
            {
                SetSurfaceEnabled(sample.remoteStreamSurface, false);
            }
        }

        public override void OnUserMuteAudio(JJRtcConnection connection, uint remoteUid, bool muted)
        {
            sample.AddLog($"OnUserMuteAudio uid={remoteUid}, muted={muted}");
        }

        public override void OnUserMuteVideo(JJRtcConnection connection, uint remoteUid, bool muted)
        {
            sample.AddLog($"OnUserMuteVideo uid={remoteUid}, muted={muted}");
        }

        public override void OnRemoteVideoStateChanged(JJRtcConnection connection, uint remoteUid, JJRemoteVideoState state, JJRemoteVideoStateReason reason, int elapsed)
        {
            sample.AddLog($"OnRemoteVideoStateChanged uid={remoteUid}, state={state}, reason={reason}");
        }

        public override void OnLocalVideoStateChanged(JJVideoSourceType source, JJLocalVideoStreamState state, JJLocalVideoStreamReason reason)
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
            sample.AddLog("OnTokenPrivilegeWillExpire");
            sample.RefreshLiveKitToken();
        }

        public override void OnRequestToken(JJRtcConnection connection)
        {
            sample.AddLog("OnRequestToken");
            sample.RefreshLiveKitToken();
        }
    }
}
