using Agora.Rtc;
using LiveKit;
using UnityEngine;

namespace Jorjin.Streaming
{
    /// <summary>
    /// Receives callbacks from the Agora RTC SDK and forwards them to specialized handlers.
    /// This class implements the <see cref="IRtcEngineEventHandler"/> callbacks used by the Agora engine.
    /// It acts as a facade that delegates video, voice and screen-share related events to the
    /// configured handler instances.
    /// </summary>
    /// <remarks>
    /// Callbacks from the Agora SDK may be invoked on threads not owned by Unity's main thread.
    /// Handlers receiving these callbacks should take care of thread-affinity when interacting
    /// with Unity objects (for example by dispatching to the main thread).
    /// </remarks>
    public class StreamingEventHandler : IRtcEngineEventHandler
    {   
        /// <summary>
        /// Handler responsible for voice/audio related operations and callbacks.
        /// May be null until set via <see cref="SetVoiceHandler"/>.
        /// </summary>
        protected VoiceStreamingHandler voiceStreamingHandler;

        /// <summary>
        /// Handler responsible for camera/local video related operations and callbacks.
        /// May be null until set via <see cref="SetVideoHandler"/>.
        /// </summary>
        protected VideoStreamingHandler videoStreamingHandler;

        /// <summary>
        /// Handler responsible for screen sharing related operations and callbacks.
        /// May be null until set via <see cref="SetScreenShareHandler"/>.
        /// </summary>
        protected ScreenShareHandler screenShareHandler;

        /// <summary>
        /// Profile containing configuration used to initialize and manage the streaming session.
        /// Provided via the constructor and kept for potential use by handlers.
        /// </summary>
        protected JJStreamingProfile jorjinStreamingProfile;

        /// <summary>
        /// Creates a new <see cref="StreamingEventHandler"/>.
        /// </summary>
        public StreamingEventHandler()
        {
        }

        /// <summary>
        /// Creates a new <see cref="StreamingEventHandler"/> bound to the provided streaming profile.
        /// </summary>
        /// <param name="profile">The <see cref="JJStreamingProfile"/> used to configure streaming behaviour.</param>
        public StreamingEventHandler(JJStreamingProfile profile)
        {
            SetProfile(profile);
        }

        /// <summary>
        /// Assigns the streaming profile used by this event handler.
        /// </summary>
        /// <param name="profile">The <see cref="JJStreamingProfile"/> used to configure streaming behaviour.</param>
        public void SetProfile(JJStreamingProfile profile)
        {
            jorjinStreamingProfile = profile;
        }

        /// <summary>
        /// Assigns the voice streaming handler used to process audio events.
        /// </summary>
        /// <param name="voiceHandler">An instance of <see cref="VoiceStreamingHandler"/> to forward audio callbacks to.</param>
        public void SetVoiceHandler(VoiceStreamingHandler voiceHandler)
        {
            voiceStreamingHandler = voiceHandler;
        }

        /// <summary>
        /// Assigns the video streaming handler used to process camera/local video events.
        /// </summary>
        /// <param name="videoHandler">An instance of <see cref="VideoStreamingHandler"/> to forward video callbacks to.</param>
        public void SetVideoHandler(VideoStreamingHandler videoHandler)
        {
            videoStreamingHandler = videoHandler;
        }

        /// <summary>
        /// Assigns the screen-share handler used to process screen sharing events.
        /// </summary>
        /// <param name="screenSharer">An instance of <see cref="ScreenShareHandler"/> to forward screen share callbacks to.</param>
        public void SetScreenShareHandler(ScreenShareHandler screenSharer)
        {
            screenShareHandler = screenSharer;
        }

        /// <summary>
        /// Called when an SDK error occurs.
        /// </summary>
        /// <param name="err">Numeric error code returned by the SDK.</param>
        /// <param name="msg">Human-readable message describing the error.</param>
        public override void OnError(int err, string msg)
        {
            Debug.Log($"err : {err} msg : {msg}");
        }

