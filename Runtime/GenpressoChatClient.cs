using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MeshPresso
{
    /// <summary>
    /// Sends the recorded speech (WAV) to Genpresso's OpenAI-compatible chat endpoint
    /// and gets back a single text-to-3D prompt. The model hears the audio directly,
    /// so there is no separate speech-to-text step.
    /// </summary>
    public static class GenpressoChatClient
    {
        public class Result
        {
            public bool Ok;
            public string Prompt;
            public string Error;
        }

        public static IEnumerator GeneratePrompt(VoiceTo3DSettings settings, byte[] wavBytes, Result result)
        {
            string apiKey = settings.ResolveApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                result.Error = "Genpresso API key is missing. Set it on the VoiceTo3DSettings asset " +
                               "or the GENPRESSO_API_KEY environment variable.";
                yield break;
            }

            var payload = new JObject
            {
                ["model"] = settings.promptModel,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = settings.promptSystemInstruction
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "text",
                                ["text"] = "This audio contains my spoken description of a 3D object. " +
                                           "Convert it into a single text-to-3D prompt following your instructions."
                            },
                            new JObject
                            {
                                ["type"] = "input_audio",
                                ["input_audio"] = new JObject
                                {
                                    ["data"] = Convert.ToBase64String(wavBytes),
                                    ["format"] = "wav"
                                }
                            }
                        }
                    }
                }
            };

            string url = settings.Url("chat/completions");
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                byte[] body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + apiKey);
                request.timeout = 180;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    result.Error = "Genpresso chat request failed: " +
                                   GenpressoError.Describe(request.responseCode, request.error, request.downloadHandler?.text);
                    yield break;
                }

                try
                {
                    var json = JObject.Parse(request.downloadHandler.text);
                    if (json["error"] != null)
                    {
                        result.Error = "Genpresso chat error: " +
                                       GenpressoError.Describe(request.responseCode, null, request.downloadHandler.text);
                        yield break;
                    }

                    string content = json["choices"]?[0]?["message"]?["content"]?.ToString();
                    string prompt = CleanPrompt(content);
                    if (string.IsNullOrEmpty(prompt))
                    {
                        result.Error = "Genpresso returned an empty prompt. Raw: " +
                                       GenpressoError.Truncate(request.downloadHandler.text, 500);
                        yield break;
                    }

                    result.Prompt = prompt;
                    result.Ok = true;
                }
                catch (Exception e)
                {
                    result.Error = "Failed to parse the Genpresso chat response: " + e.Message;
                }
            }
        }

        static string CleanPrompt(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            string prompt = content.Trim();

            if (prompt.StartsWith("```"))
            {
                int firstNewline = prompt.IndexOf('\n');
                if (firstNewline >= 0) prompt = prompt.Substring(firstNewline + 1);
                int fenceEnd = prompt.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0) prompt = prompt.Substring(0, fenceEnd);
                prompt = prompt.Trim();
            }

            prompt = prompt.Trim('"', '“', '”').Trim();
            if (prompt.Length > 1024) prompt = prompt.Substring(0, 1024);
            return prompt;
        }
    }
}
