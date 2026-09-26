using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIVTuber.Core.Vision;

/// <summary>
/// Doubao Ark (方舟) vision client over Chat Completions with typed multimodal
/// messages/content + image_url base64 data URI (plan [D8]).
///
/// NOTE: 未实测 — implemented against the published Ark Chat Completions shape; no real
/// API key was available during development, so request/response details (model name,
/// error codes) still need a contract test with a live account before relying on it.
/// Provider "qwen" is a reserved slot and not implemented (plan [D9]).
/// </summary>
public sealed class DoubaoVisionClient : IVisionClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _provider;
    private readonly bool _ownsHttp;

    public DoubaoVisionClient(VisionConfig config, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(config.Model))
            throw new ArgumentException("vision.model must name the account-available model; refusing to guess", nameof(config));
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("vision.api_key is empty", nameof(config));
        _baseUrl = config.BaseUrl.TrimEnd('/');
        _model = config.Model;
        _apiKey = config.ApiKey;
        _provider = config.Provider;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<VisionClientResult> ObserveAsync(VisionFrameRequest request, CancellationToken ct)
    {
        var url = $"{_baseUrl}/chat/completions";
        var body = new
        {
            model = _model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = request.Prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(request.Frame.Jpeg) },
                        },
                    },
                },
            },
            max_tokens = 400,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        // Never log this request: it contains the base64 frame.
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return VisionClientResult.Fail(VisionClientStatus.HttpError, "vision request timed out");
        }
        catch (OperationCanceledException)
        {
            return VisionClientResult.Fail(VisionClientStatus.Cancelled, "cancelled");
        }
        catch (Exception ex)
        {
            return VisionClientResult.Fail(VisionClientStatus.HttpError, $"vision transport error: {ex.GetType().Name}");
        }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode switch
            {
                401 or 403 => VisionClientStatus.AuthError,
                429 => VisionClientStatus.RateLimited,
                _ => VisionClientStatus.HttpError,
            };
            return VisionClientResult.Fail(status, $"vision HTTP {(int)resp.StatusCode}");
        }

        string? content;
        string? responseId;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            responseId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return VisionClientResult.Fail(VisionClientStatus.SchemaError, "unparsable provider response envelope");
        }
        if (string.IsNullOrWhiteSpace(content))
            return VisionClientResult.Fail(VisionClientStatus.SchemaError, "empty model content");

        var observation = ParseObservation(content, request, responseId);
        return observation is null
            ? VisionClientResult.Fail(VisionClientStatus.SchemaError, "model content did not match observation schema")
            : VisionClientResult.Ok(observation);
    }

    /// <summary>Strict parse of the model JSON into the 4.4 contract. Unknown fields are
    /// ignored, list sizes are clamped, and any structural error degrades to null.</summary>
    internal static VisionObservation? ParseObservation(
        string modelContent, VisionFrameRequest request, string? responseId)
    {
        // Tolerate ```json fences around the object.
        var start = modelContent.IndexOf('{');
        var end = modelContent.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        string json;
        try { json = modelContent[start..(end + 1)]; }
        catch (ArgumentOutOfRangeException) { return null; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var summary = GetString(root, "scene_summary");
        if (summary is null) return null;
        var nowMs = 0L; // caller re-stamps ObservedAt/ValidUntil; captured timing comes from the frame
        return new VisionObservation
        {
            FrameId = request.Frame.FrameId,
            SourceWindowId = request.Frame.SourceWindowId,
            CaptureEpoch = request.Frame.CaptureEpoch,
            CapturedAt = request.Frame.CapturedAtMs,
            ObservedAt = nowMs,
            ValidUntil = nowMs,
            SceneSummary = ClampText(summary, 200),
            VisibleFacts = GetStringList(root, "visible_facts", 5),
            UncertainFacts = GetStringList(root, "uncertain_facts", 5),
            SalientChanges = GetStringList(root, "salient_changes", 5),
            Evidence = request.Frame.Evidence,
            Model = request.Model,
            Provider = "doubao",
            RequestId = responseId ?? "",
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static string ClampText(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static IReadOnlyList<string> GetStringList(JsonElement root, string name, int maxItems)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(ClampText(s.Trim(), 80));
            if (list.Count >= maxItems) break;
        }
        return list;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// Factory honouring the provider slot. "qwen" is deliberately unimplemented (VIS-02 keeps it
/// as a configurable second-provider candidate only, plan [D9]).
/// </summary>
public static class VisionClientFactory
{
    public static IVisionClient Create(VisionConfig config, HttpClient? http = null) =>
        config.Provider.ToLowerInvariant() switch
        {
            "doubao" or "ark" => new DoubaoVisionClient(config, http),
            "qwen" => throw new NotSupportedException(
                "vision provider 'qwen' is a reserved slot and not implemented yet (plan [D9])"),
            var p => throw new NotSupportedException($"unknown vision provider '{p}'"),
        };
}
