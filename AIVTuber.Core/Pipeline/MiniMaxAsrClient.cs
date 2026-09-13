using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// MiniMax Speech-to-Text (<c>POST /v1/speech_to_text</c>).
/// Pipeline audio is 16 kHz mono PCM; the API requires a container, so we wrap WAV.
/// </summary>
public sealed class MiniMaxAsrClient : IAsrClient, IDisposable
{
    public const string DefaultUrl = "https://api.minimaxi.com/v1/speech_to_text";
    public const string DefaultModel = "asr-1.0";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly bool _ownsHttp;

    public MiniMaxAsrClient(string apiKey, string? model = null)
        : this(apiKey, model, new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, ownsHttp: true)
    {
    }

    internal MiniMaxAsrClient(string apiKey, string? model, HttpClient http, bool ownsHttp = false)
    {
        _apiKey = apiKey ?? "";
        _model = NormalizeModel(model);
        _http = http;
        _ownsHttp = ownsHttp;
    }

    internal static string NormalizeModel(string? model)
    {
        var trimmed = model?.Trim() ?? "";
        return trimmed.StartsWith("asr-", StringComparison.OrdinalIgnoreCase) ? trimmed : DefaultModel;
    }

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        if (pcm16k is null || pcm16k.Length == 0)
            return new AsrResult("");

        var wav = AsrClient.PcmToWav(pcm16k, 16000, 1, 16);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");
        content.Add(new StringContent(_model), "model");
        content.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, DefaultUrl) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.TryAddWithoutValidation("language", "zh");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AsrResp>(json);
        return new AsrResult(parsed?.Text ?? "");
    }

    public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new List<byte>();
        await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
            buffer.AddRange(chunk);

        if (buffer.Count == 0) yield break;
        var result = await RecognizeAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Text))
            yield return result;
    }

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = string.IsNullOrWhiteSpace(body) ? "" : $": {body}";
        throw new HttpRequestException(
            $"MiniMax ASR HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}",
            null,
            response.StatusCode);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private sealed class AsrResp
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
