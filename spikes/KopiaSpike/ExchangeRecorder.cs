using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KopiaSpike;

/// <summary>
/// Writes every HTTP exchange to <c>NNN-METHOD-path.json</c> for FINDINGS.md. Never records headers
/// (Authorization, cookies); redacts JSON "password" fields, the CSRF token and local paths.
/// </summary>
public sealed partial class ExchangeRecorder(string directory, IReadOnlyDictionary<string, string> pathSubstitutions)
{
    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private int _sequence;

    public void Record(string client, HttpMethod method, string pathAndQuery, string? requestBody, int status, string? contentType, string responseBody, TimeSpan elapsed)
    {
        var n = Interlocked.Increment(ref _sequence);
        var entry = new JsonObject
        {
            ["client"] = client,
            ["method"] = method.Method,
            ["path"] = pathAndQuery,
            ["request"] = Parse(requestBody),
            ["status"] = status,
            ["contentType"] = contentType,
            ["elapsedMs"] = (int)elapsed.TotalMilliseconds,
            ["response"] = Parse(CsrfMeta().Replace(responseBody, "content=\"***csrf-token***\"")),
        };

        var text = Scrub(entry.ToJsonString(s_write));
        var slug = Slug().Replace(pathAndQuery.Split('?')[0], "-").Trim('-');
        slug = slug.Length == 0 ? "root" : slug[..Math.Min(slug.Length, 60)];
        File.WriteAllText(Path.Combine(directory, $"{n:000}-{method.Method}-{slug}.json"), text);
    }

    /// <summary>
    /// Replaces local paths with placeholders in every form they show up in: raw, JSON-escaped once or
    /// twice (Kopia quotes paths inside error strings), forward-slash, and URL-encoded in query strings.
    /// </summary>
    public string Scrub(string text)
    {
        foreach (var (path, placeholder) in pathSubstitutions.OrderByDescending(p => p.Key.Length))
        {
            string[] forms =
            [
                path.Replace("\\", "\\\\\\\\", StringComparison.Ordinal),
                path.Replace("\\", "\\\\", StringComparison.Ordinal),
                path,
                path.Replace('\\', '/'),
                Uri.EscapeDataString(path),
            ];
            foreach (var form in forms)
            {
                text = text.Replace(form, placeholder, StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    private static JsonNode? Parse(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(body);
            Redact(node);
            return node;
        }
        catch (JsonException)
        {
            return JsonValue.Create(body);
        }
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (key.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        obj[key] = "***redacted***";
                    }
                    else
                    {
                        Redact(obj[key]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Redact(item);
                }

                break;
        }
    }

    [GeneratedRegex("content=\"[0-9a-f]{32,}\"")]
    private static partial Regex CsrfMeta();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex Slug();
}
