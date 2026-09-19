using System;
using System.Diagnostics;
using UnityEngine;

namespace Jorjin.Streaming
{
    /// <summary>
    /// Observes one LiveKit AudioSource on Unity's audio thread and emits a copied,
    /// per-participant PCM16 frame in the format requested by the public SDK.
    /// </summary>
    internal sealed class LiveKitRemoteAudioFrameTap : MonoBehaviour
    {
        private static readonly double TimestampToMilliseconds =
            1000d / Stopwatch.Frequency;

        private string channelId;
        private uint uid;
        private int targetSampleRate;
        private int targetChannels;
        private int sourceSampleRate;
        private Action<JJRemoteAudioFrame> frameReceived;

        internal void Configure(
            string channelId,
            uint uid,
            JJRemoteAudioFrameConfig config,
            Action<JJRemoteAudioFrame> callback)
        {
            this.channelId = channelId ?? string.Empty;
            this.uid = uid;
            targetSampleRate = config.sampleRate;
            targetChannels = config.channels;
            sourceSampleRate = AudioSettings.outputSampleRate > 0
                ? AudioSettings.outputSampleRate
                : 48000;
            frameReceived = callback;
            enabled = callback != null;
        }

        internal void DisableTap()
        {
            frameReceived = null;
            enabled = false;
        }

        // Unity invokes this method on the audio thread. Do not access scene/UI APIs here.
        private void OnAudioFilterRead(float[] data, int inputChannels)
        {
            Action<JJRemoteAudioFrame> callback = frameReceived;
            if (callback == null || data == null || data.Length == 0 ||
                inputChannels <= 0 || targetSampleRate <= 0 ||
                (targetChannels != 1 && targetChannels != 2))
            {
                return;
            }

            int inputFrames = data.Length / inputChannels;
            if (inputFrames <= 0)
            {
                return;
            }

            int outputFrames = Math.Max(
                1,
                (int)Math.Round(
                    inputFrames * (double)targetSampleRate /
                    Math.Max(1, sourceSampleRate)));
            var pcm16 = new short[outputFrames * targetChannels];

            for (int outputFrame = 0; outputFrame < outputFrames; outputFrame++)
            {
                int inputFrame = Math.Min(
                    inputFrames - 1,
                    (int)((long)outputFrame * inputFrames / outputFrames));
                int inputOffset = inputFrame * inputChannels;

                if (targetChannels == 1)
                {
                    float mixed = 0f;
                    for (int channel = 0; channel < inputChannels; channel++)
                    {
                        mixed += data[inputOffset + channel];
                    }
                    pcm16[outputFrame] = FloatToPcm16(mixed / inputChannels);
                    continue;
                }

                float left = data[inputOffset];
                float right = inputChannels > 1
                    ? data[inputOffset + 1]
                    : left;
                int outputOffset = outputFrame * 2;
                pcm16[outputOffset] = FloatToPcm16(left);
                pcm16[outputOffset + 1] = FloatToPcm16(right);
            }

            callback(new JJRemoteAudioFrame
            {
                ChannelId = channelId,
                Uid = uid,
                SampleRate = targetSampleRate,
                Channels = targetChannels,
                SamplesPerChannel = outputFrames,
                BytesPerSample = sizeof(short),
                RenderTimeMs = (long)(Stopwatch.GetTimestamp() * TimestampToMilliseconds),
                Pcm16 = pcm16
            });
        }

        private static short FloatToPcm16(float value)
        {
            float clamped = Mathf.Clamp(value, -1f, 1f);
            return clamped <= -1f
                ? short.MinValue
                : (short)Mathf.RoundToInt(clamped * short.MaxValue);
        }
    }
}
