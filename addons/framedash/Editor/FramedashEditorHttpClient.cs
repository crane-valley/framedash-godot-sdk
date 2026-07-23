#if TOOLS
#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Framedash.Editor.Logic;
using Godot;

namespace Framedash.Editor
{
    internal sealed class FramedashEditorHttpClient
    {
        private readonly EditorPlugin _owner;
        private HttpRequest? _request;
        private Action<bool, long, string, string>? _completion;
        private string _requestUrl = "";
        private bool _shuttingDown;

        public FramedashEditorHttpClient(EditorPlugin owner)
        {
            _owner = owner;
        }

        public void FetchMaps(
            FramedashHeatmapSettings settings,
            Action<bool, List<FramedashEditorLogic.MapInfo>?, string?> onComplete)
        {
            if (!PrepareRequest(
                    settings,
                    out string baseUrl,
                    out string apiKey,
                    out string projectId,
                    out string error))
            {
                InvokeSafely(onComplete, false, null, error);
                return;
            }

            string url = baseUrl
                + "/api/v1/projects/"
                + Uri.EscapeDataString(projectId)
                + "/maps";
            StartGet(url, apiKey, (connected, statusCode, body, requestError) =>
            {
                if (!connected)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        string.IsNullOrEmpty(requestError)
                            ? "Unable to reach the Framedash API."
                            : requestError);
                    return;
                }
                if (statusCode < 200 || statusCode > 299)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        FramedashEditorLogic.ParseProblemMessage(
                            body,
                            HttpFallback(statusCode)));
                    return;
                }
                if (!FramedashEditorLogic.ParseMapsResponse(
                        body,
                        out List<FramedashEditorLogic.MapInfo> maps,
                        out string parseError))
                {
                    InvokeSafely(onComplete, false, null, parseError);
                    return;
                }
                InvokeSafely(onComplete, true, maps, null);
            });
        }

        public void FetchHeatmap(
            FramedashHeatmapSettings settings,
            string mapSlug,
            Action<bool, List<FramedashEditorLogic.HeatmapCell>?, string?> onComplete)
        {
            if (!PrepareRequest(
                    settings,
                    out string baseUrl,
                    out string apiKey,
                    out string projectId,
                    out string error))
            {
                InvokeSafely(onComplete, false, null, error);
                return;
            }
            if (string.IsNullOrEmpty(mapSlug))
            {
                InvokeSafely(
                    onComplete,
                    false,
                    null,
                    "Select a map before fetching heatmap data.");
                return;
            }
            if (!FramedashEditorLogic.IsAllowedDays(settings.Days))
            {
                InvokeSafely(
                    onComplete,
                    false,
                    null,
                    "Days must be one of 1, 7, 14, or 30.");
                return;
            }
            if (!FramedashEditorLogic.IsAllowedCellSize(settings.CellSize))
            {
                InvokeSafely(
                    onComplete,
                    false,
                    null,
                    "Cell size must be one of 5, 10, 25, or 50.");
                return;
            }

            string url = baseUrl + FramedashEditorLogic.BuildHeatmapQueryPath(
                projectId,
                mapSlug,
                settings.CellSize,
                settings.Days,
                settings.EventNameFilter);
            StartGet(url, apiKey, (connected, statusCode, body, requestError) =>
            {
                if (!connected)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        string.IsNullOrEmpty(requestError)
                            ? "Unable to reach the Framedash API."
                            : requestError);
                    return;
                }
                if (statusCode < 200 || statusCode > 299)
                {
                    InvokeSafely(
                        onComplete,
                        false,
                        null,
                        FramedashEditorLogic.ParseProblemMessage(
                            body,
                            HttpFallback(statusCode)));
                    return;
                }
                if (!FramedashEditorLogic.ParseHeatmapResponse(
                        body,
                        out List<FramedashEditorLogic.HeatmapCell> cells,
                        out string parseError))
                {
                    InvokeSafely(onComplete, false, null, parseError);
                    return;
                }
                InvokeSafely(onComplete, true, cells, null);
            });
        }

        public void Shutdown()
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            _completion = null;
            if (_request == null)
            {
                return;
            }

            try
            {
                _request.CancelRequest();
                _request.RequestCompleted -= OnRequestCompleted;
                _request.QueueFree();
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Failed to stop an editor request: "
                    + exception);
            }
            _request = null;
        }

        private bool PrepareRequest(
            FramedashHeatmapSettings settings,
            out string baseUrl,
            out string apiKey,
            out string projectId,
            out string error)
        {
            baseUrl = "";
            apiKey = "";
            projectId = "";
            error = "";
            if (_shuttingDown)
            {
                error = "Framedash editor client is shutting down.";
                return false;
            }

            baseUrl = (settings.ApiBaseUrl ?? "").Trim();
            while (baseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
            }
            apiKey = FramedashEditorLogic.ResolveReadApiKey(
                settings.ReadApiKey,
                System.Environment.GetEnvironmentVariable(
                    "FRAMEDASH_ANALYTICS_API_KEY"));
            projectId = (settings.ProjectId ?? "").Trim();

            if (baseUrl.Length == 0)
            {
                error = "Configure the Framedash API base URL.";
                return false;
            }
            if (!EndpointSecurity.IsEndpointTransportSecure(baseUrl))
            {
                error = "The API base URL is not secure. Use HTTPS, or HTTP only for canonical localhost.";
                return false;
            }
            if (apiKey.Length == 0)
            {
                error = "Configure an analytics:read API key or set FRAMEDASH_ANALYTICS_API_KEY before launching Godot.";
                return false;
            }
            if (projectId.Length == 0)
            {
                error = "Configure a Framedash project ID.";
                return false;
            }
            return true;
        }

        private void StartGet(
            string url,
            string apiKey,
            Action<bool, long, string, string> onComplete)
        {
            if (_shuttingDown || _completion != null)
            {
                InvokeResponseSafely(onComplete, false, 0, "", "A Framedash request is already active.");
                return;
            }

            try
            {
                EnsureRequest();
                _completion = onComplete;
                _requestUrl = url;
                string[] headers =
                {
                    "X-API-Key: " + apiKey,
                    "Accept: application/json, application/problem+json"
                };
                Error result = _request!.Request(
                    url,
                    headers,
                    HttpClient.Method.Get);
                if (result != Error.Ok)
                {
                    _completion = null;
                    InvokeResponseSafely(
                        onComplete,
                        false,
                        0,
                        "",
                        "Unable to start the Framedash request (" + result + ").");
                }
            }
            catch (Exception exception)
            {
                _completion = null;
                GD.PushError(
                    "[Framedash] Failed to start an editor request: "
                    + exception);
                InvokeResponseSafely(
                    onComplete,
                    false,
                    0,
                    "",
                    "Unable to reach the Framedash API.");
            }
        }

        private void EnsureRequest()
        {
            if (_request != null && GodotObject.IsInstanceValid(_request))
            {
                return;
            }

            _request = new HttpRequest
            {
                Name = "FramedashEditorHttpRequest",
                MaxRedirects = 0,
                Timeout = 30,
                BodySizeLimit = 16 * 1024 * 1024
            };
            _request.RequestCompleted += OnRequestCompleted;
            _owner.AddChild(_request, false, Node.InternalMode.Back);
        }

        private void OnRequestCompleted(
            long result,
            long responseCode,
            string[] headers,
            byte[] body)
        {
            Action<bool, long, string, string>? completion = _completion;
            _completion = null;
            if (_shuttingDown || completion == null)
            {
                return;
            }

            try
            {
                string responseBody = Encoding.UTF8.GetString(body ?? Array.Empty<byte>());
                if (result == (long)HttpRequest.Result.RedirectLimitReached
                    || (responseCode >= 300 && responseCode <= 399))
                {
                    string location = FindHeader(headers, "Location");
                    bool crossOrigin = !string.IsNullOrEmpty(location)
                        && FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                            _requestUrl,
                            location);
                    InvokeResponseSafely(
                        completion,
                        false,
                        responseCode,
                        "",
                        crossOrigin
                            ? "Framedash request was redirected across origins; the response was rejected before the analytics read key could be forwarded."
                            : "Framedash request was redirected; the response was rejected.");
                    return;
                }
                if (result != (long)HttpRequest.Result.Success)
                {
                    InvokeResponseSafely(
                        completion,
                        false,
                        0,
                        responseBody,
                        "Unable to reach the Framedash API (request result "
                        + result
                        + ").");
                    return;
                }
                InvokeResponseSafely(
                    completion,
                    true,
                    responseCode,
                    responseBody,
                    "");
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Editor request completion failed: "
                    + exception);
                InvokeResponseSafely(
                    completion,
                    false,
                    0,
                    "",
                    "An unexpected Framedash editor request error occurred.");
            }
        }

        private static string FindHeader(string[] headers, string name)
        {
            if (headers == null)
            {
                return "";
            }

            string prefix = name + ":";
            for (int i = 0; i < headers.Length; i++)
            {
                string header = headers[i] ?? "";
                if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Substring(prefix.Length).Trim();
                }
            }
            return "";
        }

        private static string HttpFallback(long statusCode)
        {
            return statusCode > 0
                ? "Framedash request failed (HTTP " + statusCode + ")."
                : "Framedash request failed.";
        }

        private static void InvokeSafely<T>(
            Action<bool, List<T>?, string?> callback,
            bool success,
            List<T>? values,
            string? error)
        {
            try
            {
                callback(success, values, error);
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Editor response callback failed: "
                    + exception);
            }
        }

        private static void InvokeResponseSafely(
            Action<bool, long, string, string> callback,
            bool connected,
            long statusCode,
            string body,
            string error)
        {
            try
            {
                callback(connected, statusCode, body, error);
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Editor HTTP callback failed: "
                    + exception);
            }
        }
    }
}
#endif
