using System;
using Agora.Rtc;
using LiveKit;
using UnityEngine;
using UnityEngine.UI;

namespace Jorjin.Streaming
{
    [RequireComponent(typeof(VideoSurface))]
    public class JJVideoSurface : MonoBehaviour
    {
        private VideoSurface videoSurface;
        private VideoStream liveKitVideoStream;
        private Coroutine liveKitVideoCoroutine;
        private event Action<int, int> textureSizeModified;
        private bool textureSizeEventBound;

        [Header("Local Video Settings")]
        [SerializeField] private bool flipLocalVideoVertically = true;
        [SerializeField] private RawImage targetRawImage;

        private VideoSurface AgoraVideoSurface =>
            videoSurface != null ? videoSurface : videoSurface = GetComponent<VideoSurface>();

        private RawImage TargetRawImage
        {
            get
            {
                if (targetRawImage == null) targetRawImage = GetComponent<RawImage>();
                if (targetRawImage == null) targetRawImage = GetComponentInChildren<RawImage>(true);
                return targetRawImage;
            }
        }

        public event Action<int, int> OnTextureSizeModify
        {
            add
            {
                textureSizeModified += value;
                EnsureAgoraTextureEventBound();
            }
            remove => textureSizeModified -= value;
        }

        private void OnDestroy()
        {
            ClearLiveKitVideo();
            if (videoSurface != null && textureSizeEventBound)
            {
                videoSurface.OnTextureSizeModify -= HandleTextureSizeModify;
            }
        }

        private void EnsureAgoraTextureEventBound()
        {
            if (textureSizeEventBound || AgoraVideoSurface == null) return;
            AgoraVideoSurface.OnTextureSizeModify += HandleTextureSizeModify;
            textureSizeEventBound = true;
        }

        private void HandleTextureSizeModify(int width, int height)
        {
            textureSizeModified?.Invoke(width, height);
        }

        public void SetVerticalFlip(bool flip)
        {
            if (TargetRawImage != null)
            {
                TargetRawImage.uvRect = flip
                    ? new Rect(0f, 1f, 1f, -1f)
                    : new Rect(0f, 0f, 1f, 1f);
                return;
            }

            RectTransform rectTransform = GetComponent<RectTransform>();
            if (rectTransform == null) return;
            Vector3 scale = rectTransform.localScale;
            scale.y = Mathf.Abs(scale.y) * (flip ? -1f : 1f);
            rectTransform.localScale = scale;
        }

        public void SetForUser(uint uid)
        {
            ClearLiveKitVideo();
            AgoraVideoSurface.SetForUser(uid);
            SetVerticalFlip(flipLocalVideoVertically);
        }

        public void SetForUser(uint uid, string channelId)
        {
            ClearLiveKitVideo();
            AgoraVideoSurface.SetForUser(uid, channelId);
            SetVerticalFlip(flipLocalVideoVertically);
        }

        /// <summary>
        /// Binds a LiveKit local preview texture while preserving the original
        /// SetForUser naming used by StreamingSDK integrations.
        /// </summary>
        public void SetForUser(Texture texture)
        {
            SetForLiveKitLocal(texture);
        }

        public void SetForRemoteUser(uint uid, string channelId)
        {
            ClearLiveKitVideo();
            AgoraVideoSurface.SetForUser(uid, channelId, VIDEO_SOURCE_TYPE.VIDEO_SOURCE_REMOTE);
            SetVerticalFlip(false);
        }

        /// <summary>
        /// Binds a LiveKit remote video track while preserving the original
        /// SetForRemoteUser naming used by StreamingSDK integrations.
        /// </summary>
        public void SetForRemoteUser(RemoteVideoTrack track)
        {
            SetForLiveKitRemote(track);
        }

        public void SetForLiveKitLocal(Texture texture)
        {
            ClearLiveKitVideo();
            if (TargetRawImage == null) return;
            TargetRawImage.texture = texture;
            TargetRawImage.enabled = texture != null;
            // WebCamTexture already tells Unity whether the captured image is
            // vertically mirrored. Do not reuse Agora's fixed local-video flip.
            bool shouldFlip = texture is WebCamTexture webCamTexture &&
                              webCamTexture.videoVerticallyMirrored;
            SetVerticalFlip(shouldFlip);
            if (texture != null) textureSizeModified?.Invoke(texture.width, texture.height);
        }

        public void SetForLiveKitRemote(RemoteVideoTrack track)
        {
            ClearLiveKitVideo();
            if (track == null) return;
            liveKitVideoStream = new VideoStream(track);
            liveKitVideoStream.TextureReceived += HandleLiveKitTexture;
            liveKitVideoStream.Start();
            liveKitVideoCoroutine = StartCoroutine(liveKitVideoStream.Update());
            SetVerticalFlip(false);
        }

        public void ClearLiveKitVideo()
        {
            if (liveKitVideoCoroutine != null)
            {
                StopCoroutine(liveKitVideoCoroutine);
                liveKitVideoCoroutine = null;
            }
            if (liveKitVideoStream != null)
            {
                liveKitVideoStream.TextureReceived -= HandleLiveKitTexture;
                liveKitVideoStream.Stop();
                liveKitVideoStream.Dispose();
                liveKitVideoStream = null;
            }
        }

        private void HandleLiveKitTexture(Texture texture)
        {
            if (TargetRawImage == null || texture == null) return;
            TargetRawImage.texture = texture;
            TargetRawImage.enabled = true;
            textureSizeModified?.Invoke(texture.width, texture.height);
        }

        public void SetEnable(bool enable)
        {
            if (AgoraVideoSurface != null) AgoraVideoSurface.SetEnable(enable);
            if (TargetRawImage != null) TargetRawImage.enabled = enable;
        }

        public static JJVideoSurface FindOrCreate(GameObject root)
        {
            if (root == null) return null;
            JJVideoSurface wrapper = root.GetComponentInChildren<JJVideoSurface>(true);
            if (wrapper != null) return wrapper;

            VideoSurface agoraSurface = root.GetComponentInChildren<VideoSurface>(true);
            if (agoraSurface != null)
            {
                wrapper = agoraSurface.gameObject.AddComponent<JJVideoSurface>();
                wrapper.videoSurface = agoraSurface;
                return wrapper;
            }

            RawImage rawImage = root.GetComponentInChildren<RawImage>(true);
            if (rawImage == null) return null;
            wrapper = rawImage.gameObject.AddComponent<JJVideoSurface>();
            wrapper.targetRawImage = rawImage;
            return wrapper;
        }
    }
}
