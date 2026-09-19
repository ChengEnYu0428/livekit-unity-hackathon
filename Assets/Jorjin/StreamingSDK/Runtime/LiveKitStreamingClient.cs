using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using LiveKit;
using LiveKit.Proto;
using UnityEngine;

namespace Jorjin.Streaming
{
    /// <summary>
    /// LiveKit implementation used by JorjinStreamingSDK. It owns every LiveKit
    /// source/track/stream so leaving a room does not leak native resources.
    /// </summary>
    internal sealed class LiveKitStreamingClient
    {
        private const string ReactionTopic = "jorjin.meeting.reaction.v1";
        private const string TranscriptControlTopic = "jorjin.transcript.control.v1";
        private const string TranscriptEventTopic = "jorjin.transcript.event.v1";
        private const string CollaborationControlTopic = "jorjin.collaboration.control.v1";
        private const string CollaborationEventTopic = "jorjin.collaboration.event.v1";
        private const string CollaborationImageTopic = "jorjin.collaboration.image.v1";
        // Meeting role ("field" = 場域端, "expert" = 專家端). Sent as data so it
        // works with any token; attributes would need canUpdateOwnMetadata.
        private const string RoleTopic = "jorjin.meeting.role.v1";
        private const int MaxCollaborationImageBytes = 4_000_000;
        private const string AgentIdentityPrefix = "agent-";
        private const string JorjinArCameraDeviceId = "jorjin-ar-glasses-camera";
        private const int ResultUnsupported = -2;
        private const long TokenWillExpireWarningSeconds = 60;
        private const float AudioStatsIntervalSeconds = 2f;

        private readonly MonoBehaviour owner;
        private readonly JJStreamingProfile profile;
        private readonly StreamingEventHandler eventHandler;
        private readonly Dictionary<string, AudioStream> remoteAudioStreams = new();
        private readonly Dictionary<string, GameObject> remoteAudioObjects = new();
        private readonly Dictionary<string, RemoteAudioTrack> remoteAudioTracks = new();
        private readonly Dictionary<string, string> remoteAudioParticipantIdentities =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> identityToLegacyUid =
            new(StringComparer.Ordinal);
        private readonly Dictionary<uint, string> legacyUidToIdentity = new();
        private readonly HashSet<string> connectedParticipantIdentities =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> subscribedRemoteVideoSids =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> mutedRemoteTrackSids =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, JJVideoStreamType>
            remoteVideoQualityByIdentity = new(StringComparer.Ordinal);

        private Room room;
        private Coroutine joinCoroutine;
        private Coroutine cameraCoroutine;
        private Coroutine screenCoroutine;
        private Coroutine tokenMonitorCoroutine;
        private Coroutine audioStatsCoroutine;
        private Coroutine previewCoroutine;
        private MicrophoneSource microphoneSource;
        private LocalAudioTrack microphoneTrack;
        private GameObject microphoneObject;
        private int microphoneFrameCount;
        private string selectedMicrophoneDevice;
        private string activeMicrophoneDevice;
        private WebCamTexture webCamTexture;
        private JorjinArCameraCapture jorjinCameraCapture;
        private RtcVideoSource cameraSource;
        private LocalVideoTrack cameraTrack;
        private string selectedVideoDevice;
        private WebCamTexture previewWebCamTexture;
        private Texture currentLocalVideoTexture;
        private bool localPreviewEnabled = true;
        private bool muteAllRemoteAudio;
        private bool muteAllRemoteVideo;
        private JJVideoEncoderConfiguration videoEncoderConfiguration =
            new()
            {
                width = 1280,
                height = 720,
                frameRate = 30,
                bitrate = -1,
                maintainResolution = false
            };
        private JJRemoteAudioFrameConfig remoteAudioFrameConfig;
        private Action<JJRemoteAudioFrame> remoteAudioFrameReceived;
        private RtcVideoSource screenSource;
        private LocalVideoTrack screenTrack;
        private bool disposed;
        private bool leaving;
        private bool leaveCallbackSent;
        private bool joinCallbackSent;
        private bool connectionLostNotified;
        private bool tokenWillExpireNotified;
        private bool tokenRequestNotified;
        private bool publishingAllowedByRole = true;
        private bool audioEnabled = true;
        private bool videoEnabled = true;
        private int sessionGeneration;
        private int microphonePublishGeneration = -1;
        private int cameraPublishGeneration = -1;
        private int screenPublishGeneration = -1;
        private int microphoneDeviceSelectionVersion;
        private int videoDeviceSelectionVersion;
        private bool microphoneRestartRequested;
        private bool cameraRestartRequested;
        private bool cameraUpdateActive;
        private bool screenUpdateActive;
        private JJConnectionState? lastConnectionState;
        private JJConnectionChangedReason? lastConnectionReason;

        // Room.IsConnected in the currently integrated Unity LiveKit package
        // can return false after Connect() has completed successfully. Use the
        // connection lifecycle that this compatibility client already owns;
        // otherwise reactions and transcript controls are rejected immediately
        // after OnJoinChannelSuccess even though the participant is in the room.
        public bool Connected =>
            !disposed &&
            !leaving &&
            !leaveCallbackSent &&
            joinCallbackSent &&
            room?.LocalParticipant != null;
        public bool IsScreenSharing => screenTrack != null;

        public LiveKitStreamingClient(
            MonoBehaviour owner,
            JJStreamingProfile profile,
            StreamingEventHandler eventHandler)
        {
            this.owner = owner;
            this.profile = profile;
            this.eventHandler = eventHandler;
        }

        public int Join()
        {
            if (disposed || owner == null || string.IsNullOrWhiteSpace(profile.LiveKitUrl) ||
                string.IsNullOrWhiteSpace(profile.Token))
            {
                return -1;
            }

            if (Connected || joinCoroutine != null)
            {
                return 0;
            }

            // A standalone WebCamTexture preview owns the same camera device.
            // Release it before LiveKit creates the publishing source.
            StopStandalonePreview(false);

            int generation = unchecked(++sessionGeneration);
            ResetSessionState();
            joinCoroutine = StartOwnerCoroutine(ConnectRoutine(generation));
            return joinCoroutine != null ? 0 : -1;
        }

        private IEnumerator ConnectRoutine(int generation)
        {
            try
            {
                yield return ConnectSessionRoutine(generation);
            }
            finally
            {
                // A canceled, older connection attempt must never clear the
                // coroutine handle belonging to a newer Join call.
                if (sessionGeneration == generation)
                {
                    joinCoroutine = null;
                }
            }
        }

        private IEnumerator ConnectSessionRoutine(int generation)
        {
            var connectingRoom = new Room();
            if (!IsSessionCurrent(generation, null, false))
            {
                yield break;
            }
            room = connectingRoom;
            BindRoomEvents(connectingRoom);
            NotifyConnectionState(
                JJConnectionState.CONNECTING,
                JJConnectionChangedReason.CONNECTING);

            ConnectInstruction connect;
            try
            {
                connect = connectingRoom.Connect(
                    profile.LiveKitUrl,
                    profile.Token,
                    new LiveKit.RoomOptions
                    {
                        AutoSubscribe = true,
                        Dynacast = true,
                        AdaptiveStream = true
                    });
            }
            catch (Exception exception)
            {
                if (IsSessionCurrent(generation, connectingRoom, false))
                {
                    NotifyConnectionState(
                        JJConnectionState.FAILED,
                        JJConnectionChangedReason.JOIN_FAILED);
                    ReportError(
                        "LiveKit connection request failed: " +
                        exception.Message);
                    UnbindRoomEvents(connectingRoom);
                    room = null;
                }
                yield break;
            }
            yield return connect;

            if (!IsSessionCurrent(generation, connectingRoom, false))
            {
                CleanupLateConnectedRoom(connectingRoom);
                yield break;
            }

            if (connect.IsError)
            {
                NotifyConnectionState(
                    JJConnectionState.FAILED,
                    JJConnectionChangedReason.JOIN_FAILED);
                ReportError("LiveKit connection failed.");
                UnbindRoomEvents(connectingRoom);
                try
                {
                    connectingRoom.Disconnect();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "LiveKit cleanup after connection failure failed: " +
                        exception.Message);
                }
                room = null;
                yield break;
            }

            string identity =
                connectingRoom.LocalParticipant?.Identity ??
                profile.ParticipantIdentity ??
                string.Empty;
            GetOrCreateLegacyUid(identity);
            NotifyConnectionState(
                JJConnectionState.CONNECTED,
                JJConnectionChangedReason.JOIN_SUCCESS);
            if (!joinCallbackSent)
            {
                joinCallbackSent = true;
                eventHandler?.OnLiveKitConnected(connectingRoom.Name, identity);
                PublishRole(null);
                eventHandler?.OnJoinChannelSuccess(CurrentConnection(), 0);
            }

            // User callbacks can synchronously call Leave/Dispose. Validate the
            // attempt again before replaying participants or starting capture.
            if (!IsSessionCurrent(generation, connectingRoom))
            {
                CleanupLateConnectedRoom(connectingRoom);
                yield break;
            }

            // Participants that were already in the room can exist before the
            // ParticipantConnected callback is bound by the native client.
            // Replaying the current collection is safe because the meeting UI
            // de-duplicates participants by identity.
            foreach (RemoteParticipant participant in connectingRoom.RemoteParticipants.Values)
            {
                NotifyParticipantConnected(participant);
                ReplayParticipantTracks(participant);
            }

            StartSessionMonitors();

            if (publishingAllowedByRole && audioEnabled)
            {
                yield return PublishMicrophoneRoutine(
                    generation,
                    connectingRoom);
            }

            if (!IsSessionCurrent(generation, connectingRoom))
            {
                yield break;
            }

            if (publishingAllowedByRole && videoEnabled)
            {
                yield return PublishCameraRoutine(
                    generation,
                    connectingRoom);
            }
        }

        public void SetAudioEnabled(bool enabled)
        {
            audioEnabled = enabled;
            if (microphoneTrack != null)
            {
                ((ILocalTrack)microphoneTrack).SetMute(!enabled);
            }
            else if (enabled && publishingAllowedByRole && Connected)
            {
                StartMicrophonePublishIfNeeded();
            }
            if (!enabled)
            {
                microphoneRestartRequested = false;
            }
        }

        public void SetVideoEnabled(bool enabled)
        {
            videoEnabled = enabled;
            if (!Connected || IsScreenSharing || !publishingAllowedByRole)
            {
                return;
            }

            if (enabled && cameraTrack == null)
            {
                StartCameraPublishIfNeeded();
            }
            else if (!enabled)
            {
                cameraRestartRequested = false;
                StopCamera();
            }
        }

        public void SetClientRole(JJClientRoleType role)
        {
            bool allowPublishing = role == JJClientRoleType.BROADCASTER;
            if (publishingAllowedByRole == allowPublishing)
            {
                return;
            }

            publishingAllowedByRole = allowPublishing;
            if (!allowPublishing)
            {
                microphoneRestartRequested = false;
                cameraRestartRequested = false;
                // LiveKit publisher permissions are granted by the access token.
                // Locally becoming an audience member therefore means explicitly
                // unpublishing every local media track.
                StopScreen();
                StopCamera();
                StopMicrophone();
                Debug.Log(
                    "LiveKit client role set to AUDIENCE locally; all local tracks " +
                    "were unpublished. Server-side token permissions remain unchanged.");
                return;
            }

            Debug.LogWarning(
                "LiveKit client role set to BROADCASTER locally. Publishing will only " +
                "succeed when the current token grants canPublish permission.");
            if (!Connected)
            {
                return;
            }

            if (audioEnabled && microphoneTrack == null)
            {
                StartMicrophonePublishIfNeeded();
            }
            if (videoEnabled && cameraTrack == null && !IsScreenSharing)
            {
                StartCameraPublishIfNeeded();
            }
        }

        public JJDeviceInfo[] GetRecordingDevices()
        {
            string[] devices = Microphone.devices ?? Array.Empty<string>();
            var result = new JJDeviceInfo[devices.Length];
            for (int index = 0; index < devices.Length; index++)
            {
                result[index] = new JJDeviceInfo
                {
                    deviceId = devices[index],
                    deviceName = devices[index],
                    deviceTypeName = "LiveKit/Unity Microphone"
                };
            }
            return result;
        }

