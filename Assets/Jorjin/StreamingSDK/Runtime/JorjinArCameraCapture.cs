using System;
using System.Collections;
using Jorjin.Streaming.JJSDK;
using UnityEngine;

namespace Jorjin.Streaming
{
    /// <summary>
    /// Reads the Jorjin glasses camera using the same Java ByteBuffer path as
    /// the verified JJUnityPluginv2 CamRenderer, then exposes it to LiveKit.
    /// </summary>
    internal sealed class JorjinArCameraCapture : MonoBehaviour
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const int PixelDataOffset = 4;
        private const int RgbaBytesPerPixel = 4;

        private readonly object frameLock = new();
        private JJCameraManager cameraManager;
        private FrameListener frameListener;
        private Texture2D activeTexture;
        private byte[] pendingBytes;
        private byte[] verticallyFlippedBytes;
        private Color32[] pendingPixels;
        private Color32[] framePixels;
        private Color32[] verticallyFlippedPixels;
        private int pendingWidth;
        private int pendingHeight;
        private bool pendingFrameUsesPixels;
        private bool frameUpdated;
        private bool firstFrameReceived;
        private bool starting;

        public Texture2D ActiveTexture => activeTexture;
        public bool IsCapturing { get; private set; }
        public string LastError { get; private set; }

