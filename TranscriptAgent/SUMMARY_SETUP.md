# Cloud meeting summaries

The IPEC Agent uses Yating for speech recognition and LiveKit Inference
(`google/gemini-3.1-flash-lite`) for summaries and technical questions.
The Gemini model is billed through LiveKit; a Google API key is not required.

## Deployment settings

Configure these environment variables on your own LiveKit Agent:

```
AI_ENABLED=true
AI_PROVIDER=livekit
AI_MODEL=google/gemini-3.1-flash-lite
```

Keep your Yating settings for speech recognition. LiveKit Cloud supplies the
LiveKit credentials to a deployed Agent. Local execution requires your own
LIVEKIT_URL, LIVEKIT_API_KEY and LIVEKIT_API_SECRET in a local .env file.
Never put provider secrets in Unity or commit them to Git.

## Unity workflow

1. Join the meeting and open **AI Collaboration**.
2. Click **Start Recording** and speak. Confirm that transcript text appears.
3. The recording initiator can click **Summarize** for the conversation so far.
4. **View Summary** displays the last summary; **Ask AI** uses the dialogue
   and available technical documents for a question.
   Any participant can also ask without recording: the Agent then answers from the
   technical documents and general knowledge, keeping each participant's last six
   questions for follow-ups. Summaries still require the recording initiator.
5. **Export Results** saves the currently displayed result as UTF-8 TXT and JSON
   in `Application.persistentDataPath/Collaboration`. The UI shows the full path.
6. Stopping recording also requests a final summary when AI is enabled and
   transcript text is available. Keep the participant connected until it arrives.

Summaries contain the problem, reported actions, current status, action items,
and suggested next steps. Summaries and their headings are English, including
when the source transcript is Chinese. Technical answers remain Traditional
Chinese; UI controls are English. The summary model is called on a request or on recording stop,
not for every incoming video frame. The separate Agent hosting, STT and RTC
services retain their own usage charges.

## Limits and diagnostics

This version summarizes retained recent dialogue: up to 500 segments and a
22,000-character input budget. A result explicitly reports omitted segments;
it must not be represented as a full-session summary when omissions occurred.
The original transcript export is separate from the AI summary.

The model has a 65-second deadline. Unity reports a 90-second response wait
but still accepts a late result until another request replaces it or the
recording session changes. Missing configuration and model failures are shown
as errors. Agent logs record request IDs, duration and exception type without
logging credentials or dialogue.

## Verification

`tests/smoke_livekit_summary.py` makes one paid inference request using fictional
Chinese dialogue, then validates the service result, chunk reconstruction and
SHA-256. Unit tests in `tests/test_collaboration.py` do not call a paid model.

## Photo to Text (text recognition + translation)

1. Join the meeting with your camera on and open **AI Collaboration**.
2. Point the camera at the text and click **Photo to Text**. Recording is not required.
3. The Agent sends the photo to the same Gemini model. Chinese text is translated
   into English, and English text into Traditional Chinese.
4. **Read Original** / **Read Translation** play the text on this device with the
   Android system voice (install a Chinese voice in Android settings if needed).
   Read aloud is not available in the Unity Editor.
5. **Export Results** saves the original and the translation as UTF-8 TXT and JSON.

The photo goes only to the Agent (LiveKit byte stream topic
`jorjin.collaboration.image.v1`, JPEG, at most 1600 px and 4 MB) and is not stored.
Each photo is one model request. `tests/smoke_livekit_ocr.py` is an opt-in paid check
(it needs Pillow and the Windows Microsoft JhengHei font to draw its test signs).

## Action items and calendar (整理待辦)

Ported from the Conversation Action Agent (`GeminiAgentBridge.cs` + `agent_process.py`),
but it runs inside this Cloud Agent with the same LiveKit Gemini model, so it also works
on Android and needs no Google API key on the device.

- **整理待辦** with typed text (e.g. 「小美負責做簡報，星期三以前完成」) works for anyone, no recording needed.
- Pressed blank by the recording initiator, it reads the transcript since the last check.
  Stopping a recording also runs it once after the summary.
- Pressed blank by anyone else, it just shows the current list (no model request).
- Changes such as 「延到下星期一」 update the existing task instead of adding a duplicate.
- Tasks with a date form the calendar. **Export Results** writes TXT, JSON and an `.ics`
  file for Google Calendar / Outlook / phone calendars.

The list is shared by everyone in the room and kept in the Agent's memory, so it is
cleared when the room closes; export it before everyone leaves. Dates are resolved in
UTC+8 (override with `AI_UTC_OFFSET_HOURS`). Logic: `src/actions.py`.