        public int SetRecordingDevice(string deviceId)
        {
            string normalized = string.IsNullOrWhiteSpace(deviceId)
                ? null
                : deviceId.Trim();
            if (normalized != null &&
                !ContainsDevice(Microphone.devices, normalized))
            {
                Debug.LogWarning(
                    $"LiveKit recording device is unavailable: '{normalized}'.");
                return -1;
            }

            if (!string.Equals(
                    selectedMicrophoneDevice,
                    normalized,
                    StringComparison.Ordinal))
            {
                microphoneDeviceSelectionVersion++;
            }
            selectedMicrophoneDevice = normalized;
            if (Connected && publishingAllowedByRole && audioEnabled)
            {
                microphoneRestartRequested = true;
                StopMicrophone();
                StartMicrophonePublishIfNeeded();
            }
            return 0;
        }

        public JJDeviceInfo[] GetPlaybackDevices()
        {
            Debug.LogWarning(
                "LiveKit playback-device enumeration is unsupported by Unity's " +
                "AudioSource output API. Unity uses the operating-system default output.");
            return Array.Empty<JJDeviceInfo>();
        }

        public int SetPlaybackDevice(string deviceId)
        {
            Debug.LogWarning(
                "LiveKit playback-device selection is unsupported by Unity's " +
                "AudioSource output API. Change the output device in the operating system.");
            return ResultUnsupported;
        }

        public JJDeviceInfo[] GetVideoDevices()
        {
            var result = new List<JJDeviceInfo>();
#if UNITY_ANDROID && !UNITY_EDITOR
            result.Add(new JJDeviceInfo
            {
                deviceId = JorjinArCameraDeviceId,
                deviceName = "Jorjin AR Glasses Camera",
                deviceTypeName = "JJSDK UVC Camera"
            });
#endif
            WebCamDevice[] devices = WebCamTexture.devices ?? Array.Empty<WebCamDevice>();
            foreach (WebCamDevice device in devices)
            {
                result.Add(new JJDeviceInfo
                {
                    deviceId = device.name,
                    deviceName = device.name,
                    deviceTypeName = device.isFrontFacing
                        ? "LiveKit/Unity Front Camera"
                        : "LiveKit/Unity Camera"
                });
            }
            return result.ToArray();
        }

        public int SetVideoDevice(string deviceId)
        {
            string normalized = string.IsNullOrWhiteSpace(deviceId)
                ? null
                : deviceId.Trim();
            bool isAvailable = normalized == null;
#if UNITY_ANDROID && !UNITY_EDITOR
            isAvailable |= string.Equals(
                normalized,
                JorjinArCameraDeviceId,
                StringComparison.Ordinal);
#endif
            if (!isAvailable)
            {
                WebCamDevice[] devices =
                    WebCamTexture.devices ?? Array.Empty<WebCamDevice>();
                foreach (WebCamDevice device in devices)
                {
                    if (string.Equals(
                            device.name,
                            normalized,
                            StringComparison.Ordinal))
                    {
                        isAvailable = true;
                        break;
                    }
                }
            }

            if (!isAvailable)
            {
                Debug.LogWarning(
                    $"LiveKit video device is unavailable: '{normalized}'.");
                return -1;
            }

            if (!string.Equals(
                    selectedVideoDevice,
                    normalized,
                    StringComparison.Ordinal))
            {
                videoDeviceSelectionVersion++;
            }
            selectedVideoDevice = normalized;
            if (Connected && publishingAllowedByRole && videoEnabled &&
                !IsScreenSharing)
            {
                cameraRestartRequested = true;
                StopCamera();
                StartCameraPublishIfNeeded();
            }
            return 0;
        }

        public void StartPreview()
        {
            localPreviewEnabled = true;
            if (currentLocalVideoTexture != null)
            {
                eventHandler?.OnLiveKitLocalVideoTexture(
                    currentLocalVideoTexture);
                return;
            }

            if (!Connected && previewCoroutine == null)
            {
                previewCoroutine = StartOwnerCoroutine(
                    StartStandalonePreviewRoutine());
            }
        }

        public void StopPreview()
        {
            localPreviewEnabled = false;
            StopStandalonePreview(false);
            eventHandler?.OnLiveKitLocalVideoTexture(null);
        }

        private IEnumerator StartStandalonePreviewRoutine()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                bool tryGlasses =
                    string.IsNullOrWhiteSpace(selectedVideoDevice) ||
                    string.Equals(
                        selectedVideoDevice,
                        JorjinArCameraDeviceId,
                        StringComparison.Ordinal);
                if (tryGlasses)
                {
                    jorjinCameraCapture =
                        owner.GetComponent<JorjinArCameraCapture>();
                    if (jorjinCameraCapture == null)
                    {
                        jorjinCameraCapture = owner.gameObject
                            .AddComponent<JorjinArCameraCapture>();
                    }
                    yield return jorjinCameraCapture.StartCapture();
                    if (localPreviewEnabled && !Connected &&
                        jorjinCameraCapture != null &&
                        jorjinCameraCapture.IsCapturing &&
                        jorjinCameraCapture.ActiveTexture != null)
                    {
                        PublishLocalVideoTexture(
                            jorjinCameraCapture.ActiveTexture);
                        yield break;
                    }
                }
#endif

                if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                {
                    yield return Application.RequestUserAuthorization(
                        UserAuthorization.WebCam);
                }
                if (!localPreviewEnabled || Connected ||
                    !Application.HasUserAuthorization(UserAuthorization.WebCam))
                {
                    yield break;
                }

                WebCamDevice[] devices =
                    WebCamTexture.devices ?? Array.Empty<WebCamDevice>();
                if (devices.Length == 0)
                {
                    Debug.LogWarning(
                        "LiveKit preview could not find a Unity camera device.");
                    yield break;
                }

                WebCamDevice selected = devices[0];
                if (!string.IsNullOrWhiteSpace(selectedVideoDevice))
                {
                    foreach (WebCamDevice device in devices)
                    {
                        if (string.Equals(
                                device.name,
                                selectedVideoDevice,
                                StringComparison.Ordinal))
                        {
                            selected = device;
                            break;
                        }
                    }
                }
                else
                {
                    foreach (WebCamDevice device in devices)
                    {
                        if (GetCameraPriority(device) >
                            GetCameraPriority(selected))
                        {
                            selected = device;
                        }
                    }
                }

                previewWebCamTexture = new WebCamTexture(
                    selected.name,
                    videoEncoderConfiguration.width,
                    videoEncoderConfiguration.height,
                    videoEncoderConfiguration.frameRate);
                previewWebCamTexture.Play();
                int framesRemaining = 180;
                while (framesRemaining-- > 0 && localPreviewEnabled &&
                       !Connected && previewWebCamTexture != null &&
                       (previewWebCamTexture.width <= 16 ||
                        !previewWebCamTexture.didUpdateThisFrame))
                {
                    yield return null;
                }

