using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jorjin.Streaming;
using UnityEngine;
using UnityEngine.UI;

/// <summary>English collaboration UI. Provider credentials exist only on the Agent.</summary>
public sealed class LiveKitCollaborationPanel : MonoBehaviour
{
    public sealed class VideoSource
    {
        public string Key, Identity;
        public bool IsLocal, IsScreenShare;
        public Texture Texture;
        public Rect Uv = new Rect(0, 0, 1, 1);
    }
    public sealed class MeetingState
    {
        public string Room, LocalIdentity, Transcript, LocalRole;
        public Dictionary<string, string> Roles = new();
        public bool Recording, AgentReady, Pending, MicrophoneMuted;
    }
    private Action<List<VideoSource>> getSources;
    private Func<MeetingState> getMeeting;
    private Action toggleMicrophone, toggleRecording;
    private readonly List<VideoSource> sources = new();
    private readonly HashSet<string> glassesSources = new();
    private string selectedSource, answerJson, summaryJson, tasksJson;
    private RawImage mainVideo, peerVideo;
    private AspectRatioFitter mainAspect, peerAspect;
    private Text videoTitle, videoStatus, peerTitle, peerStatus, meetingStatus, transcriptText, recordingLabel, microphoneLabel;
    private Button record, microphone, capture, markGlasses, sourceButton, openButton;
    private RectTransform leftColumn, rightColumn;
    private CanvasScaler workspaceScaler;
    private string lastTranscript;
    private bool wasConnected, userPickedSource;

    public void ConfigureMeeting(Action<List<VideoSource>> readSources, Func<MeetingState> readMeeting,
        Action microphoneAction, Action recordingAction)
    { getSources = readSources; getMeeting = readMeeting; toggleMicrophone = microphoneAction; toggleRecording = recordingAction; }

    private Func<string, string, string, int> send;
    private Func<string> session;
    private Func<bool> connected;
    private readonly ConcurrentQueue<(string sender, string json)> inbox = new();
    private readonly CollaborationResultReceiver receiver = new();
    private GameObject canvasObject, panel;
    private Text output, status;
    private InputField question;
    private Button ask, summary, tasksButton, export, photo, readOriginal, readTranslation, stopReading;
    private Font font;
    private string sessionId, pendingId, latestJson, latestText, latestKind;
    private float requestStarted;
    private ScrollRect scroll;
    private bool isBusy, capturingPhoto;
    private Func<byte[], string, Action<bool>, int> sendPhoto;
    private CollaborationResult photoResult;
    private DeviceTextToSpeech speech;
    private LiveKitCalendarView calendarView;
    private const int MaxPhotoSide = 1600;

    public void Configure(Func<string, string, string, int> sender, Func<string> getSession, Func<bool> isConnected)
    {
        send = sender; session = getSession; connected = isConnected;
        if (canvasObject == null) Build();
    }

    /// <summary>Photo text recognition: (jpeg, requestId, onSent) returns 0, or -2 without an Agent.</summary>
    public void ConfigurePhoto(Func<byte[], string, Action<bool>, int> photoSender) { sendPhoto = photoSender; }

    public void Receive(string sender, string json)
    {
        if (json != null && json.Length <= 15000 && inbox.Count < 40) inbox.Enqueue((sender, json));
    }

