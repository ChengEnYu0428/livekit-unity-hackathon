using Agora.Rtc;
using UnityEngine;

namespace Jorjin.Streaming
{
    public class VoiceStreamingHandler
    {
        private IRtcEngineEx rtcEngine;

        /// <summary>
        /// Set the rtcEngine instance to be used by this handler
        /// </summary>
        /// <param name="engine"> rtcEngine instance to be used</param>
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
        /// Change audio input state of the client
        /// </summary>
        /// <param name="status"> new audio state </param>
        public virtual void EnableLocalAudio(bool status)
        {
            if (!HasEngine()) return;
            rtcEngine.EnableLocalAudio(status);
        }

        /// <summary>
        /// Stops or resumes publishing the local audio stream.
        /// </summary>
        /// <param name="status">Whether to stop publishing the local audio stream: true : Stops publishing the local audio stream.</param>
        public virtual void MuteLocalAudioStream(bool status)
        {
            if (!HasEngine()) return;
            rtcEngine.MuteLocalAudioStream(status);
        }

        /// <summary>
        /// Enables the audio module.
        /// </summary>
        public virtual void EnableAudio()
        {
            if (!HasEngine()) return;
            rtcEngine.EnableAudio();
        }

        /// <summary>
        /// Disables the audio module.
        /// </summary>
        public virtual void DisableAudio()
        {
            if (!HasEngine()) return;
            rtcEngine.DisableAudio();
        }

        /// <summary>
        /// Sets audio scenarios.
        /// </summary>
        /// <param name="scenarioType">The audio scenarios. Under different audio scenarios, the device uses different volume types. See AUDIO_SCENARIO_TYPE.</param>
        /// <returns></returns>
        public int SetAudioScenario(int scenarioType)
        {
            if (!HasEngine()) return -1;
            return rtcEngine.SetAudioScenario((AUDIO_SCENARIO_TYPE)scenarioType);
        }
        #endregion

        #region Public Event

        /// <summary>
        /// callback received when an user mute or unmute their audio 
        /// </summary>
        /// <param name="remoteUid">uid of the remote user</param>
        /// <param name="muted">mute status of the remote user</param>
        public virtual void OnUserMuteAudio(uint remoteUid, bool muted)
        {
            if (!HasEngine()) return;
            Debug.Log(remoteUid + " OnUserMuteAudio " + muted);
        }

        /// <summary>
        /// callback received when a remote user change their audio status
        /// </summary>
        /// <param name="connection"> uid of the remote user</param>
        /// <param name="stats"> audio status of the remote user</param>
        public virtual void OnRemoteAudioStats(JJRtcConnection connection, JJRemoteAudioStats stats)
        {
            if (!HasEngine()) return;
            Debug.Log(connection.LocalUid + " OnRemoteAudioStats " + " Stats :" + stats);

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="stats"></param>
        public virtual void OnLocalAudioStats(JJRtcConnection connection, JJLocalAudioStats stats)
        {
            if (!HasEngine()) return;
            Debug.Log(connection.LocalUid + " OnLocalAudioStats " + " Stats :" + stats);
        }
        #endregion
    }
}