                if (localPreviewEnabled && !Connected &&
                    previewWebCamTexture != null &&
                    previewWebCamTexture.width > 16)
                {
                    PublishLocalVideoTexture(previewWebCamTexture);
                }
                else
                {
                    StopStandalonePreview(false);
                }
            }
            finally
            {
                previewCoroutine = null;
            }
        }

        private void StopStandalonePreview(bool notifySurface)
        {
            StopOwnerCoroutine(ref previewCoroutine);
            if (previewWebCamTexture != null)
            {
                try
                {
                    previewWebCamTexture.Stop();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "LiveKit preview cleanup failed: " + exception.Message);
                }
                UnityEngine.Object.Destroy(previewWebCamTexture);
                if (ReferenceEquals(
                        currentLocalVideoTexture,
                        previewWebCamTexture))
                {
                    currentLocalVideoTexture = null;
                }
                previewWebCamTexture = null;
            }
            if (notifySurface)
            {
                eventHandler?.OnLiveKitLocalVideoTexture(null);
            }
        }

        public int SetVideoEncoderConfiguration(
            JJVideoEncoderConfiguration config)
        {
            if (config == null || config.width <= 0 || config.height <= 0 ||
                config.frameRate <= 0 || config.bitrate == 0 ||
                config.bitrate < -1)
            {
                return -1;
            }

            videoEncoderConfiguration = new JJVideoEncoderConfiguration
            {
                width = config.width,
                height = config.height,
                frameRate = config.frameRate,
                bitrate = config.bitrate,
                maintainResolution = config.maintainResolution
            };

            if (Connected && publishingAllowedByRole && videoEnabled &&
                !IsScreenSharing)
            {
                cameraRestartRequested = true;
                StopCamera();
                StartCameraPublishIfNeeded();
            }
            else if (previewWebCamTexture != null)
            {
                StopStandalonePreview(false);
                StartPreview();
            }
            return 0;
        }

        public int SetVideoEncoderConfigurationEx(
            JJVideoEncoderConfiguration config,
            JJRtcConnection connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.ChannelId) &&
                !string.Equals(
                    connection.ChannelId,
                    profile.ChannelName,
                    StringComparison.Ordinal))
            {
                return -1;
            }
            return SetVideoEncoderConfiguration(config);
        }

        public int SetRemoteVideoStreamType(
            uint uid,
            JJVideoStreamType streamType)
        {
            if (!Connected || !legacyUidToIdentity.TryGetValue(
                    uid,
                    out string identity))
            {
                return -1;
            }

            remoteVideoQualityByIdentity[identity] = streamType;
            bool applied = false;
            foreach (RemoteParticipant participant
                     in room.RemoteParticipants.Values)
            {
                if (!string.Equals(
                        ParticipantIdentity(participant),
                        identity,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (RemoteTrackPublication publication
                         in participant.Tracks.Values)
                {
                    if (publication.Kind != TrackKind.KindVideo)
                    {
                        continue;
                    }
                    publication.SetVideoQuality(
                        streamType == JJVideoStreamType.LOW
                            ? VideoQuality.Low
                            : VideoQuality.High);
                    applied = true;
                }
            }
            return applied ? 0 : -1;
        }

        public int SetCameraCapturerConfiguration(
            JJCameraCapturerConfiguration option)
        {
            if (option == null)
            {
                return -1;
            }
            if (option.cameraId != null && option.cameraId.HasValue)
            {
                return SetVideoDevice(option.cameraId.Value);
            }
            if (option.cameraDirection == null ||
                !option.cameraDirection.HasValue)
            {
                return 0;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (option.cameraDirection.Value == JJCameraDirection.REAR &&
                (string.IsNullOrWhiteSpace(selectedVideoDevice) ||
                 string.Equals(
                     selectedVideoDevice,
                     JorjinArCameraDeviceId,
                     StringComparison.Ordinal)))
            {
                return SetVideoDevice(JorjinArCameraDeviceId);
            }
#endif
            bool front =
                option.cameraDirection.Value == JJCameraDirection.FRONT;
            foreach (WebCamDevice device in
                     WebCamTexture.devices ?? Array.Empty<WebCamDevice>())
            {
                if (device.isFrontFacing == front)
                {
                    return SetVideoDevice(device.name);
                }
            }
            Debug.LogWarning(
                $"LiveKit could not find the requested {(front ? "front" : "rear")} camera.");
            return -1;
        }

        public void MuteAllRemoteAudioStreams(bool status)
        {
            muteAllRemoteAudio = status;
            SetRemoteSubscriptions(TrackKind.KindAudio, !status);
        }

        public void MuteAllRemoteVideoStreams(bool status)
        {
            muteAllRemoteVideo = status;
            SetRemoteSubscriptions(TrackKind.KindVideo, !status);
        }

        private void SetRemoteSubscriptions(
            TrackKind kind,
            bool subscribed)
        {
            if (room == null)
            {
                return;
            }
            foreach (RemoteParticipant participant
                     in room.RemoteParticipants.Values)
            {
                foreach (RemoteTrackPublication publication
                         in participant.Tracks.Values)
                {
                    if (publication.Kind == kind &&
                        publication.Subscribed != subscribed)
                    {
                        publication.SetSubscribed(subscribed);
                    }
                }
            }
        }

        public int SetAudioScenario(int scenarioType)
        {
            Debug.LogWarning(
                "LiveKit uses Unity's microphone/audio processing pipeline; " +
                "Agora Audio Scenario presets are unsupported by this backend.");
            return ResultUnsupported;
        }

        public int RenewToken(string token)
        {
            if (disposed || string.IsNullOrWhiteSpace(token))
            {
                return -1;
            }

            profile.UpdateToken(token.Trim());
            if (Connected || joinCoroutine != null || room != null)
            {
                Leave();
            }
            return Join();
        }

        public int EnableRemoteAudioFrameObserver(
            JJRemoteAudioFrameConfig config,
            Action<JJRemoteAudioFrame> callback)
        {
            if (config == null || callback == null)
            {
                return -1;
            }
            remoteAudioFrameConfig = new JJRemoteAudioFrameConfig
            {
                sampleRate = config.sampleRate,
                channels = config.channels
            };
            remoteAudioFrameReceived = callback;
            foreach (KeyValuePair<string, GameObject> entry
                     in remoteAudioObjects)
            {
                ConfigureRemoteAudioTap(entry.Key, entry.Value);
            }
            return 0;
        }

        public void DisableRemoteAudioFrameObserver()
        {
            remoteAudioFrameReceived = null;
            remoteAudioFrameConfig = null;
            foreach (GameObject audioObject in remoteAudioObjects.Values)
            {
                if (audioObject == null)
                {
                    continue;
                }
                LiveKitRemoteAudioFrameTap tap =
                    audioObject.GetComponent<LiveKitRemoteAudioFrameTap>();
                tap?.DisableTap();
            }
        }

        private void ConfigureRemoteAudioTap(
            string trackSid,
            GameObject audioObject)
        {
            if (remoteAudioFrameConfig == null ||
                remoteAudioFrameReceived == null || audioObject == null ||
                !remoteAudioParticipantIdentities.TryGetValue(
                    trackSid,
                    out string identity))
            {
                return;
            }

            LiveKitRemoteAudioFrameTap tap =
                audioObject.GetComponent<LiveKitRemoteAudioFrameTap>();
            if (tap == null)
            {
                tap = audioObject.AddComponent<LiveKitRemoteAudioFrameTap>();
            }
            tap.Configure(
                profile.ChannelName,
                GetOrCreateLegacyUid(identity),
                remoteAudioFrameConfig,
                remoteAudioFrameReceived);
        }

        public static long GetCurrentMonotonicTimeInMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
                          (1000d /
                           System.Diagnostics.Stopwatch.Frequency));
        }

        private void StartMicrophonePublishIfNeeded()
        {
            if (!Connected || !publishingAllowedByRole || !audioEnabled ||
                microphoneTrack != null)
            {
                return;
            }

            if (microphonePublishGeneration == sessionGeneration)
            {
                microphoneRestartRequested = true;
                return;
            }

            microphoneRestartRequested = false;
            StartOwnerCoroutine(PublishMicrophoneRoutine());
        }

        private IEnumerator PublishMicrophoneRoutine()
        {
            return PublishMicrophoneRoutine(sessionGeneration, room);
        }

        private IEnumerator PublishMicrophoneRoutine(
            int generation,
            Room targetRoom)
        {
            if (microphonePublishGeneration == generation)
            {
                yield break;
            }

            microphonePublishGeneration = generation;
            int deviceSelectionVersion =
                microphoneDeviceSelectionVersion;
            try
            {
                yield return PublishMicrophoneAttemptRoutine(
                    generation,
                    targetRoom,
                    deviceSelectionVersion);
            }
            finally
            {
                if (microphonePublishGeneration == generation)
                {
                    microphonePublishGeneration = -1;
                    bool shouldRestart =
                        microphoneRestartRequested &&
                        sessionGeneration == generation &&
                        IsSessionCurrent(generation, targetRoom) &&
                        publishingAllowedByRole &&
                        audioEnabled &&
                        microphoneTrack == null;
                    microphoneRestartRequested = false;
                    if (shouldRestart)
                    {
                        StartMicrophonePublishIfNeeded();
                    }
                }
            }
        }

        private IEnumerator PublishMicrophoneAttemptRoutine(
            int generation,
            Room targetRoom,
            int deviceSelectionVersion)
        {
            if (!IsMicrophoneAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion) ||
                microphoneTrack != null ||
                !publishingAllowedByRole || !audioEnabled)
            {
                yield break;
            }

            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            }

            if (!IsMicrophoneAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion))
            {
                yield break;
            }

            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                ReportError("Microphone permission was not granted.");
                yield break;
            }

            string[] detectedDevices = Microphone.devices ?? Array.Empty<string>();
            Debug.Log(
                "LiveKit microphone devices: " +
                (detectedDevices.Length == 0
                    ? "<system default>"
                    : string.Join(", ", detectedDevices)));

            // AudioProbe reports PCM at Unity's active DSP/output rate. LiveKit
            // must create its native microphone source with that same rate or
            // Android devices that run at 24 kHz will reject every frame.
            int unitySampleRate = AudioSettings.outputSampleRate;
            if (unitySampleRate <= 0)
            {
                unitySampleRate = 48000;
            }
            RtcAudioSource.DefaultMicrophoneSampleRate = (uint)unitySampleRate;
            Debug.Log($"LiveKit microphone sample rate: {unitySampleRate} Hz.");

            // Android USB/UAC devices can reorder Microphone.devices when the
            // glasses are connected. Try the system default first, then every
            // named input, and do not publish until real PCM frames arrive.
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(selectedMicrophoneDevice))
            {
                candidates.Add(selectedMicrophoneDevice);
            }
            else
            {
                candidates.Add(null);
                foreach (string device in detectedDevices)
                {
                    if (!string.IsNullOrWhiteSpace(device) &&
                        !candidates.Contains(device))
                    {
                        candidates.Add(device);
                    }
                }
            }

            string activeDevice = null;
            foreach (string candidate in candidates)
            {
                if (!IsMicrophoneAttemptCurrent(
                        generation,
                        targetRoom,
                        deviceSelectionVersion) ||
                    !publishingAllowedByRole)
                {
                    yield break;
                }

                DisposeMicrophoneCapture();
                microphoneObject = new GameObject(
                    "LiveKit Microphone: " +
                    (string.IsNullOrEmpty(candidate) ? "System Default" : candidate));
                microphoneObject.transform.SetParent(owner.transform, false);
                microphoneFrameCount = 0;

                try
                {
                    microphoneSource = new MicrophoneSource(candidate, microphoneObject);
                    microphoneSource.AudioRead += HandleMicrophoneSamples;
                    microphoneSource.Start();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        $"Could not start microphone '{candidate ?? "<system default>"}': " +
                        exception.Message);
                    DisposeMicrophoneCapture();
                    continue;
                }

                float captureTimeout = 5f;
                while (Volatile.Read(ref microphoneFrameCount) == 0 &&
                       captureTimeout > 0f &&
                       IsMicrophoneAttemptCurrent(
                           generation,
                           targetRoom,
                           deviceSelectionVersion) &&
                       publishingAllowedByRole)
                {
                    captureTimeout -= Time.unscaledDeltaTime;
                    yield return null;
                }

                if (!IsMicrophoneAttemptCurrent(
                        generation,
                        targetRoom,
                        deviceSelectionVersion) ||
                    !publishingAllowedByRole)
                {
                    yield break;
                }

                if (Volatile.Read(ref microphoneFrameCount) > 0)
                {
                    activeDevice = candidate;
                    activeMicrophoneDevice = candidate;
                    break;
                }

                Debug.LogWarning(
                    $"Microphone '{candidate ?? "<system default>"}' produced no audio frames.");
            }

            if (microphoneSource == null || Volatile.Read(ref microphoneFrameCount) == 0)
            {
                DisposeMicrophoneCapture();
                ReportError("No microphone produced audio frames; LiveKit audio was not published.");
                yield break;
            }

            if (!IsMicrophoneAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion) ||
                !publishingAllowedByRole ||
                targetRoom.LocalParticipant == null)
            {
                yield break;
            }

            var publishingTrack = LocalAudioTrack.CreateAudioTrack(
                "microphone",
                microphoneSource,
                targetRoom);
            microphoneTrack = publishingTrack;
            var publish = targetRoom.LocalParticipant.PublishTrack(
                publishingTrack,
                new TrackPublishOptions
            {
                Source = TrackSource.SourceMicrophone,
                AudioEncoding = new AudioEncoding { MaxBitrate = 64000 }
            });
            yield return publish;

            if (!IsMicrophoneAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion) ||
                !publishingAllowedByRole ||
                !ReferenceEquals(publishingTrack, microphoneTrack))
            {
                TryUnpublish(targetRoom, publishingTrack);
                yield break;
            }

            if (publish.IsError)
            {
                ReportError("LiveKit microphone publish failed.");
                StopMicrophone();
                yield break;
            }

            ((ILocalTrack)publishingTrack).SetMute(!audioEnabled);
            Debug.Log(
                $"LiveKit microphone published from " +
                $"'{activeDevice ?? "<system default>"}' with verified PCM frames.");
        }

        private void HandleMicrophoneSamples(float[] samples, int channels, int sampleRate)
        {
            int frame = Interlocked.Increment(ref microphoneFrameCount);
            if (samples == null || samples.Length == 0)
            {
                if (frame == 1)
                {
                    Debug.LogWarning("LiveKit microphone returned an empty PCM frame.");
                }
                return;
            }

            double squareSum = 0d;
            float peak = 0f;
            for (int index = 0; index < samples.Length; index++)
            {
                float absolute = Math.Abs(samples[index]);
                peak = Math.Max(peak, absolute);
                squareSum += samples[index] * samples[index];
            }
            double rms = Math.Sqrt(squareSum / samples.Length);
            double dbfs = 20d * Math.Log10(Math.Max(rms, 0.0000001d));

            if (frame == 1 || frame % 250 == 0)
            {
                string message =
                    $"LiveKit microphone received PCM: samples={samples?.Length ?? 0}, " +
                    $"channels={channels}, sampleRate={sampleRate}, " +
                    $"level={dbfs:F1} dBFS, peak={peak:F5}, " +
                    $"device='{activeMicrophoneDevice ?? "<system default>"}'.";
                if (peak < 0.0001f)
                {
                    Debug.LogWarning(
                        message + " The selected microphone is effectively silent; " +
                        "check Windows input selection/level or call SetRecordingDevice().");
                }
                else
                {
                    Debug.Log(message);
                }
            }
        }

        private void StartCameraPublishIfNeeded()
        {
            if (!Connected || !publishingAllowedByRole || !videoEnabled ||
                IsScreenSharing || cameraTrack != null)
            {
                return;
            }

            if (cameraPublishGeneration == sessionGeneration)
            {
                cameraRestartRequested = true;
                return;
            }

            if (cameraUpdateActive)
            {
                return;
            }

            cameraRestartRequested = false;
            cameraCoroutine = StartOwnerCoroutine(PublishCameraRoutine());
        }

        private IEnumerator PublishCameraRoutine()
        {
            return PublishCameraRoutine(sessionGeneration, room);
        }

        private IEnumerator PublishCameraRoutine(
            int generation,
            Room targetRoom)
        {
            if (cameraPublishGeneration == generation)
            {
                yield break;
            }

            cameraPublishGeneration = generation;
            int deviceSelectionVersion = videoDeviceSelectionVersion;
            try
            {
                yield return PublishCameraAttemptRoutine(
                    generation,
                    targetRoom,
                    deviceSelectionVersion);
            }
            finally
            {
                if (cameraPublishGeneration == generation)
                {
                    cameraPublishGeneration = -1;
                    if (sessionGeneration == generation &&
                        cameraTrack == null &&
                        cameraSource == null)
                    {
                        cameraCoroutine = null;
                    }
                    bool shouldRestart =
                        cameraRestartRequested &&
                        sessionGeneration == generation &&
                        IsSessionCurrent(generation, targetRoom) &&
                        publishingAllowedByRole &&
                        videoEnabled &&
                        !IsScreenSharing &&
                        cameraTrack == null &&
                        !cameraUpdateActive;
                    cameraRestartRequested = false;
                    if (shouldRestart)
                    {
                        StartCameraPublishIfNeeded();
                    }
                }
            }
        }

        private IEnumerator PublishCameraAttemptRoutine(
            int generation,
            Room targetRoom,
            int deviceSelectionVersion)
        {
            if (!IsCameraAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion) ||
                cameraTrack != null || !videoEnabled ||
                !publishingAllowedByRole)
            {
                cameraCoroutine = null;
                yield break;
            }

            NotifyLocalVideoState(
                JJVideoSourceType.CAMERA,
                JJLocalVideoStreamState.CAPTURING,
                JJLocalVideoStreamReason.OK);

