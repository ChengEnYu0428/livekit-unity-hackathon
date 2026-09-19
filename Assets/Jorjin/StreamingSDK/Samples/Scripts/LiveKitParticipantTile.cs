using System.Collections;
using Jorjin.Streaming;
using LiveKit;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime-created participant tile used by the LiveKit meeting grid.
/// It owns the VideoStream for one remote track and releases it when the
/// participant leaves or the track is unpublished.
/// </summary>
public sealed class LiveKitParticipantTile : MonoBehaviour
{
    private RawImage videoImage;
    private AspectRatioFitter videoAspect;
    private TMP_Text placeholderText;
    private TMP_Text nameText;
    private Image microphoneBadgeImage;
    private TMP_Text microphoneBadgeText;
    private TMP_Text reactionText;
    private CanvasGroup reactionGroup;
    private VideoStream remoteVideoStream;
    private Coroutine remoteVideoCoroutine;
    private RemoteVideoTrack pendingRemoteTrack;
    private Coroutine reactionCoroutine;
    private Texture currentTexture;
    private bool videoVisible = true;

    public string TileKey { get; private set; }
    public string OwnerIdentity { get; private set; }
    // Share the existing decoder output with the collaboration workspace.
    // The workspace does not create another subscription or own this texture.
    public Texture DisplayTexture => videoVisible && isActiveAndEnabled ? currentTexture : null;
    public Rect DisplayUvRect => videoImage != null ? videoImage.uvRect : new Rect(0, 0, 1, 1);
    public bool IsScreenShare { get; set; }

    public static LiveKitParticipantTile Create(
        Transform parent,
        string tileKey,
        string ownerIdentity,
        string displayName)
    {
        var root = new GameObject(
            $"Participant: {displayName}",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Mask));
        root.transform.SetParent(parent, false);

