using Agora.Rtc;
using UnityEngine;
namespace Jorjin.Streaming
{
    public class ScreenShareHandler
    {
        private IRtcEngineEx rtcEngine;

        /// <summary>
        /// Set the rtcEngine instance to be used by this handler
        /// </summary>
        /// <param name="engine"> rtcEngine instance to be used </param>
        public void SetEngine(IRtcEngineEx engine)
        {
            rtcEngine = engine;
        }

        private bool HasEngine()
        {
            if (rtcEngine != null)
            {
                return true;
            }

            Debug.LogError("JJ Streaming SDK was not initialized yet");
            return false;
        }

        #region public API

        /// <summary>
        /// Gets a list of shareable screens and windows.
        /// </summary>
        ///
        /// <param name="thumbSize"> The target size of the screen or window thumbnail (the width and height are in pixels). The SDK scales the original image to make the length of the longest side of the image the same as that of the target size without distorting the original image. For example, if the original image is 400 x 300 and thumbSize is 100 x 100, the actual size of the thumbnail is 100 x 75. If the target size is larger than the original size, the thumbnail is the original image and the SDK does not scale it. </param>
        ///
        /// <param name="iconSize"> The target size of the icon corresponding to the application program (the width and height are in pixels). The SDK scales the original image to make the length of the longest side of the image the same as that of the target size without distorting the original image. For example, if the original image is 400 x 300 and iconSize is 100 x 100, the actual size of the icon is 100 x 75. If the target size is larger than the original size, the icon is the original image and the SDK does not scale it. </param>
        ///
        /// <param name="includeScreen"> Whether the SDK returns the screen information in addition to the window information: true : The SDK returns screen and window information. false : The SDK returns window information only. </param>
        ///
        /// <returns></returns>
        public JJScreenCaptureSourceInfo[] GetScreenCaptureSources(Vector2Int thumbSize, Vector2Int iconSize, bool includeScreen)
        {
            if (!HasEngine()) return null;

            var sources = rtcEngine.GetScreenCaptureSources(thumbSize.ToAgora(), iconSize.ToAgora(), includeScreen);
            if (sources == null) return null;

            var result = new JJScreenCaptureSourceInfo[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                result[i] = sources[i].ToJorjin();
            }

            return result;
        }

        /// <summary>
        /// Starts a screen-share session
        /// </summary>
        /// <param name="token">Token to use for the join request.</param>
        /// <param name="connection">The <see cref="RtcConnection"/> describing the channel/uid to join.</param>
        /// <param name="options">Channel media options to apply for the join.</param>
        /// <returns></returns>
        public virtual int StartScreenCapture(string token, JJRtcConnection connection, JJChannelMediaOptions options)
        {
            if (!HasEngine()) return -1;
            int ret = 0;
            options.clientRoleType.SetValue(JJClientRoleType.BROADCASTER);
            ret = rtcEngine.JoinChannelEx(token, connection.ToAgora(), options.ToAgora());
            return ret;
        }

        /// <summary>
        /// Updates the active screen capture configuration.
        /// </summary>
        /// <param name="screenCaptureParameters2">The new screen capture parameters to apply.</param>
        public void UpdateScreenCapture(JJScreenCaptureParameters2 screenCaptureParameters2)
        {
            if (!HasEngine()) return;
            rtcEngine.UpdateScreenCapture(screenCaptureParameters2.ToAgora());
        }

        /// <summary>
        /// Updates the channel media options after joining the channel.
        /// </summary>
        ///
        /// <param name="rtcConnection"> The connection information. See RtcConnection. </param>
        /// <param name="options"> The channel media options. See ChannelMediaOptions. </param>
        public void UpdateChannelMediaOptionsEx(JJChannelMediaOptions options, JJRtcConnection rtcConnection)
        {
            if (!HasEngine()) return;
            rtcEngine.UpdateChannelMediaOptionsEx(options.ToAgora(), rtcConnection.ToAgora());
        }