#if UNITY_ANDROID && !UNITY_EDITOR
            // JJSDK reads the UVC camera attached to the Jorjin glasses. Unity's
            // WebCamTexture list does not reliably expose this device on Android,
            // so always try the vendor SDK first.
            bool shouldTryJorjinCamera =
                string.IsNullOrWhiteSpace(selectedVideoDevice) ||
                string.Equals(
                    selectedVideoDevice,
                    JorjinArCameraDeviceId,
                    StringComparison.Ordinal);
            if (shouldTryJorjinCamera)
            {
                jorjinCameraCapture = owner.GetComponent<JorjinArCameraCapture>();
                if (jorjinCameraCapture == null)
                {
                    jorjinCameraCapture =
                        owner.gameObject.AddComponent<JorjinArCameraCapture>();
                }

                yield return jorjinCameraCapture.StartCapture();

                if (!IsCameraAttemptCurrent(
                        generation,
                        targetRoom,
                        deviceSelectionVersion))
                {
                    yield break;
                }

                if (jorjinCameraCapture.IsCapturing &&
                    jorjinCameraCapture.ActiveTexture != null)
                {
                    Texture2D jorjinTexture = jorjinCameraCapture.ActiveTexture;
                    PublishLocalVideoTexture(jorjinTexture);
                    cameraSource = new JorjinArVideoSource(jorjinTexture);
                    cameraTrack = LocalVideoTrack.CreateVideoTrack(
                        "jorjin-ar-camera", cameraSource, targetRoom);
                    LocalVideoTrack publishingTrack = cameraTrack;
                    RtcVideoSource publishingSource = cameraSource;
                    var jorjinPublish = targetRoom.LocalParticipant.PublishTrack(
                        publishingTrack,
                        BuildCameraPublishOptions());
                    yield return jorjinPublish;

                    if (!IsCameraAttemptCurrent(
                            generation,
                            targetRoom,
                            deviceSelectionVersion) ||
                        !ReferenceEquals(publishingTrack, cameraTrack) ||
                        !ReferenceEquals(publishingSource, cameraSource))
                    {
                        TryUnpublish(targetRoom, publishingTrack);
                        yield break;
                    }

                    if (jorjinPublish.IsError)
                    {
                        NotifyLocalVideoState(
                            JJVideoSourceType.CAMERA,
                            JJLocalVideoStreamState.FAILED,
                            JJLocalVideoStreamReason.CAPTURE_FAILURE);
                        ReportError("LiveKit Jorjin camera publish failed.");
                        StopCamera(false);
                        cameraCoroutine = null;
                        yield break;
                    }

                    publishingSource.Start();
                    cameraUpdateActive = true;
                    cameraCoroutine =
                        StartOwnerCoroutine(publishingSource.Update());
                    NotifyLocalVideoState(
                        JJVideoSourceType.CAMERA,
                        JJLocalVideoStreamState.ENCODING,
                        JJLocalVideoStreamReason.OK);
                    Debug.Log(
                        "Publishing the Jorjin AR glasses camera through LiveKit.");
                    yield break;
                }

                Debug.LogWarning(
                    "The Jorjin AR glasses camera was unavailable. " +
                    jorjinCameraCapture.LastError);
                if (string.Equals(
                        selectedVideoDevice,
                        JorjinArCameraDeviceId,
                        StringComparison.Ordinal))
                {
                    NotifyLocalVideoState(
                        JJVideoSourceType.CAMERA,
                        JJLocalVideoStreamState.FAILED,
                        JJLocalVideoStreamReason.CAPTURE_FAILURE);
                    ReportError(
                        "The selected Jorjin AR glasses camera is unavailable.");
                    cameraCoroutine = null;
                    yield break;
                }
                Debug.LogWarning("Falling back to an Android camera.");
            }
