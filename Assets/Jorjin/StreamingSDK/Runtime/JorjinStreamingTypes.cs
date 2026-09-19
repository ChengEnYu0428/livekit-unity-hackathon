using Agora.Rtc;
using System;
using UnityEngine;

namespace Jorjin.Streaming
{
    public enum JJClientRoleType
    {
        BROADCASTER = 1,
        AUDIENCE = 2
    }

    public enum JJVideoStreamType
    {
        HIGH = 0,
        LOW = 1
    }

    public enum JJVideoSourceType
    {
        CAMERA = 0,
        SCREEN = 2
    }

    public enum JJLocalVideoStreamState
    {
        STOPPED = 0,
        CAPTURING = 1,
        ENCODING = 2,
        FAILED = 3
    }

    public enum JJLocalVideoStreamReason
    {
        OK = 0,
        FAILURE = 1,
        DEVICE_NO_PERMISSION = 2,
        DEVICE_BUSY = 3,
        CAPTURE_FAILURE = 4,
        ENCODE_FAILURE = 5,
        SCREEN_CAPTURE_WINDOW_HIDDEN = 25
    }

    public enum JJUserOfflineReason
    {
        QUIT = 0,
        DROPPED = 1,
        BECOME_AUDIENCE = 2
    }

    public enum JJRemoteVideoState
    {
        STOPPED = 0,
        STARTING = 1,
        DECODING = 2,
        FROZEN = 3,
        FAILED = 4
    }

    public enum JJRemoteVideoStateReason
    {
        INTERNAL = 0,
        NETWORK_CONGESTION = 1,
        NETWORK_RECOVERY = 2,
        LOCAL_MUTED = 3,
        LOCAL_UNMUTED = 4,
        REMOTE_MUTED = 5,
        REMOTE_UNMUTED = 6,
        REMOTE_OFFLINE = 7,
        AUDIO_FALLBACK = 8,
        AUDIO_FALLBACK_RECOVERY = 9
    }

    public enum JJConnectionChangedReason
    {
        CONNECTING = 0,
        JOIN_SUCCESS = 1,
        INTERRUPTED = 2,
        BANNED_BY_SERVER = 3,
        JOIN_FAILED = 4,
        LEAVE_CHANNEL = 5,
        INVALID_APP_ID = 6,
        INVALID_CHANNEL_NAME = 7,
        INVALID_TOKEN = 8,
        TOKEN_EXPIRED = 9,
        REJECTED_BY_SERVER = 10,
        SETTING_PROXY_SERVER = 11,
        RENEW_TOKEN = 12,
        CLIENT_IP_ADDRESS_CHANGED = 13,
        KEEP_ALIVE_TIMEOUT = 14,
        REJOIN_SUCCESS = 15,
        LOST = 16,
        ECHO_TEST = 17,
        CLIENT_IP_ADDRESS_CHANGED_BY_USER = 18,
        SAME_UID_LOGIN = 19,
        TOO_MANY_BROADCASTERS = 20
    }

    public enum JJConnectionState
    {
        DISCONNECTED = 1,
        CONNECTING = 2,
        CONNECTED = 3,
        RECONNECTING = 4,
        FAILED = 5
    }

    public enum JJScreenCaptureSourceType
    {
        UNKNOWN = -1,
        WINDOW = 0,
        SCREEN = 1,
        CUSTOM = 2
    }

    public enum JJVideoBufferType
    {
        RAW_DATA = 1
    }

    public enum JJVideoPixelFormat
    {
        RGBA = 4
    }

    public enum JJCameraDirection
    {
        REAR = 0,
        FRONT = 1
    }

    /// <summary>
    /// Lightweight meeting reactions sent over the LiveKit data channel.
    /// </summary>
    public enum JJReactionType
    {
        LIKE = 0,
        CLAP = 1
    }

    public class JJOptional<T>
    {
        public bool HasValue { get; private set; }
        public T Value { get; private set; }

        public void SetValue(T value)
        {
            Value = value;
            HasValue = true;
        }
    }

    public struct JJRtcConnection
    {
        public string ChannelId;
        public uint LocalUid;

        public JJRtcConnection(string channelId, uint localUid)
        {
            ChannelId = channelId;
            LocalUid = localUid;
        }
    }

    public struct JJDeviceInfo
    {
        public string deviceName;
        public string deviceTypeName;
        public string deviceId;
    }

    /// <summary>
    /// Controls the PCM format returned by the per-user remote audio observer.
    /// </summary>
    public class JJRemoteAudioFrameConfig
    {
        public int sampleRate = 16000;
        public int channels = 1;
    }

    /// <summary>
    /// A copied PCM16 audio frame for one remote user before application mixing.
    /// The PCM buffer remains valid after the callback returns.
    /// </summary>
    public sealed class JJRemoteAudioFrame
    {
        public string ChannelId { get; internal set; }
        public uint Uid { get; internal set; }
        public int SampleRate { get; internal set; }
        public int Channels { get; internal set; }
        public int SamplesPerChannel { get; internal set; }
        public int BytesPerSample { get; internal set; }
        public long RenderTimeMs { get; internal set; }
        public short[] Pcm16 { get; internal set; }
    }

