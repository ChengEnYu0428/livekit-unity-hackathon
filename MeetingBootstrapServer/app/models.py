from datetime import datetime

from pydantic import BaseModel, Field


class BootstrapRequest(BaseModel):
    """Backward-compatible device bootstrap request."""

    company_id: str = Field(min_length=1, max_length=64)
    device_id: str = Field(min_length=1, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)
    requested_group_id: str | None = Field(default=None, max_length=64)


class BootstrapResponse(BaseModel):
    """Backward-compatible device bootstrap response."""

    server_url: str
    participant_token: str
    room_name: str
    participant_identity: str
    company_id: str
    company_name: str
    group_id: str
    group_name: str
    auto_join: bool = True
    token_expires_in_seconds: int


class DirectoryMember(BaseModel):
    member_id: str
    display_name: str
    department: str = ""
    role: str
    group_ids: list[str] = Field(default_factory=list)
    enabled: bool = True


class DirectoryGroup(BaseModel):
    group_id: str
    display_name: str
    member_ids: list[str] = Field(default_factory=list)
    escalation_group_ids: list[str] = Field(default_factory=list)
    enabled: bool = True


class CompanyDirectoryResponse(BaseModel):
    company_id: str
    company_name: str
    members: list[DirectoryMember]
    groups: list[DirectoryGroup]


class SessionContext(BaseModel):
    device_id: str | None = None
    location_id: str | None = None
    problem_type: str | None = None
    support_group_id: str | None = None
    metadata: dict[str, str] = Field(default_factory=dict)


class DirectSessionCreateRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    device_id: str | None = Field(default=None, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)
    target_member_id: str | None = Field(default=None, max_length=128)
    caller_member_id: str | None = Field(default=None, max_length=128)
    target_device_id: str | None = Field(default=None, max_length=128)
    location_id: str | None = Field(default=None, max_length=128)
    problem_type: str | None = Field(default=None, max_length=128)
    metadata: dict[str, str] = Field(default_factory=dict)


class GroupSessionCreateRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    device_id: str = Field(min_length=1, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)
    group_id: str | None = Field(default=None, max_length=64)
    location_id: str | None = Field(default=None, max_length=128)
    problem_type: str | None = Field(default=None, max_length=128)
    metadata: dict[str, str] = Field(default_factory=dict)


class QrSessionCreateRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    qr_code: str = Field(min_length=1, max_length=512)
    participant_name: str | None = Field(default=None, max_length=128)
    metadata: dict[str, str] = Field(default_factory=dict)


class LiveKitConnection(BaseModel):
    server_url: str
    participant_token: str
    room_name: str
    participant_identity: str
    token_expires_in_seconds: int


class InternalInviteSummary(BaseModel):
    invite_id: str
    session_id: str
    target_member_id: str
    target_member_name: str
    inviter_identity: str
    status: str
    created_at: datetime
    expires_at: datetime


class SessionSummary(BaseModel):
    session_id: str
    company_id: str
    mode: str
    room_name: str
    status: str
    host_identity: str
    host_name: str
    target_member_id: str | None = None
    target_device_id: str | None = None
    group_id: str | None = None
    schedule_id: str | None = None
    claimed_responder_member_id: str | None = None
    context: SessionContext
    participant_count: int
    created_at: datetime


class SessionJoinResponse(BaseModel):
    session: SessionSummary
    connection: LiveKitConnection
    created_internal_invites: list[InternalInviteSummary] = Field(
        default_factory=list
    )


class ScheduleCreateRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    title: str = Field(min_length=1, max_length=160)
    host_member_id: str = Field(min_length=1, max_length=128)
    starts_at: datetime
    ends_at: datetime
    participant_member_ids: list[str] = Field(default_factory=list)
    device_ids: list[str] = Field(default_factory=list)
    participant_device_ids: list[str] = Field(default_factory=list)
    group_id: str | None = Field(default=None, max_length=64)
    location_id: str | None = Field(default=None, max_length=128)
    problem_type: str | None = Field(default=None, max_length=128)
    metadata: dict[str, str] = Field(default_factory=dict)


class ScheduleSummary(BaseModel):
    schedule_id: str
    company_id: str
    title: str
    host_member_id: str
    starts_at: datetime
    ends_at: datetime
    participant_member_ids: list[str]
    device_ids: list[str]
    group_id: str | None = None
    location_id: str | None = None
    problem_type: str | None = None
    status: str
    session_id: str | None = None
    created_at: datetime


class ScheduleJoinRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    device_id: str | None = Field(default=None, max_length=128)
    member_id: str | None = Field(default=None, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)


class SessionAcceptRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    device_id: str | None = Field(default=None, max_length=128)
    member_id: str | None = Field(default=None, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)


class InternalInviteCreateRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    inviter_identity: str = Field(min_length=1, max_length=128)
    target_member_id: str | None = Field(default=None, max_length=128)
    target_group_id: str | None = Field(default=None, max_length=64)
    expires_in_minutes: int = Field(default=15, ge=1, le=1440)


class InternalInviteAcceptRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    member_id: str = Field(min_length=1, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)


class InternalInviteBatchResponse(BaseModel):
    session_id: str
    invitations: list[InternalInviteSummary]


class GuestInviteCreateRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    created_by_identity: str = Field(min_length=1, max_length=128)
    expires_in_minutes: int = Field(default=30, ge=1, le=1440)
    max_uses: int = Field(default=1, ge=1, le=100)
    can_publish: bool = True
    can_subscribe: bool = True


class GuestInviteCreateResponse(BaseModel):
    guest_invite_id: str
    session_id: str
    guest_url: str
    invite_code: str
    expires_at: datetime
    max_uses: int
    can_publish: bool
    can_subscribe: bool


class GuestInviteExchangeRequest(BaseModel):
    invite_code: str = Field(min_length=1, max_length=512)
    guest_name: str = Field(min_length=1, max_length=128)


# Unity compatibility DTOs. Field names intentionally match the snake_case
# fields serialized by JJMeetingSessionTypes.cs and must not be renamed.
class UnityMeetingSessionRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    device_id: str | None = Field(default=None, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)
    route_type: str = Field(min_length=1, max_length=32)
    title: str | None = Field(default=None, max_length=160)
    caller_member_id: str | None = Field(default=None, max_length=128)
    target_expert_id: str | None = Field(default=None, max_length=128)
    target_device_id: str | None = Field(default=None, max_length=128)
    support_group_id: str | None = Field(default=None, max_length=64)
    qr_payload: str | None = Field(default=None, max_length=512)
    equipment_id: str | None = Field(default=None, max_length=128)
    location: str | None = Field(default=None, max_length=128)
    issue_category: str | None = Field(default=None, max_length=128)
    scheduled_start_utc: str | None = Field(default=None, max_length=64)
    scheduled_end_utc: str | None = Field(default=None, max_length=64)
    participant_ids: list[str] = Field(default_factory=list)
    participant_device_ids: list[str] = Field(default_factory=list)


class UnityMeetingJoinRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    device_id: str | None = Field(default=None, max_length=128)
    member_id: str | None = Field(default=None, max_length=128)
    participant_name: str | None = Field(default=None, max_length=128)
    participant_identity: str | None = Field(default=None, max_length=128)


class UnityMeetingSession(BaseModel):
    session_id: str
    status: str
    route_type: str
    title: str = ""
    room_name: str = ""
    company_id: str
    company_name: str = ""
    group_id: str = ""
    group_name: str = ""
    caller_member_id: str = ""
    target_expert_id: str = ""
    target_device_id: str = ""
    responding_expert_id: str = ""
    equipment_id: str = ""
    location: str = ""
    issue_category: str = ""
    scheduled_start_utc: str = ""
    scheduled_end_utc: str = ""
    created_at_utc: str = ""
    assigned_participant_ids: list[str] = Field(default_factory=list)
    participant_device_ids: list[str] = Field(default_factory=list)
    server_url: str = ""
    participant_token: str = ""
    participant_identity: str = ""
    auto_join: bool = False
    token_expires_in_seconds: int = 0


class UnityMeetingSessionListResponse(BaseModel):
    sessions: list[UnityMeetingSession] = Field(default_factory=list)


class UnityInternalInviteRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    inviter_identity: str = Field(min_length=1, max_length=128)
    member_ids: list[str] = Field(default_factory=list)


class UnityInternalInviteResponse(BaseModel):
    session_id: str
    status: str
    notification_status: str
    invited_member_ids: list[str] = Field(default_factory=list)


class UnityGuestInviteRequest(BaseModel):
    company_id: str = Field(min_length=1, max_length=64)
    inviter_identity: str = Field(min_length=1, max_length=128)
    expires_in_minutes: int = Field(default=30, ge=1, le=1440)
    max_uses: int = Field(default=1, ge=1, le=100)
    can_publish: bool = True
    can_subscribe: bool = True


class UnityGuestInviteResponse(BaseModel):
    invite_id: str
    session_id: str
    guest_url: str
    expires_at_utc: str
    max_uses: int
    remaining_uses: int
