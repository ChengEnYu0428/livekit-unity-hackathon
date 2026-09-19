using System;
using UnityEngine;

namespace Jorjin.Streaming.JJSDK
{
    /// <summary>
    /// C# wrapper for com.jorjin.jjsdk.camera.CameraManager.
    /// Ported from the verified JJUnityPluginv2 project.
    /// </summary>
    internal sealed class JJCameraManager : IDisposable
    {
        public enum Resolution
        {
            RES_640x480 = 0,
            RES_800x600 = 1,
            RES_320x240 = 2,
            RES_1280x720 = 3,
            RES_1600x1200 = 4,
            RES_1920x1080 = 5,
            RES_2048x1536 = 6,
            RES_2592x1944 = 7,
            RES_3264x2448 = 8
        }

        public enum ColorFormat
        {
            COLOR_FORMAT_RGBA = 0,
            COLOR_FORMAT_GRAY = 1,
            COLOR_FORMAT_YUV420P = 2,
            COLOR_FORMAT_NV21 = 3
        }

        private AndroidJavaObject cameraManager;
        private AndroidJavaObject javaCameraParameter;
        private CameraParameter cameraParameter;
        private AndroidJavaClass unityPlayer;
        private AndroidJavaObject activity;
        private AndroidJavaObject context;

        public JJCameraManager()
        {
            // Keep these Java wrappers alive for the whole camera session, as
            // done by the working jorjin_local_code_yubbb unitypackage.
            unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            context = activity.Call<AndroidJavaObject>("getApplicationContext");
            cameraManager = new AndroidJavaObject(
                "com.jorjin.jjsdk.camera.CameraManager", context);
            javaCameraParameter = cameraManager.Call<AndroidJavaObject>("getCameraParameter");
            cameraParameter = new CameraParameter(javaCameraParameter);
        }

        public void StartCamera(int colorFormat) => cameraManager.Call("startCamera", colorFormat);
        public bool IsPreviewing() => cameraManager.Call<bool>("isPreviewing");
        public void StopCamera() => cameraManager.Call("stopCamera");

        public void SetCameraFrameListener(FrameListener listener)
        {
            cameraManager.Call(
                listener.IsNativeListener
                    ? "setCameraNativeFrameListener"
                    : "setCameraFrameListener",
                listener);
        }

        public string[] GetResolutionList() => cameraManager.Call<string[]>("getResolutionList");
        public void SetResolutionIndex(int index) => cameraManager.Call("setResolutionIndex", index);
        public CameraParameter GetCameraParmeter() => cameraParameter;
        public void SetCameraParameter(AndroidJavaObject parameter) =>
            cameraManager.Call("setCameraParameter", parameter);

        public void Dispose()
        {
            // CameraManager registers USB/camera callbacks in native code.
            // Disposing only the JNI wrapper leaves that Java instance alive,
            // so a later retry can enumerate the glasses but receive no frames.
            // The vendor AAR exposes release() specifically for this cleanup.
            if (cameraManager != null)
            {
                try
                {
                    cameraManager.Call("release");
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "JJSDK CameraManager.release failed: " +
                        exception.Message);
                }
            }
            cameraParameter = null;
            javaCameraParameter?.Dispose();
            javaCameraParameter = null;
            cameraManager?.Dispose();
            cameraManager = null;
            context?.Dispose();
            context = null;
            activity?.Dispose();
            activity = null;
            unityPlayer?.Dispose();
            unityPlayer = null;
        }
    }
}