        /// <summary>
        /// Captures the screen by specifying the display ID.
        /// </summary>
        /// <param name="displayId"> The display ID of the screen to be shared. For the Windows platform, if you need to simultaneously share two screens (main screen and secondary screen), you can set displayId to -1 when calling this method. </param>
        /// <param name="regionRect"> (Optional) Sets the relative location of the region to the screen. Pass in nil to share the entire screen. See Rectangle. </param>
        /// <param name="captureParams"> Screen sharing configurations. The default video dimension is 1920 x 1080, that is, 2,073,600 pixels. Agora uses the value of this parameter to calculate the charges. See ScreenCaptureParameters. The video properties of the screen sharing stream only need to be set through this parameter, and are unrelated to SetVideoEncoderConfiguration. </param>
        /// <returns></returns>
        public int StartScreenCaptureByDisplayId(uint displayId, RectInt regionRect, JJScreenCaptureParameters captureParams)
        {
            if (!HasEngine()) return -1;
            return rtcEngine.StartScreenCaptureByDisplayId(displayId, regionRect.ToAgora(), captureParams.ToAgora());
        }

        /// <summary>
        /// Captures the screen by specifying the window ID.
        /// </summary>
        /// <param name="windowId"> The ID of the window to be shared. </param>
        /// <param name="regionRect"> (Optional) Sets the relative location of the region to the screen. If you do not set this parameter, the SDK shares the whole screen. See Rectangle. If the specified region overruns the window, the SDK shares only the region within it; if you set width or height as 0, the SDK shares the whole window. </param>
        /// <param name="captureParams"> Screen sharing configurations. The default video resolution is 1920 x 1080, that is, 2,073,600 pixels. Agora uses the value of this parameter to calculate the charges. See ScreenCaptureParameters. </param>
        /// <returns></returns>
        public int StartScreenCaptureByWindowId(long windowId, RectInt regionRect, JJScreenCaptureParameters captureParams)
        {
            if (!HasEngine()) return -1;
            return rtcEngine.StartScreenCaptureByWindowId(windowId, regionRect.ToAgora(), captureParams.ToAgora());
        }

        /// <summary>
        /// Pushes an external video frame to the engine for a particular video track.
        /// </summary>
        /// <param name="frame">The external video frame to push.</param>
        /// <param name="videoTrackId">Optional custom video track id to push the frame to (default 0).</param>
        public void PushVideoFrame(JJExternalVideoFrame frame, uint videoTrackId = 0)
        {
            if (!HasEngine()) return;
            rtcEngine.PushVideoFrame(frame.ToAgora(), videoTrackId);
        }

        /// <summary>
        /// Creates a custom video track.
        /// </summary>
        public void CreateCustomVideoTrack()
        {
            if (!HasEngine()) return;
            rtcEngine.CreateCustomVideoTrack();
        }

        /// <summary>
        /// Sets the video encoder configuration.
        /// 
        /// Sets the encoder configuration for the local video. Each configuration profile corresponds to a set of video parameters, including the resolution, frame rate, and bitrate.
        /// </summary>
        /// <param name="config">Video profile. See VideoEncoderConfiguration.</param>
        /// <returns></returns>
        public int SetVideoEncoderConfiguration(JJVideoEncoderConfiguration config)
        {
            if (!HasEngine()) return -1;
            return rtcEngine.SetVideoEncoderConfiguration(config.ToAgora());
        }

        /// <summary>
        /// Sets the video stream type to subscribe to.
        /// </summary>
        /// <param name="uid"> The user ID. </param>
        /// <param name="streamType"> The video stream type, see VIDEO_STREAM_TYPE. </param>
        /// <returns></returns>
        public int SetRemoteVideoStreamType(uint uid, JJVideoStreamType streamType)
        {
            if (!HasEngine()) return -1;
            return rtcEngine.SetRemoteVideoStreamType(uid, (VIDEO_STREAM_TYPE)streamType);
        }

        #endregion

        #region public Event

        /// <summary>
        /// callback received when a new uid joined the channel. Usually, the client spawn a new videosurface here to 
        /// display the video that new uid. However, client needs to determent if the new uid was a new user or a screen share session.
        /// </summary>
        /// <param name="connection"> connection information</param>
        /// <param name="uid"> </param>
        /// <param name="elapsed"></param>
        public virtual void OnUserJoined(JJRtcConnection connection, uint uid, int elapsed)
        {
            if (!HasEngine()) return;
            Debug.Log("OnUserJoined " + uid);
        }

        /// <summary>
        /// callback received when an uid leave the channel. Should handle disable/delete a sharescreen here.
        /// </summary>
        /// <param name="connection"> connection information</param>
        /// <param name="uid"> </param>
        /// <param name="reason"></param>
        public virtual void OnUserOffline(JJRtcConnection connection, uint uid, JJUserOfflineReason reason)
        {
            if (!HasEngine()) return;
            Debug.Log("OnUserOffline " + uid);
        }
        #endregion
    }
}
