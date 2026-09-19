using System;

namespace Jorjin.Streaming
{
    /// <summary>
    /// Business route used to create a LiveKit meeting session.
    /// The RTC room remains a LiveKit room; this value describes how the room
    /// was selected and which people should be notified.
    /// </summary>
    public enum JJMeetingRouteType
    {
        DirectExpert,
        ExpertGroup,
        EquipmentQr,
        Scheduled
    }

    public static class JJMeetingRouteTypeExtensions
    {
        public static string ToApiValue(this JJMeetingRouteType routeType)
        {
            switch (routeType)
            {
                case JJMeetingRouteType.DirectExpert:
                    return "direct_expert";
                case JJMeetingRouteType.ExpertGroup:
                    return "expert_group";
                case JJMeetingRouteType.EquipmentQr:
                    return "equipment_qr";
                case JJMeetingRouteType.Scheduled:
                    return "scheduled";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(routeType),
                        routeType,
                        "Unknown meeting route type.");
            }
        }
    }

    [Serializable]
    public sealed class JJMeetingSessionRequest
    {
        public string company_id;
        public string device_id;
        public string participant_name;
        public string route_type;
        public string title;
        public string caller_member_id;
        public string target_expert_id;
        public string target_device_id;
        public string support_group_id;
        public string qr_payload;
        public string equipment_id;
        public string location;
        public string issue_category;
        public string scheduled_start_utc;
        public string scheduled_end_utc;
        public string[] participant_ids = Array.Empty<string>();
        public string[] participant_device_ids = Array.Empty<string>();
    }

    [Serializable]
    public sealed class JJMeetingJoinRequest
    {
        public string company_id;
        public string device_id;
        public string member_id;
        public string participant_name;
        public string participant_identity;
    }

    [Serializable]
    public sealed class JJMeetingAcceptRequest
    {
        public string company_id;
        public string device_id;
        public string member_id;
        public string participant_name;
        public string participant_identity;
    }

    [Serializable]
    public sealed class JJMeetingSession
    {
        public string session_id;
        public string status;
        public string route_type;
        public string title;
        public string room_name;
        public string company_id;
        public string company_name;
        public string group_id;
        public string group_name;
        public string caller_member_id;
        public string target_expert_id;
        public string target_device_id;
        public string responding_expert_id;
        public string equipment_id;
        public string location;
        public string issue_category;
        public string scheduled_start_utc;
        public string scheduled_end_utc;
        public string created_at_utc;
        public string[] assigned_participant_ids = Array.Empty<string>();
        public string[] participant_device_ids = Array.Empty<string>();

        // Connection fields are present only when this caller is authorized to
        // join now. Scheduled-list responses intentionally omit the token.
        public string server_url;
        public string participant_token;
        public string participant_identity;
        public bool auto_join;
        public int token_expires_in_seconds;

        public bool HasConnectionData =>
            !string.IsNullOrWhiteSpace(server_url) &&
            !string.IsNullOrWhiteSpace(room_name) &&
            !string.IsNullOrWhiteSpace(participant_token) &&
            !string.IsNullOrWhiteSpace(participant_identity);
    }

    [Serializable]
    public sealed class JJMeetingSessionListResponse
    {
        public JJMeetingSession[] sessions = Array.Empty<JJMeetingSession>();
    }

    [Serializable]
    public sealed class JJMeetingInternalInviteRequest
    {
        public string company_id;
        public string inviter_identity;
        public string[] member_ids = Array.Empty<string>();
    }

    [Serializable]
    public sealed class JJMeetingInternalInviteResponse
    {
        public string session_id;
        public string status;
        public string notification_status;
        public string[] invited_member_ids = Array.Empty<string>();
    }

    [Serializable]
    public sealed class JJMeetingGuestInviteRequest
    {
        public string company_id;
        public string inviter_identity;
        public int expires_in_minutes = 30;
        public int max_uses = 1;
        public bool can_publish = true;
        public bool can_subscribe = true;
    }

    [Serializable]
    public sealed class JJMeetingGuestInviteResponse
    {
        public string invite_id;
        public string session_id;
        public string guest_url;
        public string expires_at_utc;
        public int max_uses;
        public int remaining_uses;
    }

    [Serializable]
    public sealed class JJMeetingDirectoryMember
    {
        public string member_id;
        public string display_name;
        public string department;
        public string role;
        public string[] group_ids = Array.Empty<string>();
        public bool enabled = true;
    }

    [Serializable]
    public sealed class JJMeetingDirectoryGroup
    {
        public string group_id;
        public string display_name;
        public string[] member_ids = Array.Empty<string>();
        public string[] escalation_group_ids = Array.Empty<string>();
        public bool enabled = true;
    }

    [Serializable]
    public sealed class JJMeetingDirectoryResponse
    {
        public string company_id;
        public string company_name;
        public JJMeetingDirectoryMember[] members =
            Array.Empty<JJMeetingDirectoryMember>();
        public JJMeetingDirectoryGroup[] groups =
            Array.Empty<JJMeetingDirectoryGroup>();
    }

    [Serializable]
    internal sealed class JJMeetingApiErrorResponse
    {
        public string detail;
    }
}