#endif

            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
            }

            if (!IsCameraAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion))
            {
                yield break;
            }

            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.CAMERA,
                    JJLocalVideoStreamState.FAILED,
                    JJLocalVideoStreamReason.DEVICE_NO_PERMISSION);
                ReportError("Camera permission was not granted.");
                cameraCoroutine = null;
                yield break;
            }

            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.CAMERA,
                    JJLocalVideoStreamState.FAILED,
                    JJLocalVideoStreamReason.CAPTURE_FAILURE);
                ReportError("No camera device was found.");
                cameraCoroutine = null;
                yield break;
            }

            var orderedDevices = new List<WebCamDevice>();
            foreach (WebCamDevice device in devices)
            {
                if (string.IsNullOrWhiteSpace(selectedVideoDevice) ||
                    string.Equals(
                        device.name,
                        selectedVideoDevice,
                        StringComparison.Ordinal))
                {
                    orderedDevices.Add(device);
                }
            }
            if (orderedDevices.Count == 0)
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.CAMERA,
                    JJLocalVideoStreamState.FAILED,
                    JJLocalVideoStreamReason.CAPTURE_FAILURE);
                ReportError(
                    $"Selected camera '{selectedVideoDevice}' is no longer available.");
                cameraCoroutine = null;
                yield break;
            }
            orderedDevices.Sort((left, right) =>
                GetCameraPriority(right).CompareTo(GetCameraPriority(left)));
            for (int index = 0; index < orderedDevices.Count; index++)
            {
                WebCamDevice device = orderedDevices[index];
                Debug.Log(
                    $"Camera device [{index}] name='{device.name}', " +
                    $"frontFacing={device.isFrontFacing}, priority={GetCameraPriority(device)}");
            }

            // Windows may expose a camera that is already held by the Editor or
            // another process. Try every device and publish only after a real frame
            // arrives; otherwise WebCameraSource.GetPixels32 throws continuously.
            for (int index = 0; index < orderedDevices.Count && webCamTexture == null; index++)
            {
                WebCamDevice device = orderedDevices[index];
                var candidate = new WebCamTexture(
                    device.name,
                    videoEncoderConfiguration.width,
                    videoEncoderConfiguration.height,
                    videoEncoderConfiguration.frameRate);
                bool receivedFrame = false;

                Debug.Log($"Trying camera '{device.name}' ({index + 1}/{orderedDevices.Count})...");
                try
                {
                    candidate.Play();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Could not start camera '{device.name}': {exception.Message}");
                }

                float timeout = 4f;
                while (timeout > 0f)
                {
                    if (!IsCameraAttemptCurrent(
                            generation,
                            targetRoom,
                            deviceSelectionVersion))
                    {
                        candidate.Stop();
                        UnityEngine.Object.Destroy(candidate);
                        yield break;
                    }

                    if (candidate.isPlaying && candidate.width > 16 &&
                        candidate.height > 16 && candidate.didUpdateThisFrame)
                    {
                        receivedFrame = true;
                        break;
                    }

                    timeout -= Time.unscaledDeltaTime;
                    yield return null;
                }

                if (receivedFrame)
                {
                    webCamTexture = candidate;
                    Debug.Log($"Camera '{device.name}' started at {candidate.width}x{candidate.height}.");
                }
                else
                {
                    candidate.Stop();
                    UnityEngine.Object.Destroy(candidate);
                    Debug.LogWarning($"Camera '{device.name}' did not deliver a frame.");
                }
            }

            if (webCamTexture == null)
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.CAMERA,
                    JJLocalVideoStreamState.FAILED,
                    JJLocalVideoStreamReason.CAPTURE_FAILURE);
                ReportError(
                    "No camera could be started. Close Unity Editor or other apps using the webcam, " +
                    "or connect a second camera, then toggle video off and on to retry.");
                cameraCoroutine = null;
                yield break;
            }

            if (!IsCameraAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion))
            {
                yield break;
            }

            PublishLocalVideoTexture(webCamTexture);
            cameraSource = new WebCameraSource(webCamTexture);
            cameraTrack = LocalVideoTrack.CreateVideoTrack(
                "camera",
                cameraSource,
                targetRoom);
            LocalVideoTrack webCameraPublishingTrack = cameraTrack;
            RtcVideoSource webCameraPublishingSource = cameraSource;
            var publish = targetRoom.LocalParticipant.PublishTrack(
                webCameraPublishingTrack,
                BuildCameraPublishOptions());
            yield return publish;

            if (!IsCameraAttemptCurrent(
                    generation,
                    targetRoom,
                    deviceSelectionVersion) ||
                !ReferenceEquals(webCameraPublishingTrack, cameraTrack) ||
                !ReferenceEquals(webCameraPublishingSource, cameraSource))
            {
                TryUnpublish(targetRoom, webCameraPublishingTrack);
                yield break;
            }

            if (publish.IsError)
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.CAMERA,
                    JJLocalVideoStreamState.FAILED,
                    JJLocalVideoStreamReason.ENCODE_FAILURE);
                ReportError("LiveKit camera publish failed.");
                StopCamera(false);
                cameraCoroutine = null;
                yield break;
            }

            webCameraPublishingSource.Start();
            cameraUpdateActive = true;
            cameraCoroutine =
                StartOwnerCoroutine(webCameraPublishingSource.Update());
            NotifyLocalVideoState(
                JJVideoSourceType.CAMERA,
                JJLocalVideoStreamState.ENCODING,
                JJLocalVideoStreamReason.OK);
        }

        private static int GetCameraPriority(WebCamDevice device)
        {
            string name = device.name ?? string.Empty;
            int priority = device.isFrontFacing ? 0 : 20;
            if (name.IndexOf("jorjin", StringComparison.OrdinalIgnoreCase) >= 0) priority += 200;
            if (name.IndexOf("usb", StringComparison.OrdinalIgnoreCase) >= 0) priority += 150;
            if (name.IndexOf("uvc", StringComparison.OrdinalIgnoreCase) >= 0) priority += 150;
            if (name.IndexOf("external", StringComparison.OrdinalIgnoreCase) >= 0) priority += 120;
            return priority;
        }

        private TrackPublishOptions BuildCameraPublishOptions()
        {
            var encoding = new VideoEncoding
            {
                MaxFramerate = videoEncoderConfiguration.frameRate
            };
            if (videoEncoderConfiguration.bitrate > 0)
            {
                // JJ/Agora expresses bitrate in Kbps; LiveKit expects bps.
                encoding.MaxBitrate =
                    (ulong)videoEncoderConfiguration.bitrate * 1000UL;
            }
            return new TrackPublishOptions
            {
                Source = TrackSource.SourceCamera,
                VideoEncoding = encoding,
                // SetRemoteVideoStreamType can select the Low/High simulcast layer.
                Simulcast = true
            };
        }

        public int StartDesktopShare()
        {
            if (!Connected || screenCoroutine != null ||
                screenPublishGeneration == sessionGeneration ||
                !publishingAllowedByRole)
            {
                return -1;
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            screenCoroutine =
                StartOwnerCoroutine(
                    PublishScreenRoutine(new DesktopCaptureSource()));
            return screenCoroutine != null ? 0 : -1;
#else
            ReportError("Desktop capture is currently supported only on Windows by this integration.");
            return -2;
#endif
        }

        public int StartWindowShare(IntPtr windowHandle, int width, int height)
        {
            if (!Connected || screenCoroutine != null ||
                screenPublishGeneration == sessionGeneration ||
                !publishingAllowedByRole || windowHandle == IntPtr.Zero)
            {
                return -1;
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            screenCoroutine = StartOwnerCoroutine(
                PublishScreenRoutine(new WindowCaptureSource(windowHandle, width, height)));
            return screenCoroutine != null ? 0 : -1;
#else
            ReportError("Window capture is currently supported only on Windows by this integration.");
            return -2;
#endif
        }

        private IEnumerator PublishScreenRoutine(RtcVideoSource source)
        {
            int generation = sessionGeneration;
            Room targetRoom = room;
            if (screenPublishGeneration == generation)
            {
                try
                {
                    source?.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "Unused LiveKit screen source cleanup failed: " +
                        exception.Message);
                }
                yield break;
            }

            screenPublishGeneration = generation;
            try
            {
                yield return PublishScreenAttemptRoutine(
                    source,
                    generation,
                    targetRoom);
            }
            finally
            {
                if (screenPublishGeneration == generation)
                {
                    screenPublishGeneration = -1;
                    if (sessionGeneration == generation &&
                        screenTrack == null &&
                        screenSource == null)
                    {
                        screenCoroutine = null;
                    }
                }
            }
        }

        private IEnumerator PublishScreenAttemptRoutine(
            RtcVideoSource source,
            int generation,
            Room targetRoom)
        {
            if (!IsSessionCurrent(generation, targetRoom) ||
                !publishingAllowedByRole)
            {
                try
                {
                    source?.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "Canceled LiveKit screen source cleanup failed: " +
                        exception.Message);
                }
                yield break;
            }

            StopCamera();
            NotifyLocalVideoState(
                JJVideoSourceType.SCREEN,
                JJLocalVideoStreamState.CAPTURING,
                JJLocalVideoStreamReason.OK);
            screenSource = source;
            screenTrack = LocalVideoTrack.CreateVideoTrack(
                "screen-share",
                screenSource,
                targetRoom);
            LocalVideoTrack publishingTrack = screenTrack;
            RtcVideoSource publishingSource = screenSource;
            var publish = targetRoom.LocalParticipant.PublishTrack(
                publishingTrack,
                new TrackPublishOptions
            {
                Source = TrackSource.SourceScreenshare
            });
            yield return publish;

            if (!IsSessionCurrent(generation, targetRoom) ||
                !publishingAllowedByRole ||
                !ReferenceEquals(publishingTrack, screenTrack) ||
                !ReferenceEquals(publishingSource, screenSource))
            {
                TryUnpublish(targetRoom, publishingTrack);
                yield break;
            }

            if (publish.IsError)
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.SCREEN,
                    JJLocalVideoStreamState.FAILED,
                    JJLocalVideoStreamReason.ENCODE_FAILURE);
                ReportError("LiveKit screen-share publish failed.");
                StopScreen(false);
                screenCoroutine = null;
                yield break;
            }

            publishingSource.TextureReceived += HandleLocalScreenTexture;
            publishingSource.Start();
            screenUpdateActive = true;
            screenCoroutine =
                StartOwnerCoroutine(publishingSource.Update());
            NotifyLocalVideoState(
                JJVideoSourceType.SCREEN,
                JJLocalVideoStreamState.ENCODING,
                JJLocalVideoStreamReason.OK);
            eventHandler?.OnLiveKitScreenShareChanged(true);
        }

        public int StopScreenShare()
        {
            if (!Connected)
            {
                return -1;
            }

            StopScreen();
            if (videoEnabled && cameraTrack == null)
            {
                StartCameraPublishIfNeeded();
            }
            return 0;
        }

        public int SendReaction(JJReactionType reaction)
        {
            if (!Connected || room?.LocalParticipant == null)
            {
                return -1;
            }

            string payload = reaction == JJReactionType.CLAP ? "clap" : "like";
            room.LocalParticipant.PublishData(
                Encoding.UTF8.GetBytes(payload),
                null,
                true,
                ReactionTopic);

            // LiveKit data packets are delivered to the other participants;
            // update the sender's own tile immediately as well.
            string identity = room.LocalParticipant.Identity ?? profile.ParticipantIdentity;
            eventHandler?.OnLiveKitReactionReceived(identity, reaction);
            return 0;
        }

        public int SendTranscriptControl(bool start, string sessionId)
        {
            if (!Connected || room?.LocalParticipant == null)
            {
                return -1;
            }

            var control = new TranscriptControlPacket
            {
                action = start ? "start" : "stop",
                session_id = sessionId ?? string.Empty
            };
            room.LocalParticipant.PublishData(
                Encoding.UTF8.GetBytes(JsonUtility.ToJson(control)),
                null,
                true,
                TranscriptControlTopic);
            return 0;
        }

        public int SendCollaborationRequest(string action, string sessionId, string requestId, string question)
        {
            if (!Connected || room?.LocalParticipant == null ||
                (action != "ask" && action != "summary" && action != "tasks") ||
                // Questions and action items work without a recording session; summaries need one.
                (action == "summary" && string.IsNullOrEmpty(sessionId)) ||
                string.IsNullOrEmpty(requestId) || requestId.Length > 64 || (question?.Length ?? 0) > 2000)
                return -1;
            var packet = new CollaborationControlPacket
            {
                action = action, session_id = sessionId, request_id = requestId,
                question = question ?? string.Empty
            };
            room.LocalParticipant.PublishData(Encoding.UTF8.GetBytes(JsonUtility.ToJson(packet)),
                null, true, CollaborationControlTopic);
            return 0;
        }

        /// <summary>
        /// Sends a photo to the Transcript Agent for text recognition and
        /// translation. Only the Agent receives it; the result arrives as a
        /// normal collaboration packet with the same request ID.
        /// </summary>
        public int SendCollaborationImage(byte[] jpeg, string requestId, Action<bool> sent)
        {
            if (!Connected || room?.LocalParticipant == null || jpeg == null || jpeg.Length == 0 ||
                jpeg.Length > MaxCollaborationImageBytes || string.IsNullOrEmpty(requestId) || requestId.Length > 64)
                return -1;
            string agent = null;
            foreach (RemoteParticipant participant in room.RemoteParticipants.Values)
            {
                if (IsInternalAgentIdentity(participant?.Identity)) { agent = participant.Identity; break; }
            }
            if (agent == null) return -2;

            string path = System.IO.Path.Combine(Application.temporaryCachePath, "ocr_" + requestId + ".jpg");
            try { System.IO.File.WriteAllBytes(path, jpeg); }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not write the photo for text recognition: " + exception.Message);
                return -1;
            }
            var options = new LiveKit.StreamByteOptions
            {
                Topic = CollaborationImageTopic,
                MimeType = "image/jpeg",
                Name = "photo.jpg",
                DestinationIdentities = new List<string> { agent },
                Attributes = new Dictionary<string, string> { ["request_id"] = requestId }
            };
            SendFileInstruction instruction;
            try { instruction = room.LocalParticipant.SendFile(path, options); }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not send the photo for text recognition: " + exception.Message);
                TryDeleteFile(path);
                return -1;
            }
            if (StartOwnerCoroutine(WaitForImageSent(instruction, path, sent)) == null)
            {
                TryDeleteFile(path);
                return -1;
            }
            return 0;
        }

        private static IEnumerator WaitForImageSent(SendFileInstruction instruction, string path, Action<bool> sent)
        {
            yield return instruction;
            if (instruction.IsError)
                Debug.LogWarning("Photo upload for text recognition failed: " + instruction.Error?.Message);
            TryDeleteFile(path);
            sent?.Invoke(!instruction.IsError);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception) { }
        }

        [Serializable]
        private sealed class CollaborationControlPacket
        {
            public string action, session_id, request_id, question;
        }

        private void HandleLocalScreenTexture(Texture texture)
        {
            PublishLocalVideoTexture(texture);
        }

        private void PublishLocalVideoTexture(Texture texture)
        {
            currentLocalVideoTexture = texture;
            if (localPreviewEnabled)
            {
                eventHandler?.OnLiveKitLocalVideoTexture(texture);
            }
        }

        private void HandleParticipantConnected(Participant participant)
        {
            NotifyParticipantConnected(participant);
            // Late joiners (including the Agent) have not seen our role yet.
            if (participant != null && !IsLocalParticipant(participant))
                PublishRole(ParticipantIdentity(participant));
        }

        private string localRole = "";

        /// <summary>Sets and announces this participant's role: "field" or "expert".</summary>
        public void SetMeetingRole(string role)
        {
            localRole = role == "expert" ? "expert" : "field";
            PublishRole(null);
        }

        private void PublishRole(string destination)
        {
            if (string.IsNullOrEmpty(localRole) || !Connected || room?.LocalParticipant == null) return;
            try
            {
                room.LocalParticipant.PublishData(
                    Encoding.UTF8.GetBytes("{\"role\":\"" + localRole + "\"}"),
                    destination == null ? null : new List<string> { destination },
                    true,
                    RoleTopic);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not announce the meeting role: " + exception.Message);
            }
        }

        [Serializable]
        private sealed class RolePacket { public string role; }

        private void HandleParticipantDisconnected(Participant participant)
        {
            if (participant == null)
            {
                return;
            }

            string identity = ParticipantIdentity(participant);
            if (IsInternalAgentIdentity(identity))
            {
                return;
            }
            RemoveRemoteAudioForParticipant(identity);
            if (!connectedParticipantIdentities.Remove(identity))
            {
                return;
            }

            uint legacyUid = GetOrCreateLegacyUid(identity);
            eventHandler?.OnLiveKitParticipantDisconnected(identity);
            eventHandler?.OnUserOffline(
                CurrentConnection(),
                legacyUid,
                JJUserOfflineReason.QUIT);
        }

        private void HandleTrackPublished(
            RemoteTrackPublication publication,
            RemoteParticipant participant)
        {
            if (publication == null)
            {
                return;
            }

            bool shouldSubscribe =
                publication.Kind == TrackKind.KindAudio
                    ? !muteAllRemoteAudio
                    : publication.Kind != TrackKind.KindVideo ||
                      !muteAllRemoteVideo;
            if (!shouldSubscribe && publication.Subscribed)
            {
                publication.SetSubscribed(false);
            }
            ApplyRemoteVideoQuality(publication, participant);
        }

        private void HandleTrackSubscribed(
            IRemoteTrack track,
            RemoteTrackPublication publication,
            RemoteParticipant participant)
        {
            // Native room events may arrive while Room.Connect is still yielding.
            // ConnectRoutine replays the participant publications after the legacy
            // join-success callback, preserving Agora's callback ordering.
            if (!joinCallbackSent)
            {
                return;
            }
            if ((publication.Kind == TrackKind.KindAudio &&
                 muteAllRemoteAudio) ||
                (publication.Kind == TrackKind.KindVideo &&
                 muteAllRemoteVideo))
            {
                if (publication.Subscribed)
                {
                    publication.SetSubscribed(false);
                }
                return;
            }
            NotifyParticipantConnected(participant);
            if (track is RemoteAudioTrack audioTrack)
            {
                if (owner == null)
                {
                    return;
                }
                RemoveRemoteAudio(track.Sid);
                var audioObject = new GameObject("LiveKit Audio: " + track.Sid);
                audioObject.transform.SetParent(owner.transform, false);
                var audioSource = audioObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = true;
                audioSource.loop = true;
                audioSource.spatialBlend = 0f;
                audioSource.volume = 1f;
                audioSource.mute = false;
                audioSource.ignoreListenerPause = true;
                remoteAudioObjects[track.Sid] = audioObject;
                remoteAudioStreams[track.Sid] = new AudioStream(audioTrack, audioSource);
                remoteAudioTracks[track.Sid] = audioTrack;
                remoteAudioParticipantIdentities[track.Sid] =
                    ParticipantIdentity(participant);
                ConfigureRemoteAudioTap(track.Sid, audioObject);
                Debug.Log(
                    $"LiveKit remote audio subscribed: participant={participant.Identity}, " +
                    $"track={track.Sid}.");
            }
            else if (track is RemoteVideoTrack videoTrack)
            {
                ApplyRemoteVideoQuality(publication, participant);
                if (!subscribedRemoteVideoSids.Add(track.Sid))
                {
                    return;
                }

                string identity = ParticipantIdentity(participant);
                eventHandler?.OnLiveKitVideoTrackSubscribed(
                    videoTrack,
                    identity,
                    publication.Source);
                if (IsPrimaryVideo(publication))
                {
                    eventHandler?.OnRemoteVideoStateChanged(
                        CurrentConnection(),
                        GetOrCreateLegacyUid(identity),
                        JJRemoteVideoState.DECODING,
                        JJRemoteVideoStateReason.REMOTE_UNMUTED,
                        0);
                }
            }
        }

        private void ApplyRemoteVideoQuality(
            RemoteTrackPublication publication,
            Participant participant)
        {
            if (publication == null || participant == null ||
                publication.Kind != TrackKind.KindVideo ||
                !remoteVideoQualityByIdentity.TryGetValue(
                    ParticipantIdentity(participant),
                    out JJVideoStreamType streamType))
            {
                return;
            }
            publication.SetVideoQuality(
                streamType == JJVideoStreamType.LOW
                    ? VideoQuality.Low
                    : VideoQuality.High);
        }

        private void HandleTrackUnsubscribed(
            IRemoteTrack track,
            RemoteTrackPublication publication,
            RemoteParticipant participant)
        {
            if (!joinCallbackSent)
            {
                return;
            }
            RemoveRemoteAudio(track.Sid);
            if (track is RemoteVideoTrack videoTrack)
            {
                subscribedRemoteVideoSids.Remove(track.Sid);
                mutedRemoteTrackSids.Remove(track.Sid);
                string identity = ParticipantIdentity(participant);
                eventHandler?.OnLiveKitVideoTrackUnsubscribed(
                    videoTrack,
                    identity,
                    publication.Source);
                if (IsPrimaryVideo(publication))
                {
                    JJRemoteVideoStateReason reason =
                        connectedParticipantIdentities.Contains(identity)
                            ? JJRemoteVideoStateReason.REMOTE_MUTED
                            : JJRemoteVideoStateReason.REMOTE_OFFLINE;
                    eventHandler?.OnRemoteVideoStateChanged(
                        CurrentConnection(),
                        GetOrCreateLegacyUid(identity),
                        JJRemoteVideoState.STOPPED,
                        reason,
                        0);
                }
            }
        }

        private void HandleTrackMuted(
            TrackPublication publication,
            Participant participant)
        {
            if (!joinCallbackSent ||
                publication == null || participant == null ||
                !mutedRemoteTrackSids.Add(publication.Sid))
            {
                return;
            }

            if (IsLocalParticipant(participant))
            {
                if (IsPrimaryVideo(publication))
                {
                    NotifyLocalVideoState(
                        JJVideoSourceType.CAMERA,
                        JJLocalVideoStreamState.STOPPED,
                        JJLocalVideoStreamReason.OK);
                }
                return;
            }

            NotifyParticipantConnected(participant);
            string identity = ParticipantIdentity(participant);
            uint legacyUid = GetOrCreateLegacyUid(identity);
            if (publication.Kind == TrackKind.KindAudio)
            {
                eventHandler?.OnUserMuteAudio(
                    CurrentConnection(),
                    legacyUid,
                    true);
            }
            else if (IsPrimaryVideo(publication))
            {
                eventHandler?.OnUserMuteVideo(
                    CurrentConnection(),
                    legacyUid,
                    true);
                eventHandler?.OnRemoteVideoStateChanged(
                    CurrentConnection(),
                    legacyUid,
                    JJRemoteVideoState.STOPPED,
                    JJRemoteVideoStateReason.REMOTE_MUTED,
                    0);
            }
        }

        private void HandleTrackUnmuted(
            TrackPublication publication,
            Participant participant)
        {
            if (!joinCallbackSent ||
                publication == null || participant == null ||
                !mutedRemoteTrackSids.Remove(publication.Sid))
            {
                return;
            }

            if (IsLocalParticipant(participant))
            {
                if (IsPrimaryVideo(publication))
                {
                    NotifyLocalVideoState(
                        JJVideoSourceType.CAMERA,
                        JJLocalVideoStreamState.ENCODING,
                        JJLocalVideoStreamReason.OK);
                }
                return;
            }

            NotifyParticipantConnected(participant);
            string identity = ParticipantIdentity(participant);
            uint legacyUid = GetOrCreateLegacyUid(identity);
            if (publication.Kind == TrackKind.KindAudio)
            {
                eventHandler?.OnUserMuteAudio(
                    CurrentConnection(),
                    legacyUid,
                    false);
            }
            else if (IsPrimaryVideo(publication))
            {
                eventHandler?.OnUserMuteVideo(
                    CurrentConnection(),
                    legacyUid,
                    false);
                eventHandler?.OnRemoteVideoStateChanged(
                    CurrentConnection(),
                    legacyUid,
                    JJRemoteVideoState.DECODING,
                    JJRemoteVideoStateReason.REMOTE_UNMUTED,
                    0);
            }
        }

        private void HandleConnectionStateChanged(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.ConnConnected:
                    if (connectionLostNotified)
                    {
                        NotifyConnectionState(
                            JJConnectionState.CONNECTED,
                            JJConnectionChangedReason.REJOIN_SUCCESS);
                        eventHandler?.OnRejoinChannelSuccess(
                            CurrentConnection(),
                            0);
                    }
                    connectionLostNotified = false;
                    break;
                case ConnectionState.ConnReconnecting:
                    NotifyReconnecting();
                    break;
                case ConnectionState.ConnDisconnected:
                    if (!leaving)
                    {
                        NotifyConnectionLost();
                    }
                    break;
            }
        }

        private void HandleReconnecting(Room reconnectingRoom)
        {
            NotifyReconnecting();
        }

        private void HandleReconnected(Room reconnectedRoom)
        {
            if (!connectionLostNotified)
            {
                return;
            }
            NotifyConnectionState(
                JJConnectionState.CONNECTED,
                JJConnectionChangedReason.REJOIN_SUCCESS);
            eventHandler?.OnRejoinChannelSuccess(CurrentConnection(), 0);
            connectionLostNotified = false;
        }

        private void HandleDisconnected(Room disconnectedRoom)
        {
            bool unexpected = !leaving;
            if (unexpected)
            {
                NotifyConnectionLost();
            }
            StopSessionMonitors();
            StopScreen();
            StopCamera();
            StopMicrophone();
            CleanupRemoteMedia();
            NotifyLeave(
                disconnectedRoom?.Name ?? profile.ChannelName,
                unexpected);
            UnbindRoomEvents(disconnectedRoom);
            if (room == disconnectedRoom)
            {
                room = null;
            }
        }

        private void HandleDataReceived(
            byte[] data,
            Participant participant,
            DataPacketKind kind,
            string topic)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            string payload = Encoding.UTF8.GetString(data);
            if (string.Equals(topic, CollaborationEventTopic, StringComparison.Ordinal))
            {
                if (data.Length <= 15000 && participant != null &&
                    IsInternalAgentIdentity(participant.Identity))
                    eventHandler?.OnLiveKitCollaborationPacket(participant.Identity, payload);
                return;
            }
            if (string.Equals(topic, TranscriptEventTopic, StringComparison.Ordinal))
            {
                HandleTranscriptEvent(payload);
                return;
            }
            if (string.Equals(topic, RoleTopic, StringComparison.Ordinal))
            {
                if (participant == null || data.Length > 200) return;
                RolePacket packet = null;
                try { packet = JsonUtility.FromJson<RolePacket>(payload); }
                catch (Exception) { }
                if (packet?.role == "field" || packet?.role == "expert")
                    eventHandler?.OnLiveKitParticipantRole(ParticipantIdentity(participant), packet.role);
                return;
            }

            if (!string.Equals(topic, ReactionTopic, StringComparison.Ordinal))
            {
                return;
            }

            JJReactionType reaction;
            if (string.Equals(payload, "like", StringComparison.OrdinalIgnoreCase))
            {
                reaction = JJReactionType.LIKE;
            }
            else if (string.Equals(payload, "clap", StringComparison.OrdinalIgnoreCase))
            {
                reaction = JJReactionType.CLAP;
            }
            else
            {
                return;
            }

            string identity = participant?.Identity ?? "Unknown";
            eventHandler?.OnLiveKitReactionReceived(identity, reaction);
        }

        private void HandleTranscriptEvent(string payload)
        {
            TranscriptEventPacket packet;
            try
            {
                packet = JsonUtility.FromJson<TranscriptEventPacket>(payload);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Invalid Transcript Agent packet: " + exception.Message);
                return;
            }

            if (packet == null || string.IsNullOrWhiteSpace(packet.type)) return;
            switch (packet.type)
            {
                case "ready":
                case "status":
                case "error":
                    eventHandler?.OnLiveKitTranscriptAgentStatus(
                        packet.type,
                        packet.state,
                        packet.session_id,
                        packet.message);
                    break;
                case "segment":
                    eventHandler?.OnLiveKitTranscriptSegment(
                        packet.session_id,
                        packet.timestamp,
                        packet.participant_identity,
                        packet.participant_name,
                        packet.text,
                        packet.sequence);
                    break;
                case "recording_start":
                    eventHandler?.OnLiveKitTranscriptRecordingStarted(
                        packet.session_id,
                        packet.recording_file_name,
                        packet.recording_bytes,
                        packet.recording_chunk_count,
                        packet.recording_duration_seconds,
                        packet.recording_truncated);
                    break;
                case "recording_chunk":
                    eventHandler?.OnLiveKitTranscriptRecordingChunk(
                        packet.session_id,
                        packet.recording_chunk_index,
                        packet.recording_data);
                    break;
                case "recording_complete":
                    eventHandler?.OnLiveKitTranscriptRecordingCompleted(
                        packet.session_id,
                        packet.recording_file_name,
                        packet.recording_bytes,
                        packet.recording_chunk_count,
                        packet.recording_sha256,
                        packet.recording_duration_seconds,
                        packet.recording_truncated);
                    break;
                case "recording_error":
                    eventHandler?.OnLiveKitTranscriptRecordingError(
                        packet.session_id,
                        packet.message);
                    break;
                case "complete":
                    eventHandler?.OnLiveKitTranscriptCompleted(
                        packet.session_id,
                        packet.segment_count,
                        packet.message);
                    break;
            }
        }

        private void BindRoomEvents(Room targetRoom)
        {
            if (targetRoom == null) return;
            targetRoom.ParticipantConnected += HandleParticipantConnected;
            targetRoom.ParticipantDisconnected += HandleParticipantDisconnected;
            targetRoom.TrackPublished += HandleTrackPublished;
            targetRoom.TrackSubscribed += HandleTrackSubscribed;
            targetRoom.TrackUnsubscribed += HandleTrackUnsubscribed;
            targetRoom.TrackMuted += HandleTrackMuted;
            targetRoom.TrackUnmuted += HandleTrackUnmuted;
            targetRoom.ConnectionStateChanged += HandleConnectionStateChanged;
            targetRoom.Reconnecting += HandleReconnecting;
            targetRoom.Reconnected += HandleReconnected;
            targetRoom.DataReceived += HandleDataReceived;
            targetRoom.Disconnected += HandleDisconnected;
        }

        private void ArmLateConnectCleanup(Room targetRoom)
        {
            if (targetRoom == null)
            {
                return;
            }

            // Room.Connect cannot be canceled by stopping a Unity coroutine.
            // Keep exactly one lightweight callback so a native success that
            // arrives after Leave/Dispose is disconnected immediately.
            UnbindRoomEvents(targetRoom);
            targetRoom.Connected -= HandleCanceledRoomConnected;
            targetRoom.Connected += HandleCanceledRoomConnected;
        }

        private void HandleCanceledRoomConnected(Room connectedRoom)
        {
            CleanupLateConnectedRoom(connectedRoom);
        }

        private void CleanupLateConnectedRoom(Room targetRoom)
        {
            if (targetRoom == null)
            {
                return;
            }

            UnbindRoomEvents(targetRoom);
            try
            {
                targetRoom.Disconnect();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "A canceled LiveKit connection completed late and could not " +
                    "be disconnected cleanly: " + exception.Message);
            }
        }

        private void UnbindRoomEvents(Room targetRoom)
        {
            if (targetRoom == null) return;
            targetRoom.ParticipantConnected -= HandleParticipantConnected;
            targetRoom.ParticipantDisconnected -= HandleParticipantDisconnected;
            targetRoom.TrackPublished -= HandleTrackPublished;
            targetRoom.TrackSubscribed -= HandleTrackSubscribed;
            targetRoom.TrackUnsubscribed -= HandleTrackUnsubscribed;
            targetRoom.TrackMuted -= HandleTrackMuted;
            targetRoom.TrackUnmuted -= HandleTrackUnmuted;
            targetRoom.ConnectionStateChanged -= HandleConnectionStateChanged;
            targetRoom.Reconnecting -= HandleReconnecting;
            targetRoom.Reconnected -= HandleReconnected;
            targetRoom.DataReceived -= HandleDataReceived;
            targetRoom.Disconnected -= HandleDisconnected;
            targetRoom.Connected -= HandleCanceledRoomConnected;
        }

        private void ResetSessionState()
        {
            leaving = false;
            leaveCallbackSent = false;
            joinCallbackSent = false;
            connectionLostNotified = false;
            tokenWillExpireNotified = false;
            tokenRequestNotified = false;
            microphonePublishGeneration = -1;
            cameraPublishGeneration = -1;
            screenPublishGeneration = -1;
            microphoneRestartRequested = false;
            cameraRestartRequested = false;
            cameraUpdateActive = false;
            screenUpdateActive = false;
            lastConnectionState = null;
            lastConnectionReason = null;
            identityToLegacyUid.Clear();
            legacyUidToIdentity.Clear();
            connectedParticipantIdentities.Clear();
            subscribedRemoteVideoSids.Clear();
            mutedRemoteTrackSids.Clear();
        }

        private bool IsSessionCurrent(
            int generation,
            Room targetRoom,
            bool requireConnected = true)
        {
            if (disposed || leaving || generation != sessionGeneration)
            {
                return false;
            }

            if (targetRoom == null)
            {
                return room == null;
            }

            if (!ReferenceEquals(room, targetRoom))
            {
                return false;
            }

            // Once this client has emitted JoinChannelSuccess, its own
            // lifecycle state is authoritative. Some versions of the Unity
            // LiveKit Room wrapper report IsConnected=false even while the
            // native connection is active; requiring that flag here aborts
            // microphone/camera publishing immediately after a successful
            // join.
            return !requireConnected ||
                   (targetRoom.LocalParticipant != null &&
                    (targetRoom.IsConnected || joinCallbackSent));
        }

        private bool IsMicrophoneAttemptCurrent(
            int generation,
            Room targetRoom,
            int deviceSelectionVersion)
        {
            return IsSessionCurrent(generation, targetRoom) &&
                   microphoneDeviceSelectionVersion ==
                   deviceSelectionVersion;
        }

        private bool IsCameraAttemptCurrent(
            int generation,
            Room targetRoom,
            int deviceSelectionVersion)
        {
            return IsSessionCurrent(generation, targetRoom) &&
                   videoDeviceSelectionVersion == deviceSelectionVersion &&
                   videoEnabled &&
                   publishingAllowedByRole &&
                   !IsScreenSharing;
        }

        private void NotifyParticipantConnected(Participant participant)
        {
            if (participant == null || IsLocalParticipant(participant))
            {
                return;
            }

            string identity = ParticipantIdentity(participant);
            // The transcript/recording Agent is a data and audio-processing
            // participant, not a meeting attendee. Keep it connected so its
            // ready/segment/recording packets are received, but do not expose
            // it through user/video callbacks or create a black video tile.
            if (IsInternalAgentIdentity(identity))
            {
                return;
            }
            if (!joinCallbackSent)
            {
                GetOrCreateLegacyUid(identity);
                return;
            }
            if (!connectedParticipantIdentities.Add(identity))
            {
                return;
            }

            uint legacyUid = GetOrCreateLegacyUid(identity);
            eventHandler?.OnLiveKitParticipantConnected(identity);
            eventHandler?.OnUserJoined(
                CurrentConnection(),
                legacyUid,
                0);
        }

        private static bool IsInternalAgentIdentity(string identity)
        {
            return !string.IsNullOrWhiteSpace(identity) &&
                   identity.StartsWith(
                       AgentIdentityPrefix,
                       StringComparison.OrdinalIgnoreCase);
        }

        private void ReplayParticipantTracks(RemoteParticipant participant)
        {
            if (participant == null)
            {
                return;
            }

            foreach (TrackPublication trackPublication in participant.Tracks.Values)
            {
                if (trackPublication is not RemoteTrackPublication publication)
                {
                    continue;
                }
                HandleTrackPublished(publication, participant);
                if (publication.Track is IRemoteTrack track)
                {
                    HandleTrackSubscribed(track, publication, participant);
                }
                if (publication.Muted)
                {
                    HandleTrackMuted(publication, participant);
                }
            }
        }

        private static bool IsPrimaryVideo(TrackPublication publication)
        {
            return publication != null &&
                   publication.Kind == TrackKind.KindVideo &&
                   (publication.Source == TrackSource.SourceCamera ||
                    publication.Source == TrackSource.SourceUnknown);
        }

        private bool IsLocalParticipant(Participant participant)
        {
            if (participant == null || room?.LocalParticipant == null)
            {
                return false;
            }
            return ReferenceEquals(participant, room.LocalParticipant) ||
                   string.Equals(
                       participant.Identity,
                       room.LocalParticipant.Identity,
                       StringComparison.Ordinal);
        }

        private static string ParticipantIdentity(Participant participant)
        {
            if (!string.IsNullOrWhiteSpace(participant?.Identity))
            {
                return participant.Identity;
            }
            if (!string.IsNullOrWhiteSpace(participant?.Sid))
            {
                return participant.Sid;
            }
            return "unknown-participant";
        }

        private uint GetOrCreateLegacyUid(string identity)
        {
            string key = string.IsNullOrWhiteSpace(identity)
                ? "unknown-participant"
                : identity;
            if (identityToLegacyUid.TryGetValue(key, out uint existing))
            {
                return existing;
            }

            // FNV-1a is deterministic on every Unity runtime, unlike
            // string.GetHashCode(). Linear probing only handles the extremely rare
            // case where two identities collide within this room session.
            uint candidate = 2166136261u;
            foreach (char character in key)
            {
                candidate ^= character;
                candidate *= 16777619u;
            }
            if (candidate == 0u)
            {
                candidate = 1u;
            }
            while (legacyUidToIdentity.TryGetValue(
                       candidate,
                       out string assignedIdentity) &&
                   !string.Equals(
                       assignedIdentity,
                       key,
                       StringComparison.Ordinal))
            {
                candidate++;
                if (candidate == 0u)
                {
                    candidate = 1u;
                }
            }

            identityToLegacyUid[key] = candidate;
            legacyUidToIdentity[candidate] = key;
            return candidate;
        }

        private JJRtcConnection CurrentConnection(string roomName = null)
        {
            string identity =
                room?.LocalParticipant?.Identity ??
                profile.ParticipantIdentity ??
                "local-participant";
            string resolvedRoomName =
                roomName ??
                room?.Name ??
                profile.ChannelName ??
                string.Empty;
            return new JJRtcConnection(
                resolvedRoomName,
                GetOrCreateLegacyUid(identity));
        }

        private void NotifyLocalVideoState(
            JJVideoSourceType source,
            JJLocalVideoStreamState state,
            JJLocalVideoStreamReason reason)
        {
            eventHandler?.OnLocalVideoStateChanged(source, state, reason);
        }

        private void NotifyConnectionState(
            JJConnectionState state,
            JJConnectionChangedReason reason)
        {
            if (lastConnectionState == state &&
                lastConnectionReason == reason)
            {
                return;
            }
            lastConnectionState = state;
            lastConnectionReason = reason;
            eventHandler?.OnConnectionStateChanged(
                CurrentConnection(),
                state,
                reason);
        }

        private void NotifyReconnecting()
        {
            NotifyConnectionState(
                JJConnectionState.RECONNECTING,
                JJConnectionChangedReason.INTERRUPTED);
            NotifyConnectionLost();
        }

        private void NotifyConnectionLost()
        {
            if (connectionLostNotified)
            {
                return;
            }
            connectionLostNotified = true;
            eventHandler?.OnConnectionLost(CurrentConnection());
        }

        private void NotifyLeave(string roomName, bool unexpected)
        {
            if (leaveCallbackSent)
            {
                return;
            }
            leaveCallbackSent = true;
            NotifyConnectionState(
                JJConnectionState.DISCONNECTED,
                unexpected
                    ? JJConnectionChangedReason.LOST
                    : JJConnectionChangedReason.LEAVE_CHANNEL);
            eventHandler?.OnLiveKitDisconnected(
                roomName ?? profile.ChannelName ?? string.Empty);
            eventHandler?.OnLeaveChannel(
                CurrentConnection(roomName),
                new JJRtcStats());
        }

        private Coroutine StartOwnerCoroutine(IEnumerator routine)
        {
            if (routine == null || owner == null)
            {
                return null;
            }
            try
            {
                return owner.StartCoroutine(routine);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Could not start a LiveKit coroutine because its Unity owner " +
                    "is unavailable: " + exception.Message);
                return null;
            }
        }

        private void StopOwnerCoroutine(ref Coroutine coroutine)
        {
            Coroutine activeCoroutine = coroutine;
            coroutine = null;
            if (activeCoroutine == null || owner == null)
            {
                return;
            }
            try
            {
                owner.StopCoroutine(activeCoroutine);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Could not stop a LiveKit coroutine because its Unity owner " +
                    "is unavailable: " + exception.Message);
            }
        }

        private void StartSessionMonitors()
        {
            StopSessionMonitors();

            string joinedToken = profile.Token;
            if (TryReadJwtExpiration(joinedToken, out long expirationUnixSeconds))
            {
                tokenMonitorCoroutine = StartOwnerCoroutine(
                    MonitorTokenExpirationRoutine(
                        expirationUnixSeconds,
                        joinedToken));
            }
            else
            {
                Debug.LogWarning(
                    "LiveKit token expiration cannot be monitored because the " +
                    "configured token is not a JWT with an exp claim.");
            }

            audioStatsCoroutine =
                StartOwnerCoroutine(MonitorAudioStatsRoutine());
        }

        private void StopSessionMonitors()
        {
            StopOwnerCoroutine(ref tokenMonitorCoroutine);
            StopOwnerCoroutine(ref audioStatsCoroutine);
        }

        private IEnumerator MonitorTokenExpirationRoutine(
            long expirationUnixSeconds,
            string joinedToken)
        {
            // Let StartCoroutine return before this routine can assign the field
            // back to null (important when the supplied token is already expired).
            yield return null;

            var interval = new WaitForSecondsRealtime(1f);
            while (!disposed && !leaving && Connected)
            {
                long secondsRemaining =
                    expirationUnixSeconds -
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (!tokenWillExpireNotified &&
                    secondsRemaining <= TokenWillExpireWarningSeconds)
                {
                    tokenWillExpireNotified = true;
                    eventHandler?.OnTokenPrivilegeWillExpire(
                        CurrentConnection(),
                        joinedToken);
                }

                if (!tokenRequestNotified && secondsRemaining <= 0)
                {
                    tokenRequestNotified = true;
                    eventHandler?.OnRequestToken(CurrentConnection());
                    break;
                }

                yield return interval;
            }

            tokenMonitorCoroutine = null;
        }

        private IEnumerator MonitorAudioStatsRoutine()
        {
            var interval =
                new WaitForSecondsRealtime(AudioStatsIntervalSeconds);

            while (!disposed && !leaving && Connected)
            {
                LocalAudioTrack localTrack = microphoneTrack;
                if (localTrack != null)
                {
                    GetSessionStatsInstruction instruction = null;
                    try
                    {
                        instruction = ((ITrack)localTrack).GetStats();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning(
                            "LiveKit local audio stats request failed: " +
                            exception.Message);
                    }

                    if (instruction != null)
                    {
                        yield return instruction;
                        if (!disposed && !leaving && Connected &&
                            ReferenceEquals(localTrack, microphoneTrack))
                        {
                            eventHandler?.OnLocalAudioStats(
                                CurrentConnection(),
                                new JJLocalAudioStats
                                {
                                    sentBitrate = instruction.IsError
                                        ? 0
                                        : ReadSentBitrateKbps(instruction.Stats)
                                });
                        }
                    }
                }

                if (disposed || leaving || !Connected)
                {
                    break;
                }

                var remoteTracks =
                    new List<KeyValuePair<string, RemoteAudioTrack>>(
                        remoteAudioTracks);
                foreach (KeyValuePair<string, RemoteAudioTrack> entry in remoteTracks)
                {
                    if (disposed || leaving || !Connected)
                    {
                        break;
                    }
                    if (!remoteAudioTracks.TryGetValue(
                            entry.Key,
                            out RemoteAudioTrack currentTrack) ||
                        !ReferenceEquals(currentTrack, entry.Value))
                    {
                        continue;
                    }

                    GetSessionStatsInstruction instruction = null;
                    try
                    {
                        instruction = ((ITrack)entry.Value).GetStats();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning(
                            "LiveKit remote audio stats request failed: " +
                            exception.Message);
                    }

                    if (instruction == null)
                    {
                        continue;
                    }

                    yield return instruction;
                    if (disposed || leaving || !Connected ||
                        !remoteAudioTracks.TryGetValue(
                            entry.Key,
                            out currentTrack) ||
                        !ReferenceEquals(currentTrack, entry.Value))
                    {
                        continue;
                    }

                    remoteAudioParticipantIdentities.TryGetValue(
                        entry.Key,
                        out string identity);
                    eventHandler?.OnRemoteAudioStats(
                        CurrentConnection(),
                        new JJRemoteAudioStats
                        {
                            uid = GetOrCreateLegacyUid(identity),
                            receivedSampleRate = instruction.IsError
                                ? AudioSettings.outputSampleRate
                                : ReadAudioSampleRate(instruction.Stats)
                        });
                }

                if (!disposed && !leaving && Connected)
                {
                    yield return interval;
                }
            }

            audioStatsCoroutine = null;
        }

        private static int ReadSentBitrateKbps(RtcStats[] stats)
        {
            if (stats == null)
            {
                return 0;
            }

            double bitsPerSecond = 0d;
            foreach (RtcStats stat in stats)
            {
                double targetBitrate =
                    stat?.OutboundRtp?.Outbound?.TargetBitrate ?? 0d;
                if (targetBitrate > 0d)
                {
                    bitsPerSecond += targetBitrate;
                }
            }

            double kilobitsPerSecond = bitsPerSecond / 1000d;
            if (kilobitsPerSecond >= int.MaxValue)
            {
                return int.MaxValue;
            }
            return kilobitsPerSecond <= 0d
                ? 0
                : (int)Math.Round(kilobitsPerSecond);
        }

        private static int ReadAudioSampleRate(RtcStats[] stats)
        {
            if (stats != null)
            {
                foreach (RtcStats stat in stats)
                {
                    CodecStats codec = stat?.Codec?.Codec_;
                    if (codec == null || codec.ClockRate == 0 ||
                        (!string.IsNullOrWhiteSpace(codec.MimeType) &&
                         !codec.MimeType.StartsWith(
                             "audio/",
                             StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    return codec.ClockRate >= int.MaxValue
                        ? int.MaxValue
                        : (int)codec.ClockRate;
                }
            }

            return AudioSettings.outputSampleRate;
        }

        private static bool TryReadJwtExpiration(
            string token,
            out long expirationUnixSeconds)
        {
            expirationUnixSeconds = 0;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string[] segments = token.Trim().Split('.');
            if (segments.Length < 2 || string.IsNullOrEmpty(segments[1]))
            {
                return false;
            }

            string payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 0:
                    break;
                case 2:
                    payload += "==";
                    break;
                case 3:
                    payload += "=";
                    break;
                default:
                    return false;
            }

            try
            {
                string json =
                    Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                JwtPayload jwtPayload =
                    JsonUtility.FromJson<JwtPayload>(json);
                expirationUnixSeconds = jwtPayload?.exp ?? 0;
                return expirationUnixSeconds > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ContainsDevice(
            string[] devices,
            string requestedDevice)
        {
            if (devices == null || string.IsNullOrWhiteSpace(requestedDevice))
            {
                return false;
            }
            foreach (string device in devices)
            {
                if (string.Equals(
                        device,
                        requestedDevice,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private void CleanupRemoteMedia()
        {
            foreach (AudioStream stream in remoteAudioStreams.Values)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "LiveKit remote audio cleanup failed: " +
                        exception.Message);
                }
            }
            remoteAudioStreams.Clear();
            remoteAudioTracks.Clear();
            remoteAudioParticipantIdentities.Clear();
            foreach (GameObject audioObject in remoteAudioObjects.Values)
            {
                if (audioObject != null)
                {
                    UnityEngine.Object.Destroy(audioObject);
                }
            }
            remoteAudioObjects.Clear();
            subscribedRemoteVideoSids.Clear();
            mutedRemoteTrackSids.Clear();
        }

        private void TryUnpublish(ILocalTrack track)
        {
            TryUnpublish(room, track);
        }

        private void TryUnpublish(Room targetRoom, ILocalTrack track)
        {
            if (track == null || targetRoom?.LocalParticipant == null ||
                !targetRoom.IsConnected)
            {
                return;
            }
            try
            {
                targetRoom.LocalParticipant.UnpublishTrack(track, true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "LiveKit local track unpublish failed during cleanup: " +
                    exception.Message);
            }
        }

        private void StopMicrophone()
        {
            TryUnpublish(microphoneTrack);
            microphoneTrack = null;
            DisposeMicrophoneCapture();
        }

        private void DisposeMicrophoneCapture()
        {
            MicrophoneSource source = microphoneSource;
            microphoneSource = null;
            if (source != null)
            {
                source.AudioRead -= HandleMicrophoneSamples;
                try
                {
                    // MicrophoneSource.Dispose already invokes Stop. Calling both
                    // Stop and Dispose schedules the LiveKit stop coroutine twice.
                    source.Dispose();
                }
                catch (MissingReferenceException exception)
                {
                    Debug.LogWarning(
                        "LiveKit microphone context was already destroyed during " +
                        "Unity shutdown: " + exception.Message);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "LiveKit microphone cleanup failed: " +
                        exception.Message);
                }
            }
            if (microphoneObject != null)
            {
                UnityEngine.Object.Destroy(microphoneObject);
                microphoneObject = null;
            }
            microphoneFrameCount = 0;
            activeMicrophoneDevice = null;
        }

        private void StopCamera(bool notifyState = true)
        {
            bool hadCamera =
                cameraTrack != null ||
                cameraSource != null ||
                webCamTexture != null ||
                jorjinCameraCapture != null;
            if (cameraPublishGeneration == sessionGeneration &&
                !cameraUpdateActive)
            {
                // Do not abandon a native PublishTrack instruction. Let the
                // guarded publish coroutine observe that its track/source were
                // replaced, then unpublish the late completion by SID.
                cameraCoroutine = null;
            }
            else
            {
                StopOwnerCoroutine(ref cameraCoroutine);
            }
            cameraUpdateActive = false;
            TryUnpublish(cameraTrack);
            cameraTrack = null;
            if (cameraSource != null)
            {
                try
                {
                    cameraSource.Stop();
                    cameraSource.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "LiveKit camera source cleanup failed: " +
                        exception.Message);
                }
            }
            cameraSource = null;
            if (webCamTexture != null)
            {
                try
                {
                    webCamTexture.Stop();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "Unity camera cleanup failed: " + exception.Message);
                }
                UnityEngine.Object.Destroy(webCamTexture);
                webCamTexture = null;
            }
            if (jorjinCameraCapture != null)
            {
                try
                {
                    jorjinCameraCapture.StopCapture();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "Jorjin camera cleanup failed: " + exception.Message);
                }
                UnityEngine.Object.Destroy(jorjinCameraCapture);
                jorjinCameraCapture = null;
            }
            if (screenSource == null)
            {
                PublishLocalVideoTexture(null);
            }
            if (notifyState && hadCamera)
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.CAMERA,
                    JJLocalVideoStreamState.STOPPED,
                    JJLocalVideoStreamReason.OK);
            }
        }

        private void StopScreen(bool notifyState = true)
        {
            bool hadScreen = screenTrack != null || screenSource != null;
            if (screenPublishGeneration == sessionGeneration &&
                !screenUpdateActive)
            {
                // As with camera switching, allow an in-flight native publish
                // to complete so the guarded coroutine can unpublish its SID.
                screenCoroutine = null;
            }
            else
            {
                StopOwnerCoroutine(ref screenCoroutine);
            }
            screenUpdateActive = false;
            TryUnpublish(screenTrack);
            screenTrack = null;
            if (screenSource != null)
            {
                try
                {
                    screenSource.TextureReceived -= HandleLocalScreenTexture;
                    screenSource.Stop();
                    screenSource.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "LiveKit screen source cleanup failed: " +
                        exception.Message);
                }
                screenSource = null;
            }
            if (cameraSource == null)
            {
                PublishLocalVideoTexture(null);
            }
            if (notifyState && hadScreen)
            {
                NotifyLocalVideoState(
                    JJVideoSourceType.SCREEN,
                    JJLocalVideoStreamState.STOPPED,
                    JJLocalVideoStreamReason.OK);
                eventHandler?.OnLiveKitScreenShareChanged(false);
            }
        }

        private void RemoveRemoteAudio(string sid)
        {
            if (remoteAudioStreams.TryGetValue(sid, out AudioStream stream))
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "LiveKit remote audio cleanup failed: " +
                        exception.Message);
                }
                remoteAudioStreams.Remove(sid);
            }
            if (remoteAudioObjects.TryGetValue(sid, out GameObject audioObject))
            {
                if (audioObject != null)
                {
                    UnityEngine.Object.Destroy(audioObject);
                }
                remoteAudioObjects.Remove(sid);
            }
            remoteAudioTracks.Remove(sid);
            remoteAudioParticipantIdentities.Remove(sid);
        }

        private void RemoveRemoteAudioForParticipant(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) ||
                remoteAudioParticipantIdentities.Count == 0)
            {
                return;
            }

            var trackSids = new List<string>();
            foreach (KeyValuePair<string, string> entry
                     in remoteAudioParticipantIdentities)
            {
                if (string.Equals(
                        entry.Value,
                        identity,
                        StringComparison.Ordinal))
                {
                    trackSids.Add(entry.Key);
                }
            }

            foreach (string trackSid in trackSids)
            {
                RemoveRemoteAudio(trackSid);
            }
        }

        public void Leave()
        {
            if (leaveCallbackSent && room == null && joinCoroutine == null)
            {
                return;
            }

            unchecked
            {
                sessionGeneration++;
            }
            leaving = true;
            Room activeRoom = room;
            bool connectionStillPending =
                activeRoom != null &&
                activeRoom.LocalParticipant == null;

            StopOwnerCoroutine(ref joinCoroutine);
            StopStandalonePreview(false);
            StopSessionMonitors();
            StopScreen();
            StopCamera();
            StopMicrophone();
            CleanupRemoteMedia();

            string roomName =
                activeRoom?.Name ??
                profile.ChannelName ??
                string.Empty;
            if (activeRoom != null)
            {
                if (connectionStillPending)
                {
                    ArmLateConnectCleanup(activeRoom);
                }
                else
                {
                    // Keep events bound while asking LiveKit to disconnect, then
                    // complete the legacy callback synchronously. If Disconnected is
                    // raised synchronously, NotifyLeave's guard prevents duplicates.
                    try
                    {
                        activeRoom.Disconnect();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning(
                            "LiveKit disconnect failed during leave: " +
                            exception.Message);
                    }
                    UnbindRoomEvents(activeRoom);
                }
                NotifyLeave(roomName, false);
                if (room == activeRoom)
                {
                    room = null;
                }
            }
            else
            {
                NotifyLeave(roomName, false);
            }

            connectedParticipantIdentities.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Leave();
        }

        private void ReportError(string message)
        {
            Debug.LogError(message);
            eventHandler?.OnError(-1000, message);
        }

        [Serializable]
        private sealed class JwtPayload
        {
            public long exp = 0;
        }

        [Serializable]
        private sealed class TranscriptControlPacket
        {
            public string action;
            public string session_id;
        }

        [Serializable]
        private sealed class TranscriptEventPacket
        {
            public string type;
            public string state;
            public string session_id;
            public string timestamp;
            public string participant_identity;
            public string participant_name;
            public string text;
            public string message;
            public int sequence;
            public int segment_count;
            public string recording_file_name;
            public long recording_bytes;
            public int recording_chunk_count;
            public int recording_chunk_index;
            public string recording_data;
            public string recording_sha256;
            public float recording_duration_seconds;
            public bool recording_truncated;
        }
    }
}