    private void Update()
    {
        if (session == null) return;
        RefreshMeeting();
        string current = connected() ? session() ?? "" : "";
        if (current != sessionId)
        {
            sessionId = current; pendingId = null; isBusy = false; receiver.Clear();
            latestJson = latestText = answerJson = summaryJson = null; // tasksJson stays: tasks belong to the room
            output.text = "Type a question and press 詢問 AI, or take a photo. Summaries need recording.";
            status.text = "Anyone can ask. While you record, answers also use the conversation.";
        }
        while (inbox.TryDequeue(out var item))
        {
            try
            {
                var packet = JsonUtility.FromJson<CollaborationPacket>(item.json);
                if (packet == null) continue;
                // Replies to this panel's own request are matched by its unique request ID,
                // because direct questions and photos do not need a recording session.
                // Automatic stop-recording summaries must belong to the current session.
                bool mine = pendingId != null && packet.request_id == pendingId;
                bool automatic = packet.request_id != null && packet.request_id.StartsWith("auto_", StringComparison.Ordinal);
                if (!mine && !(automatic && !string.IsNullOrEmpty(sessionId) && packet.session_id == sessionId)) continue;
                if (packet.type == "busy" || packet.type == "progress")
                { isBusy = true; requestStarted = Time.realtimeSinceStartup; status.text = EnglishAgentMessage(packet.message); }
                else if (packet.type == "error")
                { isBusy = false; pendingId = null; status.text = EnglishAgentMessage(packet.message); }
                else if (packet.type == "result_chunk" || packet.type == "result_complete")
                {
                    string json = receiver.Accept(item.sender, packet);
                    if (json == null) continue;
                    var result = JsonUtility.FromJson<CollaborationResult>(json);
                    if (result == null || string.IsNullOrEmpty(result.display_text)) throw new InvalidDataException();
                    if (result.kind == "ocr") photoResult = result;
                    else if (result.kind == "tasks") { tasksJson = json; if (calendarView.Visible) calendarView.Refresh(result); }
                    else if (result.kind == "summary") summaryJson = json; else answerJson = json;
                    ShowResult(json);
                    status.text = result.kind == "ocr" ? "Text ready. Export Results saves it as TXT; Read plays it on this device." :
                        result.kind == "tasks" ? "Action items updated. Press 查看待辦 to open the calendar; Export Results also saves an .ics file." :
                        result.kind == "summary" ? "Summary ready. You can export it now." : "Advice ready. Review the suggestions before taking action.";
                    isBusy = false; pendingId = null;
                    Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition = 1;
                }
            }
            catch (Exception)
            { isBusy = false; pendingId = null; receiver.Clear(); status.text = "Incomplete result received. Ask again or generate a new summary."; }
        }
        if (isBusy && Time.realtimeSinceStartup - requestStarted > 90)
        { isBusy = false; status.text = "No response for 90 seconds. A late result can still arrive; retry if needed."; }
        ask.interactable = connected() && !isBusy;
        summary.interactable = connected() && !string.IsNullOrEmpty(sessionId) && !isBusy;
        tasksButton.interactable = connected() && !isBusy;
        photo.interactable = connected() && sendPhoto != null && !isBusy && !capturingPhoto;
        readOriginal.interactable = readTranslation.interactable = stopReading.interactable = photoResult != null;
        export.interactable = !string.IsNullOrEmpty(latestJson);
    }

    private IEnumerator RecognizePhoto()
    {
        if (isBusy || capturingPhoto) yield break;
        var camera = sources.Find(v => v.IsLocal && !v.IsScreenShare && v.Texture != null);
        if (camera == null) { status.text = "Turn on your camera first, then point it at the text."; yield break; }
        capturingPhoto = true;
        yield return new WaitForEndOfFrame();
        byte[] jpeg = null;
        try { jpeg = EncodePhoto(camera.Texture, camera.Uv); }
        catch (Exception) { }
        capturingPhoto = false;
        if (jpeg == null) { status.text = "Could not capture the camera image. Try again."; yield break; }

        receiver.Clear();
        string requestId = Guid.NewGuid().ToString("N");
        pendingId = requestId;
        isBusy = true; requestStarted = Time.realtimeSinceStartup;
        status.text = "Sending photo...";
        int result = sendPhoto(jpeg, requestId, sent =>
        {
            if (pendingId != requestId) return;
            if (sent) status.text = "Photo sent. Reading text...";
            else { isBusy = false; pendingId = null; status.text = "Could not send the photo. Check the connection and try again."; }
        });
        if (result != 0)
        {
            isBusy = false; pendingId = null;
            status.text = result == -2 ? "The AI service is not in this room yet. Wait until it joins, then try again."
                                       : "Join a room first, then take a photo.";
        }
    }

