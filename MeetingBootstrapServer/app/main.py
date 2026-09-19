import hashlib
import json
import os
import re
import secrets
from datetime import datetime, timedelta, timezone
from pathlib import Path
from threading import RLock
from typing import Any
from urllib.parse import urlencode

from fastapi import FastAPI, Header, HTTPException, Query
from livekit import api

from .models import (
    BootstrapRequest,
    BootstrapResponse,
    CompanyDirectoryResponse,
    DirectSessionCreateRequest,
    DirectoryGroup,
    DirectoryMember,
    GuestInviteCreateRequest,
    GuestInviteCreateResponse,
    GuestInviteExchangeRequest,
    GroupSessionCreateRequest,
    InternalInviteAcceptRequest,
    InternalInviteBatchResponse,
    InternalInviteCreateRequest,
    InternalInviteSummary,
    LiveKitConnection,
    QrSessionCreateRequest,
    ScheduleCreateRequest,
    ScheduleJoinRequest,
    ScheduleSummary,
    SessionAcceptRequest,
    SessionContext,
    SessionJoinResponse,
    SessionSummary,
    UnityGuestInviteRequest,
    UnityGuestInviteResponse,
    UnityInternalInviteRequest,
    UnityInternalInviteResponse,
    UnityMeetingJoinRequest,
    UnityMeetingSession,
    UnityMeetingSessionListResponse,
    UnityMeetingSessionRequest,
)


CONFIG_PATH = Path(
    os.getenv(
        "COMPANY_CONFIG_PATH",
        "/app/config/companies.example.json",
    )
)
LIVEKIT_URL = os.getenv("LIVEKIT_URL", "")
TOKEN_TTL_MINUTES = int(os.getenv("TOKEN_TTL_MINUTES", "30"))
GUEST_JOIN_BASE_URL = os.getenv(
    "GUEST_JOIN_BASE_URL",
    "https://example.invalid/meeting/join",
)
SCHEDULE_EARLY_JOIN_MINUTES = int(
    os.getenv("SCHEDULE_EARLY_JOIN_MINUTES", "15")
)
SCHEDULE_JOIN_GRACE_MINUTES = int(
    os.getenv("SCHEDULE_JOIN_GRACE_MINUTES", "15")
)

app = FastAPI(
    title="Company LiveKit Meeting Bootstrap and Session API",
    version="2.0.0-demo",
)


# Demo-only in-memory state. Every record is lost when the process restarts.
# Production deployments must replace these dictionaries with a shared,
# durable database and use a transaction/lock that works across replicas.
STATE_LOCK = RLock()
SESSIONS: dict[str, dict[str, Any]] = {}
SCHEDULES: dict[str, dict[str, Any]] = {}
INTERNAL_INVITES: dict[str, dict[str, Any]] = {}
GUEST_INVITES: dict[str, dict[str, Any]] = {}


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def load_config() -> dict[str, Any]:
    try:
        return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except FileNotFoundError as exception:
        raise HTTPException(
            status_code=503,
            detail=f"Company configuration not found: {CONFIG_PATH}",
        ) from exception
    except json.JSONDecodeError as exception:
        raise HTTPException(
            status_code=503,
            detail="Company configuration is invalid JSON.",
        ) from exception


def safe_identifier(value: str, fallback: str) -> str:
    normalized = re.sub(r"[^a-zA-Z0-9_-]+", "-", value.strip())
    normalized = normalized.strip("-_")
    return (normalized or fallback)[:96]


def require_livekit_configuration() -> None:
    if not LIVEKIT_URL:
        raise HTTPException(
            status_code=503,
            detail="LIVEKIT_URL is not configured.",
        )
    if not os.getenv("LIVEKIT_API_KEY") or not os.getenv("LIVEKIT_API_SECRET"):
        raise HTTPException(
            status_code=503,
            detail="LiveKit server API credentials are not configured.",
        )


def get_company(config: dict[str, Any], company_id: str) -> dict[str, Any]:
    companies = config.get("companies") or {}
    company = companies.get(company_id)
    if not isinstance(company, dict):
        raise HTTPException(status_code=404, detail="Unknown company.")
    return company


def resolve_device_key(company: dict[str, Any]) -> str:
    environment_name = str(company.get("device_key_env", "")).strip()
    if environment_name:
        return os.getenv(environment_name, "")
    return str(company.get("device_key", ""))


def verify_device_key(
    company: dict[str, Any],
    provided_key: str | None,
) -> None:
    expected_key = resolve_device_key(company)
    if not expected_key:
        raise HTTPException(
            status_code=503,
            detail="Company device authentication is not configured.",
        )
    if not provided_key or not secrets.compare_digest(
        provided_key,
        expected_key,
    ):
        raise HTTPException(status_code=401, detail="Invalid device key.")


def get_member(company: dict[str, Any], member_id: str) -> dict[str, Any]:
    members = company.get("members") or {}
    member = members.get(member_id)
    if not isinstance(member, dict):
        raise HTTPException(status_code=404, detail="Unknown company member.")
    if member.get("enabled", True) is False:
        raise HTTPException(status_code=403, detail="Company member is disabled.")
    return member


def get_group(company: dict[str, Any], group_id: str) -> dict[str, Any]:
    groups = company.get("groups") or {}
    group = groups.get(group_id)
    if not isinstance(group, dict):
        raise HTTPException(status_code=404, detail="Unknown meeting group.")
    if group.get("enabled", True) is False:
        raise HTTPException(status_code=403, detail="Meeting group is disabled.")
    return group


def resolve_bootstrap_group(
    company: dict[str, Any],
    request: BootstrapRequest,
) -> tuple[str, dict[str, Any]]:
    assignments = company.get("device_assignments") or {}
    group_id = str(
        assignments.get(request.device_id)
        or company.get("default_group")
        or ""
    )
    if (
        request.requested_group_id
        and company.get("allow_group_override", False)
    ):
        group_id = request.requested_group_id
    if not group_id:
        raise HTTPException(
            status_code=404,
            detail="No meeting group is assigned to this device.",
        )
    return group_id, get_group(company, group_id)


def resolve_session_group(
    company: dict[str, Any],
    device_id: str,
    requested_group_id: str | None,
) -> tuple[str, dict[str, Any]]:
    assignments = company.get("device_assignments") or {}
    group_id = str(
        assignments.get(device_id)
        or company.get("default_group")
        or ""
    )
    if requested_group_id:
        if not company.get("allow_group_session_selection", True):
            raise HTTPException(
                status_code=403,
                detail="Selecting a meeting group is not allowed.",
            )
        group_id = requested_group_id
    if not group_id:
        raise HTTPException(
            status_code=404,
            detail="No meeting group is assigned to this device.",
        )
    return group_id, get_group(company, group_id)


def group_member_ids(
    company: dict[str, Any],
    group_id: str,
) -> list[str]:
    group = get_group(company, group_id)
    result: list[str] = []
    for value in group.get("member_ids") or []:
        member_id = str(value).strip()
        if not member_id or member_id in result:
            continue
        get_member(company, member_id)
        result.append(member_id)
    return result


def default_device_name(
    company: dict[str, Any],
    company_id: str,
    device_id: str,
) -> str:
    company_name = str(company.get("display_name") or company_id)
    return f"{company_name} AR {device_id}"