    public class JJVideoEncoderConfiguration
    {
        public int width = 960;
        public int height = 540;
        public int frameRate = 15;
        public int bitrate = -1;
        public bool maintainResolution;
    }

    public class JJCameraCapturerConfiguration
    {
        public JJOptional<JJCameraDirection> cameraDirection = new();
        public JJOptional<string> cameraId = new();
    }

    public class JJScreenCaptureParameters
    {
        public int frameRate;
    }

    public class JJScreenVideoParameters
    {
        public int width;
        public int height;
        public int frameRate;
        public int bitrate;
    }

    public class JJScreenCaptureParameters2
    {
        public bool captureAudio;
        public bool captureVideo;
        public JJScreenVideoParameters videoParams;
    }

    public struct JJRectangle
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }

    public struct JJSize
    {
        public int width;
        public int height;
    }

    public class JJChannelMediaOptions
    {
        public JJOptional<bool> autoSubscribeAudio = new();
        public JJOptional<bool> autoSubscribeVideo = new();
        public JJOptional<bool> publishCameraTrack = new();
        public JJOptional<bool> publishScreenTrack = new();
        public JJOptional<bool> publishSecondaryScreenTrack = new();
        public JJOptional<bool> publishCustomVideoTrack = new();
        public JJOptional<bool> publishScreenCaptureVideo = new();
        public JJOptional<bool> publishScreenCaptureAudio = new();
        public JJOptional<bool> enableAudioRecordingOrPlayout = new();
        public JJOptional<JJClientRoleType> clientRoleType = new();
    }

    public class JJScreenCaptureSourceInfo
    {
        public JJScreenCaptureSourceType type;
        public long sourceId;
        public string sourceName;
        public JJThumbImageBuffer thumbImage;
        public JJThumbImageBuffer iconImage;
        public string processPath;
        public string sourceTitle;
        public bool primaryMonitor;
        public bool isOccluded;
        public RectInt position;
        public bool minimizeWindow;
        public long sourceDisplayId;
        internal ScreenCaptureSourceInfo AgoraSourceInfo;
    }

    public class JJThumbImageBuffer
    {
        public byte[] buffer;
        public uint width;
        public uint height;
    }

    public class JJExternalVideoFrame
    {
        public JJVideoBufferType type;
        public JJVideoPixelFormat format;
        public byte[] buffer;
        public int stride;
        public int height;
        public int rotation;
        public long timestamp;
    }

    public struct JJRemoteAudioStats
    {
        public uint uid;
        public int receivedSampleRate;
    }

    public struct JJLocalAudioStats
    {
        public int sentBitrate;
    }

    public struct JJRtcStats
    {
    }

    internal static class JorjinStreamingTypeConverter
    {
        internal static RtcConnection ToAgora(this JJRtcConnection connection)
        {
            return new RtcConnection(connection.ChannelId, connection.LocalUid);
        }

        internal static JJRtcConnection ToJorjin(this RtcConnection connection)
        {
            return new JJRtcConnection(connection.channelId, connection.localUid);
        }

        internal static JJDeviceInfo ToJorjin(this DeviceInfo info)
        {
            return new JJDeviceInfo
            {
                deviceName = info.deviceName,
                deviceTypeName = info.deviceTypeName,
                deviceId = info.deviceId
            };
        }

        internal static VideoEncoderConfiguration ToAgora(this JJVideoEncoderConfiguration config)
        {
            if (config == null) return new VideoEncoderConfiguration();

            return new VideoEncoderConfiguration
            {
                dimensions = new VideoDimensions(config.width, config.height),
                frameRate = config.frameRate,
                bitrate = config.bitrate,
                orientationMode = ORIENTATION_MODE.ORIENTATION_MODE_ADAPTIVE,
                degradationPreference = config.maintainResolution
                    ? DEGRADATION_PREFERENCE.MAINTAIN_RESOLUTION
                    : DEGRADATION_PREFERENCE.MAINTAIN_AUTO
            };
        }

        internal static CameraCapturerConfiguration ToAgora(this JJCameraCapturerConfiguration config)
        {
            var result = new CameraCapturerConfiguration();
            if (config == null) return result;

            if (config.cameraDirection.HasValue)
            {
                result.cameraDirection.SetValue(config.cameraDirection.Value == JJCameraDirection.FRONT
                    ? CAMERA_DIRECTION.CAMERA_FRONT
                    : CAMERA_DIRECTION.CAMERA_REAR);
            }

            if (config.cameraId.HasValue)
            {
                result.cameraId.SetValue(config.cameraId.Value);
            }

            return result;
        }

        internal static ChannelMediaOptions ToAgora(this JJChannelMediaOptions options)
        {
            var result = new ChannelMediaOptions();
            if (options == null) return result;

            SetOptional(options.autoSubscribeAudio, result.autoSubscribeAudio);
            SetOptional(options.autoSubscribeVideo, result.autoSubscribeVideo);
            SetOptional(options.publishCameraTrack, result.publishCameraTrack);
            SetOptional(options.publishScreenTrack, result.publishScreenTrack);
            SetOptional(options.publishSecondaryScreenTrack, result.publishSecondaryScreenTrack);
            SetOptional(options.publishCustomVideoTrack, result.publishCustomVideoTrack);
            SetOptional(options.publishScreenCaptureVideo, result.publishScreenCaptureVideo);
            SetOptional(options.publishScreenCaptureAudio, result.publishScreenCaptureAudio);
            SetOptional(options.enableAudioRecordingOrPlayout, result.enableAudioRecordingOrPlayout);
            if (options.clientRoleType.HasValue)
            {
                result.clientRoleType.SetValue((CLIENT_ROLE_TYPE)options.clientRoleType.Value);
            }

            return result;
        }

        internal static ScreenCaptureParameters ToAgora(this JJScreenCaptureParameters parameters)
        {
            return new ScreenCaptureParameters
            {
                frameRate = parameters?.frameRate ?? 0
            };
        }

        internal static ScreenCaptureParameters2 ToAgora(this JJScreenCaptureParameters2 parameters)
        {
            var result = new ScreenCaptureParameters2();
            if (parameters == null) return result;

            result.captureAudio = parameters.captureAudio;
            result.captureVideo = parameters.captureVideo;
            if (parameters.videoParams != null)
            {
                result.videoParams = new ScreenVideoParameters
                {
                    dimensions = new VideoDimensions(parameters.videoParams.width, parameters.videoParams.height),
                    frameRate = parameters.videoParams.frameRate,
                    bitrate = parameters.videoParams.bitrate
                };
            }

            return result;
        }

        internal static Rectangle ToAgora(this JJRectangle rect)
        {
            return new Rectangle
            {
                x = rect.x,
                y = rect.y,
                width = rect.width,
                height = rect.height
            };
        }

        internal static SIZE ToAgora(this JJSize size)
        {
            return new SIZE(size.width, size.height);
        }

        internal static Rectangle ToAgora(this RectInt rect)
        {
            return new Rectangle
            {
                x = rect.x,
                y = rect.y,
                width = rect.width,
                height = rect.height
            };
        }

        internal static SIZE ToAgora(this Vector2Int size)
        {
            return new SIZE(size.x, size.y);
        }

        internal static JJScreenCaptureSourceInfo ToJorjin(this ScreenCaptureSourceInfo info)
        {
            if (info == null) return null;
            return new JJScreenCaptureSourceInfo
            {
                type = (JJScreenCaptureSourceType)info.type,
                sourceId = info.sourceId,
                sourceName = info.sourceName,
                thumbImage = info.thumbImage.ToJorjin(),
                iconImage = info.iconImage.ToJorjin(),
                processPath = info.processPath,
                sourceTitle = info.sourceTitle,
                primaryMonitor = info.primaryMonitor,
                isOccluded = info.isOccluded,
                position = info.position.ToJorjin(),
                minimizeWindow = info.minimizeWindow,
                sourceDisplayId = info.sourceDisplayId,
                AgoraSourceInfo = info
            };
        }

        internal static JJThumbImageBuffer ToJorjin(this ThumbImageBuffer image)
        {
            if (image == null) return new JJThumbImageBuffer { buffer = new byte[0] };
            return new JJThumbImageBuffer
            {
                buffer = image.buffer,
                width = image.width,
                height = image.height
            };
        }

        internal static RectInt ToJorjin(this Rectangle rect)
        {
            if (rect == null) return new RectInt();
            return new RectInt(rect.x, rect.y, rect.width, rect.height);
        }

        internal static ExternalVideoFrame ToAgora(this JJExternalVideoFrame frame)
        {
            return new ExternalVideoFrame
            {
                type = (VIDEO_BUFFER_TYPE)frame.type,
                format = (VIDEO_PIXEL_FORMAT)frame.format,
                buffer = frame.buffer,
                stride = frame.stride,
                height = frame.height,
                rotation = frame.rotation,
                timestamp = frame.timestamp
            };
        }

        internal static JJRemoteAudioStats ToJorjin(this RemoteAudioStats stats)
        {
            return new JJRemoteAudioStats
            {
                uid = stats.uid,
                receivedSampleRate = stats.receivedSampleRate
            };
        }

        internal static JJLocalAudioStats ToJorjin(this LocalAudioStats stats)
        {
            return new JJLocalAudioStats
            {
                sentBitrate = stats.sentBitrate
            };
        }

        private static void SetOptional<T>(JJOptional<T> source, Optional<T> target)
        {
            if (source.HasValue)
            {
                target.SetValue(source.Value);
            }
        }
    }

}