    /// <summary>Copies the visible camera frame (honouring flips) into a JPEG no larger than MaxPhotoSide.</summary>
    private static byte[] EncodePhoto(Texture source, Rect uv)
    {
        float scale = Mathf.Min(1f, (float)MaxPhotoSide / Mathf.Max(source.width, source.height));
        int width = Mathf.Max(1, Mathf.RoundToInt(source.width * Mathf.Abs(uv.width) * scale));
        int height = Mathf.Max(1, Mathf.RoundToInt(source.height * Mathf.Abs(uv.height) * scale));
        RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        Texture2D frame = null;
        try
        {
            Graphics.Blit(source, target, new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
            RenderTexture.active = target;
            frame = new Texture2D(width, height, TextureFormat.RGB24, false);
            frame.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            frame.Apply();
            return frame.EncodeToJPG(85);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (frame != null) Destroy(frame);
        }
    }

    private void ReadPhotoText(bool original)
    {
        if (photoResult == null) return;
        string text = original ? photoResult.original_text : photoResult.translated_text;
        string language = original ? photoResult.source_language : photoResult.target_language;
        speech ??= new DeviceTextToSpeech();
        string problem = speech.Speak(text, language == "zh");
        status.text = problem ?? (original ? "Reading the original text..." : "Reading the translation...");
    }

    private void Request(string action)
    {
        if (isBusy) return;
        string text = action == "ask" || action == "tasks" ? question.text?.Trim() ?? "" : "";
        if (action == "ask" && text.Length == 0 && string.IsNullOrEmpty(sessionId))
        { status.text = "Type a question first. Leaving it blank analyzes the conversation only while recording."; return; }
        receiver.Clear();
        pendingId = Guid.NewGuid().ToString("N");
        int result = send(action, pendingId, text);
        if (result != 0)
        {
            pendingId = null;
            status.text = action == "summary" ? "Join a room and start recording first." : "Join a room first, then try again.";
            return;
        }
        isBusy = true; requestStarted = Time.realtimeSinceStartup;
        status.text = "Request sent. Waiting for AI response...";
    }

    private void Export()
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "Collaboration");
            Directory.CreateDirectory(dir);
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                (latestKind == "summary" ? "summary" : latestKind == "ocr" ? "photo_text" : latestKind == "tasks" ? "tasks" : "answer") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            File.WriteAllText(Path.Combine(dir, name + ".json"), latestJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, name + ".txt"), output.text, new UTF8Encoding(false));
            if (latestKind == "tasks")
            {
                var result = JsonUtility.FromJson<CollaborationResult>(latestJson);
                File.WriteAllText(Path.Combine(dir, name + ".ics"), CalendarFile.Build(result?.calendar_events), new UTF8Encoding(false));
            }
            status.text = "Saved to: " + dir;
        }
        catch (Exception) { status.text = "Could not save files. Check storage space and write permissions."; }
    }

    private void ShowResult(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        var result = JsonUtility.FromJson<CollaborationResult>(json);
        latestJson = json; latestText = result.display_text; latestKind = result.kind;
        output.text = (result.kind == "summary" ? "Session Summary\n\n" : result.kind == "ocr" ? "Photo Text\n\n" : result.kind == "tasks" ? "Action Items\n\n" : "Technical Advice\n\n") +
            (string.IsNullOrEmpty(result.question) ? "" : "Question: " + result.question + "\n\n") + latestText;
        Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition = 1;
    }

    public void SetWorkspaceVisible(bool visible)
    {
        panel.SetActive(visible); openButton.gameObject.SetActive(!visible);
        if (visible) RefreshMeeting();
    }

    private void SelectNextSource()
    {
        if (sources.Count == 0) return;
        int index = sources.FindIndex(s => s.Key == selectedSource);
        selectedSource = sources[(index + 1) % sources.Count].Key;
        userPickedSource = true;
        RefreshMeeting();
    }

    private void ToggleGlassesSource()
    {
        if (string.IsNullOrEmpty(selectedSource)) return;
        if (!glassesSources.Add(selectedSource)) glassesSources.Remove(selectedSource);
        RefreshMeeting();
    }

    private void RefreshMeeting()
    {
        if (mainVideo == null) return;
        bool portrait = Screen.height > Screen.width;
        calendarView?.SetPortrait(portrait);
        workspaceScaler.referenceResolution = portrait ? new Vector2(900, 1600) : new Vector2(1600, 900);
        if (portrait)
        {
            Stretch(leftColumn, new Vector2(0, .52f), Vector2.one, new Vector2(20, 10), new Vector2(-20, -109));
            Stretch(rightColumn, Vector2.zero, new Vector2(1, .52f), new Vector2(20, 65), new Vector2(-20, -10));
        }
        else
        {
            Stretch(leftColumn, Vector2.zero, new Vector2(.60f, 1), new Vector2(20, 65), new Vector2(-10, -109));
            Stretch(rightColumn, new Vector2(.60f, 0), Vector2.one, new Vector2(10, 65), new Vector2(-20, -109));
        }
        bool online = connected != null && connected();
        var state = getMeeting?.Invoke() ?? new MeetingState();
        if (wasConnected && !online) { selectedSource = null; userPickedSource = false; glassesSources.Clear(); }
        wasConnected = online;
        sources.Clear();
        if (online) getSources?.Invoke(sources);
        // Keep the explicitly selected source when its track disappears. Never silently
        // relabel another person's video as the glasses feed.
        bool IsField(VideoSource v) => v != null && state.Roles != null &&
            state.Roles.TryGetValue(v.Identity ?? "", out string role) && role == "field";
        // An expert sees the field side's camera first, until they pick a source themselves.
        var fieldVideo = state.LocalRole == "expert"
            ? sources.Find(v => !v.IsLocal && !v.IsScreenShare && IsField(v) && v.Texture != null) : null;
        if (!userPickedSource && fieldVideo != null) selectedSource = fieldVideo.Key;
        if (selectedSource == null && sources.Count > 0)
        {
            var preferred = sources.Find(v => !v.IsLocal && !v.IsScreenShare && v.Texture != null)
                ?? sources.Find(v => !v.IsLocal && !v.IsScreenShare) ?? sources[0];
            selectedSource = preferred.Key;
        }
        var main = sources.Find(v => v.Key == selectedSource);
        var peer = sources.Find(v => v.Identity != main?.Identity && v.Texture != null)
            ?? sources.Find(v => v.Identity != main?.Identity);
        bool glasses = main != null && glassesSources.Contains(main.Key);
        bool fieldSide = main != null && !main.IsScreenShare && IsField(main);
        videoTitle.text = glasses ? "AR Glasses Video (Selected)" : fieldSide ? "場域端畫面" : "Live Video / Select Glasses";
        videoStatus.text = main == null ? "Source unavailable. Connect or switch sources." :
            (main.IsLocal ? "Local preview" : "LiveKit remote feed") + ": " + Compact(main.Identity, 36) +
            (main.IsScreenShare ? " / Screen share" : " / Camera") +
            (main.Texture == null ? " / Waiting for video or camera off" : "");
        peerTitle.text = peer == null ? "Other Participant" : (peer.IsLocal ? "Local Participant" : "Remote Participant") + ": " + Compact(peer.Identity, 20);
        peerStatus.text = peer == null ? "Waiting for another participant" : peer.Texture == null ? "Participant joined. No video yet." : "";
        BindVideo(mainVideo, mainAspect, main);
        BindVideo(peerVideo, peerAspect, peer);
        var identities = new HashSet<string>(); foreach (var v in sources) identities.Add(v.Identity);
        meetingStatus.text = (online ? "LiveKit connected" : "LiveKit disconnected") + "    Room: " + Compact(state.Room, 24) +
            "    Participants: " + identities.Count + "    身分：" + (state.LocalRole == "expert" ? "專家端" : "場域端") + "    " + (state.Recording ? "Recording" : state.AgentReady ? "Transcript service ready" : "Waiting for transcript service");
        recordingLabel.text = state.Pending ? "處理中…" : state.Recording ? "停止錄音" : "開始錄音";
        microphoneLabel.text = state.MicrophoneMuted ? "麥克風：關" : "麥克風：開";
        record.interactable = online && state.AgentReady && !state.Pending;
        microphone.interactable = online;
        sourceButton.interactable = sources.Count > 0;
        markGlasses.interactable = main != null && !main.IsScreenShare;
        markGlasses.GetComponentInChildren<Text>().text = glasses ? "取消標記" : "標記眼鏡";
        string transcript = online ? state.Transcript : "";
        if (transcript != lastTranscript)
        {
            lastTranscript = transcript;
            transcriptText.text = string.IsNullOrWhiteSpace(transcript) ? "Live conversation transcripts appear here once recording starts." : transcript;
        }
    }

    private static string Compact(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        return value.Length > limit ? value.Substring(0, limit) + "…" : value;
    }

    private static void BindVideo(RawImage image, AspectRatioFitter aspect, VideoSource source)
    {
        image.texture = source?.Texture;
        image.enabled = image.texture != null;
        if (image.texture == null) return;
        image.uvRect = source.Uv;
        aspect.aspectRatio = image.texture.height > 0 ? (float)image.texture.width / image.texture.height : 16f / 9f;
    }

    private IEnumerator CaptureWorkspace()
    {
        capture.interactable = false;
        yield return new WaitForEndOfFrame();
        Texture2D shot = null;
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "Collaboration"); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "collaboration_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            shot = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(path, shot.EncodeToPNG());
            status.text = "Screenshot saved: " + path;
        }
        catch (Exception) { status.text = "Could not save screenshot. Check storage space and permissions."; }
        finally { if (shot != null) Destroy(shot); capture.interactable = true; }
    }

    private void Build()
    {
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft JhengHei", "Noto Sans CJK TC", "Noto Sans CJK SC", "Arial" }, 24);
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        canvasObject = new GameObject("AI Collaboration Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        var canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 500;
        var scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        workspaceScaler = scaler;
        scaler.referenceResolution = new Vector2(1600, 900); scaler.matchWidthOrHeight = .5f;
        openButton = ButtonAt(canvasObject.transform, "AI 協作", Vector2.one, new Vector2(-205, -72), new Vector2(185, 44), () => SetWorkspaceVisible(true));
        panel = new GameObject("Remote Collaboration Workspace", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvasObject.transform, false);
        Stretch(panel.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        panel.GetComponent<Image>().color = new Color(.035f, .055f, .09f, 1);
        Label(panel.transform, "AR Remote Collaboration", 29, new Vector2(22, -15), new Vector2(600, 44));
        capture = ButtonAt(panel.transform, "截圖", Vector2.one, new Vector2(-295, -18), new Vector2(125, 38), () => StartCoroutine(CaptureWorkspace()));
        ButtonAt(panel.transform, "返回通話", Vector2.one, new Vector2(-155, -18), new Vector2(130, 38), () => SetWorkspaceVisible(false));
        meetingStatus = Label(panel.transform, "", 19, new Vector2(24, -65), new Vector2(1500, 30));
        Stretch(meetingStatus.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(24, -103), new Vector2(-24, -65));
        leftColumn = Region(panel.transform, "Live video and conversation", new Vector2(0, 0), new Vector2(.60f, 1), new Vector2(20, 65), new Vector2(-10, -109));
        rightColumn = Region(panel.transform, "AI assistance", new Vector2(.60f, 0), Vector2.one, new Vector2(10, 65), new Vector2(-20, -109));
        videoTitle = Label(leftColumn, "Live Video", 23, Vector2.zero, new Vector2(550, 36));
        Stretch(videoTitle.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(0, -38), new Vector2(-276, 0));
        sourceButton = ButtonAt(leftColumn, "切換畫面", Vector2.one, new Vector2(-264, 0), new Vector2(120, 34), SelectNextSource);
        markGlasses = ButtonAt(leftColumn, "標記眼鏡", Vector2.one, new Vector2(-132, 0), new Vector2(132, 34), ToggleGlassesSource);
        videoStatus = Label(leftColumn, "", 18, new Vector2(0, -44), new Vector2(920, 32));
        Stretch(videoStatus.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(0, -79), new Vector2(0, -44));
        var videoArea = Region(leftColumn, "Main video", new Vector2(0, .36f), Vector2.one, new Vector2(0, 10), new Vector2(0, -85), true);
        Video(videoArea, out mainVideo, out mainAspect);
        var transcriptArea = Region(leftColumn, "Transcript", Vector2.zero, new Vector2(.65f, .34f), Vector2.zero, new Vector2(-8, 0));
        Label(transcriptArea, "Conversation / Recent Transcript", 22, Vector2.zero, new Vector2(550, 34));
        ScrollRect transcriptScroll;
        transcriptText = ScrollingText(transcriptArea, "Transcript text", 41, 0, out transcriptScroll, 20);
        var peerArea = Region(leftColumn, "Other participant", new Vector2(.65f, 0), new Vector2(1, .34f), new Vector2(8, 0), Vector2.zero);
        peerTitle = Label(peerArea, "Other Participant", 18, Vector2.zero, new Vector2(350, 46));
        var peerFrame = Region(peerArea, "Peer video", Vector2.zero, Vector2.one, new Vector2(0, 28), new Vector2(0, -50), true);
        Video(peerFrame, out peerVideo, out peerAspect);
        peerStatus = Label(peerArea, "", 17, Vector2.zero, Vector2.zero);
        Stretch(peerStatus.rectTransform, Vector2.zero, new Vector2(1, 0), Vector2.zero, new Vector2(0, 27));
        Label(rightColumn, "AI Technical Assistance", 25, Vector2.zero, new Vector2(560, 36));
        Label(rightColumn, "Ask anytime. While recording, answers also use the conversation.", 18, new Vector2(0, -42), new Vector2(640, 32));
        var input = new GameObject("Question", typeof(RectTransform), typeof(Image), typeof(InputField)); input.transform.SetParent(rightColumn, false);
        Stretch(input.GetComponent<RectTransform>(), new Vector2(0, 1), Vector2.one, new Vector2(0, -160), new Vector2(0, -82));
        input.GetComponent<Image>().color = new Color(.14f, .20f, .28f);
        question = input.GetComponent<InputField>(); question.characterLimit = 2000; question.lineType = InputField.LineType.MultiLineNewline;
        var entry = Label(input.transform, "", 21, Vector2.zero, Vector2.zero);
        Stretch(entry.rectTransform, Vector2.zero, Vector2.one, new Vector2(12, 8), new Vector2(-12, -8)); question.textComponent = entry;
        var hint = Label(input.transform, "Type a question, or text like \"小美 does the slides by Wednesday\" then press 整理待辦.", 19, Vector2.zero, Vector2.zero);
        Stretch(hint.rectTransform, Vector2.zero, Vector2.one, new Vector2(12, 8), new Vector2(-12, -8)); hint.color = new Color(.65f,.73f,.81f); question.placeholder = hint;
        ask = ButtonAt(rightColumn, "詢問 AI", new Vector2(0, 1), new Vector2(0, -172), new Vector2(140, 38), () => Request("ask"));
        summary = ButtonAt(rightColumn, "產生摘要", new Vector2(0, 1), new Vector2(150, -172), new Vector2(140, 38), () => Request("summary"));
        tasksButton = ButtonAt(rightColumn, "整理待辦", new Vector2(0, 1), new Vector2(300, -172), new Vector2(140, 38), () => Request("tasks"));
        export = ButtonAt(rightColumn, "匯出結果", new Vector2(0, 1), new Vector2(450, -172), new Vector2(140, 38), Export);
        ButtonAt(rightColumn, "查看建議", new Vector2(0, 1), new Vector2(0, -223), new Vector2(140, 34), () => ShowResult(answerJson));
        ButtonAt(rightColumn, "查看摘要", new Vector2(0, 1), new Vector2(150, -223), new Vector2(140, 34), () => ShowResult(summaryJson));
        ButtonAt(rightColumn, "查看待辦", new Vector2(0, 1), new Vector2(300, -223), new Vector2(140, 34), () => OpenCalendar());
        photo = ButtonAt(rightColumn, "拍照辨識", new Vector2(0, 1), new Vector2(450, -223), new Vector2(140, 34), () => StartCoroutine(RecognizePhoto()));
        readOriginal = ButtonAt(rightColumn, "朗讀原文", new Vector2(0, 1), new Vector2(0, -267), new Vector2(140, 34), () => ReadPhotoText(true));
        readTranslation = ButtonAt(rightColumn, "朗讀翻譯", new Vector2(0, 1), new Vector2(150, -267), new Vector2(140, 34), () => ReadPhotoText(false));
        stopReading = ButtonAt(rightColumn, "停止朗讀", new Vector2(0, 1), new Vector2(300, -267), new Vector2(140, 34), () => speech?.Stop());
        output = ScrollingText(rightColumn, "Results", 314, 66, out scroll, 21);
        status = Label(rightColumn, "Anyone can ask. While you record, answers also use the conversation.", 17, Vector2.zero, Vector2.zero);
        Stretch(status.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(0, 2), new Vector2(0, 60));
        microphone = ButtonAt(panel.transform, "麥克風：開", Vector2.zero, new Vector2(22, 51), new Vector2(160, 38), () => toggleMicrophone?.Invoke());
        microphoneLabel = microphone.GetComponentInChildren<Text>();
        record = ButtonAt(panel.transform, "開始錄音", Vector2.zero, new Vector2(196, 51), new Vector2(155, 38), () => toggleRecording?.Invoke());
        recordingLabel = record.GetComponentInChildren<Text>();
        var foot = Label(panel.transform, "Select the glasses video source, then click Mark Glasses.", 18, Vector2.zero, Vector2.zero);
        Stretch(foot.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(374, 12), new Vector2(-22, 46));
        output.text = "AI answers, summaries and photo text appear here.";
        calendarView = new LiveKitCalendarView(panel.transform, font, () => calendarView.Hide());
        SetWorkspaceVisible(false);
    }

    private void OpenCalendar()
    {
        // Keep the text list available too: it is what Export Results saves.
        if (!string.IsNullOrEmpty(tasksJson)) ShowResult(tasksJson);
        calendarView.Show(string.IsNullOrEmpty(tasksJson) ? null : JsonUtility.FromJson<CollaborationResult>(tasksJson));
    }

    private RectTransform Region(Transform parent, string name, Vector2 min, Vector2 max, Vector2 insetMin, Vector2 insetMax, bool background = false)
    {
        var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); Stretch(rect, min, max, insetMin, insetMax);
        if (background) { var img = go.AddComponent<Image>(); img.color = new Color(.075f, .105f, .15f); img.raycastTarget = false; go.AddComponent<RectMask2D>(); }
        return rect;
    }

    private void Video(Transform parent, out RawImage image, out AspectRatioFitter aspect)
    {
        var go = new GameObject("Live texture", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter)); go.transform.SetParent(parent, false);
        image = go.GetComponent<RawImage>(); image.raycastTarget = false; image.enabled = false;
        aspect = go.GetComponent<AspectRatioFitter>(); aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent; aspect.aspectRatio = 16f / 9f;
    }

    private Text ScrollingText(Transform parent, string name, float top, float bottom, out ScrollRect scrollView, int size)
    {
        var view = Region(parent, name, Vector2.zero, Vector2.one, new Vector2(0, bottom), new Vector2(0, -top), true);
        view.GetComponent<Image>().raycastTarget = true;
        scrollView = view.gameObject.AddComponent<ScrollRect>();
        var viewport = Region(view, "Viewport", Vector2.zero, Vector2.one, new Vector2(12, 8), new Vector2(-12, -8)); viewport.gameObject.AddComponent<RectMask2D>();
        var text = Label(viewport, "", size, Vector2.zero, Vector2.zero);
        Stretch(text.rectTransform, new Vector2(0, 1), Vector2.one, Vector2.zero, Vector2.zero); text.rectTransform.pivot = new Vector2(.5f, 1);
        text.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scrollView.viewport = viewport; scrollView.content = text.rectTransform; scrollView.horizontal = false;
        scrollView.movementType = ScrollRect.MovementType.Clamped; scrollView.scrollSensitivity = 28;
        return text;
    }

    private static string EnglishAgentMessage(string message)
    {
        switch (message)
        {
            case "正在整理對話與技術資料…": return "Preparing conversation and technical context...";
            case "請由本次記錄的發起者操作 AI 協作。": return "Only the recording initiator can generate summaries.";
            case "請輸入問題。": return "Type a question first.";
            case "尚未有新的逐字稿可以整理待辦。": return "No new transcript since the last check. Type the task text, or keep recording.";
            case "問題過長，請限制在 2000 字以內。": return "Keep your question within 2,000 characters.";
            case "AI 正在處理上一個請求，請稍候。": return "AI is processing the previous request. Please wait.";
            case "請稍候再提出下一個請求。": return "Please wait before sending another request.";
            case "尚未收到逐字稿，請先開始記錄並說明問題。": return "No transcript received. Start recording and describe the issue.";
            case "AI 尚未設定。請在服務端設定模型服務後再試。": return "AI is not configured. Configure the model service on the server.";
            case "模型回覆過長，請縮小問題範圍。":
            case "AI 結果過長，請縮小問題範圍。": return "The AI result is too long. Narrow the scope of your question.";
            case "模型回覆未完成，請縮小問題範圍後再試。": return "The AI response is incomplete. Narrow your question and try again.";
            case "模型回覆格式不正確，請重新提出問題。":
            case "模型回覆格式不正確，請重新嘗試。": return "Invalid AI response format. Please try again.";
            case "模型未提供完整內容，請重新嘗試。": return "AI returned incomplete content. Please try again.";
            case "AI 暫時無法完成請求，請檢查模型服務或技術文件後重試。": return "AI could not complete the request. Check the model service and technical documents, then retry.";
            case "照片過大或是空的，請重新拍攝。": return "The photo is empty or too large. Take it again.";
            case "照片格式不支援，請使用 JPG 或 PNG。": return "Unsupported photo format. Use JPG or PNG.";
            case "照片中沒有可辨識的文字，請靠近一點或讓文字更清楚後再拍。": return "No readable text found. Move closer or make the text clearer, then try again.";
            case "AI 結果過長，請只拍需要辨識的部分。": return "Too much text. Photograph only the part you need.";
        }
        if (!string.IsNullOrEmpty(message) && message.StartsWith("模型服務無法完成請求"))
            return "The model service rejected the request. Check the server configuration.";
        return message;
    }

    private Text Label(Transform parent, string value, int size, Vector2 pos, Vector2 dimensions)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text)); go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1); rect.anchoredPosition = pos; rect.sizeDelta = dimensions;
        var text = go.GetComponent<Text>(); text.font = font; text.fontSize = size; text.color = Color.white; text.text = value;
        text.supportRichText = false; text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow; text.raycastTarget = false;
        return text;
    }

    private Button ButtonAt(Transform parent, string title, Vector2 anchor, Vector2 pos, Vector2 dimensions, UnityEngine.Events.UnityAction click)
    {
        var go = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(Button)); go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = anchor; rect.pivot = new Vector2(0, 1); rect.anchoredPosition = pos; rect.sizeDelta = dimensions;
        go.GetComponent<Image>().color = new Color(.08f, .39f, .52f);
        var button = go.GetComponent<Button>(); button.onClick.AddListener(click);
        var label = Label(go.transform, title, 20, Vector2.zero, dimensions); label.alignment = TextAnchor.MiddleCenter;
        Stretch(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(6, 3), new Vector2(-6, -3));
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = 12;
        label.resizeTextMaxSize = 20;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        return button;
    }

    private static void Stretch(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
    { rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = offsetMin; rect.offsetMax = offsetMax; }

    private void OnDestroy()
    {
        speech?.Dispose(); speech = null;
        if (canvasObject != null) Destroy(canvasObject);
    }
}