def participant_identity(company_id: str, subject_id: str, fallback: str) -> str:
    return safe_identifier(f"{company_id}-{subject_id}", fallback)


def issue_token(
    *,
    room_name: str,
    identity: str,
    participant_name: str,
    attributes: dict[str, str],
    can_publish: bool = True,
    can_subscribe: bool = True,
    ttl_seconds: int | None = None,
) -> tuple[str, int]:
    require_livekit_configuration()
    token_seconds = (
        TOKEN_TTL_MINUTES * 60
        if ttl_seconds is None
        else max(1, min(ttl_seconds, TOKEN_TTL_MINUTES * 60))
    )
    normalized_attributes = {
        str(key): str(value)
        for key, value in attributes.items()
        if value is not None
    }
    try:
        token = (
            api.AccessToken()
            .with_identity(identity)
            .with_name(participant_name)
            .with_ttl(timedelta(seconds=token_seconds))
            .with_attributes(normalized_attributes)
            .with_grants(
                api.VideoGrants(
                    room_join=True,
                    room=room_name,
                    can_publish=can_publish,
                    can_subscribe=can_subscribe,
                    can_publish_data=can_publish,
                )
            )
            .to_jwt()
        )
    except Exception as exception:
        raise HTTPException(
            status_code=503,
            detail="Unable to create a LiveKit participant token.",
        ) from exception
    return token, token_seconds


def make_id(prefix: str) -> str:
    return f"{prefix}_{secrets.token_urlsafe(12)}"


def create_session_record(
    *,
    company_id: str,
    mode: str,
    host_identity: str,
    host_name: str,
    context: SessionContext,
    target_member_id: str | None = None,
    target_device_id: str | None = None,
    group_id: str | None = None,
    schedule_id: str | None = None,
    title: str = "",
    caller_member_id: str | None = None,
    assigned_participant_ids: list[str] | None = None,
    participant_device_ids: list[str] | None = None,
) -> dict[str, Any]:
    session_id = make_id("ses")
    room_name = safe_identifier(
        f"{company_id}-{mode}-{secrets.token_hex(8)}",
        "meeting-session",
    )
    record: dict[str, Any] = {
        "session_id": session_id,
        "company_id": company_id,
        "mode": mode,
        "room_name": room_name,
        "status": "active",
        "host_identity": host_identity,
        "host_name": host_name,
        "target_member_id": target_member_id,
        "target_device_id": target_device_id,
        "group_id": group_id,
        "schedule_id": schedule_id,
        "claimed_responder_member_id": None,
        "title": title,
        "caller_member_id": caller_member_id,
        "assigned_participant_ids": list(
            assigned_participant_ids or []
        ),
        "participant_device_ids": list(participant_device_ids or []),
        "context": context,
        "participants": {},
        "created_at": utc_now(),
    }
    with STATE_LOCK:
        SESSIONS[session_id] = record
    return record


def require_session(
    session_id: str,
    company_id: str | None = None,
) -> dict[str, Any]:
    with STATE_LOCK:
        record = SESSIONS.get(session_id)
    if record is None:
        raise HTTPException(status_code=404, detail="Unknown meeting session.")
    if company_id is not None and record["company_id"] != company_id:
        raise HTTPException(status_code=404, detail="Unknown meeting session.")
    if record["status"] != "active":
        raise HTTPException(status_code=409, detail="Meeting session is not active.")
    return record


def public_session(record: dict[str, Any]) -> SessionSummary:
    return SessionSummary(
        session_id=record["session_id"],
        company_id=record["company_id"],
        mode=record["mode"],
        room_name=record["room_name"],
        status=record["status"],
        host_identity=record["host_identity"],
        host_name=record["host_name"],
        target_member_id=record.get("target_member_id"),
        target_device_id=record.get("target_device_id"),
        group_id=record.get("group_id"),
        schedule_id=record.get("schedule_id"),
        claimed_responder_member_id=record.get(
            "claimed_responder_member_id"
        ),
        context=record["context"],
        participant_count=len(record["participants"]),
        created_at=record["created_at"],
    )


def create_connection(
    session: dict[str, Any],
    *,
    identity: str,
    participant_name: str,
    role: str,
    can_publish: bool = True,
    can_subscribe: bool = True,
    ttl_seconds: int | None = None,
) -> LiveKitConnection:
    attributes = {
        "company_id": session["company_id"],
        "session_id": session["session_id"],
        "session_mode": session["mode"],
        "role": role,
    }
    context: SessionContext = session["context"]
    if context.device_id:
        attributes["device_id"] = context.device_id
    if context.location_id:
        attributes["location_id"] = context.location_id
    if context.problem_type:
        attributes["problem_type"] = context.problem_type
    if context.support_group_id:
        attributes["support_group_id"] = context.support_group_id
    token, expires_in = issue_token(
        room_name=session["room_name"],
        identity=identity,
        participant_name=participant_name,
        attributes=attributes,
        can_publish=can_publish,
        can_subscribe=can_subscribe,
        ttl_seconds=ttl_seconds,
    )
    with STATE_LOCK:
        session["participants"][identity] = {
            "name": participant_name,
            "role": role,
            "joined_at": utc_now(),
        }
    return LiveKitConnection(
        server_url=LIVEKIT_URL,
        participant_token=token,
        room_name=session["room_name"],
        participant_identity=identity,
        token_expires_in_seconds=expires_in,
    )


def public_internal_invite(record: dict[str, Any]) -> InternalInviteSummary:
    return InternalInviteSummary(
        invite_id=record["invite_id"],
        session_id=record["session_id"],
        target_member_id=record["target_member_id"],
        target_member_name=record["target_member_name"],
        inviter_identity=record["inviter_identity"],
        status=record["status"],
        created_at=record["created_at"],
        expires_at=record["expires_at"],
    )


def create_internal_invites(
    *,
    company: dict[str, Any],
    session: dict[str, Any],
    inviter_identity: str,
    member_ids: list[str],
    expires_in_minutes: int = 15,
) -> list[InternalInviteSummary]:
    result: list[InternalInviteSummary] = []
    now = utc_now()
    for member_id in member_ids:
        member = get_member(company, member_id)
        target_identity = participant_identity(
            session["company_id"],
            member_id,
            "member",
        )
        if target_identity == inviter_identity:
            continue

        existing: dict[str, Any] | None = None
        with STATE_LOCK:
            for invite in INTERNAL_INVITES.values():
                if (
                    invite["session_id"] == session["session_id"]
                    and invite["target_member_id"] == member_id
                    and invite["status"] == "pending"
                    and invite["expires_at"] > now
                ):
                    existing = invite
                    break
        if existing is not None:
            result.append(public_internal_invite(existing))
            continue

        invite_id = make_id("int")
        record = {
            "invite_id": invite_id,
            "session_id": session["session_id"],
            "company_id": session["company_id"],
            "target_member_id": member_id,
            "target_member_name": str(
                member.get("display_name") or member_id
            ),
            "inviter_identity": inviter_identity,
            "status": "pending",
            "created_at": now,
            "expires_at": now + timedelta(minutes=expires_in_minutes),
        }
        with STATE_LOCK:
            INTERNAL_INVITES[invite_id] = record
        result.append(public_internal_invite(record))
    return result