        public IEnumerator StartCapture()
        {
            if (IsCapturing) yield break;
            if (starting)
            {
                // A startup prewarm may already be waiting for Android/USB
                // permission. LiveKit must wait for that same attempt instead
                // of immediately falling back to the phone camera.
                while (starting) yield return null;
                yield break;
            }
            starting = true;
            LastError = null;

#if UNITY_ANDROID
            if (Application.isEditor)
            {
                Fail("Jorjin camera capture is available only in an Android player.");
                yield break;
            }

            // This ordering is copied from the verified CamRenderer. Waiting
            // for both runtime permissions prevents their dialogs from colliding
            // with the USB permission dialog opened by JJSDK startCamera().
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Camera))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    UnityEngine.Android.Permission.Camera);
            }
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    UnityEngine.Android.Permission.Microphone);
            }

            float permissionTimeout = 45f;
            while ((!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                        UnityEngine.Android.Permission.Camera) ||
                    !UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                        UnityEngine.Android.Permission.Microphone)) &&
                   permissionTimeout > 0f)
            {
                permissionTimeout -= Time.unscaledDeltaTime;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Camera) ||
                !UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                Fail("Camera and microphone permissions are required before JJSDK can request USB access.");
                yield break;
            }

            yield return new WaitForSecondsRealtime(1.5f);

            activeTexture = new Texture2D(
                Width, Height, TextureFormat.RGBA32, false, false)
            {
                name = "Jorjin AR Camera"
            };

            try
            {
                cameraManager = new JJCameraManager();
                string[] resolutions = cameraManager.GetResolutionList();
                Debug.Log("JJSDK camera resolutions: " +
                          (resolutions == null ? "<none>" : string.Join(", ", resolutions)));

                StartCameraWithListener(false);
            }
            catch (Exception exception)
            {
                Fail("Unable to start the Jorjin camera: " + exception.Message);
                StopCapture();
                yield break;
            }

            // The vendor sample uses the ByteBuffer listener. Some recent
            // Android/Unity combinations can enumerate the glasses but never
            // invoke that Java callback, so retry through the native callback
            // exported by the same AAR before declaring the camera unavailable.
            float frameTimeout = 20f;
            while (!firstFrameReceived && frameTimeout > 0f)
            {
                frameTimeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (!firstFrameReceived)
            {
                Debug.LogWarning(
                    "JJSDK ByteBuffer listener did not deliver a frame; retrying with the native listener.");
                try
                {
                    cameraManager.StopCamera();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "JJSDK stopCamera before native retry failed: " +
                        exception.Message);
                }

                cameraManager.Dispose();
                cameraManager = null;

                yield return new WaitForSecondsRealtime(0.5f);

                try
                {
                    cameraManager = new JJCameraManager();
                    StartCameraWithListener(true);
                }
                catch (Exception exception)
                {
                    Fail("Unable to retry the Jorjin camera: " + exception.Message);
                    StopCapture();
                    yield break;
                }

                frameTimeout = 12f;
                while (!firstFrameReceived && frameTimeout > 0f)
                {
                    frameTimeout -= Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            if (!firstFrameReceived)
            {
                Fail("JJSDK did not deliver a frame. Accept the USB permission dialog and check the glasses connection.");
                StopCapture();
                yield break;
            }

            IsCapturing = true;
            starting = false;
            Debug.Log($"Jorjin ByteBuffer camera started at {activeTexture.width}x{activeTexture.height}.");
#else
            Fail("Jorjin camera capture is available only in an Android player.");
            yield return null;
#endif
        }

#if UNITY_ANDROID
        private void StartCameraWithListener(bool useNativeListener)
        {
            frameListener = useNativeListener
                ? new FrameListener(OnIncomingFrame)
                : new FrameListener(OnIncomingBytes);
            cameraManager.SetResolutionIndex(
                (int)JJCameraManager.Resolution.RES_1280x720);
            cameraManager.SetCameraFrameListener(frameListener);
            cameraManager.StartCamera(
                (int)JJCameraManager.ColorFormat.COLOR_FORMAT_RGBA);
            Debug.Log(
                "JJSDK camera started with " +
                (useNativeListener ? "NativeFrameListener." : "FrameListener (ByteBuffer)."));
        }
#endif

        private void OnIncomingFrame(
            in Color32[] pixels,
            int width,
            int height,
            int format)
        {
            if (pixels == null || width <= 0 || height <= 0 ||
                pixels.Length < width * height)
            {
                return;
            }

            lock (frameLock)
            {
                int pixelCount = checked(width * height);
                if (pendingPixels == null || pendingPixels.Length != pixelCount)
                {
                    pendingPixels = new Color32[pixelCount];
                }
                Array.Copy(pixels, pendingPixels, pixelCount);
                pendingWidth = width;
                pendingHeight = height;
                pendingFrameUsesPixels = true;
                frameUpdated = true;
            }
        }

        private void OnIncomingBytes(in byte[] bytes, int width, int height, int format)
        {
            if (bytes == null || width <= 0 || height <= 0) return;
            lock (frameLock)
            {
                pendingBytes = bytes;
                pendingWidth = width;
                pendingHeight = height;
                pendingFrameUsesPixels = false;
                frameUpdated = true;
            }
        }

        private void Update()
        {
            byte[] bytes;
            Color32[] pixels = null;
            int width;
            int height;
            bool usesPixels;
            lock (frameLock)
            {
                if (!frameUpdated) return;
                bytes = pendingBytes;
                width = pendingWidth;
                height = pendingHeight;
                usesPixels = pendingFrameUsesPixels;
                if (usesPixels)
                {
                    int pixelCount = checked(width * height);
                    if (framePixels == null || framePixels.Length != pixelCount)
                    {
                        framePixels = new Color32[pixelCount];
                    }
                    Array.Copy(pendingPixels, framePixels, pixelCount);
                    pixels = framePixels;
                }
                frameUpdated = false;
            }

            int requiredLength = checked(
                width * height * RgbaBytesPerPixel + PixelDataOffset);
            if (!usesPixels && (bytes == null || bytes.Length < requiredLength))
            {
                Debug.LogWarning(
                    $"JJSDK frame is too short: {bytes?.Length ?? 0} bytes, expected at least {requiredLength}.");
                return;
            }

            if (activeTexture == null ||
                activeTexture.width != width || activeTexture.height != height)
            {
                if (activeTexture != null) Destroy(activeTexture);
                activeTexture = new Texture2D(
                    width, height, TextureFormat.RGBA32, false, false)
                {
                    name = "Jorjin AR Camera"
                };
            }

            // JJSDK's Java ByteBuffer stores rows from top to bottom while
            // Unity textures store row zero at the bottom. Reverse the row
            // order before upload so both the local preview and the LiveKit
            // stream from the glasses are upright. This path is used only for
            // the Jorjin source and does not affect Android WebCamTexture.
            if (usesPixels)
            {
                int pixelCount = checked(width * height);
                if (verticallyFlippedPixels == null ||
                    verticallyFlippedPixels.Length != pixelCount)
                {
                    verticallyFlippedPixels = new Color32[pixelCount];
                }

                for (int sourceRow = 0; sourceRow < height; sourceRow++)
                {
                    Array.Copy(
                        pixels,
                        sourceRow * width,
                        verticallyFlippedPixels,
                        (height - 1 - sourceRow) * width,
                        width);
                }

                activeTexture.SetPixels32(verticallyFlippedPixels);
            }
            else
            {
                int pixelDataLength = checked(width * height * RgbaBytesPerPixel);
                if (verticallyFlippedBytes == null ||
                    verticallyFlippedBytes.Length != pixelDataLength)
                {
                    verticallyFlippedBytes = new byte[pixelDataLength];
                }

                int rowBytes = checked(width * RgbaBytesPerPixel);
                for (int sourceRow = 0; sourceRow < height; sourceRow++)
                {
                    int sourceOffset = PixelDataOffset + sourceRow * rowBytes;
                    int destinationOffset = (height - 1 - sourceRow) * rowBytes;
                    Buffer.BlockCopy(
                        bytes,
                        sourceOffset,
                        verticallyFlippedBytes,
                        destinationOffset,
                        rowBytes);
                }

                activeTexture.LoadRawTextureData(verticallyFlippedBytes);
            }
            activeTexture.Apply(false, false);
            if (!firstFrameReceived)
            {
                Debug.Log(
                    "Jorjin camera delivered its first " +
                    (usesPixels ? "native" : "ByteBuffer") +
                    " frame; rows were flipped vertically for Unity/LiveKit.");
            }
            firstFrameReceived = true;
        }

        public void StopCapture()
        {
#if UNITY_ANDROID
            if (cameraManager != null)
            {
                try
                {
                    cameraManager.StopCamera();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("JJSDK stopCamera failed: " + exception.Message);
                }
                cameraManager.Dispose();
                cameraManager = null;
            }
#endif
            frameListener = null;
            lock (frameLock)
            {
                pendingBytes = null;
                pendingPixels = null;
                frameUpdated = false;
            }
            verticallyFlippedBytes = null;
            framePixels = null;
            verticallyFlippedPixels = null;
            firstFrameReceived = false;
            IsCapturing = false;
            starting = false;
            if (activeTexture != null)
            {
                Destroy(activeTexture);
                activeTexture = null;
            }
        }

        private void Fail(string message)
        {
            LastError = message;
            starting = false;
            Debug.LogWarning(message);
        }

        private void OnDestroy() => StopCapture();
    }
}