        /// <summary>
        /// Called when the local video state changes (for example when the camera starts or stops).
        /// Forwards the event to the configured <see cref="VideoStreamingHandler"/>.
        /// </summary>
        /// <param name="source">Type of the video source that changed (camera, screen, etc.).</param>
        /// <param name="state">New state of the local video stream.</param>
        /// <param name="reason">Reason for the state change.</param>
        public override void OnLocalVideoStateChanged(VIDEO_SOURCE_TYPE source, LOCAL_VIDEO_STREAM_STATE state, LOCAL_VIDEO_STREAM_REASON reason)
        {
            OnLocalVideoStateChanged((JJVideoSourceType)source, (JJLocalVideoStreamState)state, (JJLocalVideoStreamReason)reason);
        }

        public virtual void OnLocalVideoStateChanged(JJVideoSourceType source, JJLocalVideoStreamState state, JJLocalVideoStreamReason reason)
        {
            videoStreamingHandler?.OnLocalVideoStateChanged(source, state, reason);
        }

        /// <summary>
        /// Called when the local user successfully joins a channel.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="elapsed">Time elapsed (ms) since the join request.</param>
        public override void OnJoinChannelSuccess(RtcConnection connection, int elapsed)
        {
            OnJoinChannelSuccess(connection.ToJorjin(), elapsed);
        }

        public virtual void OnJoinChannelSuccess(JJRtcConnection connection, int elapsed)
        {
            Debug.Log("OnJoinChannelSuccess " + connection.LocalUid);
        }

        /// <summary>
        /// Called when the local user successfully rejoins a channel after a network interruption.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="elapsed">Time elapsed (ms) since the rejoin request.</param>
        public override void OnRejoinChannelSuccess(RtcConnection connection, int elapsed)
        {
            OnRejoinChannelSuccess(connection.ToJorjin(), elapsed);
        }

        public virtual void OnRejoinChannelSuccess(JJRtcConnection connection, int elapsed)
        {
            Debug.Log("OnRejoinChannelSuccess " + connection.LocalUid);
        }

        /// <summary>
        /// Called when the local user leaves a channel.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="stats">Channel statistics reported on leave.</param>
        public override void OnLeaveChannel(RtcConnection connection, RtcStats stats)
        {
            OnLeaveChannel(connection.ToJorjin(), new JJRtcStats());
        }

        public virtual void OnLeaveChannel(JJRtcConnection connection, JJRtcStats stats)
        {
            Debug.Log("OnLeaveChannel " + connection.LocalUid);
        }

        /// <summary>
        /// Called when the connection state changes.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="state">The new connection state.</param>
        /// <param name="reason">Reason for the connection state change.</param>
        public override void OnConnectionStateChanged(RtcConnection connection, CONNECTION_STATE_TYPE state, CONNECTION_CHANGED_REASON_TYPE reason)
        {
            OnConnectionStateChanged(connection.ToJorjin(), (JJConnectionState)state, (JJConnectionChangedReason)reason);
        }

        public virtual void OnConnectionStateChanged(JJRtcConnection connection, JJConnectionState state, JJConnectionChangedReason reason)
        {
            Debug.Log("OnConnectionStateChanged " + connection.LocalUid + "State: " + state + "Reason: " + reason);
        }

        /// <summary>
        /// Called when the SDK detects the connection was lost.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid) that lost connectivity.</param>
        public override void OnConnectionLost(RtcConnection connection)
        {
            OnConnectionLost(connection.ToJorjin());
        }

        public virtual void OnConnectionLost(JJRtcConnection connection)
        {
            Debug.Log(connection.LocalUid + " OnConnectionLost");
        }

        /// <summary>
        /// Called when a remote user (or screen share stream) joins the channel.
        /// Forwards the event to both video and screen-share handlers so each can determine
        /// whether the joined uid represents a camera or a screen-share.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="uid">The uid of the remote user that joined.</param>
        /// <param name="elapsed">Time elapsed (ms) since the join request.</param>
        public override void OnUserJoined(RtcConnection connection, uint uid, int elapsed)
        {
            OnUserJoined(connection.ToJorjin(), uid, elapsed);
        }