def start_device_session(
    *,
    company_id: str,
    company: dict[str, Any],
    mode: str,
    device_id: str,
    participant_name: str | None,
    context: SessionContext,
    target_member_id: str | None = None,
    group_id: str | None = None,
) -> SessionJoinResponse:
    identity = participant_identity(company_id, device_id, "ar-device")
    display_name = participant_name or default_device_name(
        company,
        company_id,
        device_id,
    )
    session = create_session_record(
        company_id=company_id,
        mode=mode,
        host_identity=identity,
        host_name=display_name,
        context=context,
        target_member_id=target_member_id,
        group_id=group_id,
    )
    connection = create_connection(
        session,
        identity=identity,
        participant_name=display_name,
        role="device",
    )
    targets: list[str] = []
    if target_member_id:
        targets.append(target_member_id)
    if group_id:
        targets.extend(group_member_ids(company, group_id))
    with STATE_LOCK:
        session["assigned_participant_ids"] = list(
            dict.fromkeys(targets)
        )
        session["participant_device_ids"] = [device_id]
    invitations = create_internal_invites(
        company=company,
        session=session,
        inviter_identity=identity,
        member_ids=list(dict.fromkeys(targets)),
    )
    return SessionJoinResponse(
        session=public_session(session),
        connection=connection,
        created_internal_invites=invitations,
    )


def normalized_scheduled_datetime(
    value: datetime,
    field_name: str,
) -> datetime:
    if value.tzinfo is None or value.utcoffset() is None:
        raise HTTPException(
            status_code=422,
            detail=f"{field_name} must include a timezone offset.",
        )
    return value.astimezone(timezone.utc)


