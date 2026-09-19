import os
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient


ROOT = Path(__file__).resolve().parents[1]
os.environ["COMPANY_CONFIG_PATH"] = str(
    ROOT / "config" / "companies.example.json"
)
os.environ["LIVEKIT_URL"] = "wss://unit-test.livekit.cloud"
os.environ["LIVEKIT_API_KEY"] = "devkey"
os.environ["LIVEKIT_API_SECRET"] = (
    "local-contract-test-secret-at-least-32-bytes"
)
os.environ["EXAMPLE_COMPANY_DEVICE_KEY"] = "test-device-key"
os.environ["GUEST_JOIN_BASE_URL"] = "https://meeting.test/join"

from app import main  # noqa: E402


client = TestClient(main.app)
headers = {"X-Device-Key": "test-device-key"}


@pytest.fixture(autouse=True)
def clear_demo_state():
    main.SESSIONS.clear()
    main.SCHEDULES.clear()
    main.INTERNAL_INVITES.clear()
    main.GUEST_INVITES.clear()


def post(path: str, payload: dict):
    response = client.post(path, headers=headers, json=payload)
    assert response.status_code in (200, 201), response.text
    return response.json()


def test_unity_six_flow_contract():
    directory = client.get(
        "/api/v1/companies/example-company/directory",
        headers=headers,
    )
    assert directory.status_code == 200
    assert directory.json()["members"][0]["enabled"] is True

    direct = post(
        "/api/v1/sessions",
        {
            "company_id": "example-company",
            "device_id": "AR-GLASSES-001",
            "participant_name": "Factory AR 01",
            "route_type": "direct_expert",
            "target_expert_id": "expert-alice",
            "equipment_id": "ASSET-01",
            "location": "factory-line-01",
            "issue_category": "equipment-support",
            "scheduled_start_utc": "",
            "scheduled_end_utc": "",
            "participant_ids": [],
            "participant_device_ids": [],
        },
    )
    assert direct["route_type"] == "direct_expert"
    assert direct["target_expert_id"] == "expert-alice"
    assert direct["participant_token"].count(".") == 2

    incoming = client.get(
        "/api/v1/sessions/incoming"
        "?company_id=example-company&member_id=expert-alice",
        headers=headers,
    )
    assert incoming.status_code == 200
    assert incoming.json()["sessions"][0]["session_id"] == direct["session_id"]

    accepted = post(
        f"/api/v1/sessions/{direct['session_id']}/accept",
        {
            "company_id": "example-company",
            "member_id": "expert-alice",
            "participant_name": "Alice Chen",
        },
    )
    assert accepted["responding_expert_id"] == "expert-alice"
    assert accepted["participant_token"]

    refreshed = post(
        f"/api/v1/sessions/{direct['session_id']}/join",
        {
            "company_id": "example-company",
            "device_id": "AR-GLASSES-001",
            "participant_name": "Factory AR 01",
        },
    )
    assert refreshed["participant_identity"] == direct["participant_identity"]

    reverse = post(
        "/api/v1/sessions",
        {
            "company_id": "example-company",
            "route_type": "direct_expert",
            "caller_member_id": "expert-bob",
            "target_device_id": "AR-GLASSES-002",
            "participant_name": "Bob Lin",
        },
    )
    device_incoming = client.get(
        "/api/v1/sessions/incoming"
        "?company_id=example-company&device_id=AR-GLASSES-002",
        headers=headers,
    )
    assert device_incoming.status_code == 200
    assert reverse["session_id"] in {
        item["session_id"]
        for item in device_incoming.json()["sessions"]
    }
    reverse_accept = post(
        f"/api/v1/sessions/{reverse['session_id']}/accept",
        {
            "company_id": "example-company",
            "device_id": "AR-GLASSES-002",
            "participant_name": "Warehouse AR 02",
        },
    )
    assert reverse_accept["target_device_id"] == "AR-GLASSES-002"

    group = post(
        "/api/v1/sessions",
        {
            "company_id": "example-company",
            "device_id": "AR-GLASSES-001",
            "participant_name": "Factory AR 01",
            "route_type": "expert_group",
            "support_group_id": "support-l1",
        },
    )
    group_accept = post(
        f"/api/v1/sessions/{group['session_id']}/accept",
        {
            "company_id": "example-company",
            "member_id": "expert-bob",
            "participant_name": "Bob Lin",
        },
    )
    assert group_accept["responding_expert_id"] == "expert-bob"

    qr = post(
        "/api/v1/sessions",
        {
            "company_id": "example-company",
            "route_type": "equipment_qr",
            "qr_payload": "DEMO-QR-ASSET-LINE-01",
            "participant_name": "QR AR",
        },
    )
    assert qr["route_type"] == "equipment_qr"
    assert qr["location"] == "factory-line-01"
    assert qr["group_id"] == "support-l1"

    now = datetime.now(timezone.utc)
    scheduled = post(
        "/api/v1/sessions",
        {
            "company_id": "example-company",
            "route_type": "scheduled",
            "title": "Maintenance",
            "caller_member_id": "operations-host",
            "scheduled_start_utc": (now - timedelta(minutes=1)).isoformat(),
            "scheduled_end_utc": (now + timedelta(minutes=30)).isoformat(),
            "participant_ids": ["expert-alice"],
            "participant_device_ids": ["AR-GLASSES-001"],
            "support_group_id": "support-l1",
        },
    )
    assert scheduled["session_id"].startswith("sch_")
    assert scheduled["participant_device_ids"] == ["AR-GLASSES-001"]
    scheduled_list = client.get(
        "/api/v1/sessions/scheduled"
        "?company_id=example-company&device_id=AR-GLASSES-001",
        headers=headers,
    )
    assert scheduled_list.status_code == 200
    assert scheduled_list.json()["sessions"][0]["session_id"] == (
        scheduled["session_id"]
    )
    scheduled_join = post(
        f"/api/v1/sessions/{scheduled['session_id']}/join",
        {
            "company_id": "example-company",
            "device_id": "AR-GLASSES-001",
            "participant_name": "Factory AR 01",
        },
    )
    assert scheduled_join["route_type"] == "scheduled"
    assert scheduled_join["participant_token"]
    scheduled_list_after_join = client.get(
        "/api/v1/sessions/scheduled"
        "?company_id=example-company&device_id=AR-GLASSES-001",
        headers=headers,
    )
    assert scheduled_list_after_join.json()["sessions"][0]["session_id"] == (
        scheduled["session_id"]
    )

    internal = post(
        f"/api/v1/sessions/{group['session_id']}/invites/internal",
        {
            "company_id": "example-company",
            "inviter_identity": group_accept["participant_identity"],
            "member_ids": ["expert-carol"],
        },
    )
    assert internal["invited_member_ids"] == ["expert-carol"]
    carol_incoming = client.get(
        "/api/v1/sessions/incoming"
        "?company_id=example-company&member_id=expert-carol",
        headers=headers,
    )
    assert carol_incoming.status_code == 200
    assert group["session_id"] in {
        item["session_id"]
        for item in carol_incoming.json()["sessions"]
    }
    carol_accept = post(
        f"/api/v1/sessions/{group['session_id']}/accept",
        {
            "company_id": "example-company",
            "member_id": "expert-carol",
            "participant_name": "Carol Wang",
        },
    )
    assert carol_accept["participant_identity"].endswith("expert-carol")

    guest = post(
        f"/api/v1/sessions/{group['session_id']}/invites/guest",
        {
            "company_id": "example-company",
            "inviter_identity": group_accept["participant_identity"],
            "expires_in_minutes": 30,
            "max_uses": 1,
            "can_publish": True,
            "can_subscribe": True,
        },
    )
    assert guest["invite_id"].startswith("gst_")
    assert "?invite=" in guest["guest_url"]


def test_legacy_bootstrap_remains_compatible():
    response = client.post(
        "/api/v1/meeting/bootstrap",
        headers=headers,
        json={
            "company_id": "example-company",
            "device_id": "AR-GLASSES-001",
            "participant_name": "Factory AR 01",
        },
    )
    assert response.status_code == 201, response.text
    payload = response.json()
    assert payload["group_id"] == "support-l1"
    assert payload["participant_token"].count(".") == 2
