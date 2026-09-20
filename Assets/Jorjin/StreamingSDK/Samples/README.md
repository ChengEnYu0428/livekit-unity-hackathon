# Jorjin Streaming SDK LiveKit Sample

## Scene

Open:

```text
Assets/Jorjin/StreamingSDK/Samples/Scene/StreamingSDKSimpleSample.unity
```

The scene contains the compatibility input fields and the
`JorjinStreamingSimpleSample` component. A LiveKit meeting grid, participant
tiles, microphone status, reactions, and transcript controls are created at
runtime.

## Unity Editor automatic token

For development in Unity Editor:

1. Open `Jorjin > LiveKit Automatic Token Settings`.
2. Enter the LiveKit URL, API key, API secret, room, and participant identity.
3. Enable `Auto Join On Play` and save.
4. Enter Play mode.

The sample creates a short-lived room token and joins automatically. The API
secret is stored only in local Unity Editor preferences and is excluded from
Player builds. Do not put the API secret in an APK or EXE.

If automatic token settings are absent, manual Channel / Token join remains
available.

## Features

- LiveKit-only sample initialization and room join / leave
- Local microphone capture and remote audio playback
- Windows / Editor webcam capture
- Jorjin glasses camera capture through JJSDK on Android
- Local and remote video rendering
- Dynamic multi-participant meeting grid
- Microphone ON / OFF status
- LIKE / CLAP data-channel reactions
- Windows desktop or window screen sharing
- Room-level Transcript Agent control
- Speaker-labelled UTF-8 transcript TXT export
- Mixed room-audio MP3 export with SHA-256 verification
- Optional company/device meeting bootstrap client
- Direct expert and expert-group Session routing
- Equipment QR/deep-link Session routing
- Scheduled meeting list and join
- In-session internal invitations and time-limited guest links

## StreamingSDK API compatibility on LiveKit

When `JJStreamingProfile.Backend` is `LiveKit`, the existing
`JorjinStreamingSDK` entry points continue to be used:

- `Initialize`, `Dispose`, `JoinChannel`, `LeaveChannel`
- `SetVoiceHandler`, `SetVideoHandler`, `SetClientRole`
- `EnableAudio`, `EnableLocalAudio`, `MuteLocalAudioStream`
- `EnableVideo`, `EnableLocalVideo`
- `GetVideoDevices`, `SetVideoDevice`
- `JJVideoSurface.SetForUser`, `SetForRemoteUser`, `SetEnable`
- `StartPreview`, `StopPreview`
- `MuteAllRemoteAudioStreams`, `MuteAllRemoteVideoStreams`
- `SetCameraCapturerConfiguration`
- `SetVideoEncoderConfiguration`, `SetVideoEncoderConfigurationEx`
- `SetRemoteVideoStreamType`
- `RenewToken` (applies the token and automatically reconnects)
- `EnableRemoteAudioFrameObserver`, `DisableRemoteAudioFrameObserver`
- `GetCurrentMonotonicTimeInMs`

The connection, participant, mute, local/remote video, token-expiry, and
local/remote audio-stat callbacks listed in `StreamingEventHandler` are mapped
from LiveKit events. LiveKit identities are strings; legacy `uint uid` values
are deterministic compatibility IDs for the current Session.

On Android, `GetVideoDevices` also exposes the JJSDK glasses camera.
Unity does not provide portable speaker-output enumeration, so the LiveKit
playback-device APIs return unsupported and use the operating-system default.
`SetClientRole(AUDIENCE)` unpublishes local tracks, while actual publish
permission remains controlled by the server-issued LiveKit token.

`SetVideoEncoderConfiguration` controls Unity camera request size/FPS and the
LiveKit maximum bitrate. Camera publications enable simulcast so
`SetRemoteVideoStreamType(uid, LOW/HIGH)` can select a remote layer. Glasses
camera resolution remains the native resolution supplied by its camera SDK;
the LiveKit bitrate/FPS limits still apply.

`SecondBatchApiSample.cs` contains methods ready to connect to Unity Buttons.
The remote PCM callback runs on Unity's audio thread; queue its copied `Pcm16`
data and update UI/GameObjects later on the main thread.