        public virtual void OnUserJoined(JJRtcConnection connection, uint uid, int elapsed)
        {
            videoStreamingHandler?.OnUserJoined(connection, uid, elapsed);
            screenShareHandler?.OnUserJoined(connection, uid, elapsed);
        }

        /// <summary>
        /// Called when a remote user leaves the channel or goes offline.
        /// Forwards the event to both video and screen-share handlers so they can cleanup UI/state.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="uid">The uid of the remote user that went offline.</param>
        /// <param name="reason">Reason why the user went offline.</param>
        public override void OnUserOffline(RtcConnection connection, uint uid, USER_OFFLINE_REASON_TYPE reason)
        {
            OnUserOffline(connection.ToJorjin(), uid, (JJUserOfflineReason)reason);
        }

        public virtual void OnUserOffline(JJRtcConnection connection, uint uid, JJUserOfflineReason reason)
        {
            videoStreamingHandler?.OnUserOffline(connection, uid, reason);
            screenShareHandler?.OnUserOffline(connection, uid, reason);
        }

        /// <summary>
        /// Called when a remote user mutes or unmutes their video.
        /// Forwards the event to the video handler.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="remoteUid">The uid of the remote user whose video mute state changed.</param>
        /// <param name="muted">Whether the remote user's video is now muted.</param>
        public override void OnUserMuteVideo(RtcConnection connection, uint remoteUid, bool muted)
        {
            OnUserMuteVideo(connection.ToJorjin(), remoteUid, muted);
        }

        public virtual void OnUserMuteVideo(JJRtcConnection connection, uint remoteUid, bool muted)
        {
            videoStreamingHandler?.OnUserMuteVideo(connection, remoteUid, muted);
        }

        /// <summary>
        /// Called when a remote user mutes or unmutes their audio.
        /// Forwards the event to the voice handler.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="remoteUid">The uid of the remote user whose audio mute state changed.</param>
        /// <param name="muted">Whether the remote user's audio is now muted.</param>
        public override void OnUserMuteAudio(RtcConnection connection, uint remoteUid, bool muted)
        {
            OnUserMuteAudio(connection.ToJorjin(), remoteUid, muted);
        }

        public virtual void OnUserMuteAudio(JJRtcConnection connection, uint remoteUid, bool muted)
        {
            voiceStreamingHandler?.OnUserMuteAudio(remoteUid, muted);
        }

        /// <summary>
        /// Called when the remote video state changes for a remote user.
        /// This implementation logs the new state; handlers may react elsewhere.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="remoteUid">The uid of the remote user whose video state changed.</param>
        /// <param name="state">The new remote video state.</param>
        /// <param name="reason">Reason for the state change.</param>
        /// <param name="elapsed">Time elapsed (ms) related to the state change.</param>
        public override void OnRemoteVideoStateChanged(RtcConnection connection, uint remoteUid, REMOTE_VIDEO_STATE state, REMOTE_VIDEO_STATE_REASON reason, int elapsed)
        {
            OnRemoteVideoStateChanged(connection.ToJorjin(), remoteUid, (JJRemoteVideoState)state, (JJRemoteVideoStateReason)reason, elapsed);
        }

        public virtual void OnRemoteVideoStateChanged(JJRtcConnection connection, uint remoteUid, JJRemoteVideoState state, JJRemoteVideoStateReason reason, int elapsed)
        {
            Debug.Log(remoteUid + " OnRemoteVideoStateChanged " + " State :" + state + " Reason :" + reason);
        }

        /// <summary>
        /// Called periodically to report remote audio statistics.
        /// Forwards the stats to the voice handler for processing.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="stats">Remote audio statistics reported by the SDK.</param>
        public override void OnRemoteAudioStats(RtcConnection connection, RemoteAudioStats stats)
        {
            OnRemoteAudioStats(connection.ToJorjin(), stats.ToJorjin());
        }

        public virtual void OnRemoteAudioStats(JJRtcConnection connection, JJRemoteAudioStats stats)
        {
            voiceStreamingHandler?.OnRemoteAudioStats(connection, stats);
        }