def parse_unity_datetime(value: str | None, field_name: str) -> datetime:
    if not value or not value.strip():
        raise HTTPException(
            status_code=422,
            detail=f"{field_name} is required.",
        )
    normalized = value.strip()
    if normalized.endswith("Z"):
        normalized = normalized[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError as exception:
        raise HTTPException(
            status_code=422,
            detail=f"{field_name} must be an ISO-8601 timestamp.",
        ) from exception
    return normalized_scheduled_datetime(parsed, field_name)


def public_schedule(record: dict[str, Any]) -> ScheduleSummary:
    now = utc_now()
    if record.get("cancelled"):
        status = "cancelled"
    elif now > record["ends_at"] + timedelta(
        minutes=SCHEDULE_JOIN_GRACE_MINUTES
    ):
        status = "ended"
    elif record.get("session_id"):
        status = "active"
    else:
        status = "scheduled"
    return ScheduleSummary(
        schedule_id=record["schedule_id"],
        company_id=record["company_id"],
        title=record["title"],
        host_member_id=record["host_member_id"],
        starts_at=record["starts_at"],
        ends_at=record["ends_at"],
        participant_member_ids=list(record["participant_member_ids"]),
        device_ids=list(record["device_ids"]),
        group_id=record.get("group_id"),
        location_id=record.get("location_id"),
        problem_type=record.get("problem_type"),
        status=status,
        session_id=record.get("session_id"),
        created_at=record["created_at"],
    )


def iso_utc(value: datetime | None) -> str:
    if value is None:
        return ""
    return (
        value.astimezone(timezone.utc)
        .isoformat()
        .replace("+00:00", "Z")
    )


def unity_route_type(mode: str) -> str:
    return {
        "direct": "direct_expert",
        "group": "expert_group",
        "qr": "equipment_qr",
        "scheduled": "scheduled",
    }.get(mode, mode)


def unity_session_response(
    record: dict[str, Any],
    company: dict[str, Any],
    connection: LiveKitConnection | None = None,
) -> UnityMeetingSession:
    context: SessionContext = record["context"]
    schedule = (
        SCHEDULES.get(record.get("schedule_id"))
        if record.get("schedule_id")
        else None
    )
    group_id = str(record.get("group_id") or "")
    group_name = ""
    if group_id:
        group = (company.get("groups") or {}).get(group_id) or {}
        group_name = str(group.get("display_name") or group_id)
    metadata = context.metadata or {}
    assigned_ids = list(record.get("assigned_participant_ids") or [])
    participant_devices = list(record.get("participant_device_ids") or [])
    if schedule:
        assigned_ids = list(schedule["participant_member_ids"])
        participant_devices = list(schedule["device_ids"])
    return UnityMeetingSession(
        session_id=record["session_id"],
        status=record["status"],
        route_type=unity_route_type(record["mode"]),
        title=str(record.get("title") or ""),
        room_name=record["room_name"],
        company_id=record["company_id"],
        company_name=str(
            company.get("display_name") or record["company_id"]
        ),
        group_id=group_id,
        group_name=group_name,
        caller_member_id=str(record.get("caller_member_id") or ""),
        target_expert_id=str(record.get("target_member_id") or ""),
        target_device_id=str(record.get("target_device_id") or ""),
        responding_expert_id=str(
            record.get("claimed_responder_member_id") or ""
        ),
        equipment_id=str(
            metadata.get("equipment_id")
            or metadata.get("asset_id")
            or ""
        ),
        location=str(context.location_id or ""),
        issue_category=str(context.problem_type or ""),
        scheduled_start_utc=iso_utc(
            schedule.get("starts_at") if schedule else None
        ),
        scheduled_end_utc=iso_utc(
            schedule.get("ends_at") if schedule else None
        ),
        created_at_utc=iso_utc(record["created_at"]),
        assigned_participant_ids=assigned_ids,
        participant_device_ids=participant_devices,
        server_url=connection.server_url if connection else "",
        participant_token=connection.participant_token if connection else "",
        participant_identity=(
            connection.participant_identity if connection else ""
        ),
        auto_join=connection is not None,
        token_expires_in_seconds=(
            connection.token_expires_in_seconds if connection else 0
        ),
    )


def unity_schedule_response(
    schedule: dict[str, Any],
    company: dict[str, Any],
) -> UnityMeetingSession:
    session = (
        SESSIONS.get(schedule.get("session_id"))
        if schedule.get("session_id")
        else None
    )
    if session:
        result = unity_session_response(session, company)
        # Scheduled-list records always keep the stable schedule ID. Unity
        # passes this value back to /join, which resolves the active Session.
        result.session_id = schedule["schedule_id"]
        result.status = public_schedule(schedule).status
        return result
    group_id = str(schedule.get("group_id") or "")
    group = (company.get("groups") or {}).get(group_id) or {}
    return UnityMeetingSession(
        session_id=schedule["schedule_id"],
        status=public_schedule(schedule).status,
        route_type="scheduled",
        title=schedule["title"],
        room_name="",
        company_id=schedule["company_id"],
        company_name=str(
            company.get("display_name") or schedule["company_id"]
        ),
        group_id=group_id,
        group_name=str(group.get("display_name") or group_id),
        caller_member_id=schedule["host_member_id"],
        location=str(schedule.get("location_id") or ""),
        issue_category=str(schedule.get("problem_type") or ""),
        scheduled_start_utc=iso_utc(schedule["starts_at"]),
        scheduled_end_utc=iso_utc(schedule["ends_at"]),
        created_at_utc=iso_utc(schedule["created_at"]),
        assigned_participant_ids=list(
            schedule["participant_member_ids"]
        ),
        participant_device_ids=list(schedule["device_ids"]),
        auto_join=False,
    )


def hash_invite_code(invite_code: str) -> str:
    return hashlib.sha256(invite_code.encode("utf-8")).hexdigest()


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok", "storage": "memory-demo"}


@app.post(
    "/api/v1/meeting/bootstrap",
    response_model=BootstrapResponse,
    status_code=201,
)
async def bootstrap_meeting(
    request: BootstrapRequest,
    x_device_key: str | None = Header(default=None),
) -> BootstrapResponse:
    """Backward-compatible fixed-group bootstrap endpoint."""

    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    group_id, group = resolve_bootstrap_group(company, request)

    company_id = safe_identifier(request.company_id, "company")
    device_id = safe_identifier(request.device_id, "device")
    room_name = safe_identifier(
        str(group.get("room_name") or f"{company_id}-{group_id}"),
        f"{company_id}-meeting",
    )
    identity = participant_identity(company_id, device_id, "ar-device")
    display_name = request.participant_name or default_device_name(
        company,
        company_id,
        device_id,
    )
    participant_token, expires_in = issue_token(
        room_name=room_name,
        identity=identity,
        participant_name=display_name,
        attributes={
            "company_id": company_id,
            "group_id": group_id,
            "device_id": device_id,
            "device_type": "ar-glasses",
            "session_mode": "bootstrap",
        },
    )
    return BootstrapResponse(
        server_url=LIVEKIT_URL,
        participant_token=participant_token,
        room_name=room_name,
        participant_identity=identity,
        company_id=company_id,
        company_name=str(company.get("display_name") or company_id),
        group_id=group_id,
        group_name=str(group.get("display_name") or group_id),
        auto_join=bool(group.get("auto_join", True)),
        token_expires_in_seconds=expires_in,
    )


@app.get(
    "/api/v1/companies/{company_id}/directory",
    response_model=CompanyDirectoryResponse,
)
async def get_company_directory(
    company_id: str,
    x_device_key: str | None = Header(default=None),
) -> CompanyDirectoryResponse:
    config = load_config()
    company = get_company(config, company_id)
    verify_device_key(company, x_device_key)

    members: list[DirectoryMember] = []
    for member_id, member in (company.get("members") or {}).items():
        if not isinstance(member, dict) or member.get("enabled", True) is False:
            continue
        members.append(
            DirectoryMember(
                member_id=member_id,
                display_name=str(member.get("display_name") or member_id),
                department=str(member.get("department") or ""),
                role=str(member.get("role") or "member"),
                group_ids=[
                    str(value)
                    for value in (member.get("group_ids") or [])
                ],
                enabled=True,
            )
        )

    groups: list[DirectoryGroup] = []
    for group_id, group in (company.get("groups") or {}).items():
        if not isinstance(group, dict) or group.get("enabled", True) is False:
            continue
        groups.append(
            DirectoryGroup(
                group_id=group_id,
                display_name=str(group.get("display_name") or group_id),
                member_ids=[
                    str(value)
                    for value in (group.get("member_ids") or [])
                ],
                escalation_group_ids=[
                    str(value)
                    for value in (
                        group.get("escalation_group_ids") or []
                    )
                ],
                enabled=True,
            )
        )
    return CompanyDirectoryResponse(
        company_id=company_id,
        company_name=str(company.get("display_name") or company_id),
        members=members,
        groups=groups,
    )


@app.post(
    "/api/v1/sessions/direct",
    response_model=SessionJoinResponse,
    status_code=201,
)
async def create_direct_session(
    request: DirectSessionCreateRequest,
    x_device_key: str | None = Header(default=None),
) -> SessionJoinResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    device_calls_member = bool(request.device_id) and bool(
        request.target_member_id
    )
    member_calls_device = bool(request.caller_member_id) and bool(
        request.target_device_id
    )
    if device_calls_member == member_calls_device:
        raise HTTPException(
            status_code=422,
            detail=(
                "Use either device_id + target_member_id, or "
                "caller_member_id + target_device_id."
            ),
        )
    if device_calls_member:
        if request.caller_member_id or request.target_device_id:
            raise HTTPException(
                status_code=422,
                detail="Direct-call direction fields cannot be mixed.",
            )
        get_member(company, request.target_member_id or "")
        return start_device_session(
            company_id=request.company_id,
            company=company,
            mode="direct",
            device_id=request.device_id or "",
            participant_name=request.participant_name,
            target_member_id=request.target_member_id,
            context=SessionContext(
                device_id=request.device_id,
                location_id=request.location_id,
                problem_type=request.problem_type,
                metadata=request.metadata,
            ),
        )

    if request.device_id or request.target_member_id:
        raise HTTPException(
            status_code=422,
            detail="Direct-call direction fields cannot be mixed.",
        )
    caller = get_member(company, request.caller_member_id or "")
    caller_identity = participant_identity(
        request.company_id,
        request.caller_member_id or "",
        "member",
    )
    caller_name = request.participant_name or str(
        caller.get("display_name") or request.caller_member_id
    )
    session = create_session_record(
        company_id=request.company_id,
        mode="direct",
        host_identity=caller_identity,
        host_name=caller_name,
        target_device_id=request.target_device_id,
        caller_member_id=request.caller_member_id,
        participant_device_ids=[request.target_device_id or ""],
        context=SessionContext(
            device_id=request.target_device_id,
            location_id=request.location_id,
            problem_type=request.problem_type,
            metadata=request.metadata,
        ),
    )
    connection = create_connection(
        session,
        identity=caller_identity,
        participant_name=caller_name,
        role=str(caller.get("role") or "member"),
    )
    return SessionJoinResponse(
        session=public_session(session),
        connection=connection,
    )


@app.post(
    "/api/v1/sessions/group",
    response_model=SessionJoinResponse,
    status_code=201,
)
async def create_group_session(
    request: GroupSessionCreateRequest,
    x_device_key: str | None = Header(default=None),
) -> SessionJoinResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    group_id, _ = resolve_session_group(
        company,
        request.device_id,
        request.group_id,
    )
    return start_device_session(
        company_id=request.company_id,
        company=company,
        mode="group",
        device_id=request.device_id,
        participant_name=request.participant_name,
        group_id=group_id,
        context=SessionContext(
            device_id=request.device_id,
            location_id=request.location_id,
            problem_type=request.problem_type,
            support_group_id=group_id,
            metadata=request.metadata,
        ),
    )


@app.post(
    "/api/v1/sessions/qr",
    response_model=SessionJoinResponse,
    status_code=201,
)
async def create_qr_session(
    request: QrSessionCreateRequest,
    x_device_key: str | None = Header(default=None),
) -> SessionJoinResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    assignment = (company.get("qr_assignments") or {}).get(request.qr_code)
    if not isinstance(assignment, dict):
        raise HTTPException(status_code=404, detail="Unknown QR assignment.")

    device_id = str(assignment.get("device_id") or "").strip()
    group_id = str(
        assignment.get("group_id")
        or company.get("default_group")
        or ""
    ).strip()
    if not device_id or not group_id:
        raise HTTPException(
            status_code=503,
            detail="QR assignment is missing device or support group data.",
        )
    get_group(company, group_id)
    combined_metadata = {
        str(key): str(value)
        for key, value in (assignment.get("metadata") or {}).items()
    }
    combined_metadata.update(request.metadata)
    return start_device_session(
        company_id=request.company_id,
        company=company,
        mode="qr",
        device_id=device_id,
        participant_name=request.participant_name,
        group_id=group_id,
        context=SessionContext(
            device_id=device_id,
            location_id=str(assignment.get("location_id") or "") or None,
            problem_type=str(assignment.get("problem_type") or "") or None,
            support_group_id=group_id,
            metadata=combined_metadata,
        ),
    )


@app.get(
    "/api/v1/sessions/incoming",
    response_model=UnityMeetingSessionListResponse,
)
async def list_incoming_sessions(
    company_id: str = Query(min_length=1, max_length=64),
    device_id: str | None = Query(default=None, max_length=128),
    member_id: str | None = Query(default=None, max_length=128),
    x_device_key: str | None = Header(default=None),
) -> UnityMeetingSessionListResponse:
    config = load_config()
    company = get_company(config, company_id)
    verify_device_key(company, x_device_key)
    if bool(device_id) == bool(member_id):
        raise HTTPException(
            status_code=422,
            detail="Provide exactly one of device_id or member_id.",
        )
    if member_id:
        get_member(company, member_id)

    with STATE_LOCK:
        records = [
            session
            for session in SESSIONS.values()
            if session["company_id"] == company_id
            and session["status"] == "active"
        ]
    result: list[UnityMeetingSession] = []
    for session in records:
        if device_id:
            identity = participant_identity(
                company_id,
                device_id,
                "ar-device",
            )
            if (
                session.get("target_device_id") != device_id
                or identity in session["participants"]
            ):
                continue
        else:
            identity = participant_identity(
                company_id,
                member_id or "",
                "member",
            )
            if identity in session["participants"]:
                continue
            direct_target = session.get("target_member_id") == member_id
            with STATE_LOCK:
                pending_internal_invite = any(
                    invitation["session_id"] == session["session_id"]
                    and invitation["target_member_id"] == member_id
                    and invitation["status"] == "pending"
                    and invitation["expires_at"] > utc_now()
                    for invitation in INTERNAL_INVITES.values()
                )
            group_target = False
            if (
                session["mode"] in ("group", "qr")
                and session.get("group_id")
                and session.get("claimed_responder_member_id") is None
            ):
                group_target = (member_id or "") in group_member_ids(
                    company,
                    session["group_id"],
                )
            if (
                not direct_target
                and not group_target
                and not pending_internal_invite
            ):
                continue
        result.append(unity_session_response(session, company))
    result.sort(key=lambda item: item.created_at_utc)
    return UnityMeetingSessionListResponse(sessions=result)


@app.post(
    "/api/v1/sessions/{session_id}/accept",
    response_model=UnityMeetingSession,
)
async def accept_incoming_session(
    session_id: str,
    request: UnityMeetingJoinRequest,
    x_device_key: str | None = Header(default=None),
) -> UnityMeetingSession:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    if bool(request.device_id) == bool(request.member_id):
        raise HTTPException(
            status_code=422,
            detail="Provide exactly one of device_id or member_id.",
        )
    session = require_session(session_id, request.company_id)

    claimed_now = False
    if request.device_id:
        if session.get("target_device_id") != request.device_id:
            raise HTTPException(
                status_code=403,
                detail="This incoming session targets another device.",
            )
        identity = participant_identity(
            request.company_id,
            request.device_id,
            "ar-device",
        )
        display_name = request.participant_name or default_device_name(
            company,
            request.company_id,
            request.device_id,
        )
        role = "device"
    else:
        member = get_member(company, request.member_id or "")
        with STATE_LOCK:
            pending_internal_invite = any(
                invitation["session_id"] == session_id
                and invitation["target_member_id"] == request.member_id
                and invitation["status"] == "pending"
                and invitation["expires_at"] > utc_now()
                for invitation in INTERNAL_INVITES.values()
            )
        if session.get("target_member_id"):
            if session["target_member_id"] != request.member_id:
                raise HTTPException(
                    status_code=403,
                    detail="This incoming session targets another member.",
                )
            with STATE_LOCK:
                session["claimed_responder_member_id"] = request.member_id
        elif (
            session["mode"] in ("group", "qr")
            and session.get("group_id")
            and session.get("claimed_responder_member_id") is None
        ):
            if (request.member_id or "") not in group_member_ids(
                company,
                session["group_id"],
            ):
                raise HTTPException(
                    status_code=403,
                    detail="This member is not eligible for the group call.",
                )
            with STATE_LOCK:
                claimed_by = session.get("claimed_responder_member_id")
                if claimed_by and claimed_by != request.member_id:
                    raise HTTPException(
                        status_code=409,
                        detail="Another group member already accepted this call.",
                    )
                if not claimed_by:
                    session["claimed_responder_member_id"] = request.member_id
                    claimed_now = True
        elif pending_internal_invite:
            pass
        else:
            raise HTTPException(
                status_code=403,
                detail="This session has no incoming member target.",
            )
        identity = participant_identity(
            request.company_id,
            request.member_id or "",
            "member",
        )
        display_name = request.participant_name or str(
            member.get("display_name") or request.member_id
        )
        role = str(member.get("role") or "member")

    try:
        connection = create_connection(
            session,
            identity=identity,
            participant_name=display_name,
            role=role,
        )
    except Exception:
        if claimed_now:
            with STATE_LOCK:
                if (
                    session.get("claimed_responder_member_id")
                    == request.member_id
                ):
                    session["claimed_responder_member_id"] = None
        raise

    with STATE_LOCK:
        for invitation in INTERNAL_INVITES.values():
            if (
                invitation["session_id"] != session_id
                or invitation["status"] != "pending"
            ):
                continue
            if invitation["target_member_id"] == request.member_id:
                invitation["status"] = "accepted"
                invitation["accepted_at"] = utc_now()
            elif claimed_now:
                invitation["status"] = "not_selected"
    return unity_session_response(session, company, connection)


@app.post(
    "/api/v1/schedules",
    response_model=ScheduleSummary,
    status_code=201,
)
async def create_schedule(
    request: ScheduleCreateRequest,
    x_device_key: str | None = Header(default=None),
) -> ScheduleSummary:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    get_member(company, request.host_member_id)

    starts_at = normalized_scheduled_datetime(
        request.starts_at,
        "starts_at",
    )
    ends_at = normalized_scheduled_datetime(request.ends_at, "ends_at")
    if ends_at <= starts_at:
        raise HTTPException(
            status_code=422,
            detail="ends_at must be later than starts_at.",
        )

    member_ids = list(dict.fromkeys(request.participant_member_ids))
    if request.host_member_id not in member_ids:
        member_ids.insert(0, request.host_member_id)
    for member_id in member_ids:
        get_member(company, member_id)
    if request.group_id:
        get_group(company, request.group_id)
    device_ids = list(
        dict.fromkeys(
            list(request.device_ids)
            + list(request.participant_device_ids)
        )
    )
    if not member_ids and not device_ids and not request.group_id:
        raise HTTPException(
            status_code=422,
            detail="A schedule must target a member, device, or group.",
        )

    schedule_id = make_id("sch")
    record = {
        "schedule_id": schedule_id,
        "company_id": request.company_id,
        "title": request.title,
        "host_member_id": request.host_member_id,
        "starts_at": starts_at,
        "ends_at": ends_at,
        "participant_member_ids": member_ids,
        "device_ids": device_ids,
        "group_id": request.group_id,
        "location_id": request.location_id,
        "problem_type": request.problem_type,
        "metadata": dict(request.metadata),
        "session_id": None,
        "cancelled": False,
        "created_at": utc_now(),
    }
    with STATE_LOCK:
        SCHEDULES[schedule_id] = record
    return public_schedule(record)


@app.get(
    "/api/v1/schedules",
    response_model=list[ScheduleSummary],
)
async def list_schedules(
    company_id: str = Query(min_length=1, max_length=64),
    device_id: str | None = Query(default=None, max_length=128),
    member_id: str | None = Query(default=None, max_length=128),
    x_device_key: str | None = Header(default=None),
) -> list[ScheduleSummary]:
    config = load_config()
    company = get_company(config, company_id)
    verify_device_key(company, x_device_key)
    if member_id:
        get_member(company, member_id)

    with STATE_LOCK:
        records = [
            record
            for record in SCHEDULES.values()
            if record["company_id"] == company_id
        ]
    result: list[ScheduleSummary] = []
    for record in records:
        if device_id and device_id not in record["device_ids"]:
            continue
        if member_id:
            allowed_members = set(record["participant_member_ids"])
            if record.get("group_id"):
                allowed_members.update(
                    group_member_ids(company, record["group_id"])
                )
            if member_id not in allowed_members:
                continue
        result.append(public_schedule(record))
    result.sort(key=lambda item: item.starts_at)
    return result


@app.post(
    "/api/v1/schedules/{schedule_id}/join",
    response_model=SessionJoinResponse,
)
async def join_schedule(
    schedule_id: str,
    request: ScheduleJoinRequest,
    x_device_key: str | None = Header(default=None),
) -> SessionJoinResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    if bool(request.device_id) == bool(request.member_id):
        raise HTTPException(
            status_code=422,
            detail="Provide exactly one of device_id or member_id.",
        )

    with STATE_LOCK:
        schedule = SCHEDULES.get(schedule_id)
    if schedule is None or schedule["company_id"] != request.company_id:
        raise HTTPException(status_code=404, detail="Unknown scheduled meeting.")

    now = utc_now()
    if now < schedule["starts_at"] - timedelta(
        minutes=SCHEDULE_EARLY_JOIN_MINUTES
    ):
        raise HTTPException(
            status_code=409,
            detail="The scheduled meeting is not open for joining yet.",
        )
    if now > schedule["ends_at"] + timedelta(
        minutes=SCHEDULE_JOIN_GRACE_MINUTES
    ):
        raise HTTPException(
            status_code=410,
            detail="The scheduled meeting join window has ended.",
        )

    member: dict[str, Any] | None = None
    if request.device_id:
        if request.device_id not in schedule["device_ids"]:
            raise HTTPException(
                status_code=403,
                detail="This device is not invited to the scheduled meeting.",
            )
        identity = participant_identity(
            request.company_id,
            request.device_id,
            "ar-device",
        )
        display_name = request.participant_name or default_device_name(
            company,
            request.company_id,
            request.device_id,
        )
        role = "device"
    else:
        member = get_member(company, request.member_id or "")
        allowed_members = set(schedule["participant_member_ids"])
        if schedule.get("group_id"):
            allowed_members.update(
                group_member_ids(company, schedule["group_id"])
            )
        if request.member_id not in allowed_members:
            raise HTTPException(
                status_code=403,
                detail="This member is not invited to the scheduled meeting.",
            )
        identity = participant_identity(
            request.company_id,
            request.member_id or "",
            "member",
        )
        display_name = request.participant_name or str(
            member.get("display_name") or request.member_id
        )
        role = (
            "host"
            if request.member_id == schedule["host_member_id"]
            else str(member.get("role") or "member")
        )

    created_session = False
    with STATE_LOCK:
        session_id = schedule.get("session_id")
        session = SESSIONS.get(session_id) if session_id else None
        if session is None:
            session = create_session_record(
                company_id=request.company_id,
                mode="scheduled",
                host_identity=participant_identity(
                    request.company_id,
                    schedule["host_member_id"],
                    "host",
                ),
                host_name=str(
                    get_member(
                        company,
                        schedule["host_member_id"],
                    ).get("display_name")
                    or schedule["host_member_id"]
                ),
                context=SessionContext(
                    device_id=request.device_id,
                    location_id=schedule.get("location_id"),
                    problem_type=schedule.get("problem_type"),
                    support_group_id=schedule.get("group_id"),
                    metadata=dict(schedule.get("metadata") or {}),
                ),
                group_id=schedule.get("group_id"),
                schedule_id=schedule_id,
                title=schedule["title"],
                caller_member_id=schedule["host_member_id"],
                assigned_participant_ids=list(
                    schedule["participant_member_ids"]
                ),
                participant_device_ids=list(schedule["device_ids"]),
            )
            schedule["session_id"] = session["session_id"]
            created_session = True

    connection = create_connection(
        session,
        identity=identity,
        participant_name=display_name,
        role=role,
    )
    invitations: list[InternalInviteSummary] = []
    if created_session:
        targets = list(schedule["participant_member_ids"])
        if schedule.get("group_id"):
            targets.extend(group_member_ids(company, schedule["group_id"]))
        invitations = create_internal_invites(
            company=company,
            session=session,
            inviter_identity=identity,
            member_ids=list(dict.fromkeys(targets)),
        )
    return SessionJoinResponse(
        session=public_session(session),
        connection=connection,
        created_internal_invites=invitations,
    )


@app.post(
    "/api/v1/sessions/{session_id}/internal-invites",
    response_model=InternalInviteBatchResponse,
    status_code=201,
)
async def invite_internal_participants(
    session_id: str,
    request: InternalInviteCreateRequest,
    x_device_key: str | None = Header(default=None),
) -> InternalInviteBatchResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    session = require_session(session_id, request.company_id)
    if request.inviter_identity not in session["participants"]:
        raise HTTPException(
            status_code=403,
            detail="The inviter is not a participant in this session.",
        )
    if bool(request.target_member_id) == bool(request.target_group_id):
        raise HTTPException(
            status_code=422,
            detail="Provide exactly one target_member_id or target_group_id.",
        )

    if request.target_member_id:
        member_ids = [request.target_member_id]
    else:
        member_ids = group_member_ids(
            company,
            request.target_group_id or "",
        )
    invitations = create_internal_invites(
        company=company,
        session=session,
        inviter_identity=request.inviter_identity,
        member_ids=member_ids,
        expires_in_minutes=request.expires_in_minutes,
    )
    return InternalInviteBatchResponse(
        session_id=session_id,
        invitations=invitations,
    )


@app.post(
    "/api/v1/internal-invites/{invite_id}/accept",
    response_model=SessionJoinResponse,
)
async def accept_internal_invite(
    invite_id: str,
    request: InternalInviteAcceptRequest,
    x_device_key: str | None = Header(default=None),
) -> SessionJoinResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    member = get_member(company, request.member_id)

    with STATE_LOCK:
        invitation = INTERNAL_INVITES.get(invite_id)
    if invitation is None or invitation["company_id"] != request.company_id:
        raise HTTPException(status_code=404, detail="Unknown internal invitation.")
    if invitation["target_member_id"] != request.member_id:
        raise HTTPException(
            status_code=403,
            detail="This invitation is for another member.",
        )
    if invitation["status"] != "pending":
        raise HTTPException(
            status_code=409,
            detail="Internal invitation is no longer pending.",
        )
    if utc_now() >= invitation["expires_at"]:
        with STATE_LOCK:
            invitation["status"] = "expired"
        raise HTTPException(status_code=410, detail="Internal invitation expired.")

    session = require_session(
        invitation["session_id"],
        request.company_id,
    )
    identity = participant_identity(
        request.company_id,
        request.member_id,
        "member",
    )
    display_name = request.participant_name or str(
        member.get("display_name") or request.member_id
    )
    connection = create_connection(
        session,
        identity=identity,
        participant_name=display_name,
        role=str(member.get("role") or "member"),
    )
    with STATE_LOCK:
        invitation["status"] = "accepted"
        invitation["accepted_at"] = utc_now()
    return SessionJoinResponse(
        session=public_session(session),
        connection=connection,
    )


@app.post(
    "/api/v1/sessions/{session_id}/guest-invites",
    response_model=GuestInviteCreateResponse,
    status_code=201,
)
async def create_guest_invite(
    session_id: str,
    request: GuestInviteCreateRequest,
    x_device_key: str | None = Header(default=None),
) -> GuestInviteCreateResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    session = require_session(session_id, request.company_id)
    if request.created_by_identity not in session["participants"]:
        raise HTTPException(
            status_code=403,
            detail="The guest-invite creator is not in this session.",
        )

    invite_code = secrets.token_urlsafe(32)
    guest_invite_id = make_id("gst")
    expires_at = utc_now() + timedelta(
        minutes=request.expires_in_minutes
    )
    record = {
        "guest_invite_id": guest_invite_id,
        "session_id": session_id,
        "company_id": request.company_id,
        "code_hash": hash_invite_code(invite_code),
        "created_by_identity": request.created_by_identity,
        "created_at": utc_now(),
        "expires_at": expires_at,
        "max_uses": request.max_uses,
        "use_count": 0,
        "can_publish": request.can_publish,
        "can_subscribe": request.can_subscribe,
        "revoked": False,
    }
    with STATE_LOCK:
        GUEST_INVITES[record["code_hash"]] = record

    separator = "&" if "?" in GUEST_JOIN_BASE_URL else "?"
    guest_url = (
        GUEST_JOIN_BASE_URL
        + separator
        + urlencode({"invite": invite_code})
    )
    return GuestInviteCreateResponse(
        guest_invite_id=guest_invite_id,
        session_id=session_id,
        guest_url=guest_url,
        invite_code=invite_code,
        expires_at=expires_at,
        max_uses=request.max_uses,
        can_publish=request.can_publish,
        can_subscribe=request.can_subscribe,
    )


@app.post(
    "/api/v1/guest-invites/exchange",
    response_model=SessionJoinResponse,
)
async def exchange_guest_invite(
    request: GuestInviteExchangeRequest,
) -> SessionJoinResponse:
    require_livekit_configuration()
    code_hash = hash_invite_code(request.invite_code)
    with STATE_LOCK:
        invitation = GUEST_INVITES.get(code_hash)
    if invitation is None:
        raise HTTPException(status_code=404, detail="Unknown guest invitation.")
    now = utc_now()
    if invitation["revoked"]:
        raise HTTPException(status_code=410, detail="Guest invitation was revoked.")
    if now >= invitation["expires_at"]:
        raise HTTPException(status_code=410, detail="Guest invitation expired.")
    if invitation["use_count"] >= invitation["max_uses"]:
        raise HTTPException(
            status_code=410,
            detail="Guest invitation has reached its usage limit.",
        )

    session = require_session(
        invitation["session_id"],
        invitation["company_id"],
    )
    remaining_seconds = max(
        1,
        int((invitation["expires_at"] - now).total_seconds()),
    )
    identity = participant_identity(
        invitation["company_id"],
        "guest-" + secrets.token_hex(6),
        "guest",
    )
    connection = create_connection(
        session,
        identity=identity,
        participant_name=request.guest_name.strip(),
        role="guest",
        can_publish=invitation["can_publish"],
        can_subscribe=invitation["can_subscribe"],
        ttl_seconds=remaining_seconds,
    )
    with STATE_LOCK:
        invitation["use_count"] += 1
        invitation["last_used_at"] = utc_now()
    return SessionJoinResponse(
        session=public_session(session),
        connection=connection,
    )


# ---------------------------------------------------------------------------
# Unity JJMeetingSessionClient compatibility surface
# ---------------------------------------------------------------------------


@app.post(
    "/api/v1/sessions",
    response_model=UnityMeetingSession,
    status_code=201,
)
async def create_unity_session(
    request: UnityMeetingSessionRequest,
    x_device_key: str | None = Header(default=None),
) -> UnityMeetingSession:
    metadata = {}
    if request.equipment_id:
        metadata["equipment_id"] = request.equipment_id

    if request.route_type == "direct_expert":
        result = await create_direct_session(
            DirectSessionCreateRequest(
                company_id=request.company_id,
                device_id=request.device_id,
                participant_name=request.participant_name,
                target_member_id=request.target_expert_id,
                caller_member_id=request.caller_member_id,
                target_device_id=request.target_device_id,
                location_id=request.location,
                problem_type=request.issue_category,
                metadata=metadata,
            ),
            x_device_key,
        )
    elif request.route_type == "expert_group":
        if not request.device_id:
            raise HTTPException(
                status_code=422,
                detail="expert_group requires device_id.",
            )
        result = await create_group_session(
            GroupSessionCreateRequest(
                company_id=request.company_id,
                device_id=request.device_id,
                participant_name=request.participant_name,
                group_id=request.support_group_id,
                location_id=request.location,
                problem_type=request.issue_category,
                metadata=metadata,
            ),
            x_device_key,
        )
    elif request.route_type == "equipment_qr":
        if not request.qr_payload:
            raise HTTPException(
                status_code=422,
                detail="equipment_qr requires qr_payload.",
            )
        result = await create_qr_session(
            QrSessionCreateRequest(
                company_id=request.company_id,
                qr_code=request.qr_payload,
                participant_name=request.participant_name,
                metadata=metadata,
            ),
            x_device_key,
        )
    elif request.route_type == "scheduled":
        if (
            not request.caller_member_id
            or request.scheduled_start_utc is None
            or request.scheduled_end_utc is None
        ):
            raise HTTPException(
                status_code=422,
                detail=(
                    "scheduled requires caller_member_id, "
                    "scheduled_start_utc, and scheduled_end_utc."
                ),
            )
        schedule = await create_schedule(
            ScheduleCreateRequest(
                company_id=request.company_id,
                title=request.title or "Scheduled meeting",
                host_member_id=request.caller_member_id,
                starts_at=parse_unity_datetime(
                    request.scheduled_start_utc,
                    "scheduled_start_utc",
                ),
                ends_at=parse_unity_datetime(
                    request.scheduled_end_utc,
                    "scheduled_end_utc",
                ),
                participant_member_ids=request.participant_ids,
                device_ids=request.participant_device_ids,
                group_id=request.support_group_id,
                location_id=request.location,
                problem_type=request.issue_category,
                metadata=metadata,
            ),
            x_device_key,
        )
        config = load_config()
        company = get_company(config, request.company_id)
        with STATE_LOCK:
            schedule_record = SCHEDULES[schedule.schedule_id]
        return unity_schedule_response(schedule_record, company)
    else:
        raise HTTPException(
            status_code=422,
            detail="Unsupported route_type.",
        )

    config = load_config()
    company = get_company(config, request.company_id)
    with STATE_LOCK:
        session = SESSIONS[result.session.session_id]
        session["title"] = request.title or ""
        if request.caller_member_id:
            session["caller_member_id"] = request.caller_member_id
    return unity_session_response(session, company, result.connection)


@app.get(
    "/api/v1/sessions/scheduled",
    response_model=UnityMeetingSessionListResponse,
)
async def list_unity_scheduled_sessions(
    company_id: str = Query(min_length=1, max_length=64),
    device_id: str | None = Query(default=None, max_length=128),
    member_id: str | None = Query(default=None, max_length=128),
    x_device_key: str | None = Header(default=None),
) -> UnityMeetingSessionListResponse:
    config = load_config()
    company = get_company(config, company_id)
    verify_device_key(company, x_device_key)
    if bool(device_id) == bool(member_id):
        raise HTTPException(
            status_code=422,
            detail="Provide exactly one of device_id or member_id.",
        )
    if member_id:
        get_member(company, member_id)

    with STATE_LOCK:
        records = [
            record
            for record in SCHEDULES.values()
            if record["company_id"] == company_id
        ]
    result: list[UnityMeetingSession] = []
    for record in records:
        if device_id and device_id not in record["device_ids"]:
            continue
        if member_id:
            allowed_members = set(record["participant_member_ids"])
            if record.get("group_id"):
                allowed_members.update(
                    group_member_ids(company, record["group_id"])
                )
            if member_id not in allowed_members:
                continue
        result.append(unity_schedule_response(record, company))
    result.sort(key=lambda item: item.scheduled_start_utc)
    return UnityMeetingSessionListResponse(sessions=result)


@app.post(
    "/api/v1/sessions/{session_or_schedule_id}/join",
    response_model=UnityMeetingSession,
)
async def join_unity_session(
    session_or_schedule_id: str,
    request: UnityMeetingJoinRequest,
    x_device_key: str | None = Header(default=None),
) -> UnityMeetingSession:
    with STATE_LOCK:
        is_schedule = session_or_schedule_id in SCHEDULES
        is_session = session_or_schedule_id in SESSIONS
    if is_schedule:
        result = await join_schedule(
            session_or_schedule_id,
            ScheduleJoinRequest(
                company_id=request.company_id,
                device_id=request.device_id,
                member_id=request.member_id,
                participant_name=request.participant_name,
            ),
            x_device_key,
        )
        config = load_config()
        company = get_company(config, request.company_id)
        with STATE_LOCK:
            session = SESSIONS[result.session.session_id]
        return unity_session_response(session, company, result.connection)
    if is_session:
        config = load_config()
        company = get_company(config, request.company_id)
        verify_device_key(company, x_device_key)
        if bool(request.device_id) == bool(request.member_id):
            raise HTTPException(
                status_code=422,
                detail="Provide exactly one of device_id or member_id.",
            )
        session = require_session(
            session_or_schedule_id,
            request.company_id,
        )
        if request.device_id:
            identity = participant_identity(
                request.company_id,
                request.device_id,
                "ar-device",
            )
            default_name = default_device_name(
                company,
                request.company_id,
                request.device_id,
            )
            default_role = "device"
        else:
            member = get_member(company, request.member_id or "")
            identity = participant_identity(
                request.company_id,
                request.member_id or "",
                "member",
            )
            default_name = str(
                member.get("display_name") or request.member_id
            )
            default_role = str(member.get("role") or "member")
        with STATE_LOCK:
            existing = session["participants"].get(identity)
        if existing is not None:
            connection = create_connection(
                session,
                identity=identity,
                participant_name=(
                    request.participant_name
                    or str(existing.get("name") or default_name)
                ),
                role=str(existing.get("role") or default_role),
            )
            return unity_session_response(session, company, connection)
        return await accept_incoming_session(
            session_or_schedule_id,
            request,
            x_device_key,
        )
    raise HTTPException(
        status_code=404,
        detail="Unknown meeting session or schedule.",
    )


@app.post(
    "/api/v1/sessions/{session_id}/invites/internal",
    response_model=UnityInternalInviteResponse,
    status_code=201,
)
async def invite_unity_internal_members(
    session_id: str,
    request: UnityInternalInviteRequest,
    x_device_key: str | None = Header(default=None),
) -> UnityInternalInviteResponse:
    config = load_config()
    company = get_company(config, request.company_id)
    verify_device_key(company, x_device_key)
    session = require_session(session_id, request.company_id)
    if request.inviter_identity not in session["participants"]:
        raise HTTPException(
            status_code=403,
            detail="The inviter is not a participant in this session.",
        )
    member_ids = list(dict.fromkeys(request.member_ids))
    if not member_ids:
        raise HTTPException(
            status_code=422,
            detail="member_ids must contain at least one member.",
        )
    invitations = create_internal_invites(
        company=company,
        session=session,
        inviter_identity=request.inviter_identity,
        member_ids=member_ids,
    )
    invited_ids = [item.target_member_id for item in invitations]
    return UnityInternalInviteResponse(
        session_id=session_id,
        status="pending",
        notification_status="external_delivery_required",
        invited_member_ids=invited_ids,
    )


@app.post(
    "/api/v1/sessions/{session_id}/invites/guest",
    response_model=UnityGuestInviteResponse,
    status_code=201,
)
async def create_unity_guest_invite(
    session_id: str,
    request: UnityGuestInviteRequest,
    x_device_key: str | None = Header(default=None),
) -> UnityGuestInviteResponse:
    result = await create_guest_invite(
        session_id,
        GuestInviteCreateRequest(
            company_id=request.company_id,
            created_by_identity=request.inviter_identity,
            expires_in_minutes=request.expires_in_minutes,
            max_uses=request.max_uses,
            can_publish=request.can_publish,
            can_subscribe=request.can_subscribe,
        ),
        x_device_key,
    )
    return UnityGuestInviteResponse(
        invite_id=result.guest_invite_id,
        session_id=result.session_id,
        guest_url=result.guest_url,
        expires_at_utc=iso_utc(result.expires_at),
        max_uses=result.max_uses,
        remaining_uses=result.max_uses,
    )
