using Agora.Rtc;
using UnityEngine;
namespace Jorjin.Streaming
{
    public class VideoStreamingHandler
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

        #region Public API

        /// <summary>
        /// Enables the video module.
        /// </summary>
        public void EnableVideo()
        {
            if (!HasEngine()) return;
            rtcEngine.EnableVideo();
        }

        /// <summary>
        /// Disables the video module.
        /// </summary>
        public void DisableVideo()
        {
            if (!HasEngine()) return;
            rtcEngine.DisableVideo();
        }

        /// <summary>
        /// Enables/Disables the local video capture.
        /// </summary>
        /// <param name="value">Whether to enable the local video capture. true : (Default) Enable the local video capture. false : Disable the local video capture. Once the local video is disabled, the remote users cannot receive the video stream of the local user, while the local user can still receive the video streams of remote users. When set to false, this method does not require a local camera.</param>
        public void EnableLocalVideo(bool value)
        {
            if (!HasEngine()) return;
            rtcEngine.EnableLocalVideo(value);
        }

        /// <summary>
        /// Sets the camera capturer configuration.
        /// </summary>
        /// <param name="option"> The camera capture configuration. See CameraCapturerConfiguration. In this method, you do not need to set the deviceId parameter. </param>
        /// <returns></returns>
        public int SetCameraCapturerConfiguration(JJCameraCapturerConfiguration option)
        {
            if (!HasEngine()) return -1;
            return rtcEngine.SetCameraCapturerConfiguration(option.ToAgora());
        }
        #endregion

        #region Event
        /// <summary>
        /// callback received when a video of client changed it state
        /// </summary>
        /// <param name="source"> source type of the video</param>
        /// <param name="state"> new state of the video</param>
        /// <param name="reason"> video state change reason</param>
        public virtual void OnLocalVideoStateChanged(JJVideoSourceType source, JJLocalVideoStreamState state, JJLocalVideoStreamReason reason)
        {
            if (!HasEngine()) return;
            Debug.Log("OnLocalVideoStateChanged " + state);
        }

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
        /// callback received when an uid leave the channel. Should handle when an user leave the channel here.
        /// </summary>
        /// <param name="connection"> connection information</param>
        /// <param name="uid"> </param>
        /// <param name="reason"></param>
        public virtual void OnUserOffline(JJRtcConnection connection, uint uid, JJUserOfflineReason reason)
        {
            Debug.Log(uid + " OnUserOffline ");
        }

        /// <summary>
        /// callback received when a remote user mute their video.
        /// </summary>
        /// <param name="connection"> connection information</param>
        /// <param name="remoteUid"> </param>
        /// <param name="muted"></param>
        public virtual void OnUserMuteVideo(JJRtcConnection connection, uint remoteUid, bool muted)
        {
            Debug.Log(remoteUid + " OnUserMuteVideo " + muted);

        }
        #endregion
    }
}
