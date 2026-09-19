using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public static class CompanyMeetingBootstrapClient
{
    [Serializable]
    public sealed class Request
    {
        public string company_id;
        public string device_id;
        public string participant_name;
        public string requested_group_id;
    }

    [Serializable]
    public sealed class Response
    {
        public string server_url;
        public string participant_token;
        public string room_name;
        public string participant_identity;
        public string company_id;
        public string company_name;
        public string group_id;
        public string group_name;
        public bool auto_join;
        public int token_expires_in_seconds;
    }

    [Serializable]
    private sealed class ErrorResponse
    {
        public string detail;
    }

    public static IEnumerator Fetch(
        string endpoint,
        string companyId,
        string deviceId,
        string participantName,
        string requestedGroupId,
        string deviceKey,
        int timeoutSeconds,
        Action<Response> onSuccess,
        Action<string> onError)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            onError?.Invoke("Meeting bootstrap endpoint is empty.");
            yield break;
        }

        var requestBody = new Request
        {
            company_id = companyId?.Trim(),
            device_id = deviceId?.Trim(),
            participant_name = participantName?.Trim(),
            requested_group_id = requestedGroupId?.Trim()
        };
        byte[] body = Encoding.UTF8.GetBytes(
            JsonUtility.ToJson(requestBody));

        using var request = new UnityWebRequest(
            endpoint.Trim(),
            UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.timeout = Mathf.Clamp(timeoutSeconds, 5, 60);
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(deviceKey))
        {
            request.SetRequestHeader("X-Device-Key", deviceKey.Trim());
        }

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            string detail = null;
            try
            {
                detail = JsonUtility.FromJson<ErrorResponse>(
                    request.downloadHandler.text)?.detail;
            }
            catch
            {
                // Preserve the HTTP error below when the response is not JSON.
            }
            onError?.Invoke(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Meeting bootstrap failed: HTTP {request.responseCode} " +
                      request.error
                    : detail);
            yield break;
        }

        Response response;
        try
        {
            response = JsonUtility.FromJson<Response>(
                request.downloadHandler.text);
        }
        catch (Exception exception)
        {
            onError?.Invoke(
                "Invalid meeting bootstrap response: " + exception.Message);
            yield break;
        }

        if (response == null ||
            string.IsNullOrWhiteSpace(response.server_url) ||
            string.IsNullOrWhiteSpace(response.room_name) ||
            string.IsNullOrWhiteSpace(response.participant_token) ||
            string.IsNullOrWhiteSpace(response.participant_identity))
        {
            onError?.Invoke(
                "Meeting bootstrap response is missing connection data.");
            yield break;
        }

        onSuccess?.Invoke(response);
    }
}