        var rootImage = root.GetComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            rootImage,
            LiveKitMeetingStyle.Surface);
        rootImage.raycastTarget = false;
        root.GetComponent<Mask>().showMaskGraphic = true;
        LiveKitMeetingStyle.AddSoftShadow(root, 0.3f);
        LiveKitMeetingStyle.AddBorder(root, 0.62f);

        var tile = root.AddComponent<LiveKitParticipantTile>();
        tile.TileKey = tileKey;
        tile.OwnerIdentity = ownerIdentity;
        tile.BuildVisuals(displayName);
        return tile;
    }

    private void BuildVisuals(string displayName)
    {
        GameObject videoObject = CreateRect("Video", transform);
        RectTransform videoRect = (RectTransform)videoObject.transform;
        videoRect.anchorMin = new Vector2(0.5f, 0.5f);
        videoRect.anchorMax = new Vector2(0.5f, 0.5f);
        videoRect.pivot = new Vector2(0.5f, 0.5f);
        videoRect.sizeDelta = new Vector2(156f, 88f);
        videoImage = videoObject.AddComponent<RawImage>();
        videoImage.color = Color.white;
        videoImage.raycastTarget = false;
        videoImage.enabled = false;
        videoAspect = videoObject.AddComponent<AspectRatioFitter>();
        videoAspect.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        videoAspect.aspectRatio = 16f / 9f;

        placeholderText = CreateText("Placeholder", transform, 42f, FontStyles.Bold);
        Stretch(placeholderText.rectTransform, 10f, 10f, 10f, 10f);
        placeholderText.alignment = TextAlignmentOptions.Center;
        placeholderText.color = LiveKitMeetingStyle.TextSecondary;

        GameObject nameBarObject = CreateRect("Name Bar", transform);
        RectTransform nameBarRect = (RectTransform)nameBarObject.transform;
        nameBarRect.anchorMin = new Vector2(0f, 0f);
        nameBarRect.anchorMax = new Vector2(1f, 0f);
        nameBarRect.pivot = new Vector2(0.5f, 0f);
        nameBarRect.offsetMin = Vector2.zero;
        nameBarRect.offsetMax = new Vector2(0f, 38f);
        Image nameBar = nameBarObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            nameBar,
            new Color(0.035f, 0.055f, 0.09f, 0.9f));
        nameBar.raycastTarget = false;

        nameText = CreateText("Name", nameBarObject.transform, 18f, FontStyles.Bold);
        Stretch(nameText.rectTransform, 10f, 5f, 108f, 4f);
        nameText.alignment = TextAlignmentOptions.MidlineLeft;
        nameText.color = LiveKitMeetingStyle.TextPrimary;

        GameObject microphoneBadgeObject = CreateRect(
            "Microphone Status",
            nameBarObject.transform);
        RectTransform microphoneBadgeRect =
            (RectTransform)microphoneBadgeObject.transform;
        microphoneBadgeRect.anchorMin = new Vector2(1f, 0f);
        microphoneBadgeRect.anchorMax = new Vector2(1f, 1f);
        microphoneBadgeRect.pivot = new Vector2(1f, 0.5f);
        microphoneBadgeRect.sizeDelta = new Vector2(98f, 0f);
        microphoneBadgeRect.anchoredPosition = new Vector2(-4f, 0f);
        microphoneBadgeImage = microphoneBadgeObject.AddComponent<Image>();
        LiveKitMeetingStyle.ApplyRounded(
            microphoneBadgeImage,
            LiveKitMeetingStyle.Success,
            true);
        microphoneBadgeImage.raycastTarget = false;

        microphoneBadgeText = CreateText(
            "Microphone Label",
            microphoneBadgeObject.transform,
            15f,
            FontStyles.Bold);
        Stretch(microphoneBadgeText.rectTransform, 5f, 3f, 5f, 3f);
        microphoneBadgeText.alignment = TextAlignmentOptions.Center;
        microphoneBadgeText.color = Color.white;
        SetMicrophoneMuted(false);

        reactionText = CreateText("Reaction", transform, 38f, FontStyles.Bold);
        Stretch(reactionText.rectTransform, 16f, 48f, 16f, 16f);
        reactionText.alignment = TextAlignmentOptions.Center;
        reactionText.color = Color.white;
        reactionText.textWrappingMode = TextWrappingModes.NoWrap;
        reactionGroup = reactionText.gameObject.AddComponent<CanvasGroup>();
        reactionGroup.alpha = 0f;
        reactionText.gameObject.SetActive(false);

        SetDisplayName(displayName);
    }

    public void SetDisplayName(string displayName)
    {
        string safeName = string.IsNullOrWhiteSpace(displayName) ? "Guest" : displayName.Trim();
        nameText.text = safeName;
        placeholderText.text = GetInitials(safeName);
        gameObject.name = $"Participant: {safeName}";
    }

    public void BindLocalTexture(Texture texture)
    {
        pendingRemoteTrack = null;
        ReleaseRemoteStream();
        ApplyTexture(texture, true);
    }

    public void BindRemoteTrack(RemoteVideoTrack track)
    {
        ReleaseRemoteStream();
        ApplyTexture(null, false);
        pendingRemoteTrack = track;
        TryStartRemoteStream();
    }

    private void OnEnable()
    {
        // LiveKit can report TrackSubscribed while the meeting grid is still
        // hidden during Connect(). Start the stream when SetConnected(true)
        // activates this tile instead of losing that subscription.
        TryStartRemoteStream();
    }

    private void TryStartRemoteStream()
    {
        if (pendingRemoteTrack == null ||
            remoteVideoStream != null ||
            !isActiveAndEnabled)
        {
            return;
        }

        remoteVideoStream = new VideoStream(pendingRemoteTrack);
        remoteVideoStream.TextureReceived += HandleRemoteTexture;
        remoteVideoStream.Start();
        remoteVideoCoroutine = StartCoroutine(remoteVideoStream.Update());
    }

    public void ClearVideo()
    {
        pendingRemoteTrack = null;
        ReleaseRemoteStream();
        ApplyTexture(null, false);
    }

    public void SetVideoVisible(bool visible)
    {
        videoVisible = visible;
        bool showVideo = videoVisible && currentTexture != null;
        videoImage.enabled = showVideo;
        placeholderText.gameObject.SetActive(!showVideo);
    }

    public void SetMicrophoneMuted(bool muted)
    {
        if (microphoneBadgeText == null || microphoneBadgeImage == null) return;
        microphoneBadgeText.text = muted ? "MIC OFF" : "MIC ON";
        LiveKitMeetingStyle.ApplyRounded(
            microphoneBadgeImage,
            muted
                ? new Color32(239, 68, 68, 235)
                : new Color32(34, 197, 94, 235),
            true);
    }

    public void PlayReaction(JJReactionType reaction)
    {
        if (reactionCoroutine != null)
        {
            StopCoroutine(reactionCoroutine);
        }
        reactionCoroutine = StartCoroutine(PlayReactionRoutine(reaction));
    }

    private IEnumerator PlayReactionRoutine(JJReactionType reaction)
    {
        reactionText.text = reaction == JJReactionType.CLAP
            ? "CLAP!"
            : "LIKE!";
        reactionText.gameObject.SetActive(true);
        reactionGroup.alpha = 1f;

        RectTransform rect = reactionText.rectTransform;
        float elapsed = 0f;
        const float popDuration = 0.22f;
        while (elapsed < popDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / popDuration);
            float scale = Mathf.LerpUnclamped(0.45f, 1.12f, 1f - Mathf.Pow(1f - t, 3f));
            rect.localScale = Vector3.one * scale;
            yield return null;
        }

        rect.localScale = Vector3.one;
        yield return new WaitForSecondsRealtime(1.35f);

        elapsed = 0f;
        const float fadeDuration = 0.45f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            reactionGroup.alpha = 1f - Mathf.Clamp01(elapsed / fadeDuration);
            yield return null;
        }

        reactionGroup.alpha = 0f;
        reactionText.gameObject.SetActive(false);
        reactionCoroutine = null;
    }

    private void HandleRemoteTexture(Texture texture)
    {
        ApplyTexture(texture, false);
    }

    private void ApplyTexture(Texture texture, bool local)
    {
        currentTexture = texture;
        videoImage.texture = texture;
        if (texture != null && texture.height > 0)
        {
            videoAspect.aspectRatio = Mathf.Max(0.01f, (float)texture.width / texture.height);
        }

        bool flip = local && texture is WebCamTexture webCam && webCam.videoVerticallyMirrored;
        videoImage.uvRect = flip
            ? new Rect(0f, 1f, 1f, -1f)
            : new Rect(0f, 0f, 1f, 1f);
        SetVideoVisible(videoVisible);
    }

    private void ReleaseRemoteStream()
    {
        if (remoteVideoCoroutine != null)
        {
            StopCoroutine(remoteVideoCoroutine);
            remoteVideoCoroutine = null;
        }
        if (remoteVideoStream != null)
        {
            remoteVideoStream.TextureReceived -= HandleRemoteTexture;
            remoteVideoStream.Stop();
            remoteVideoStream.Dispose();
            remoteVideoStream = null;
        }
    }

    private void OnDestroy()
    {
        pendingRemoteTrack = null;
        ReleaseRemoteStream();
    }

    private static GameObject CreateRect(string name, Transform parent)
    {
        var result = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
        result.transform.SetParent(parent, false);
        return result;
    }

    private static TMP_Text CreateText(
        string name,
        Transform parent,
        float fontSize,
        FontStyles style)
    {
        GameObject textObject = CreateRect(name, parent);
        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.raycastTarget = false;
        text.enableAutoSizing = true;
        text.fontSizeMin = Mathf.Max(10f, fontSize * 0.55f);
        text.fontSizeMax = fontSize;
        return text;
    }

    private static void Stretch(
        RectTransform rect,
        float left,
        float bottom,
        float right,
        float top)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static string GetInitials(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "?";
        string[] words = value.Trim().Split(' ');
        if (words.Length > 1 && words[0].Length > 0 && words[1].Length > 0)
        {
            return (words[0][0].ToString() + words[1][0]).ToUpperInvariant();
        }
        return value.Length > 2 ? value.Substring(0, 2).ToUpperInvariant() : value.ToUpperInvariant();
    }
}
