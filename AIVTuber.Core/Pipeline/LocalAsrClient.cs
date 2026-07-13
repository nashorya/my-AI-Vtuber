using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// IAsrClient backed by the local asr_server.py (Qwen3-ASR-0.6B via FastAPI).
/// POSTs raw int16 PCM to POST /recognize and reads {"text":"..."}.
/// </summary>
public sealed class LocalAsrClient : IAsrClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public LocalAsrClient(string baseUrl)
        : this(baseUrl, SharedHttp)
    {
    }

    internal LocalAsrClient(string baseUrl, HttpClient http)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http;
    }

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        using var content = new ByteArrayContent(pcm16k);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync($"{_baseUrl}/recognize?sr=16000", content, cancellationToken);
        await EnsureSuccessWithBodyAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AsrResponse>(cancellationToken: cancellationToken);
        return new AsrResult(result?.Text ?? "");
    }

    public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chunks = new List<byte[]>();
        await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
            chunks.Add(chunk);

        if (chunks.Count > 0)
        {
            var pcm = chunks.SelectMany(c => c).ToArray();
            var result = await RecognizeAsync(pcm, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Text)) yield return result;
        }
    }

    /// <summary>Returns the sidecar's structured loading/ready/failed health state.</summary>
    public async Task<LocalAsrHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{_baseUrl}/health", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LocalAsrHealthResponse? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<LocalAsrHealthResponse>(body, JsonOptions);
            }
            catch (JsonException)
            {
                // The raw body is returned below so malformed sidecars remain diagnosable.
            }

            var status = ParseStatus(payload?.Status);
            var detail = payload?.Detail ?? payload?.Message;
            if (!response.IsSuccessStatusCode)
            {
                var bodyDetail = string.IsNullOrWhiteSpace(detail) ? body : detail;
                detail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {bodyDetail}".TrimEnd();
                if (status == LocalAsrHealthStatus.Unknown)
                    status = LocalAsrHealthStatus.Failed;
            }

            if (status == LocalAsrHealthStatus.Unknown && string.IsNullOrWhiteSpace(detail))
                detail = string.IsNullOrWhiteSpace(body) ? "Health response did not contain a status." : body;

            return new LocalAsrHealth(status, detail, payload?.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LocalAsrHealth(LocalAsrHealthStatus.Unknown, ex.Message);
        }
    }

    /// <summary>Returns true only when the local ASR explicitly reports ready.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetHealthAsync(cancellationToken);
        return health.Status == LocalAsrHealthStatus.Ready;
    }

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body) ? "" : $": {body}";
        throw new HttpRequestException(
            $"Local ASR HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}",
            null,
            response.StatusCode);
    }

    private sealed record AsrResponse(string Text);
    private sealed record LocalAsrHealthResponse(string? Status, string? Detail, string? Message, string? Version);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static LocalAsrHealthStatus ParseStatus(string? value) => value?.ToLowerInvariant() switch
    {
        "loading" => LocalAsrHealthStatus.Loading,
        "ready" => LocalAsrHealthStatus.Ready,
        "failed" => LocalAsrHealthStatus.Failed,
        _ => LocalAsrHealthStatus.Unknown,
    };
}

public enum LocalAsrHealthStatus
{
    Unknown,
    Loading,
    Ready,
    Failed,
}

public sealed record LocalAsrHealth(
    LocalAsrHealthStatus Status,
    string? Detail = null,
    string? Version = null);