        /// <summary>
        /// Called periodically to report local audio statistics.
        /// Forwards the stats to the voice handler for processing.
        /// </summary>
        /// <param name="connection">Information about the RTC connection (channel and uid).</param>
        /// <param name="stats">Local audio statistics reported by the SDK.</param>
        public override void OnLocalAudioStats(RtcConnection connection, LocalAudioStats stats)
        {
            OnLocalAudioStats(connection.ToJorjin(), stats.ToJorjin());
        }

        public virtual void OnLocalAudioStats(JJRtcConnection connection, JJLocalAudioStats stats)
        {
            voiceStreamingHandler?.OnLocalAudioStats(connection, stats);
        }

        public override void OnRequestToken(RtcConnection connection)
        {
            OnRequestToken(connection.ToJorjin());
        }

        public virtual void OnRequestToken(JJRtcConnection connection)
        {
        }

        public override void OnTokenPrivilegeWillExpire(RtcConnection connection, string token)
        {
            OnTokenPrivilegeWillExpire(connection.ToJorjin(), token);
        }

        public virtual void OnTokenPrivilegeWillExpire(JJRtcConnection connection, string token)
        {
        }

        public virtual void OnLiveKitConnected(string roomName, string localIdentity)
        {
            Debug.Log($"LiveKit connected: room={roomName}, identity={localIdentity}");
        }

        public virtual void OnLiveKitDisconnected(string roomName)
        {
            Debug.Log($"LiveKit disconnected: room={roomName}");
        }

        public virtual void OnLiveKitParticipantConnected(string identity)
        {
            Debug.Log($"LiveKit participant connected: {identity}");
        }

        public virtual void OnLiveKitParticipantDisconnected(string identity)
        {
            Debug.Log($"LiveKit participant disconnected: {identity}");
        }

        public virtual void OnLiveKitVideoTrackSubscribed(
            RemoteVideoTrack track,
            string participantIdentity,
            LiveKit.Proto.TrackSource source)
        {
        }

        public virtual void OnLiveKitVideoTrackUnsubscribed(
            RemoteVideoTrack track,
            string participantIdentity,
            LiveKit.Proto.TrackSource source)
        {
        }

        public virtual void OnLiveKitLocalVideoTexture(Texture texture)
        {
        }

        public virtual void OnLiveKitScreenShareChanged(bool sharing)
        {
        }

        /// <summary>
        /// Called when a local or remote LiveKit participant sends a meeting reaction.
        /// </summary>
        public virtual void OnLiveKitReactionReceived(
            string participantIdentity,
            JJReactionType reaction)
        {
        }

        /// <summary>A remote participant announced their role: "field" (場域端) or "expert" (專家端).</summary>
        public virtual void OnLiveKitParticipantRole(string participantIdentity, string role)
        {
        }

        /// <summary>Reliable AI collaboration packet from an internal Agent.</summary>
        public virtual void OnLiveKitCollaborationPacket(string participantIdentity, string json)
        {
        }

        public virtual void OnLiveKitTranscriptAgentStatus(
            string eventType,
            string state,
            string sessionId,
            string message)
        {
        }

        public virtual void OnLiveKitTranscriptSegment(
            string sessionId,
            string timestamp,
            string participantIdentity,
            string participantName,
            string text,
            int sequence)
        {
        }

        public virtual void OnLiveKitTranscriptCompleted(
            string sessionId,
            int segmentCount,
            string message)
        {
        }

        public virtual void OnLiveKitTranscriptRecordingStarted(
            string sessionId,
            string fileName,
            long totalBytes,
            int chunkCount,
            float durationSeconds,
            bool truncated)
        {
        }

        public virtual void OnLiveKitTranscriptRecordingChunk(
            string sessionId,
            int chunkIndex,
            string base64Data)
        {
        }

        public virtual void OnLiveKitTranscriptRecordingCompleted(
            string sessionId,
            string fileName,
            long totalBytes,
            int chunkCount,
            string sha256,
            float durationSeconds,
            bool truncated)
        {
        }

        public virtual void OnLiveKitTranscriptRecordingError(
            string sessionId,
            string message)
        {
        }
    }
}
