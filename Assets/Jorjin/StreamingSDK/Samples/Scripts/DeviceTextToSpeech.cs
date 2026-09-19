using System;
using UnityEngine;

/// <summary>
/// Reads text aloud with the Android system text-to-speech engine. Only the
/// person holding the device hears it; nothing is sent to the meeting.
/// Methods return null on success, otherwise a message for the status line.
/// </summary>
public sealed class DeviceTextToSpeech : IDisposable
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private const int Success = 0;
    private const int QueueFlush = 0;
    private const int LangMissingData = -1;
    private AndroidJavaObject engine;
    private volatile int initStatus = int.MinValue;

    private sealed class InitListener : AndroidJavaProxy
    {
        private readonly DeviceTextToSpeech owner;
        public InitListener(DeviceTextToSpeech owner)
            : base("android.speech.tts.TextToSpeech$OnInitListener") { this.owner = owner; }
        // Called on the Android main thread, not the Unity thread.
        public void onInit(int status) { owner.initStatus = status; }
    }

    public DeviceTextToSpeech()
    {
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            engine = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new InitListener(this));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Text-to-speech is unavailable: " + exception.Message);
            engine = null;
        }
    }

    public string Speak(string text, bool chinese)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Nothing to read.";
        if (engine == null || (initStatus != int.MinValue && initStatus != Success))
            return "This device has no text-to-speech engine.";
        if (initStatus == int.MinValue) return "The voice engine is starting. Try again in a moment.";
        try
        {
            using var locale = chinese
                ? new AndroidJavaObject("java.util.Locale", "zh", "TW")
                : new AndroidJavaObject("java.util.Locale", "en", "US");
            int language = engine.Call<int>("setLanguage", locale);
            if (language <= LangMissingData)
                return chinese
                    ? "This device has no Chinese voice. Install one in Android text-to-speech settings."
                    : "This device has no English voice. Install one in Android text-to-speech settings.";
            // Android limits one utterance to about 4,000 characters.
            if (text.Length > 3900) text = text.Substring(0, 3900);
            using var parameters = new AndroidJavaObject("android.os.Bundle");
            int result = engine.Call<int>("speak", text, QueueFlush, parameters, "photo_text");
            return result == Success ? null : "The voice engine could not read this text.";
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Text-to-speech failed: " + exception.Message);
            return "The voice engine could not read this text.";
        }
    }

    public void Stop()
    {
        try { engine?.Call<int>("stop"); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (engine == null) return;
        try { engine.Call<int>("stop"); engine.Call("shutdown"); }
        catch (Exception) { }
        engine.Dispose();
        engine = null;
    }
#else
    public string Speak(string text, bool chinese) => "Read aloud works in the Android device build.";
    public void Stop() { }
    public void Dispose() { }
#endif
}
