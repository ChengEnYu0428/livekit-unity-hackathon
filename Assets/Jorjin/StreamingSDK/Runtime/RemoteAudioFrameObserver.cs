using System;
using System.Runtime.InteropServices;
using Agora.Rtc;

namespace Jorjin.Streaming
{
    /// <summary>
    /// Copies Agora's native per-user PCM callback into SDK-owned managed data.
    /// LiveKit uses LiveKitRemoteAudioFrameTap instead.
    /// </summary>
    internal sealed class RemoteAudioFrameObserver : IAudioFrameObserver
    {
        private readonly Action<JJRemoteAudioFrame> frameReceived;
        private readonly Action<Exception> callbackFailed;

        internal RemoteAudioFrameObserver(
            Action<JJRemoteAudioFrame> frameReceived,
            Action<Exception> callbackFailed)
        {
            this.frameReceived = frameReceived;
            this.callbackFailed = callbackFailed;
        }

        public override bool OnPlaybackAudioFrameBeforeMixing(
            string channelId,
            uint uid,
            AudioFrame audioFrame)
        {
            try
            {
                if (audioFrame == null ||
                    audioFrame.buffer == IntPtr.Zero ||
                    audioFrame.samplesPerChannel <= 0 ||
                    audioFrame.channels <= 0 ||
                    (int)audioFrame.bytesPerSample != sizeof(short))
                {
                    return true;
                }

                long sampleCount =
                    (long)audioFrame.samplesPerChannel * audioFrame.channels;
                if (sampleCount > int.MaxValue)
                {
                    return true;
                }

                var pcm16 = new short[(int)sampleCount];
                Marshal.Copy(audioFrame.buffer, pcm16, 0, pcm16.Length);
                frameReceived?.Invoke(new JJRemoteAudioFrame
                {
                    ChannelId = channelId,
                    Uid = uid,
                    SampleRate = audioFrame.samplesPerSec,
                    Channels = audioFrame.channels,
                    SamplesPerChannel = audioFrame.samplesPerChannel,
                    BytesPerSample = sizeof(short),
                    RenderTimeMs = audioFrame.renderTimeMs,
                    Pcm16 = pcm16
                });
            }
            catch (Exception exception)
            {
                callbackFailed?.Invoke(exception);
            }

            return true;
        }
    }
}
