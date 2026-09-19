using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Jorjin.Streaming
{
    /// <summary>
    /// HTTP client for the meeting-session service.
    ///
    /// LiveKit API secrets never belong in Unity. This client sends a
    /// provisioned, rotatable device credential to the application server and
    /// receives only short-lived participant tokens.
    /// </summary>
    public static class JJMeetingSessionClient
    {
        private const string DeviceKeyHeader = "X-Device-Key";

        public static IEnumerator CreateSession(
            string apiBaseUrl,
            string deviceKey,
            JJMeetingSessionRequest body,
            int timeoutSeconds,
            Action<JJMeetingSession> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                BuildUrl(apiBaseUrl, "/api/v1/sessions"),
                UnityWebRequest.kHttpVerbPOST,
                body,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator JoinSession(
            string apiBaseUrl,
            string sessionId,
            string deviceKey,
            JJMeetingJoinRequest body,
            int timeoutSeconds,
            Action<JJMeetingSession> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                BuildUrl(
                    apiBaseUrl,
                    "/api/v1/sessions/" + EscapePath(sessionId) + "/join"),
                UnityWebRequest.kHttpVerbPOST,
                body,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator FetchScheduledSessions(
            string apiBaseUrl,
            string companyId,
            string deviceId,
            string deviceKey,
            int timeoutSeconds,
            Action<JJMeetingSessionListResponse> onSuccess,
            Action<string> onError)
        {
            return FetchScheduledSessions(
                apiBaseUrl,
                companyId,
                deviceId,
                null,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator FetchScheduledSessions(
            string apiBaseUrl,
            string companyId,
            string deviceId,
            string memberId,
            string deviceKey,
            int timeoutSeconds,
            Action<JJMeetingSessionListResponse> onSuccess,
            Action<string> onError)
        {
            string query =
                "?company_id=" +
                UnityWebRequest.EscapeURL(companyId ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(memberId))
            {
                query += "&member_id=" + UnityWebRequest.EscapeURL(memberId.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(deviceId))
            {
                query += "&device_id=" + UnityWebRequest.EscapeURL(deviceId.Trim());
            }
            return SendJson<EmptyBody, JJMeetingSessionListResponse>(
                BuildUrl(apiBaseUrl, "/api/v1/sessions/scheduled") + query,
                UnityWebRequest.kHttpVerbGET,
                null,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator FetchIncomingSessions(
            string apiBaseUrl,
            string companyId,
            string deviceId,
            string memberId,
            string deviceKey,
            int timeoutSeconds,
            Action<JJMeetingSessionListResponse> onSuccess,
            Action<string> onError)
        {
            string query =
                "?company_id=" +
                UnityWebRequest.EscapeURL(companyId ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(memberId))
            {
                query += "&member_id=" + UnityWebRequest.EscapeURL(memberId.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(deviceId))
            {
                query += "&device_id=" + UnityWebRequest.EscapeURL(deviceId.Trim());
            }
            return SendJson<EmptyBody, JJMeetingSessionListResponse>(
                BuildUrl(apiBaseUrl, "/api/v1/sessions/incoming") + query,
                UnityWebRequest.kHttpVerbGET,
                null,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator AcceptSession(
            string apiBaseUrl,
            string sessionId,
            string deviceKey,
            JJMeetingAcceptRequest body,
            int timeoutSeconds,
            Action<JJMeetingSession> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                BuildUrl(
                    apiBaseUrl,
                    "/api/v1/sessions/" + EscapePath(sessionId) + "/accept"),
                UnityWebRequest.kHttpVerbPOST,
                body,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator InviteInternalMembers(
            string apiBaseUrl,
            string sessionId,
            string deviceKey,
            JJMeetingInternalInviteRequest body,
            int timeoutSeconds,
            Action<JJMeetingInternalInviteResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                BuildUrl(
                    apiBaseUrl,
                    "/api/v1/sessions/" + EscapePath(sessionId) +
                    "/invites/internal"),
                UnityWebRequest.kHttpVerbPOST,
                body,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator CreateGuestInvite(
            string apiBaseUrl,
            string sessionId,
            string deviceKey,
            JJMeetingGuestInviteRequest body,
            int timeoutSeconds,
            Action<JJMeetingGuestInviteResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                BuildUrl(
                    apiBaseUrl,
                    "/api/v1/sessions/" + EscapePath(sessionId) +
                    "/invites/guest"),
                UnityWebRequest.kHttpVerbPOST,
                body,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        public static IEnumerator FetchDirectory(
            string apiBaseUrl,
            string companyId,
            string deviceKey,
            int timeoutSeconds,
            Action<JJMeetingDirectoryResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson<EmptyBody, JJMeetingDirectoryResponse>(
                BuildUrl(
                    apiBaseUrl,
                    "/api/v1/companies/" + EscapePath(companyId) + "/directory"),
                UnityWebRequest.kHttpVerbGET,
                null,
                deviceKey,
                timeoutSeconds,
                onSuccess,
                onError);
        }

        /// <summary>
        /// Accepts either a service root such as https://host.example or the old
        /// /api/v1/meeting/bootstrap URL already used by the sample scene.
        /// </summary>
        public static string NormalizeBaseUrl(string value)
        {
            string result = (value ?? string.Empty).Trim().TrimEnd('/');
            const string oldBootstrapPath = "/api/v1/meeting/bootstrap";
            if (result.EndsWith(
                    oldBootstrapPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(0, result.Length - oldBootstrapPath.Length);
            }
            return result;
        }

        private static IEnumerator SendJson<TRequest, TResponse>(
            string url,
            string method,
            TRequest body,
            string deviceKey,
            int timeoutSeconds,
            Action<TResponse> onSuccess,
            Action<string> onError)
            where TResponse : class
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                onError?.Invoke("Meeting service URL is invalid.");
                yield break;
            }

            using var request = new UnityWebRequest(url, method);
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.Clamp(timeoutSeconds, 5, 60);
            request.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(deviceKey))
            {
                request.SetRequestHeader(DeviceKeyHeader, deviceKey.Trim());
            }

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(ReadError(request));
                yield break;
            }

            TResponse response;
            try
            {
                response = JsonUtility.FromJson<TResponse>(
                    request.downloadHandler.text);
            }
            catch (Exception exception)
            {
                onError?.Invoke(
                    "Meeting service returned invalid JSON: " + exception.Message);
                yield break;
            }

            if (response == null)
            {
                onError?.Invoke("Meeting service returned an empty response.");
                yield break;
            }
            onSuccess?.Invoke(response);
        }

        private static string ReadError(UnityWebRequest request)
        {
            string detail = null;
            try
            {
                string payload = request.downloadHandler?.text;
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    detail = JsonUtility.FromJson<JJMeetingApiErrorResponse>(
                        payload)?.detail;
                }
            }
            catch
            {
                // Keep the HTTP fallback below if the server did not return JSON.
            }

            return !string.IsNullOrWhiteSpace(detail)
                ? detail
                : $"Meeting service request failed: HTTP {request.responseCode} " +
                  request.error;
        }

        private static string BuildUrl(string baseUrl, string path)
        {
            return NormalizeBaseUrl(baseUrl) + path;
        }

        private static string EscapePath(string value)
        {
            return UnityWebRequest.EscapeURL(value ?? string.Empty);
        }

        [Serializable]
        private sealed class EmptyBody
        {
        }
    }
}
