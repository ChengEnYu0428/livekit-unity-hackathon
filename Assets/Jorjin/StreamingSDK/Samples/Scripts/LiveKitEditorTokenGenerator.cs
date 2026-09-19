#if UNITY_EDITOR
using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class LiveKitEditorTokenGenerator
{
    private const string Prefix = "Jorjin.LiveKit.EditorToken.";
    private const string DefaultUrl = "wss://bb-afbn6bvt.livekit.cloud";

    public sealed class Connection
    {
        public string serverUrl;
        public string roomName;
        public string participantIdentity;
        public string participantToken;
        public bool autoJoin;
    }

    [Serializable]
    private sealed class TokenClaims
    {
        public string iss;
        public string sub;
        public string name;
        public long nbf;
        public long exp;
        public VideoGrant video;
    }

    [Serializable]
    private sealed class VideoGrant
    {
        public string room;
        public bool roomJoin = true;
        public bool canPublish = true;
        public bool canSubscribe = true;
        public bool canPublishData = true;
    }

    public static bool TryCreateConnection(
        string fallbackServerUrl,
        string fallbackRoomName,
        out Connection connection,
        out string error)
    {
        connection = null;
        error = null;

        string apiKey = ReadCredential("LIVEKIT_API_KEY", "ApiKey");
        string apiSecret = ReadCredential(
            "LIVEKIT_API_SECRET",
            "ApiSecret");
        if (string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(apiSecret))
        {
            error =
                "Unity automatic Token is not configured. Open " +
                "Jorjin > LiveKit Automatic Token Settings.";
            return false;
        }

        string roomName = Read(
            "RoomName",
            string.IsNullOrWhiteSpace(fallbackRoomName)
                ? "test-room"
                : fallbackRoomName);
        string identity = Read(
            "Identity",
            "unity-" + Sanitize(SystemInfo.deviceUniqueIdentifier));
        string participantName = Read(
            "ParticipantName",
            "Unity Editor");
        string serverUrl = Read(
            "ServerUrl",
            string.IsNullOrWhiteSpace(fallbackServerUrl)
                ? DefaultUrl
                : fallbackServerUrl);
        int ttlMinutes = Mathf.Clamp(
            EditorPrefs.GetInt(Prefix + "TtlMinutes", 60),
            5,
            1440);

        roomName = Sanitize(roomName);
        identity = Sanitize(identity);
        if (string.IsNullOrWhiteSpace(roomName) ||
            string.IsNullOrWhiteSpace(identity))
        {
            error = "Unity automatic Token room or identity is empty.";
            return false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new TokenClaims
        {
            iss = apiKey.Trim(),
            sub = identity,
            name = participantName.Trim(),
            nbf = now - 5,
            exp = now + ttlMinutes * 60L,
            video = new VideoGrant { room = roomName }
        };

        string header = Base64Url(
            Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        string payload = Base64Url(
            Encoding.UTF8.GetBytes(JsonUtility.ToJson(claims)));
        string unsignedToken = header + "." + payload;
        byte[] signature;
        using (var hmac = new HMACSHA256(
                   Encoding.UTF8.GetBytes(apiSecret.Trim())))
        {
            signature = hmac.ComputeHash(
                Encoding.ASCII.GetBytes(unsignedToken));
        }

        connection = new Connection
        {
            serverUrl = serverUrl.Trim(),
            roomName = roomName,
            participantIdentity = identity,
            participantToken =
                unsignedToken + "." + Base64Url(signature),
            autoJoin = EditorPrefs.GetBool(Prefix + "AutoJoin", true)
        };
        return true;
    }

    private static string ReadCredential(
        string environmentName,
        string preferenceName)
    {
        string environmentValue =
            Environment.GetEnvironmentVariable(environmentName);
        return string.IsNullOrWhiteSpace(environmentValue)
            ? Read(preferenceName, string.Empty)
            : environmentValue;
    }

    private static string Read(string name, string defaultValue)
    {
        return EditorPrefs.GetString(Prefix + name, defaultValue);
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length);
        foreach (char character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) ||
                character == '-' ||
                character == '_')
            {
                result.Append(character);
            }
            else
            {
                result.Append('-');
            }
        }
        return result.ToString().Trim('-', '_');
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    [MenuItem("Jorjin/LiveKit Automatic Token Settings")]
    private static void OpenSettings()
    {
        LiveKitAutomaticTokenSettingsWindow.Open();
    }

    private sealed class LiveKitAutomaticTokenSettingsWindow :
        EditorWindow
    {
        private string serverUrl;
        private string apiKey;
        private string apiSecret;
        private string roomName;
        private string identity;
        private string participantName;
        private int ttlMinutes;
        private bool autoJoin;

        public static void Open()
        {
            var window = GetWindow<
                LiveKitAutomaticTokenSettingsWindow>(
                utility: false,
                title: "LiveKit Automatic Token",
                focus: true);
            window.minSize = new Vector2(480f, 330f);
            window.Load();
            window.Show();
        }

        private void OnEnable()
        {
            Load();
        }

        private void Load()
        {
            serverUrl = Read("ServerUrl", DefaultUrl);
            apiKey = Read("ApiKey", string.Empty);
            apiSecret = Read("ApiSecret", string.Empty);
            roomName = Read("RoomName", "test-room");
            identity = Read(
                "Identity",
                "unity-" + Sanitize(SystemInfo.deviceUniqueIdentifier));
            participantName = Read(
                "ParticipantName",
                "Unity Editor");
            ttlMinutes = EditorPrefs.GetInt(
                Prefix + "TtlMinutes",
                60);
            autoJoin = EditorPrefs.GetBool(
                Prefix + "AutoJoin",
                true);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "These development credentials are stored only in this " +
                "computer's Unity Editor preferences. They are never " +
                "serialized into the Scene or Player build.",
                MessageType.Info);
            serverUrl = EditorGUILayout.TextField(
                "LiveKit URL",
                serverUrl);
            apiKey = EditorGUILayout.TextField("API Key", apiKey);
            apiSecret = EditorGUILayout.PasswordField(
                "API Secret",
                apiSecret);
            EditorGUILayout.Space(8f);
            roomName = EditorGUILayout.TextField(
                "Meeting Room",
                roomName);
            identity = EditorGUILayout.TextField(
                "Participant Identity",
                identity);
            participantName = EditorGUILayout.TextField(
                "Participant Name",
                participantName);
            ttlMinutes = EditorGUILayout.IntSlider(
                "Token Lifetime (minutes)",
                ttlMinutes,
                5,
                1440);
            autoJoin = EditorGUILayout.Toggle(
                "Auto Join On Play",
                autoJoin);

            EditorGUILayout.Space(12f);
            if (GUILayout.Button("Save Settings"))
            {
                EditorPrefs.SetString(
                    Prefix + "ServerUrl",
                    serverUrl.Trim());
                EditorPrefs.SetString(
                    Prefix + "ApiKey",
                    apiKey.Trim());
                EditorPrefs.SetString(
                    Prefix + "ApiSecret",
                    apiSecret.Trim());
                EditorPrefs.SetString(
                    Prefix + "RoomName",
                    roomName.Trim());
                EditorPrefs.SetString(
                    Prefix + "Identity",
                    identity.Trim());
                EditorPrefs.SetString(
                    Prefix + "ParticipantName",
                    participantName.Trim());
                EditorPrefs.SetInt(
                    Prefix + "TtlMinutes",
                    ttlMinutes);
                EditorPrefs.SetBool(
                    Prefix + "AutoJoin",
                    autoJoin);
                ShowNotification(
                    new GUIContent("LiveKit settings saved"));
            }

            if (GUILayout.Button("Clear Local API Credentials"))
            {
                EditorPrefs.DeleteKey(Prefix + "ApiKey");
                EditorPrefs.DeleteKey(Prefix + "ApiSecret");
                apiKey = string.Empty;
                apiSecret = string.Empty;
                ShowNotification(
                    new GUIContent("Local credentials cleared"));
            }
        }
    }
}
#endif
