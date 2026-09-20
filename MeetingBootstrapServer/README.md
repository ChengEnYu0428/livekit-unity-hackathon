# LiveKit meeting bootstrap and Session API sample

This FastAPI service keeps the original device bootstrap endpoint and adds a
server-side sample for the six meeting-room workflows:

1. call one named internal member;
2. call a fixed support group, with first-responder claiming;
3. scan a provisioned equipment QR code and start an independent Session;
4. create, list, and join scheduled meetings;
5. invite another internal member or escalation group during a Session;
6. create an expiring guest link and exchange it for a restricted token.

Every LiveKit participant token is signed on this server. The LiveKit API
secret must never be stored in a Unity scene, APK, EXE, QR code, or guest URL.

## Important demo limitations

This folder is a runnable integration sample, not a production meeting
backend.

- Sessions, schedules, and invitations are stored in Python dictionaries.
  They disappear whenever the process restarts and are not shared between
  multiple server replicas.
- Creating an internal invitation records it and returns its ID, but this
  sample does not send push notifications, email, SMS, or enterprise chat
  messages. A Unity client can poll `GET /api/v1/sessions/incoming`; production
  systems should also connect the invite event to their notification service.
- `X-Device-Key` is a simple company credential for the sample. Production
  systems should authenticate individual devices and users, authorize their
  roles, rotate credentials, rate-limit endpoints, and keep an audit log.
- Guest URLs require a real web page, mobile app link, or Unity deep link at
  `GUEST_JOIN_BASE_URL`. This API creates and exchanges the opaque invite code;
  it does not provide that frontend.
- Ended/cancelled Session administration, token refresh, presence, and durable
  notification delivery must be added for production.

Use a transactional database such as PostgreSQL/Cloud SQL and a shared
notification/event system before running more than one API process.

## Configure

1. Copy `config/companies.example.json` to `config/companies.json`.
2. Copy `.env.example` to your secret/environment configuration.
3. Replace the LiveKit URL, API key, API secret, and company device key.
4. Set `COMPANY_CONFIG_PATH` to the copied company file.

The generic company configuration contains:

- `members`: the named internal-member directory;
- `groups`: group members and optional escalation groups;
- `device_assignments`: the default support group for each device;
- `qr_assignments`: opaque QR value to device, location, problem type, group,
  and asset metadata;
- the legacy `room_name` and `auto_join` fields used by the original bootstrap
  endpoint.

Do not put a device credential or LiveKit secret in `qr_assignments`. A
production QR value should be an opaque, signed, revocable provisioning ID.

## Run locally

```powershell
python -m pip install -r requirements.txt
uvicorn app.main:app --env-file .env --host 0.0.0.0 --port 8080
```

Interactive OpenAPI documentation is available at:

```text
http://localhost:8080/docs
```

All company/internal endpoints in this sample require:

```http
X-Device-Key: <company device key>
```

The guest exchange endpoint intentionally does not require that header. Its
opaque, expiring invite code is the credential.

