# LiveKit Meeting Transcript and MP3 Agent (Yating ASR)

AI technical assistance, local document retrieval, and meeting summaries are
available as an optional extension. See [AI collaboration setup](AI_COLLABORATION.md).

This room-level LiveKit Agent subscribes to participant microphone tracks and
streams 16 kHz mono PCM audio to Yating's realtime ASR service. Final sentences
are returned to Unity with participant labels. The existing meeting recording
path remains independent: audio is mixed, encoded to MP3 with FFmpeg, and sent
to the Unity device that requested the recording.

The Yating API key is read only from `YATING_API_KEY`. Never put the real key in
source code, `.env.example`, a Unity project, a token, or Git history.

## Required secrets

For local development, copy `.env.example` to `.env` and replace the
placeholders. LiveKit Cloud injects its own `LIVEKIT_*` values automatically;
only the Yating values need to be added as Agent secrets.

| Variable | Default | Purpose |
| --- | --- | --- |
| `YATING_API_KEY` | required | API key issued by Yating Developer Console |
| `YATING_PIPELINE` | `asr-zh-en-std` | Mandarin/English recognition pipeline |
| `YATING_CUSTOM_MODEL_ID` | empty | Optional Yating custom language model ID |

Use `asr-zh-tw-std` instead when the meeting contains Mandarin and Taiwanese.
The short `auth_token` returned by Yating's token endpoint is single-use and is
created by the Agent automatically; do not configure it as a secret.

## LiveKit Cloud deployment

Sign in and select the LiveKit Cloud project used by Unity:

```powershell
lk cloud auth
lk project list
lk project set-default "<project-name>"
```

For an existing deployment, add or update the Yating secret without displaying
the value in source code. The CLI can load it from a local, ignored secrets file:

```text
# yating-secrets.env (do not commit)
YATING_API_KEY=<your-yating-developer-api-key>
YATING_PIPELINE=asr-zh-en-std
```

```powershell
lk agent update-secrets --secrets-file=yating-secrets.env
lk agent deploy
lk agent status
```

For the first deployment, use `lk agent create --secrets-file=yating-secrets.env`
instead of `update-secrets`. Updating secrets restarts the Cloud Agent. After
status becomes `Running`, join a new room from Unity and confirm `Agent Ready`.
The deployment computer can then be shut down.

## Local development

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
Copy-Item .env.example .env
# Edit .env with your own LiveKit and Yating credentials.
.\run_local_agent.ps1
```

Do not run the local worker at the same time as the automatic Cloud worker for
the same LiveKit project unless you intentionally want two Agent instances.

## Recognition path

For each participant audio track, the Agent:

1. Waits until Unity sends the `start` action; room join and `Agent Ready` do
   not open a Yating connection.
2. Requests a one-time Yating WebSocket token using `YATING_API_KEY`.
3. Opens `wss://asr.api.yating.tw/ws/v1/`.
4. Converts LiveKit audio to 16 kHz, mono, signed 16-bit PCM.
5. Sends 2,000-byte binary chunks until Unity sends `stop`.
6. Publishes only messages where `pipe.asr_final` is `true`, preventing partial
   hypotheses from being duplicated in the final TXT.
7. Sends EOF and closes all Yating streams before publishing `complete`.

If the key is invalid, unavailable for ASR, or out of quota, the Agent publishes
an `error` event to Unity and writes the provider response to Cloud logs without
logging the key.

## Unity protocol

- Unity control topic: `jorjin.transcript.control.v1`
- Agent event topic: `jorjin.transcript.event.v1`
- Control actions: `start`, `stop`
- Event types: `ready`, `status`, `segment`, `recording_start`,
  `recording_chunk`, `recording_complete`, `recording_error`, `complete`,
  `error`

Unity writes final segments to UTF-8 TXT and verifies the returned MP3 with
SHA-256. Both files are saved under:

```text
Application.persistentDataPath/Transcripts
```

## Recording configuration

| Variable | Default | Purpose |
| --- | ---: | --- |
| `TRANSCRIPT_RECORDING_ENABLED` | `true` | Enable room MP3 recording |
| `TRANSCRIPT_RECORDING_MAX_MINUTES` | `45` | Maximum captured duration |
| `TRANSCRIPT_RECORDING_BITRATE_KBPS` | `48` | Mono MP3 bitrate |
| `TRANSCRIPT_RECORDING_CHUNK_BYTES` | `9000` | Raw bytes per data packet |
| `TRANSCRIPT_RECORDING_MAX_TRANSFER_MB` | `32` | Maximum MP3 sent to Unity |
| `TRANSCRIPT_RECORDING_CHUNK_DELAY_MS` | `2` | Delay between packets |
| `TRANSCRIPT_RECORDING_FFMPEG_TIMEOUT_SECONDS` | `180` | Encoding timeout |

Yating ASR is a metered external service. In this implementation, Yating audio
usage begins only after `START AGENT` and ends during `STOP & SAVE`. LiveKit
Cloud Agent compute is a separate service and may have its own usage while the
room-level worker is running. Verify both providers' current billing, retention,
privacy, and consent requirements before production use.
