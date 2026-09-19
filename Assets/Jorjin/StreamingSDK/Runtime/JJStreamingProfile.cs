using System;
using Agora.Rtc;

namespace Jorjin.Streaming
{
    public enum JJStreamingBackend
    {
        Agora,
        LiveKit
    }

    [Serializable]
    public sealed class JJStreamingProfile
    {
        public JJStreamingBackend Backend { get; private set; }
        public string AppId { get; private set; }
        public string ChannelName { get; private set; }
        public uint UserId { get; private set; }
        public string Token { get; private set; }
        public uint ScreenShareId { get; private set; }
        public string ShareToken { get; private set; }
        public string LiveKitUrl { get; private set; }
        public string ParticipantIdentity { get; private set; }
        public StreamingEventHandler StreamEventHandler { get; set; }

        /// <summary>Creates an Agora profile and preserves the original public API.</summary>
        public JJStreamingProfile(
            string appId,
            string channelName,
            int userId,
            string token,
            int screenShareId,
            string shareToken)
        {
            Backend = JJStreamingBackend.Agora;
            AppId = appId ?? string.Empty;
            ChannelName = channelName ?? string.Empty;
            UserId = userId < 0 ? 0u : (uint)userId;
            Token = token ?? string.Empty;
            ScreenShareId = screenShareId < 0 ? 0u : (uint)screenShareId;
            ShareToken = shareToken ?? string.Empty;
            LiveKitUrl = string.Empty;
            ParticipantIdentity = UserId.ToString();
        }

        private JJStreamingProfile(
            string liveKitUrl,
            string roomName,
            string participantIdentity,
            string token,
            string appId)
        {
            Backend = JJStreamingBackend.LiveKit;
            LiveKitUrl = liveKitUrl ?? string.Empty;
            ChannelName = roomName ?? string.Empty;
            ParticipantIdentity = participantIdentity ?? string.Empty;
            Token = token ?? string.Empty;
            // Retained for compatibility with the original StreamingSDK UI/API.
            // LiveKit does not require or validate an Agora App ID.
            AppId = appId ?? string.Empty;
            ShareToken = string.Empty;
        }

        public static JJStreamingProfile CreateLiveKit(
            string liveKitUrl,
            string roomName,
            string participantIdentity,
            string token,
            string appId = "")
        {
            return new JJStreamingProfile(liveKitUrl, roomName, participantIdentity, token, appId);
        }

        /// <summary>
        /// Replaces the connection token used by the next LiveKit join/rejoin.
        /// Kept internal so applications continue to update tokens through
        /// JorjinStreamingSDK.RenewToken().
        /// </summary>
        internal void UpdateToken(string token)
        {
            Token = token ?? string.Empty;
        }

        internal IRtcEngineEx InitSDK()
        {
            if (Backend != JJStreamingBackend.Agora)
            {
                return null;
            }

            IRtcEngineEx engine = RtcEngine.CreateAgoraRtcEngineEx();
            var context = new RtcEngineContext
            {
                appId = AppId,
                channelProfile = CHANNEL_PROFILE_TYPE.CHANNEL_PROFILE_LIVE_BROADCASTING,
                audioScenario = AUDIO_SCENARIO_TYPE.AUDIO_SCENARIO_DEFAULT
            };

            int result = engine.Initialize(context);
            if (result != 0)
            {
                engine.Dispose();
                return null;
            }

            if (StreamEventHandler != null)
            {
                engine.InitEventHandler(StreamEventHandler);
            }

            return engine;
        }
    }
}
