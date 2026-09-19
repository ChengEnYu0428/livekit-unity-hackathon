using UnityEngine;

namespace Jorjin.Streaming.JJSDK
{
    /// <summary>
    /// C# wrapper for com.jorjin.jjsdk.camera.CameraParameter.
    /// Ported from the verified JJUnityPluginv2 project.
    /// </summary>
    internal sealed class CameraParameter
    {
        public AndroidJavaObject JavaObject { get; }

        public CameraParameter(AndroidJavaObject androidJavaObject)
        {
            JavaObject = androidJavaObject;
        }

        public string[] GetResolutionList() => JavaObject.Call<string[]>("getResolutionList");
        public int GetBrightness() => JavaObject.Call<int>("getBrightness");
        public int GetContrast() => JavaObject.Call<int>("getContrast");
        public int GetGamma() => JavaObject.Call<int>("getGamma");
        public int GetHue() => JavaObject.Call<int>("getHue");
        public int GetSharpness() => JavaObject.Call<int>("getSharpness");
        public int GetSaturation() => JavaObject.Call<int>("getSaturation");
        public int GetPowerLineFrequency() => JavaObject.Call<int>("getPowerLineFrequency");
        public void SetBrightness(int value) => JavaObject.Call("setBrightness", value);
        public void SetContrast(int value) => JavaObject.Call("setContrast", value);
        public void SetGamma(int value) => JavaObject.Call("setGamma", value);
        public void SetHue(int value) => JavaObject.Call("setHue", value);
        public void SetSharpness(int value) => JavaObject.Call("setSharpness", value);
        public void SetSaturation(int value) => JavaObject.Call("setSaturation", value);
        public bool IsAutoFocusOn() => JavaObject.Call<bool>("isAutoFocusOn");
        public void SetAutoFocus(bool enabled) => JavaObject.Call("setAutoFocus", enabled);
        public void SetPowerLineFrequency(int value) =>
            JavaObject.Call("setPowerLineFrequency", value);
    }
}