## Endpoint summary

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/meeting/bootstrap` | Existing fixed-room bootstrap; retained for compatibility |
| `GET` | `/api/v1/companies/{company_id}/directory` | Members and groups for Unity selection UI |
| `POST` | `/api/v1/sessions/direct` | Device calls member, or member calls a target device |
| `POST` | `/api/v1/sessions/group` | Device starts a group call |
| `POST` | `/api/v1/sessions/qr` | Resolve QR provisioning and start a group Session |
| `GET` | `/api/v1/sessions/incoming` | Poll calls/invites for one device or member |
| `POST` | `/api/v1/sessions/{session_id}/accept` | Accept an incoming direct/group/internal invitation |
| `POST` | `/api/v1/schedules` | Create a scheduled meeting |
| `GET` | `/api/v1/schedules` | List meetings for a company/device/member |
| `POST` | `/api/v1/schedules/{schedule_id}/join` | Join in the allowed time window |
| `POST` | `/api/v1/sessions/{session_id}/internal-invites` | Invite a member or group during a Session |
| `POST` | `/api/v1/internal-invites/{invite_id}/accept` | Alternative invite-ID acceptance flow |
| `POST` | `/api/v1/sessions/{session_id}/guest-invites` | Create an expiring guest link |
| `POST` | `/api/v1/guest-invites/exchange` | Exchange guest code for a restricted LiveKit token |

## Unity `JJMeetingSessionClient` compatibility API

The richer endpoints above remain available. The following compatibility
surface exactly matches the snake_case DTOs and paths in
`JJMeetingSessionTypes.cs` and `JJMeetingSessionClient.cs`:

| Unity client method | HTTP contract |
| --- | --- |
| `CreateSession` | `POST /api/v1/sessions` with one `route_type`: `direct_expert`, `expert_group`, `equipment_qr`, or `scheduled` |
| `JoinSession` | `POST /api/v1/sessions/{session_or_schedule_id}/join` |
| `FetchScheduledSessions` | `GET /api/v1/sessions/scheduled?company_id=...&device_id=...` or `member_id=...` |
| `FetchIncomingSessions` | `GET /api/v1/sessions/incoming?company_id=...&device_id=...` or `member_id=...` |
| `AcceptSession` | `POST /api/v1/sessions/{session_id}/accept` |
| `InviteInternalMembers` | `POST /api/v1/sessions/{session_id}/invites/internal` |
| `CreateGuestInvite` | `POST /api/v1/sessions/{session_id}/invites/guest` |
| `FetchDirectory` | `GET /api/v1/companies/{company_id}/directory` |

Create/join/accept return a **flat** `JJMeetingSession` JSON object. Incoming
and scheduled list endpoints return:

```json
{
  "sessions": [
    {
      "session_id": "ses_...",
      "route_type": "direct_expert",
      "participant_token": ""
    }
  ]
}
```

Scheduled list records intentionally omit connection fields. For those
records, `session_id` contains the schedule ID (`sch_...`). Passing it to
`/join` creates or reuses the real Session and returns the flat object with a
short-lived token.

Unity schedule creation uses:

- `caller_member_id` as the required host;
- `participant_ids` for invited company members;
- `participant_device_ids` for specified glasses;
- ISO-8601 `scheduled_start_utc` and `scheduled_end_utc`.

Join/accept and incoming/scheduled queries require exactly one caller type:
`device_id` or `member_id`. Calling `/join` again for an identity already in a
Session safely issues a fresh short-lived token.

## Shared Session response

Session create/join/accept endpoints return:

```json
{
  "session": {
    "session_id": "ses_...",
    "company_id": "example-company",
    "mode": "group",
    "room_name": "example-company-group-...",
    "status": "active",
    "host_identity": "example-company-AR-GLASSES-001",
    "host_name": "Factory 01",
    "target_member_id": null,
    "target_device_id": null,
    "group_id": "support-l1",
    "schedule_id": null,
    "claimed_responder_member_id": null,
    "context": {
      "device_id": "AR-GLASSES-001",
      "location_id": "factory-line-01",
      "problem_type": "equipment-support",
      "support_group_id": "support-l1",
      "metadata": {}
    },
    "participant_count": 1,
    "created_at": "2026-07-30T10:00:00Z"
  },
  "connection": {
    "server_url": "wss://your-project.livekit.cloud",
    "participant_token": "eyJ...",
    "room_name": "example-company-group-...",
    "participant_identity": "example-company-AR-GLASSES-001",
    "token_expires_in_seconds": 1800
  },
  "created_internal_invites": []
}
```

Each new Session receives a unique room name. This prevents separate support
cases from overlapping in the permanent legacy bootstrap room.

## 1. Directory and direct call

Unity can populate its member/group selector from:

```http
GET /api/v1/companies/example-company/directory
X-Device-Key: <device key>
```

Device calls a named member:

```http
POST /api/v1/sessions/direct
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "device_id": "AR-GLASSES-001",
  "participant_name": "Factory 01",
  "target_member_id": "expert-alice",
  "location_id": "factory-line-01",
  "problem_type": "equipment-support"
}
```

An internal member can also call a target pair of glasses:

```json
{
  "company_id": "example-company",
  "caller_member_id": "expert-alice",
  "target_device_id": "AR-GLASSES-001",
  "participant_name": "Alice Chen",
  "problem_type": "remote-guidance"
}
```

Use only one direction in a request:

- `device_id` + `target_member_id`; or
- `caller_member_id` + `target_device_id`.

## 2. Group call, incoming calls, and first responder

Start the device's assigned/default group:

```http
POST /api/v1/sessions/group
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "device_id": "AR-GLASSES-001",
  "participant_name": "Factory 01",
  "group_id": "support-l1",
  "location_id": "factory-line-01",
  "problem_type": "equipment-support"
}
```

A member polls incoming direct calls, eligible group calls, and explicit
in-Session invitations:

```http
GET /api/v1/sessions/incoming?company_id=example-company&member_id=expert-alice
X-Device-Key: <device key>
```

A device polls calls made by a remote member:

```http
GET /api/v1/sessions/incoming?company_id=example-company&device_id=AR-GLASSES-001
X-Device-Key: <device key>
```

Accept:

```http
POST /api/v1/sessions/{session_id}/accept
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "member_id": "expert-alice",
  "participant_name": "Alice Chen"
}
```

For an initial group/QR call, the first eligible member to accept atomically
becomes `claimed_responder_member_id`. Other initial group invitations are
closed. The responder can still invite second-line, third-line, or
cross-department participants through the internal-invite endpoint.

To accept on glasses, send `device_id` instead of `member_id`.

## 3. QR-guided Session

The client sends the opaque text obtained from its QR scanner:

```http
POST /api/v1/sessions/qr
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "qr_code": "DEMO-QR-ASSET-LINE-01",
  "participant_name": "Factory 01"
}
```

The server resolves `device_id`, `location_id`, `problem_type`, support group,
and asset metadata from `qr_assignments`, then creates a unique Session and
short-lived token.

## 4. Scheduled meeting

Create:

```http
POST /api/v1/schedules
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "title": "Line 01 maintenance",
  "host_member_id": "operations-host",
  "starts_at": "2026-08-01T09:00:00+08:00",
  "ends_at": "2026-08-01T10:00:00+08:00",
  "participant_member_ids": ["expert-alice"],
  "device_ids": ["AR-GLASSES-001"],
  "group_id": "support-l1",
  "location_id": "factory-line-01",
  "problem_type": "scheduled-maintenance"
}
```

`starts_at` and `ends_at` must include a timezone offset.

List for one device:

```http
GET /api/v1/schedules?company_id=example-company&device_id=AR-GLASSES-001
X-Device-Key: <device key>
```

List for one member:

```http
GET /api/v1/schedules?company_id=example-company&member_id=expert-alice
X-Device-Key: <device key>
```

Join as a device:

```http
POST /api/v1/schedules/{schedule_id}/join
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "device_id": "AR-GLASSES-001",
  "participant_name": "Factory 01"
}
```

Send `member_id` instead of `device_id` for an internal member. The first join
creates the unique LiveKit Session; later joins reuse it. The early/grace
windows are controlled by `SCHEDULE_EARLY_JOIN_MINUTES` and
`SCHEDULE_JOIN_GRACE_MINUTES`.

## 5. Invite internal participants during a Session

Invite one member:

```http
POST /api/v1/sessions/{session_id}/internal-invites
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "inviter_identity": "example-company-expert-alice",
  "target_member_id": "expert-carol",
  "expires_in_minutes": 15
}
```

Invite an escalation/cross-department group by sending
`target_group_id` instead of `target_member_id`:

```json
{
  "company_id": "example-company",
  "inviter_identity": "example-company-expert-alice",
  "target_group_id": "support-l2",
  "expires_in_minutes": 15
}
```

The invited member can find this through `/sessions/incoming` and accept the
Session by ID. If the notification system delivers the returned `invite_id`,
the alternative endpoint is:

```http
POST /api/v1/internal-invites/{invite_id}/accept
```

## 6. Expiring guest link

Create a link from a current Session participant:

```http
POST /api/v1/sessions/{session_id}/guest-invites
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "created_by_identity": "example-company-expert-alice",
  "expires_in_minutes": 30,
  "max_uses": 1,
  "can_publish": true,
  "can_subscribe": true
}
```

The response contains `guest_url` and the same opaque `invite_code`. The
server stores only a SHA-256 hash of the code.

The guest frontend extracts the `invite` query parameter and exchanges it:

```http
POST /api/v1/guest-invites/exchange
Content-Type: application/json

{
  "invite_code": "<opaque code>",
  "guest_name": "External Technician"
}
```

The returned LiveKit token cannot outlive either `TOKEN_TTL_MINUTES` or the
remaining guest-link lifetime. The token's publish/subscribe grants come from
the invitation.

## Original compatibility bootstrap

The previous fixed-room flow remains unchanged:

```http
POST /api/v1/meeting/bootstrap
X-Device-Key: <device key>
Content-Type: application/json

{
  "company_id": "example-company",
  "device_id": "AR-GLASSES-001",
  "participant_name": "Factory 01"
}
```

Its flat response still contains `server_url`, `participant_token`,
`room_name`, `participant_identity`, company/group display names, `auto_join`,
and `token_expires_in_seconds`.

For new case-based calls, prefer the Session endpoints because they create a
unique room and expose incoming/accept/invite semantics.
