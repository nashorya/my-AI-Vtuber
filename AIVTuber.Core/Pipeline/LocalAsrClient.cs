using System.Net.Http.Json;
using System.Runtime.CompilerServices;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// IAsrClient backed by the local asr_server.py (Qwen3-ASR-0.6B via FastAPI).
/// POSTs raw int16 PCM to POST /recognize and reads {"text":"..."}.
/// </summary>
public sealed class LocalAsrClient : IAsrClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _baseUrl;

    public LocalAsrClient(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        using var content = new ByteArrayContent(pcm16k);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var response = await _http.PostAsync($"{_baseUrl}/recognize?sr=16000", content, cancellationToken);
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

    /// <summary>Returns true if the local ASR server is reachable and ready.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/health", cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
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
}
