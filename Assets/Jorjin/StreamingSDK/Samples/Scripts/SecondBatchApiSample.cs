using System.Threading;
using Jorjin.Streaming;
using UnityEngine;

/// <summary>
/// Minimal button-callable example for the second-batch compatibility APIs.
/// Add this beside JorjinStreamingSDK after Initialize() has been called.
/// </summary>
public sealed class SecondBatchApiSample : MonoBehaviour
{
    [SerializeField] private JorjinStreamingSDK streamingSdk;
    [SerializeField] private int width = 1280;
    [SerializeField] private int height = 720;
    [SerializeField] private int frameRate = 30;
    [Tooltip("Kbps. Use -1 for the LiveKit default.")]
    [SerializeField] private int bitrateKbps = 1500;
    [SerializeField] private uint remoteUid;

    private int receivedPcmFrames;

    private void Awake()
    {
        if (streamingSdk == null)
        {
            streamingSdk = GetComponent<JorjinStreamingSDK>();
        }
    }

    private void OnDisable()
    {
        if (streamingSdk == null)
        {
            return;
        }
        streamingSdk.RemoteAudioFrameReceived -= HandleRemoteAudioFrame;
        streamingSdk.DisableRemoteAudioFrameObserver();
    }

    public void StartPreview() => streamingSdk?.StartPreview();

    public void StopPreview() => streamingSdk?.StopPreview();

    public void ApplyVideoProfile()
    {
        streamingSdk?.SetVideoEncoderConfiguration(
            new JJVideoEncoderConfiguration
            {
                width = width,
                height = height,
                frameRate = frameRate,
                bitrate = bitrateKbps,
                maintainResolution = true
            });
    }

    public void UseFrontCamera() => SelectCamera(JJCameraDirection.FRONT);

    public void UseRearCamera() => SelectCamera(JJCameraDirection.REAR);

    private void SelectCamera(JJCameraDirection direction)
    {
        var config = new JJCameraCapturerConfiguration();
        config.cameraDirection.SetValue(direction);
        streamingSdk?.SetCameraCapturerConfiguration(config);
    }

    public void StopReceivingRemoteAudio() =>
        streamingSdk?.MuteAllRemoteAudioStreams(true);

    public void ResumeReceivingRemoteAudio() =>
        streamingSdk?.MuteAllRemoteAudioStreams(false);

    public void StopReceivingRemoteVideo() =>
        streamingSdk?.MuteAllRemoteVideoStreams(true);

    public void ResumeReceivingRemoteVideo() =>
        streamingSdk?.MuteAllRemoteVideoStreams(false);

    public void UseRemoteLowStream() =>
        streamingSdk?.SetRemoteVideoStreamType(
            remoteUid,
            JJVideoStreamType.LOW);

    public void UseRemoteHighStream() =>
        streamingSdk?.SetRemoteVideoStreamType(
            remoteUid,
            JJVideoStreamType.HIGH);

    public void StartRemotePcmObserver()
    {
        if (streamingSdk == null)
        {
            return;
        }
        streamingSdk.RemoteAudioFrameReceived -= HandleRemoteAudioFrame;
        streamingSdk.RemoteAudioFrameReceived += HandleRemoteAudioFrame;
        streamingSdk.EnableRemoteAudioFrameObserver(
            new JJRemoteAudioFrameConfig
            {
                sampleRate = 16000,
                channels = 1
            });
    }

    public void StopRemotePcmObserver()
    {
        if (streamingSdk == null)
        {
            return;
        }
        streamingSdk.DisableRemoteAudioFrameObserver();
        streamingSdk.RemoteAudioFrameReceived -= HandleRemoteAudioFrame;
    }

    public int ConsumeReceivedPcmFrameCount() =>
        Interlocked.Exchange(ref receivedPcmFrames, 0);

    private void HandleRemoteAudioFrame(JJRemoteAudioFrame frame)
    {
        // This callback is not on Unity's main thread. Copy/queue data here;
        // update GameObjects or UI later from Update(). Pcm16 is already copied.
        Interlocked.Increment(ref receivedPcmFrames);
    }
}
