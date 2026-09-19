using Agora.Rtc;
using System;
using System.Threading;
using UnityEngine;

namespace Jorjin.Streaming
{
    public class JorjinStreamingSDK : MonoBehaviour
    {
        #region Agora config
        /// <summary>
        /// The streaming profile containing Agora App ID, channel, tokens and event handler used to initialize the SDK.
        /// Set this before calling <see cref="Initialize"/> if you need to customize initialization parameters.
        /// </summary>
        private JJStreamingProfile streamingProfile;

        /// <summary>
        /// Gets the <see cref="JJStreamingProfile"/> used to initialize and configure the RTC engine.
        /// </summary>
        public JJStreamingProfile StreamingProfile
        {
            get
            {
                return streamingProfile;
            }
            private set
            {
                streamingProfile = value;
            }
        }

        private IRtcEngineEx rtcEngine;
        private LiveKitStreamingClient liveKitClient;
        private JorjinArCameraCapture jorjinCameraCapture;
        private StreamingEventHandler streamingEventHandler;
        public bool HasEngine => rtcEngine != null || liveKitClient != null;
        public bool IsConnected => liveKitClient != null ? liveKitClient.Connected : initialized;
        public JJStreamingBackend Backend => streamingProfile?.Backend ?? JJStreamingBackend.Agora;
        #endregion

        /// <summary>
        /// The audio device manager provided by the Agora engine. Null until engine is initialized.
        /// </summary>
        private AudioDeviceManager audioDeviceManager;

        /// <summary>
        /// The video device manager provided by the Agora engine. Null until engine is initialized.
        /// </summary>
        private VideoDeviceManager videoDeviceManager;

        /// <summary>
        /// Reference to the voice streaming handler that will be used to control audio features.
        /// </summary>
        private VoiceStreamingHandler voiceStreamingHandler;

        /// <summary>
        /// Reference to the video streaming handler that will be used to control camera/video features.
        /// </summary>
        private VideoStreamingHandler videoStreamingHandler;
         
        /// <summary>
        /// Reference to the screen share handler used to manage screen capture.
        /// </summary>
        private ScreenShareHandler screenShareHandler;

        private bool initialized = false;
        public bool Initialized => initialized;

        private RemoteAudioFrameObserver remoteAudioFrameObserver;
        private volatile bool remoteAudioFrameObserverEnabled;
        private JJRemoteAudioFrameConfig remoteAudioFrameConfig;
        private int remoteAudioFrameErrorCount;

        /// <summary>
        /// Raised on the RTC/Unity audio thread for every subscribed remote user's
        /// PCM16 frame. Do not access Unity UI objects directly in this callback.
        /// </summary>
        public event Action<JJRemoteAudioFrame> RemoteAudioFrameReceived;

        public bool RemoteAudioFrameObserverEnabled =>
            remoteAudioFrameObserverEnabled;

        private void Awake()
        {
            // The verified JJUnityPluginv2 scene starts CamRenderer as soon as
            // the app opens. Do the same here so JJSDK can display its USB/AR
            // glasses permission prompt before a LiveKit room is joined.
            PrewarmJorjinCamera();
        }

        private void PrewarmJorjinCamera()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (jorjinCameraCapture == null)
            {
                jorjinCameraCapture = GetComponent<JorjinArCameraCapture>();
                if (jorjinCameraCapture == null)
                {
                    jorjinCameraCapture = gameObject.AddComponent<JorjinArCameraCapture>();
                }
            }

            StartCoroutine(jorjinCameraCapture.StartCapture());
            Debug.Log("Prewarming the Jorjin AR glasses camera for USB authorization.");
#endif
        }

