using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MeshPresso
{
    /// <summary>
    /// Client for Genpresso's media queue, targeting the Rodin text-to-3D model.
    /// Submit returns absolute status/response URLs; polling never includes the
    /// model path, only the request id.
    /// </summary>
    public static class GenpressoMediaClient
    {
        public class FileRef
        {
            public string Url;
            public string FileName;
        }

        public class GenerationResult
        {
            public bool Ok;
            public string Error;
            public FileRef ModelMesh;
            public List<FileRef> Textures = new List<FileRef>();
            public string RawJson;
        }

        public class DownloadResult
        {
            public bool Ok;
            public byte[] Data;
            public string Error;
        }

        public static IEnumerator GenerateModel(
            VoiceTo3DSettings settings,
            string prompt,
            Action<string> statusCallback,
            GenerationResult result)
        {
            string apiKey = settings.ResolveApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                result.Error = "Genpresso API key is missing. Set it on the VoiceTo3DSettings asset " +
                               "or the GENPRESSO_API_KEY environment variable.";
                yield break;
            }

            // 1. Submit.
            var payload = new JObject
            {
                ["prompt"] = prompt,
                ["geometry_file_format"] = settings.geometryFileFormat,
                ["material"] = settings.MaterialApiString(),
                ["tier"] = settings.TierApiString()
            };

            string statusUrl = null;
            string responseUrl = null;
            string cancelUrl = null;

            string submitUrl = settings.Url("media/" + settings.mediaModel.Trim().Trim('/'));
            using (var request = new UnityWebRequest(submitUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + apiKey);
                request.timeout = 60;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    result.Error = "Genpresso submit failed: " +
                                   GenpressoError.Describe(request.responseCode, request.error, request.downloadHandler?.text);
                    yield break;
                }

                string requestId;
                try
                {
                    var json = JObject.Parse(request.downloadHandler.text);
                    requestId = json["request_id"]?.ToString();
                    statusUrl = json["status_url"]?.ToString();
                    responseUrl = json["response_url"]?.ToString();
                    cancelUrl = json["cancel_url"]?.ToString();
                }
                catch (Exception e)
                {
                    result.Error = "Failed to parse the Genpresso submit response: " + e.Message;
                    yield break;
                }

                // Fall back to building the polling URLs from the id if they were omitted.
                if (!string.IsNullOrEmpty(requestId))
                {
                    if (string.IsNullOrEmpty(statusUrl)) statusUrl = settings.Url($"media/requests/{requestId}/status");
                    if (string.IsNullOrEmpty(responseUrl)) responseUrl = settings.Url($"media/requests/{requestId}");
                    if (string.IsNullOrEmpty(cancelUrl)) cancelUrl = settings.Url($"media/requests/{requestId}/cancel");
                }
            }

            if (string.IsNullOrEmpty(statusUrl) || string.IsNullOrEmpty(responseUrl))
            {
                result.Error = "The Genpresso submit response contained neither polling URLs nor a request id.";
                yield break;
            }

            // 2. Poll until the request leaves the queue.
            float startTime = Time.realtimeSinceStartup;
            while (true)
            {
                if (Time.realtimeSinceStartup - startTime > settings.generationTimeoutSeconds)
                {
                    result.Error = $"Genpresso generation timed out after {settings.generationTimeoutSeconds}s.";
                    if (!string.IsNullOrEmpty(cancelUrl)) yield return Cancel(cancelUrl, apiKey);
                    yield break;
                }

                string status = null;
                using (var request = UnityWebRequest.Get(statusUrl))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + apiKey);
                    request.timeout = 30;
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            var json = JObject.Parse(request.downloadHandler.text);
                            status = json["status"]?.ToString();
                            var queuePosition = json["queue_position"];
                            statusCallback?.Invoke(queuePosition != null
                                ? $"{status} (queue position {queuePosition})"
                                : status);
                        }
                        catch (Exception e)
                        {
                            statusCallback?.Invoke("status parse error: " + e.Message);
                        }
                    }
                    else if (IsHardHttpError(request))
                    {
                        result.Error = "Genpresso status check failed: " +
                                       GenpressoError.Describe(request.responseCode, request.error, request.downloadHandler?.text);
                        yield break;
                    }
                    // Transient failures: keep polling until the timeout.
                }

                if (status == "COMPLETED") break;
                if (IsTerminalFailure(status))
                {
                    result.Error = "Genpresso generation ended with status: " + status;
                    yield break;
                }

                yield return new WaitForSecondsRealtime(2f);
            }

            // 3. Fetch the result. Two quirks drive the shape of this loop: a request the
            // model rejected still reports COMPLETED, so only this call reveals the
            // failure; and the result can briefly lag the status with a
            // "Request is still in progress" body.
            while (true)
            {
                if (Time.realtimeSinceStartup - startTime > settings.generationTimeoutSeconds)
                {
                    result.Error = $"Genpresso generation timed out after {settings.generationTimeoutSeconds}s " +
                                   "while waiting for the result body.";
                    yield break;
                }

                using (var request = UnityWebRequest.Get(responseUrl))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + apiKey);
                    request.timeout = 60;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        if (IsHardHttpError(request))
                        {
                            result.Error = "Genpresso generation failed: " +
                                           GenpressoError.Describe(request.responseCode, request.error, request.downloadHandler?.text);
                            yield break;
                        }

                        // Dropped connection, 5xx, 408/429: the model is already generated and
                        // paid for, so retry inside the remaining budget instead of binning it.
                        statusCallback?.Invoke("fetching result (retrying)");
                        yield return new WaitForSecondsRealtime(2f);
                        continue;
                    }

                    string body = request.downloadHandler.text;
                    JObject json;
                    try
                    {
                        json = JObject.Parse(body);
                    }
                    catch (Exception e)
                    {
                        result.Error = "Failed to parse the Genpresso result: " + e.Message;
                        yield break;
                    }

                    var mesh = json["model_mesh"];
                    if (mesh?["url"] == null)
                    {
                        if (IsStillRunning(json))
                        {
                            statusCallback?.Invoke("finishing up");
                            yield return new WaitForSecondsRealtime(2f);
                            continue;
                        }

                        result.Error = "The Genpresso result contained no model_mesh. Raw: " +
                                       GenpressoError.Truncate(body, 500);
                        yield break;
                    }

                    result.RawJson = body;
                    result.ModelMesh = ToFileRef(mesh, "model." + settings.geometryFileFormat);

                    if (json["textures"] is JArray textures)
                    {
                        int index = 0;
                        foreach (var texture in textures)
                        {
                            result.Textures.Add(ToFileRef(texture, $"texture_{index}.png"));
                            index++;
                        }
                    }

                    result.Ok = true;
                    yield break;
                }
            }
        }

        /// <summary>
        /// The result endpoint answers 200 with {"detail":"Request is still in progress"}
        /// for a short window after the status flips to COMPLETED.
        /// </summary>
        static bool IsStillRunning(JObject json)
        {
            string detail = json["detail"]?.Type == JTokenType.String ? json["detail"].ToString() : null;
            return !string.IsNullOrEmpty(detail) &&
                   detail.IndexOf("in progress", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Best-effort cancel so an abandoned request stops costing credits.</summary>
        public static IEnumerator Cancel(string cancelUrl, string apiKey)
        {
            // Built by hand rather than via UnityWebRequest.Put: an UploadHandlerRaw
            // rejects an empty payload, and this endpoint takes no body.
            using (var request = new UnityWebRequest(cancelUrl, UnityWebRequest.kHttpVerbPUT))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Authorization", "Bearer " + apiKey);
                request.timeout = 15;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning("[MeshPresso] Could not cancel the abandoned request: " + request.error);
            }
        }

        public static IEnumerator DownloadFile(string url, DownloadResult result)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 300;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    result.Error = $"Download failed ({url}): {request.error}";
                    yield break;
                }

                result.Data = request.downloadHandler.data;
                result.Ok = true;
            }
        }

        static bool IsTerminalFailure(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            switch (status.ToUpperInvariant())
            {
                // Genpresso spells it CANCELED; accept the double-L form too.
                case "FAILED":
                case "ERROR":
                case "CANCELED":
                case "CANCELLED":
                    return true;
                default:
                    return false;
            }
        }

        static FileRef ToFileRef(JToken token, string fallbackName)
        {
            string url = token["url"]?.ToString();
            string fileName = token["file_name"]?.ToString();

            if (string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(url))
            {
                try
                {
                    fileName = System.IO.Path.GetFileName(new Uri(url).LocalPath);
                }
                catch
                {
                    fileName = null;
                }
            }

            if (string.IsNullOrEmpty(fileName)) fileName = fallbackName;
            return new FileRef { Url = url, FileName = fileName };
        }

        static bool IsHardHttpError(UnityWebRequest request)
        {
            // 4xx = auth/validation problems that will not fix themselves by retrying,
            // except 408 and 429, which are transient and belong in the polling retry path.
            return request.result == UnityWebRequest.Result.ProtocolError
                   && request.responseCode >= 400 && request.responseCode < 500
                   && request.responseCode != 408
                   && request.responseCode != 429;
        }
    }
}
