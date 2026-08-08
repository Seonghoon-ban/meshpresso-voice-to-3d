using Newtonsoft.Json.Linq;

namespace MeshPresso
{
    /// <summary>
    /// Turns a Genpresso error body into one readable line. Two shapes show up:
    /// OpenAI-style {"error":{message,code,type}} and FastAPI-style
    /// {"detail":[{loc,msg,...}]} for request-validation failures.
    /// </summary>
    public static class GenpressoError
    {
        public static string Describe(long statusCode, string transportError, string body)
        {
            string detail = Parse(body);
            string prefix = statusCode > 0 ? "HTTP " + statusCode : transportError;

            if (!string.IsNullOrEmpty(detail))
                return string.IsNullOrEmpty(prefix) ? detail : prefix + " - " + detail;

            if (!string.IsNullOrEmpty(transportError) && statusCode > 0)
                return prefix + " " + transportError + " " + Truncate(body, 300);

            return (prefix ?? "request failed") + " " + Truncate(body, 300);
        }

        static string Parse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                var json = JObject.Parse(body);

                var error = json["error"];
                if (error != null)
                {
                    string message = error["message"]?.ToString();
                    string code = error["code"]?.ToString();
                    if (!string.IsNullOrEmpty(message))
                        return string.IsNullOrEmpty(code) ? message : $"{message} ({code})";
                }

                // FastAPI validation errors: which field was wrong and why.
                if (json["detail"] is JArray details && details.Count > 0)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var item in details)
                    {
                        string field = item["loc"] is JArray loc ? string.Join(".", loc) : null;
                        string message = item["msg"]?.ToString();
                        parts.Add(string.IsNullOrEmpty(field) ? message : $"{field}: {message}");
                    }
                    return string.Join("; ", parts);
                }

                if (json["detail"] != null) return json["detail"].ToString();
            }
            catch
            {
                // Not JSON — fall through and let the caller show the raw body.
            }
            return null;
        }

        public static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