        /// <summary>
        /// Initializes the Agora SDK using the provided <see cref="JJStreamingProfile"/>.
        /// - Calls <see cref="JJStreamingProfile.InitSDK"/> to create and initialize the engine.
        /// - Enables dual stream mode and populates device manager references.
        /// After this call <see cref="RtcEngine"/> will be non-null if initialization succeeds.
        /// </summary>
        public void Initialize(JJStreamingProfile profile, StreamingEventHandler eventHandler)
        {
            if (profile == null)
            {
                Debug.LogError("Jorjin Streaming SDK initialize failed: profile is null.");
                return;
            }

            // Initialize is intentionally re-entrant because the sample UI and
            // vendor integrations can press/call Init more than once. Tear down
            // only the active RTC backend here; keep an idle prewarmed glasses
            // camera alive so Android does not repeat USB authorization unless
            // the old LiveKit client actually owned and stopped that capture.
            if (liveKitClient != null || rtcEngine != null || initialized)
            {
                DisposeBackend();
            }

            streamingProfile = profile;
            streamingEventHandler = eventHandler ?? new StreamingEventHandler();
            streamingEventHandler.SetProfile(streamingProfile);
            if (voiceStreamingHandler != null)
            {
                streamingEventHandler.SetVoiceHandler(voiceStreamingHandler);
            }
            if (videoStreamingHandler != null)
            {
                streamingEventHandler.SetVideoHandler(videoStreamingHandler);
            }
            if (screenShareHandler != null)
            {
                streamingEventHandler.SetScreenShareHandler(screenShareHandler);
            }
            streamingProfile.StreamEventHandler = streamingEventHandler;

            if (streamingProfile.Backend == JJStreamingBackend.LiveKit)
            {
                PrewarmJorjinCamera();
                liveKitClient = new LiveKitStreamingClient(this, streamingProfile, streamingEventHandler);
                initialized = true;
                return;
            }

            rtcEngine = streamingProfile.InitSDK();
            if (rtcEngine == null)
            {
                Debug.LogError("Agora initialization failed.");
                initialized = false;
                return;
            }
            rtcEngine.EnableDualStreamMode(true);
            initialized = true;
            audioDeviceManager = (AudioDeviceManager)rtcEngine.GetAudioDeviceManager();
            videoDeviceManager = (VideoDeviceManager)rtcEngine.GetVideoDeviceManager();
        }

