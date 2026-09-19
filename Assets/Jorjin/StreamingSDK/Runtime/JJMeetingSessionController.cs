using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Jorjin.Streaming
{
    /// <summary>
    /// Unity-facing meeting workflow for direct expert calls, support groups,
    /// equipment QR routing, scheduled meetings, internal invitations and
    /// external guest links.
    ///
    /// This component owns no LiveKit secret. It asks the application server
    /// for a short-lived connection, then raises ConnectionReady so the existing
    /// JorjinStreamingSDK can initialize and join the returned room.
    /// </summary>
    public sealed class JJMeetingSessionController : MonoBehaviour
    {
        [Header("Meeting Service")]
        [SerializeField] private string apiBaseUrl;
        [SerializeField] private string companyId;
        [Tooltip("Rotatable application device credential; never a LiveKit API secret.")]
        [SerializeField] private string deviceKey;
        [SerializeField, Range(5, 60)] private int timeoutSeconds = 15;

        [Header("Provisioned Device")]
        [Tooltip("SystemInfo.deviceUniqueIdentifier is used when empty.")]
        [SerializeField] private string deviceId;
        [SerializeField] private string participantName = "AR Field User";

        [Header("Incoming Calls")]
        [SerializeField] private bool pollIncomingCalls;
        [SerializeField, Range(2, 60)] private float incomingPollIntervalSeconds = 5f;
        [Tooltip("Set when this Unity client represents an expert rather than a field device.")]
        [SerializeField] private string localMemberId;

        [Header("Equipment QR / Deep Link")]
        [SerializeField] private bool enableEquipmentDeepLinks = true;
        [SerializeField] private string equipmentDeepLinkScheme = "ar-meeting";

        public event Action<JJMeetingSession> SessionUpdated;
        public event Action<JJMeetingSession> ConnectionReady;
        public event Action<JJMeetingSession[]> ScheduledMeetingsUpdated;
        public event Action<JJMeetingSession[]> IncomingSessionsUpdated;
        public event Action<JJMeetingDirectoryResponse> DirectoryUpdated;
        public event Action<JJMeetingInternalInviteResponse> InternalInviteCreated;
        public event Action<JJMeetingGuestInviteResponse> GuestInviteCreated;
        public event Action<string> RequestFailed;

        public JJMeetingSession ActiveSession { get; private set; }
        public JJMeetingSession[] ScheduledMeetings { get; private set; } =
            Array.Empty<JJMeetingSession>();
        public JJMeetingSession[] IncomingSessions { get; private set; } =
            Array.Empty<JJMeetingSession>();
        public JJMeetingDirectoryResponse Directory { get; private set; }
        public bool RequestInProgress { get; private set; }

        private bool initialDeepLinkHandled;
        private Coroutine incomingPollCoroutine;
        private bool incomingRequestInProgress;
        private string lastIncomingPollingError;
        private readonly Queue<string> queuedEquipmentDeepLinks = new();
        private int lifecycleVersion;

        private void OnEnable()
        {
            Application.deepLinkActivated -= HandleEquipmentDeepLink;
            Application.deepLinkActivated += HandleEquipmentDeepLink;
            if (pollIncomingCalls && HasMinimumConfiguration)
            {
                StartIncomingCallPolling();
            }
            TryProcessQueuedEquipmentDeepLink();
        }

        private void Start()
        {
            if (!initialDeepLinkHandled &&
                !string.IsNullOrWhiteSpace(Application.absoluteURL))
            {
                initialDeepLinkHandled = true;
                HandleEquipmentDeepLink(Application.absoluteURL);
            }
            if (pollIncomingCalls)
            {
                StartIncomingCallPolling();
            }
        }

        private void OnDisable()
        {
            Application.deepLinkActivated -= HandleEquipmentDeepLink;
            CancelPendingRequests();
        }

        public void Configure(
            string serviceUrl,
            string configuredCompanyId,
            string configuredDeviceKey,
            string configuredDeviceId,
            string configuredParticipantName,
            int requestTimeoutSeconds = 15,
            string configuredLocalMemberId = null)
        {
            apiBaseUrl = JJMeetingSessionClient.NormalizeBaseUrl(serviceUrl);
            companyId = configuredCompanyId?.Trim();
            deviceKey = configuredDeviceKey?.Trim();
            deviceId = configuredDeviceId?.Trim();
            participantName = configuredParticipantName?.Trim();
            timeoutSeconds = Mathf.Clamp(requestTimeoutSeconds, 5, 60);
            if (configuredLocalMemberId != null)
            {
                SetLocalMemberId(configuredLocalMemberId);
            }
            if (isActiveAndEnabled)
            {
                TryProcessQueuedEquipmentDeepLink();
            }
        }

        public void StartDirectExpertCall(
            string expertId,
            string title = "Remote expert support")
        {
            if (string.IsNullOrWhiteSpace(expertId))
            {
                Fail("Direct call requires an expert ID.");
                return;
            }

            StartCreate(new JJMeetingSessionRequest
            {
                route_type = JJMeetingRouteType.DirectExpert.ToApiValue(),
                target_expert_id = expertId.Trim(),
                title = title
            });
        }

        /// <summary>
        /// Reverse direct-call route used by a remote expert client to ring a
        /// provisioned AR device/field user.
        /// </summary>
        public void StartDirectDeviceCall(
            string targetDeviceId,
            string callerMemberId,
            string title = "Incoming expert call")
        {
            if (string.IsNullOrWhiteSpace(targetDeviceId) ||
                string.IsNullOrWhiteSpace(callerMemberId))
            {
                Fail("Device call requires target device and caller member IDs.");
                return;
            }
            StartCreate(new JJMeetingSessionRequest
            {
                route_type = JJMeetingRouteType.DirectExpert.ToApiValue(),
                target_device_id = targetDeviceId.Trim(),
                caller_member_id = callerMemberId.Trim(),
                title = title
            }, false);
        }

        public void SetLocalMemberId(string memberId)
        {
            localMemberId = memberId?.Trim();
        }

        public void StartExpertGroupCall(
            string groupId,
            string title = "Support group call")
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                Fail("Group call requires a support group ID.");
                return;
            }

            StartCreate(new JJMeetingSessionRequest
            {
                route_type = JJMeetingRouteType.ExpertGroup.ToApiValue(),
                support_group_id = groupId.Trim(),
                title = title
            });
        }

        /// <summary>
        /// Called by any QR scanner after it has read the equipment code.
        /// The QR contains an opaque routing payload, not an API secret.
        /// The application server resolves equipment, location, issue category
        /// and support group before issuing the LiveKit token. A production
        /// server should also verify a signed, expiring, one-time code.
        /// </summary>
        public void StartEquipmentSessionFromQr(
            string qrPayload,
            string title = "Equipment assistance")
        {
            if (string.IsNullOrWhiteSpace(qrPayload))
            {
                Fail("Equipment session requires a QR payload.");
                return;
            }

            StartCreate(new JJMeetingSessionRequest
            {
                route_type = JJMeetingRouteType.EquipmentQr.ToApiValue(),
                qr_payload = qrPayload.Trim(),
                title = title
            });
        }

        /// <summary>
        /// Handles a QR that opens the app with
        /// ar-meeting://equipment?code=&lt;opaque-routing-code&gt;.
        /// The full URL is sent to the server for routing and, in a production
        /// implementation, signature/expiry/replay validation.
        /// </summary>
        public void HandleEquipmentDeepLink(string url)
        {
            if (!enableEquipmentDeepLinks || string.IsNullOrWhiteSpace(url))
            {
                return;
            }
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri uri) ||
                !string.Equals(
                    uri.Scheme,
                    equipmentDeepLinkScheme,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    uri.Host,
                    "equipment",
                    StringComparison.OrdinalIgnoreCase))
            {
                Fail(
                    "Unsupported equipment link. Expected " +
                    equipmentDeepLinkScheme + "://equipment?code=<value>.");
                return;
            }
            if (string.IsNullOrWhiteSpace(uri.Query) ||
                uri.Query.IndexOf("code=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Fail("Equipment link is missing its opaque routing code.");
                return;
            }
            initialDeepLinkHandled = true;
            if (RequestInProgress)
            {
                const int maxQueuedLinks = 8;
                if (queuedEquipmentDeepLinks.Count >= maxQueuedLinks)
                {
                    queuedEquipmentDeepLinks.Dequeue();
                }
                queuedEquipmentDeepLinks.Enqueue(uri.AbsoluteUri);
                Debug.Log("Meeting request is busy; equipment deep link queued.");
                return;
            }
            StartEquipmentSessionFromQr(uri.AbsoluteUri);
        }

        public void ScheduleMeeting(
            string title,
            string scheduledStartUtc,
            string scheduledEndUtc,
            string[] participantIds,
            string supportGroupId = null,
            string[] participantDeviceIds = null)
        {
            if (!IsExpertClient)
            {
                Fail(
                    "Creating a scheduled meeting requires Local Member Id. " +
                    "AR device clients can view and join scheduled meetings.");
                return;
            }
            if (!TryNormalizeUtc(scheduledStartUtc, out string startUtc) ||
                !TryNormalizeUtc(scheduledEndUtc, out string endUtc))
            {
                Fail(
                    "Scheduled start/end must be valid ISO-8601 times, " +
                    "for example 2026-07-30T09:00:00Z.");
                return;
            }

            StartCreate(new JJMeetingSessionRequest
            {
                route_type = JJMeetingRouteType.Scheduled.ToApiValue(),
                title = title,
                caller_member_id = IsExpertClient ? localMemberId.Trim() : null,
                scheduled_start_utc = startUtc,
                scheduled_end_utc = endUtc,
                participant_ids = participantIds ?? Array.Empty<string>(),
                participant_device_ids =
                    participantDeviceIds ??
                    (IsExpertClient
                        ? Array.Empty<string>()
                        : new[] { ResolvedDeviceId }),
                support_group_id = supportGroupId
            }, !IsExpertClient);
        }

        public void RefreshScheduledMeetings()
        {
            if (!ValidateConfiguration() || RequestInProgress) return;
            StartCoroutine(RefreshScheduledMeetingsRoutine());
        }

        public void RefreshIncomingSessions()
        {
            if (!ValidateConfiguration() || incomingRequestInProgress) return;
            StartCoroutine(RefreshIncomingSessionsRoutine(false));
        }

        public void StartIncomingCallPolling()
        {
            if (!ValidateConfiguration()) return;
            pollIncomingCalls = true;
            if (incomingPollCoroutine == null && isActiveAndEnabled)
            {
                incomingPollCoroutine =
                    StartCoroutine(IncomingCallPollingRoutine());
            }
        }

        public void StopIncomingCallPolling()
        {
            pollIncomingCalls = false;
            StopIncomingCallPollingCoroutine();
        }

        /// <summary>
        /// Cancels this component's active HTTP/polling coroutines and clears
        /// their busy flags. Pending equipment deep links remain queued so they
        /// can be resumed when the workflow is enabled again.
        /// </summary>
        public void CancelPendingRequests()
        {
            lifecycleVersion++;
            StopAllCoroutines();
            incomingPollCoroutine = null;
            RequestInProgress = false;
            incomingRequestInProgress = false;
        }

        public void RefreshDirectory()
        {
            if (!ValidateConfiguration() || RequestInProgress) return;
            StartCoroutine(RefreshDirectoryRoutine());
        }

        public void JoinScheduledMeeting(string sessionId)
        {
            if (!ValidateConfiguration() || RequestInProgress) return;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Fail("Join requires a session ID.");
                return;
            }
            StartCoroutine(JoinSessionRoutine(sessionId.Trim()));
        }

        public void AcceptIncomingSession(string sessionId)
        {
            if (!ValidateConfiguration() || RequestInProgress) return;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Fail("Accept requires a session ID.");
                return;
            }
            StartCoroutine(AcceptIncomingSessionRoutine(sessionId.Trim()));
        }

        public void JoinActiveSession()
        {
            if (ActiveSession == null)
            {
                Fail("There is no active meeting session.");
                return;
            }
            if (ActiveSession.HasConnectionData)
            {
                ConnectionReady?.Invoke(ActiveSession);
                return;
            }
            JoinScheduledMeeting(ActiveSession.session_id);
        }

        /// <summary>
        /// Always exchanges the active Session for a new short-lived token.
        /// Use this from OnTokenPrivilegeWillExpire/OnRequestToken.
        /// </summary>
        public void RefreshActiveSessionConnection()
        {
            if (!ValidateActiveSession() || RequestInProgress) return;
            StartCoroutine(JoinSessionRoutine(ActiveSession.session_id));
        }

        public void InviteInternalMember(string memberId)
        {
            InviteInternalMembers(
                string.IsNullOrWhiteSpace(memberId)
                    ? Array.Empty<string>()
                    : new[] { memberId.Trim() });
        }

        public void InviteInternalMembers(string[] memberIds)
        {
            if (!ValidateActiveSession() || RequestInProgress) return;
            if (memberIds == null || memberIds.Length == 0)
            {
                Fail("Internal invitation requires at least one member ID.");
                return;
            }
            StartCoroutine(InviteInternalMembersRoutine(memberIds));
        }

        public void CreateExternalGuestLink(
            int validMinutes = 30,
            int maxUses = 1,
            bool canPublish = true,
            bool canSubscribe = true)
        {
            if (!ValidateActiveSession() || RequestInProgress) return;
            StartCoroutine(CreateExternalGuestLinkRoutine(
                Mathf.Clamp(validMinutes, 1, 1440),
                Mathf.Clamp(maxUses, 1, 100),
                canPublish,
                canSubscribe));
        }

        private void StartCreate(
            JJMeetingSessionRequest request,
            bool includeDeviceId = true)
        {
            if (!ValidateConfiguration() || RequestInProgress) return;
            request.company_id = companyId.Trim();
            request.device_id = includeDeviceId ? ResolvedDeviceId : null;
            request.participant_name = ResolvedParticipantName;
            StartCoroutine(CreateSessionRoutine(request));
        }

        private IEnumerator CreateSessionRoutine(JJMeetingSessionRequest request)
        {
            int requestVersion = lifecycleVersion;
            RequestInProgress = true;
            JJMeetingSession response = null;
            string error = null;
            try
            {
                yield return JJMeetingSessionClient.CreateSession(
                    apiBaseUrl,
                    deviceKey,
                    request,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null)
                {
                    Fail(error ?? "Meeting session could not be created.");
                    yield break;
                }

                ActiveSession = response;
                SessionUpdated?.Invoke(response);
                if (response.auto_join && response.HasConnectionData)
                {
                    ConnectionReady?.Invoke(response);
                }
            }
            finally
            {
                CompleteForegroundRequest(requestVersion);
            }
        }

        private IEnumerator JoinSessionRoutine(string sessionId)
        {
            int requestVersion = lifecycleVersion;
            RequestInProgress = true;
            JJMeetingSession response = null;
            string error = null;
            var request = new JJMeetingJoinRequest
            {
                company_id = companyId.Trim(),
                device_id = IsExpertClient ? null : ResolvedDeviceId,
                member_id = IsExpertClient ? localMemberId.Trim() : null,
                participant_name = ResolvedParticipantName,
                participant_identity = null
            };
            try
            {
                yield return JJMeetingSessionClient.JoinSession(
                    apiBaseUrl,
                    sessionId,
                    deviceKey,
                    request,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null || !response.HasConnectionData)
                {
                    Fail(error ?? "Meeting join response is missing connection data.");
                    yield break;
                }

                ActiveSession = response;
                SessionUpdated?.Invoke(response);
                ConnectionReady?.Invoke(response);
            }
            finally
            {
                CompleteForegroundRequest(requestVersion);
            }
        }

        private IEnumerator RefreshScheduledMeetingsRoutine()
        {
            int requestVersion = lifecycleVersion;
            RequestInProgress = true;
            JJMeetingSessionListResponse response = null;
            string error = null;
            try
            {
                yield return JJMeetingSessionClient.FetchScheduledSessions(
                    apiBaseUrl,
                    companyId,
                    IsExpertClient ? null : ResolvedDeviceId,
                    IsExpertClient ? localMemberId.Trim() : null,
                    deviceKey,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null)
                {
                    Fail(error ?? "Scheduled meetings could not be loaded.");
                    yield break;
                }
                ScheduledMeetings =
                    response.sessions ?? Array.Empty<JJMeetingSession>();
                ScheduledMeetingsUpdated?.Invoke(ScheduledMeetings);
            }
            finally
            {
                CompleteForegroundRequest(requestVersion);
            }
        }

        private IEnumerator IncomingCallPollingRoutine()
        {
            while (pollIncomingCalls)
            {
                if (!incomingRequestInProgress)
                {
                    yield return RefreshIncomingSessionsRoutine(true);
                }
                yield return new WaitForSecondsRealtime(
                    Mathf.Clamp(incomingPollIntervalSeconds, 2f, 60f));
            }
            incomingPollCoroutine = null;
        }

        private IEnumerator RefreshIncomingSessionsRoutine(bool silent)
        {
            int requestVersion = lifecycleVersion;
            incomingRequestInProgress = true;
            JJMeetingSessionListResponse response = null;
            string error = null;
            try
            {
                yield return JJMeetingSessionClient.FetchIncomingSessions(
                    apiBaseUrl,
                    companyId,
                    IsExpertClient ? null : ResolvedDeviceId,
                    IsExpertClient ? localMemberId.Trim() : null,
                    deviceKey,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null)
                {
                    if (!silent ||
                        !string.Equals(
                            lastIncomingPollingError,
                            error,
                            StringComparison.Ordinal))
                    {
                        Fail(error ?? "Incoming calls could not be loaded.");
                    }
                    lastIncomingPollingError = error;
                    yield break;
                }
                lastIncomingPollingError = null;
                IncomingSessions =
                    response.sessions ?? Array.Empty<JJMeetingSession>();
                IncomingSessionsUpdated?.Invoke(IncomingSessions);
            }
            finally
            {
                if (requestVersion == lifecycleVersion)
                {
                    incomingRequestInProgress = false;
                }
            }
        }

        private IEnumerator AcceptIncomingSessionRoutine(string sessionId)
        {
            int requestVersion = lifecycleVersion;
            RequestInProgress = true;
            JJMeetingSession response = null;
            string error = null;
            var request = new JJMeetingAcceptRequest
            {
                company_id = companyId.Trim(),
                device_id = IsExpertClient ? null : ResolvedDeviceId,
                member_id = IsExpertClient ? localMemberId.Trim() : null,
                participant_name = ResolvedParticipantName,
                participant_identity = null
            };
            try
            {
                yield return JJMeetingSessionClient.AcceptSession(
                    apiBaseUrl,
                    sessionId,
                    deviceKey,
                    request,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null || !response.HasConnectionData)
                {
                    Fail(error ?? "Incoming call could not be accepted.");
                    yield break;
                }
                ActiveSession = response;
                SessionUpdated?.Invoke(response);
                ConnectionReady?.Invoke(response);
            }
            finally
            {
                CompleteForegroundRequest(requestVersion);
            }
        }

        private IEnumerator RefreshDirectoryRoutine()
        {
            int requestVersion = lifecycleVersion;
            RequestInProgress = true;
            JJMeetingDirectoryResponse response = null;
            string error = null;
            try
            {
                yield return JJMeetingSessionClient.FetchDirectory(
                    apiBaseUrl,
                    companyId,
                    deviceKey,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null)
                {
                    Fail(error ?? "Company meeting directory could not be loaded.");
                    yield break;
                }
                Directory = response;
                DirectoryUpdated?.Invoke(response);
            }
            finally
            {
                CompleteForegroundRequest(requestVersion);
            }
        }

        private IEnumerator InviteInternalMembersRoutine(string[] memberIds)
        {
            int requestVersion = lifecycleVersion;
            RequestInProgress = true;
            JJMeetingInternalInviteResponse response = null;
            string error = null;
            var request = new JJMeetingInternalInviteRequest
            {
                company_id = companyId.Trim(),
                inviter_identity = ActiveSession.participant_identity,
                member_ids = memberIds
            };
            try
            {
                yield return JJMeetingSessionClient.InviteInternalMembers(
                    apiBaseUrl,
                    ActiveSession.session_id,
                    deviceKey,
                    request,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null)
                {
                    Fail(error ?? "Internal invitation could not be created.");
                    yield break;
                }
                InternalInviteCreated?.Invoke(response);
            }
            finally
            {
                CompleteForegroundRequest(requestVersion);
            }
        }

        private IEnumerator CreateExternalGuestLinkRoutine(
            int validMinutes,
            int maxUses,
            bool canPublish,
            bool canSubscribe)
        {
            int requestVersion = lifecycleVersion;
            RequestInProgress = true;
            JJMeetingGuestInviteResponse response = null;
            string error = null;
            var request = new JJMeetingGuestInviteRequest
            {
                company_id = companyId.Trim(),
                inviter_identity = ActiveSession.participant_identity,
                expires_in_minutes = validMinutes,
                max_uses = maxUses,
                can_publish = canPublish,
                can_subscribe = canSubscribe
            };
            try
            {
                yield return JJMeetingSessionClient.CreateGuestInvite(
                    apiBaseUrl,
                    ActiveSession.session_id,
                    deviceKey,
                    request,
                    timeoutSeconds,
                    result => response = result,
                    message => error = message);
                if (requestVersion != lifecycleVersion) yield break;

                if (response == null)
                {
                    Fail(error ?? "External guest link could not be created.");
                    yield break;
                }
                GuestInviteCreated?.Invoke(response);
            }
            finally
            {
                CompleteForegroundRequest(requestVersion);
            }
        }

        private void CompleteForegroundRequest(int requestVersion)
        {
            if (requestVersion != lifecycleVersion)
            {
                return;
            }
            RequestInProgress = false;
            TryProcessQueuedEquipmentDeepLink();
        }

        private bool ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                Fail("Meeting service URL is empty.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(companyId))
            {
                Fail("Company ID is empty.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(deviceKey))
            {
                Fail("Meeting device credential is empty.");
                return false;
            }
            if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out Uri serviceUri))
            {
                Fail("Meeting service URL is invalid.");
                return false;
            }
            bool usesHttps = string.Equals(
                serviceUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase);
            bool usesDebugLoopbackHttp =
                string.Equals(
                    serviceUri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) &&
                (Application.isEditor || Debug.isDebugBuild) &&
                IsLoopbackDevelopmentHost(serviceUri);
            if (!usesHttps && !usesDebugLoopbackHttp)
            {
                Fail(
                    "Meeting service must use HTTPS. A loopback HTTP URL is " +
                    "allowed only in the Unity Editor or a Development Build.");
                return false;
            }
            return true;
        }

        private void StopIncomingCallPollingCoroutine()
        {
            if (incomingPollCoroutine == null) return;
            StopCoroutine(incomingPollCoroutine);
            incomingPollCoroutine = null;
            incomingRequestInProgress = false;
        }

        private bool ValidateActiveSession()
        {
            if (!ValidateConfiguration()) return false;
            if (ActiveSession == null ||
                string.IsNullOrWhiteSpace(ActiveSession.session_id))
            {
                Fail("Join or create a meeting session before inviting participants.");
                return false;
            }
            return true;
        }

        private string ResolvedDeviceId =>
            string.IsNullOrWhiteSpace(deviceId)
                ? SystemInfo.deviceUniqueIdentifier
                : deviceId.Trim();

        private string ResolvedParticipantName =>
            string.IsNullOrWhiteSpace(participantName)
                ? "AR Field User"
                : participantName.Trim();

        private bool IsExpertClient =>
            !string.IsNullOrWhiteSpace(localMemberId);

        private bool HasMinimumConfiguration =>
            !string.IsNullOrWhiteSpace(apiBaseUrl) &&
            !string.IsNullOrWhiteSpace(companyId) &&
            !string.IsNullOrWhiteSpace(deviceKey);

        private static bool IsLoopbackDevelopmentHost(Uri serviceUri)
        {
            if (serviceUri == null) return false;
            return serviceUri.IsLoopback ||
                   string.Equals(
                       serviceUri.Host,
                       "10.0.2.2",
                       StringComparison.OrdinalIgnoreCase);
        }

        private void TryProcessQueuedEquipmentDeepLink()
        {
            if (!isActiveAndEnabled ||
                RequestInProgress ||
                queuedEquipmentDeepLinks.Count == 0 ||
                !HasMinimumConfiguration)
            {
                return;
            }

            string nextUrl = queuedEquipmentDeepLinks.Dequeue();
            StartEquipmentSessionFromQr(nextUrl);
        }

        private static bool TryNormalizeUtc(string value, out string result)
        {
            if (DateTimeOffset.TryParse(value, out DateTimeOffset parsed))
            {
                result = parsed.ToUniversalTime().ToString("o");
                return true;
            }
            result = null;
            return false;
        }

        private void Fail(string message)
        {
            Debug.LogError(message);
            RequestFailed?.Invoke(message);
        }
    }
}