The main `StreamingSDKSimpleSample` scene also creates a second control row at
runtime. It contains Preview, Camera Front/Rear, Remote Audio, Remote Video,
High/Low Quality, 720P/540P/360P Video Profile, and PCM Observer buttons. Gray
buttons are off or unavailable; colored buttons show the active state.

Unity's `AudioSource` always uses the operating-system output device, so
`GetPlaybackDevices` returns an empty list and `SetPlaybackDevice` returns
`-2`. Agora-specific `SetAudioScenario` also returns `-2` on LiveKit instead of
reporting a false success.

## Advanced meeting Sessions

The RTC layer only transports audio/video. Session assignment, authorization,
notifications, schedules and guest links are handled by the application server
in `MeetingBootstrapServer`.

On `JorjinStreamingSimpleSample`:

1. Enable **Advanced Meeting Sessions**.
2. Set **Meeting Session Api Base Url** to the HTTPS service root.
3. Set the Company ID and a rotatable Company Device Key.
4. On an expert/meeting-host client, set **Local Member Id** to a member
   provisioned by the server. Leave it empty on a device client.
5. Never put the LiveKit API secret in Unity.

The included server is a runnable prototype. Its in-memory Sessions,
schedules, and invitations are lost when it restarts. Production deployments
need durable storage, per-user/per-device authentication, rate limiting, and a
notification service such as push notifications or enterprise messaging.

The sample exposes methods that can be connected to Unity UI buttons or called
from a glasses workflow:

```csharp
sample.StartDirectExpertCall("expert-amy");
sample.StartDirectDeviceCall("AR-GLASSES-001", "expert-amy");
sample.StartExpertGroupCall("field-support");
sample.StartEquipmentSessionFromQr(scannedQrPayload);
sample.ScheduleMeeting(
    "Machine inspection",
    "2026-07-30T09:00:00Z",
    "2026-07-30T10:00:00Z",
    "expert-amy,quality-bob",
    "field-support");
sample.RefreshScheduledMeetings();
sample.JoinScheduledMeeting(scheduleId);
sample.RefreshIncomingSessions();
sample.AcceptIncomingSession(sessionId);
sample.InviteInternalMember("quality-bob");
sample.CreateExternalGuestLink(30);
```

Only an internal member with **Local Member Id** can create a scheduled
meeting. Devices can list and join schedules assigned to their device ID.

An equipment QR can also open the Android app through:

```text
ar-meeting://equipment?code=<opaque-signed-routing-code>
```

The demo server resolves this opaque code from its company configuration.
A production service should validate a signed, expiring, one-time code and
prevent replay. Do not put a device key, LiveKit token, API key or API secret
in a QR code.

Creating an external guest link only creates an expiring credential. A real
guest join page or application is still required to exchange it and connect
the guest to LiveKit.

## Android requirements

The project must include:

```text
Assets/Plugins/Android/jjsdk.aar
Assets/Plugins/Android/AndroidManifest.xml
```

On first launch, accept camera, microphone, and Jorjin USB permissions.

## Transcript

The deployed Transcript Agent must be online in the same LiveKit Cloud
project. Press `START TRANSCRIPT`, then press the button again to stop and
save. The Agent records all participant audio while transcription is active,
then sends the encoded MP3 to the device that requested Stop.

TXT and MP3 files are written to:

```text
Application.persistentDataPath/Transcripts
```

The exact TXT and MP3 paths are printed in the on-screen log. A partial
`.part` file is removed automatically if the transfer is interrupted or its
size/hash does not match.

## Notes

- The App ID field is retained for Agora API / UI compatibility and is
  optional for LiveKit.
- For LiveKit, camera/microphone enumeration uses Unity device APIs. Android
  continues to prefer the Jorjin JJSDK camera before WebCamTexture fallback.
- LiveKit participant identities are strings. The legacy `uint uid` callbacks
  use a deterministic mapping valid for the current Session; new integrations
  should keep the string identity as their primary key.
- The Unity Editor token generator is for development. A shipped app should
  use an authenticated token endpoint.