        /// <summary>
        /// Disposes the Agora engine if it has been created and marks the SDK as uninitialized.
        /// Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            DisposeBackend();
            DisposePrewarmedJorjinCamera();
        }

        private void DisposeBackend()
        {
            DisableRemoteAudioFrameObserver();
            if (liveKitClient != null)
            {
                liveKitClient.Dispose();
                liveKitClient = null;
            }
            if (rtcEngine != null)
            {
                rtcEngine.Dispose();
                initialized = false;
                rtcEngine = null;
                audioDeviceManager = null;
                videoDeviceManager = null;
            }
            initialized = false;
        }

        /// <summary>
        /// Enables copied, per-user remote PCM16 callbacks for Agora or LiveKit.
        /// Supported rates are 8, 16, 32, 44.1 and 48 kHz; channels are 1 or 2.
        /// </summary>
        public int EnableRemoteAudioFrameObserver(
            JJRemoteAudioFrameConfig config = null)
        {
            if (!initialized || (liveKitClient == null && rtcEngine == null))
            {
                return -1;
            }

            config ??= new JJRemoteAudioFrameConfig();
            if (!IsSupportedRemoteAudioFrameConfig(config))
            {
                return -2;
            }

            if (remoteAudioFrameObserverEnabled &&
                remoteAudioFrameConfig != null &&
                remoteAudioFrameConfig.sampleRate == config.sampleRate &&
                remoteAudioFrameConfig.channels == config.channels)
            {
                return 0;
            }

            DisableRemoteAudioFrameObserver();
            var appliedConfig = new JJRemoteAudioFrameConfig
            {
                sampleRate = config.sampleRate,
                channels = config.channels
            };

            int result;
            if (liveKitClient != null)
            {
                result = liveKitClient.EnableRemoteAudioFrameObserver(
                    appliedConfig,
                    HandleRemoteAudioFrame);
            }
            else
            {
                result = rtcEngine.SetPlaybackAudioFrameBeforeMixingParameters(
                    appliedConfig.sampleRate,
                    appliedConfig.channels);
                if (result >= 0)
                {
                    var observer = new RemoteAudioFrameObserver(
                        HandleRemoteAudioFrame,
                        HandleRemoteAudioFrameError);
                    result = rtcEngine.RegisterAudioFrameObserver(
                        observer,
                        AUDIO_FRAME_POSITION.AUDIO_FRAME_POSITION_BEFORE_MIXING,
                        OBSERVER_MODE.INTPTR);
                    if (result >= 0)
                    {
                        remoteAudioFrameObserver = observer;
                    }
                }
            }

            if (result < 0)
            {
                return result;
            }

            remoteAudioFrameConfig = appliedConfig;
            remoteAudioFrameErrorCount = 0;
            remoteAudioFrameObserverEnabled = true;
            return result;
        }

        /// <summary>Stops per-user remote PCM callbacks. Safe to call repeatedly.</summary>
        public int DisableRemoteAudioFrameObserver()
        {
            remoteAudioFrameObserverEnabled = false;
            remoteAudioFrameConfig = null;

            if (liveKitClient != null)
            {
                liveKitClient.DisableRemoteAudioFrameObserver();
            }

            if (rtcEngine == null || remoteAudioFrameObserver == null)
            {
                remoteAudioFrameObserver = null;
                return 0;
            }

            int result = rtcEngine.UnRegisterAudioFrameObserver();
            remoteAudioFrameObserver = null;
            return result;
        }

        private static bool IsSupportedRemoteAudioFrameConfig(
            JJRemoteAudioFrameConfig config)
        {
            bool validSampleRate =
                config.sampleRate == 8000 ||
                config.sampleRate == 16000 ||
                config.sampleRate == 32000 ||
                config.sampleRate == 44100 ||
                config.sampleRate == 48000;
            return validSampleRate &&
                   (config.channels == 1 || config.channels == 2);
        }

        private void HandleRemoteAudioFrame(JJRemoteAudioFrame frame)
        {
            if (!remoteAudioFrameObserverEnabled)
            {
                return;
            }

            Action<JJRemoteAudioFrame> handlers = RemoteAudioFrameReceived;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<JJRemoteAudioFrame> handler
                     in handlers.GetInvocationList())
            {
                try
                {
                    handler(frame);
                }
                catch (Exception exception)
                {
                    HandleRemoteAudioFrameError(exception);
                }
            }
        }

        private void HandleRemoteAudioFrameError(Exception exception)
        {
            int errorCount = Interlocked.Increment(ref remoteAudioFrameErrorCount);
            if (errorCount <= 3 || errorCount % 300 == 0)
            {
                UnityEngine.Debug.LogError(
                    $"Remote audio frame callback failed ({errorCount}): {exception}");
            }
        }

        private void DisposePrewarmedJorjinCamera()
        {
            if (jorjinCameraCapture != null)
            {
                jorjinCameraCapture.StopCapture();
                Destroy(jorjinCameraCapture);
                jorjinCameraCapture = null;
            }
        }

        /// <summary>
        /// Attaches a <see cref="VoiceStreamingHandler"/> to the SDK.
        /// Also registers the handler with the profile event handler and sets its engine reference.
        /// </summary>
        /// <param name="voiceHandler">The voice streaming handler instance to attach.</param>
        public void SetVoiceHandler(VoiceStreamingHandler voiceHandler)
        {
            streamingEventHandler?.SetVoiceHandler(voiceHandler);
            if (rtcEngine != null && voiceHandler != null) voiceHandler.SetEngine(rtcEngine);
            voiceStreamingHandler = voiceHandler;
        }

        /// <summary>
        /// Attaches a <see cref="VideoStreamingHandler"/> to the SDK.
        /// Also registers the handler with the profile event handler and sets its engine reference.
        /// </summary>
        /// <param name="videoHandler">The video streaming handler instance to attach.</param>
        public void SetVideoHandler(VideoStreamingHandler videoHandler)
        {
            streamingEventHandler?.SetVideoHandler(videoHandler);
            if (rtcEngine != null && videoHandler != null) videoHandler.SetEngine(rtcEngine);
            videoStreamingHandler = videoHandler;
        }

        /// <summary>
        /// Attaches a <see cref="ScreenShareHandler"/> to the SDK.
        /// Also registers the handler with the profile event handler and sets its engine reference.
        /// </summary>
        /// <param name="shareHandler">The screen share handler instance to attach.</param>
        public void SetShareScreenHandler(ScreenShareHandler shareHandler)
        {
            streamingEventHandler?.SetScreenShareHandler(shareHandler);
            if (rtcEngine != null && shareHandler != null) shareHandler.SetEngine(rtcEngine);
            screenShareHandler = shareHandler;
        }

        /// <summary>
        /// Joins the configured channel using the <see cref="StreamingProfile"/> token and user id.
        /// </summary>
        /// <returns>
        /// The result code returned by the Agora SDK. Returns -1 if the engine is not initialized.
        /// </returns>
        public int JoinChannel()
        {   
            if (liveKitClient != null) return liveKitClient.Join();
            if(rtcEngine == null)
            {
                return -1;
            }
            return rtcEngine.JoinChannel(streamingProfile.Token, streamingProfile.ChannelName, "", streamingProfile.UserId);
        }

        /// <summary>
        /// Joins a channel using the Agora <see cref="IRtcEngineEx.JoinChannelEx"/> API for advanced connection scenarios.
        /// </summary>
        /// <param name="token">Token to use for the join request.</param>
        /// <param name="connection">The <see cref="RtcConnection"/> describing the channel/uid to join.</param>
        /// <param name="options">Channel media options to apply for the join.</param>
        public void JoinChannelEx(string token, JJRtcConnection connection, JJChannelMediaOptions options)
        {
            rtcEngine.JoinChannelEx(token, connection.ToAgora(), options.ToAgora());
        }

        /// <summary>
        /// Sets the client role for this user (broadcaster/audience) using the Agora <see cref="CLIENT_ROLE_TYPE"/>.
        /// </summary>
        /// <param name="role">Role to set.</param>
        public void SetClientRole(JJClientRoleType role)
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetClientRole(role);
                return;
            }
            if (rtcEngine == null) return;
            rtcEngine.SetClientRole((CLIENT_ROLE_TYPE)role);
        }

        /// <summary>
        /// Enables or disables local video publishing via the attached video handler.
        /// No-op if no video handler is attached.
        /// </summary>
        /// <param name="value">True to enable local video, false to disable.</param>
        public void EnableLocalVideo(bool value)
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetVideoEnabled(value);
                return;
            }
            if (videoStreamingHandler == null) return;
            videoStreamingHandler.EnableLocalVideo(value);
        }

        /// <summary>
        /// Enables or disables local audio (microphone) via the attached voice handler.
        /// No-op if no voice handler is attached.
        /// </summary>
        /// <param name="status">True to enable local audio, false to disable.</param>
        public void EnableLocalAudio(bool status)
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetAudioEnabled(status);
                return;
            }
            if (voiceStreamingHandler == null) return;
            voiceStreamingHandler.EnableLocalAudio(status);
        }

        /// <summary>
        /// Mutes or unmutes publishing of the local audio stream.
        /// No-op if no voice handler is attached.
        /// </summary>
        /// <param name="status">True to stop publishing local audio, false to resume.</param>
        public void MuteLocalAudioStream(bool status)
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetAudioEnabled(!status);
                return;
            }
            if (voiceStreamingHandler == null) return;
            voiceStreamingHandler.MuteLocalAudioStream(status);
        }

        /// <summary>
        /// Requests the attached video handler to enable video functionality.
        /// No-op if no video handler is attached.
        /// </summary>
        public void EnableVideo()
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetVideoEnabled(true);
                return;
            }
            if (videoStreamingHandler == null) return;
            videoStreamingHandler.EnableVideo();
        }

        /// <summary>
        /// Requests the attached video handler to disable video functionality.
        /// No-op if no video handler is attached.
        /// </summary>
        public void DisableVideo()
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetVideoEnabled(false);
                return;
            }
            if (videoStreamingHandler == null) return;
            videoStreamingHandler.DisableVideo();
        }

        /// <summary>
        /// Requests the attached voice handler to enable the audio module.
        /// No-op if no voice handler is attached.
        /// </summary>
        public void EnableAudio()
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetAudioEnabled(true);
                return;
            }
            if (voiceStreamingHandler == null) return;
            voiceStreamingHandler.EnableAudio();
        }

        /// <summary>
        /// Requests the attached voice handler to disable the audio module.
        /// No-op if no voice handler is attached.
        /// </summary>
        public void DisableAudio()
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetAudioEnabled(false);
                return;
            }
            if (voiceStreamingHandler == null) return;
            voiceStreamingHandler.DisableAudio();
        }

        /// <summary>
        /// Starts the local video preview on the engine if initialized.
        /// </summary>
        public void StartPreview()
        {
            if (liveKitClient != null)
            {
                liveKitClient.StartPreview();
                return;
            }
            if (rtcEngine == null || !initialized) return;
            rtcEngine.StartPreview();
        }

        /// <summary>
        /// Stops the local video preview on the engine if initialized.
        /// </summary>
        public void StopPreview()
        {
            if (liveKitClient != null)
            {
                liveKitClient.StopPreview();
                return;
            }
            if (rtcEngine == null || !initialized) return;
            rtcEngine.StopPreview();
        }

        /// <summary>
        /// Renews the RTC token for the current engine.
        /// </summary>
        /// <param name="token">The new token to apply.</param>
        /// <returns>
        /// Result code from Agora, or -1 for LiveKit/uninitialized backends.
        /// LiveKit connection tokens are refreshed by requesting a new
        /// connection payload from the Meeting Session service and rebuilding
        /// the LiveKit client; this Unity SDK version has no in-place Room token
        /// update API.
        /// </returns>
        public int RenewToken(string token)
        {
            if (liveKitClient != null)
            {
                return liveKitClient.RenewToken(token);
            }
            if (rtcEngine == null) return -1;
            return rtcEngine.RenewToken(token);
        }

        /// <summary>
        /// Leaves the current channel using the default leave API.
        /// </summary>
        /// <returns>
        /// The result code returned by the Agora SDK. Returns -1 if the engine is not initialized.
        /// </returns>
        public int LeaveChannel()
        {
            if (liveKitClient != null)
            {
                liveKitClient.Leave();
                return 0;
            }
            if (rtcEngine == null)
            {
                return -1;
            }
            return rtcEngine.LeaveChannel();
        }

        /// <summary>
        /// Leaves a channel specified by <paramref name="rtcConnection"/> using the extended API.
        /// </summary>
        /// <param name="rtcConnection">Connection describing the channel to leave.</param>
        /// <returns>Result code from the Agora SDK or -1 when engine is not initialized.</returns>
        public int LeaveChannelEx(JJRtcConnection rtcConnection)
        {
            if (rtcEngine == null) return -1;
            return rtcEngine.LeaveChannelEx(rtcConnection.ToAgora());
        }

        /// <summary>
        /// Starts a screen-share session by delegating to the attached screen share handler.
        /// </summary>
        /// <param name="options">Channel media options to use for the share session.</param>
        /// <returns>Result code from the handler or -1 if no screen share handler is attached.</returns>
        public int ShareScreen(JJChannelMediaOptions options)
        {
            if (liveKitClient != null) return liveKitClient.StartDesktopShare();
            if (screenShareHandler == null) return -1;
            return screenShareHandler.StartScreenCapture(streamingProfile.ShareToken, new JJRtcConnection(streamingProfile.ChannelName, streamingProfile.ScreenShareId), options);
        }
        
        /// <summary>
        /// Stops the screen-share session by leaving the dedicated screen-share connection.
        /// </summary>
        /// <returns>Result code from the Agora SDK or -1 when engine is not initialized.</returns>
        public int StopShareScreen()
        {
            if (liveKitClient != null) return liveKitClient.StopScreenShare();
            if (rtcEngine == null) return -1;
            return rtcEngine.LeaveChannelEx(new RtcConnection(streamingProfile.ChannelName, streamingProfile.ScreenShareId));
        }

        /// <summary>
        /// Sends a lightweight LiveKit meeting reaction to every participant.
        /// </summary>
        public int SendReaction(JJReactionType reaction)
        {
            return liveKitClient != null ? liveKitClient.SendReaction(reaction) : -1;
        }

        /// <summary>
        /// Starts or stops the room-level Transcript Agent session.
        /// </summary>
        public int SendTranscriptControl(bool start, string sessionId)
        {
            return liveKitClient != null
                ? liveKitClient.SendTranscriptControl(start, sessionId)
                : -1;
        }

        /// <summary>Ask about the current transcript or request a meeting summary.</summary>
        public int SendCollaborationRequest(string action, string sessionId, string requestId, string question = "")
        {
            return liveKitClient != null
                ? liveKitClient.SendCollaborationRequest(action, sessionId, requestId, question)
                : -1;
        }

        /// <summary>
        /// Sends a JPEG photo to the Agent for text recognition and Chinese/English
        /// translation. Returns 0 when sending starts, -2 when no Agent is in the room.
        /// </summary>
        public int SendCollaborationImage(byte[] jpeg, string requestId, Action<bool> sent = null, string instruction = "")
        {
            return liveKitClient != null
                ? liveKitClient.SendCollaborationImage(jpeg, requestId, sent, instruction)
                : -1;
        }

        /// <summary>Announces this participant as "field" (場域端) or "expert" (專家端).</summary>
        public void SetLiveKitMeetingRole(string role)
        {
            liveKitClient?.SetMeetingRole(role);
        }

        public int StartLiveKitDesktopShare()
        {
            return liveKitClient?.StartDesktopShare() ?? -1;
        }

        public int StartLiveKitWindowShare(System.IntPtr windowHandle, int width, int height)
        {
            return liveKitClient?.StartWindowShare(windowHandle, width, height) ?? -1;
        }

        /// <summary>
        /// Starts screen capture using the engine-level API and given parameters.
        /// </summary>
        /// <param name="captureParams">Parameters that control screen capture behaviour.</param>
        /// <returns>Result code from the Agora SDK or -1 when engine is not initialized.</returns>
        public int StartScreenCapture(JJScreenCaptureParameters2 captureParams)
        {
            if (liveKitClient != null)
            {
                if (captureParams != null && captureParams.captureAudio)
                {
                    Debug.LogWarning(
                        "LiveKit Unity screen capture publishes video only; " +
                        "system-audio capture is not available in this backend.");
                }
                return liveKitClient.StartDesktopShare();
            }
            if (rtcEngine == null) return -1;
            return rtcEngine.StartScreenCapture(captureParams.ToAgora());
        }

        /// <summary>
        /// Stops engine-level screen capture.
        /// </summary>
        /// <returns>Result code from the Agora SDK or -1 when engine is not initialized.</returns>
        public int StopScreenCapture()
        {
            if (liveKitClient != null)
            {
                return liveKitClient.StopScreenShare();
            }
            if (rtcEngine == null) return -1;
            return rtcEngine.StopScreenCapture();
        }

        /// <summary>
        /// Destroys a custom video track created previously.
        /// </summary>
        /// <param name="video_track_id">Identifier of the custom video track to destroy.</param>
        public void DestroyCustomVideoTrack(uint video_track_id)
        {
            if (rtcEngine == null) return;
            rtcEngine.DestroyCustomVideoTrack(video_track_id);
        }

        /// <summary>
        /// Mutes or unmutes all remote audio streams.
        /// </summary>
        /// <param name="status">True to mute all remote audio streams, false to unmute.</param>
        public void MuteAllRemoteAudioStreams(bool status)
        {
            if (liveKitClient != null)
            {
                liveKitClient.MuteAllRemoteAudioStreams(status);
                return;
            }
            if (rtcEngine == null) return;
            rtcEngine.MuteAllRemoteAudioStreams(status);
        }

        /// <summary>
        /// Mutes or unmutes all remote video streams.
        /// </summary>
        /// <param name="status">True to mute all remote video streams, false to unmute.</param>
        public void MuteAllRemoteVideoStreams(bool status)
        {
            if (liveKitClient != null)
            {
                liveKitClient.MuteAllRemoteVideoStreams(status);
                return;
            }
            if (rtcEngine == null) return;
            rtcEngine.MuteAllRemoteVideoStreams(status);
        }

        /// <summary>
        /// Mutes or unmutes publishing of the local video stream.
        /// </summary>
        /// <param name="status">True to mute the local video stream, false to unmute.</param>
        public void MuteLocalVideoStream(bool status)
        {
            if (liveKitClient != null)
            {
                liveKitClient.SetVideoEnabled(!status);
                return;
            }
            if (rtcEngine == null) return;
            rtcEngine.MuteLocalVideoStream(status);
        }

        /// <summary>
        /// Updates the active screen capture configuration via the attached screen share handler.
        /// No-op if no screen share handler is attached.
        /// </summary>
        /// <param name="screenCaptureParameters2">The new screen capture parameters to apply.</param>
        public void UpdateScreenCapture(JJScreenCaptureParameters2 screenCaptureParameters2)
        {
            if (screenShareHandler == null) return;
            screenShareHandler.UpdateScreenCapture(screenCaptureParameters2);
        }

        /// <summary>
        /// Updates channel media options for a specific connection using the engine API.
        /// </summary>
        /// <param name="options">Channel media options to apply.</param>
        /// <param name="rtcConnection">Connection describing the target channel.</param>
        public void UpdateChannelMediaOptionsEx(JJChannelMediaOptions options, JJRtcConnection rtcConnection)
        {
            if (rtcEngine == null) return;
            rtcEngine.UpdateChannelMediaOptionsEx(options.ToAgora(), rtcConnection.ToAgora());
        }

        /// <summary>
        /// Starts screen capture by display id using engine API.
        /// Delegates to the engine and returns the engine result code.
        /// </summary>
        /// <param name="displayId">Display identifier.</param>
        /// <param name="regionRect">Region rectangle to capture.</param>
        /// <param name="captureParams">Capture parameters.</param>
        /// <returns>Result code from the Agora SDK or -1 when handler not attached.</returns>
        public int StartScreenCaptureByDisplayId(uint displayId, RectInt regionRect, JJScreenCaptureParameters captureParams)
        {
            if (liveKitClient != null)
            {
                if (displayId != 0)
                {
                    Debug.LogWarning(
                        "LiveKit Unity currently captures the primary display only.");
                    return -2;
                }
                return liveKitClient.StartDesktopShare();
            }
            if (rtcEngine == null) return -1;
            return rtcEngine.StartScreenCaptureByDisplayId(displayId, regionRect.ToAgora(), captureParams.ToAgora());
        }

        /// <summary>
        /// Starts screen capture by window id using engine API.
        /// </summary>
        /// <param name="windowId">Window identifier.</param>
        /// <param name="regionRect">Region rectangle to capture.</param>
        /// <param name="captureParams">Capture parameters.</param>
        /// <returns>Result code from the Agora SDK or -1 when handler not attached.</returns>
        public int StartScreenCaptureByWindowId(long windowId, RectInt regionRect, JJScreenCaptureParameters captureParams)
        {
            if (liveKitClient != null)
            {
                int width = regionRect.width > 0 ? regionRect.width : Screen.width;
                int height = regionRect.height > 0 ? regionRect.height : Screen.height;
                return liveKitClient.StartWindowShare(
                    new IntPtr(windowId),
                    width,
                    height);
            }
            if (rtcEngine == null) return -1;
            return rtcEngine.StartScreenCaptureByWindowId(windowId, regionRect.ToAgora(), captureParams.ToAgora());
        }

        /// <summary>
        /// Pushes an external video frame to the engine for a particular video track.
        /// </summary>
        /// <param name="frame">The external video frame to push.</param>
        /// <param name="videoTrackId">Optional custom video track id to push the frame to (default 0).</param>
        public void PushVideoFrame(JJExternalVideoFrame frame, uint videoTrackId = 0)
        {
            if (rtcEngine == null) return;
            rtcEngine.PushVideoFrame(frame.ToAgora(), videoTrackId);
        }

        /// <summary>
        /// Creates a custom video track via the engine.
        /// </summary>
        /// <returns>The created custom video track id, or 0xffffffff if no screen share handler is attached.</returns>
        public uint CreateCustomVideoTrack()
        {
            if (rtcEngine == null) return 0xffffffff;
            return rtcEngine.CreateCustomVideoTrack();
        }

        /// <summary>
        /// Returns a process-monotonic timestamp in milliseconds. Unlike wall-clock
        /// time, this value never jumps when the system clock is adjusted.
        /// </summary>
        public long GetCurrentMonotonicTimeInMs()
        {
            if (liveKitClient != null)
            {
                return LiveKitStreamingClient.GetCurrentMonotonicTimeInMs();
            }
            if (rtcEngine != null)
            {
                return rtcEngine.GetCurrentMonotonicTimeInMs();
            }
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
                          (1000d /
                           System.Diagnostics.Stopwatch.Frequency));
        }

        /// <summary>
        /// Sets the global video encoder configuration via the engine.
        /// </summary>
        /// <param name="config">Video encoder configuration to apply.</param>
        /// <returns>Result code from the Agora SDK or -1 if no screen share handler is attached.</returns>
        public int SetVideoEncoderConfiguration(JJVideoEncoderConfiguration config)
        {
            if (liveKitClient != null)
            {
                return liveKitClient.SetVideoEncoderConfiguration(config);
            }
            if (rtcEngine == null) return -1;   
            return rtcEngine.SetVideoEncoderConfiguration(config.ToAgora());
        }

        /// <summary>
        /// Sets the video encoder configuration for a specific connection.
        /// </summary>
        /// <param name="config">Video encoder configuration to apply.</param>
        /// <param name="connection">The target connection for the configuration.</param>
        /// <returns>Result code from the Agora SDK or -1 if no screen share handler is attached.</returns>
        public int SetVideoEncoderConfigurationEx(JJVideoEncoderConfiguration config, JJRtcConnection connection)
        {
            if (liveKitClient != null)
            {
                return liveKitClient.SetVideoEncoderConfigurationEx(
                    config,
                    connection);
            }
            if (rtcEngine == null) return -1;   
            return rtcEngine.SetVideoEncoderConfigurationEx(config.ToAgora(), connection.ToAgora());
        }

        /// <summary>
        /// Sets the remote video stream type (high/low) for a remote user on the engine.
        /// </summary>
        /// <param name="uid">Remote user id.</param>
        /// <param name="streamType">Requested <see cref="VIDEO_STREAM_TYPE"/>.</param>
        /// <returns>Result code from the Agora SDK or -1 if no screen share handler is attached.</returns>
        public int SetRemoteVideoStreamType(uint uid, JJVideoStreamType streamType)
        {
            if (liveKitClient != null)
            {
                return liveKitClient.SetRemoteVideoStreamType(uid, streamType);
            }
            if (rtcEngine == null) return -1;
            return rtcEngine.SetRemoteVideoStreamType(uid, (VIDEO_STREAM_TYPE)streamType);
        }

        /// <summary>
        /// Sets the camera capturer configuration via the attached video handler.
        /// </summary>
        /// <param name="option">Capturer configuration options.</param>
        /// <returns>Result code from the video handler or -1 if no video handler is attached.</returns>
        public int SetCameraCapturerConfiguration(JJCameraCapturerConfiguration option)
        {
            if (liveKitClient != null)
            {
                return liveKitClient.SetCameraCapturerConfiguration(option);
            }
            if (videoStreamingHandler == null) return -1;
            return videoStreamingHandler.SetCameraCapturerConfiguration(option);
        }

        /// <summary>
        /// Gets available microphone devices.
        /// </summary>
        public JJDeviceInfo[] GetRecordingDevices()
        {
            if (liveKitClient != null) return liveKitClient.GetRecordingDevices();
            return ConvertDeviceInfos(audioDeviceManager?.EnumerateRecordingDevices());
        }

        /// <summary>
        /// Gets available speaker/playback devices.
        /// </summary>
        public JJDeviceInfo[] GetPlaybackDevices()
        {
            if (liveKitClient != null) return liveKitClient.GetPlaybackDevices();
            return ConvertDeviceInfos(audioDeviceManager?.EnumeratePlaybackDevices());
        }

        /// <summary>
        /// Gets available camera devices.
        /// </summary>
        public JJDeviceInfo[] GetVideoDevices()
        {
            if (liveKitClient != null) return liveKitClient.GetVideoDevices();
            return ConvertDeviceInfos(videoDeviceManager?.EnumerateVideoDevices());
        }

        /// <summary>
        /// Selects the active microphone device.
        /// </summary>
        public int SetRecordingDevice(string deviceId)
        {
            if (liveKitClient != null) return liveKitClient.SetRecordingDevice(deviceId);
            if (audioDeviceManager == null) return -1;
            return audioDeviceManager.SetRecordingDevice(deviceId);
        }

        /// <summary>
        /// Selects the active speaker/playback device.
        /// </summary>
        public int SetPlaybackDevice(string deviceId)
        {
            if (liveKitClient != null) return liveKitClient.SetPlaybackDevice(deviceId);
            if (audioDeviceManager == null) return -1;
            return audioDeviceManager.SetPlaybackDevice(deviceId);
        }

        /// <summary>
        /// Selects the active camera device.
        /// </summary>
        public int SetVideoDevice(string deviceId)
        {
            if (liveKitClient != null) return liveKitClient.SetVideoDevice(deviceId);
            if (videoDeviceManager == null) return -1;
            return videoDeviceManager.SetDevice(deviceId);
        }

        /// <summary>
        /// Retrieves available screen capture sources (screens/windows) using the screen share handler.
        /// </summary>
        /// <param name="thumbSize">Thumbnail size requested.</param>
        /// <param name="iconSize">Icon size requested.</param>
        /// <param name="includeScreen">Whether to include entire screen sources.</param>
        /// <returns>Array of <see cref="ScreenCaptureSourceInfo"/> or null if no handler attached.</returns>
        public JJScreenCaptureSourceInfo[] GetScreenCaptureSources(Vector2Int thumbSize, Vector2Int iconSize, bool includeScreen)
        {   
            if(screenShareHandler == null) return null;
            return screenShareHandler.GetScreenCaptureSources(thumbSize, iconSize, includeScreen);
        }

        /// <summary>
        /// Sets the audio scenario using the attached voice handler.
        /// Audio scenarios change device audio processing behaviour.
        /// </summary>
        /// <param name="scenarioType">The <see cref="AUDIO_SCENARIO_TYPE"/> to set.</param>
        /// <returns>Result code from the voice handler or -1 if no voice handler is attached.</returns>
        public int SetAudioScenario(int scenarioType)
        {
            if (liveKitClient != null)
            {
                return liveKitClient.SetAudioScenario(scenarioType);
            }
            if (voiceStreamingHandler == null) return -1;
            return voiceStreamingHandler.SetAudioScenario(scenarioType);
        }

        private static JJDeviceInfo[] ConvertDeviceInfos(DeviceInfo[] deviceInfos)
        {
            if (deviceInfos == null) return new JJDeviceInfo[0];

            var result = new JJDeviceInfo[deviceInfos.Length];
            for (int i = 0; i < deviceInfos.Length; i++)
            {
                result[i] = new JJDeviceInfo
                {
                    deviceId = deviceInfos[i].deviceId,
                    deviceName = deviceInfos[i].deviceName,
                    deviceTypeName = deviceInfos[i].deviceTypeName
                };
            }

            return result;
        }
    }
}
