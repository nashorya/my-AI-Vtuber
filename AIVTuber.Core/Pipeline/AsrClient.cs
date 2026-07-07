using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// ASR client using OpenAI-compatible Whisper API format.
/// </summary>
public sealed class AsrClient : IAsrClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public AsrClient(string baseUrl, string apiKey, string? model = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = NormalizeModel(model);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        var wavData = PcmToWav(pcm16k, 16000, 1, 16);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavData);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(_model), "model");
        content.Add(new StringContent("zh"), "language");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/audio/transcriptions")
        {
            Content = content
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithBodyAsync(response, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<TranscriptionResponse>(json);
        return new AsrResult(result?.Text ?? string.Empty);
    }

    public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var audioBuffer = new List<byte>();
        await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
            audioBuffer.AddRange(chunk);

        if (audioBuffer.Count > 0)
        {
            var result = await RecognizeAsync(audioBuffer.ToArray(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Text))
                yield return result;
        }
    }

    internal static byte[] PcmToWav(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        writer.Flush();
        return ms.ToArray();
    }

    internal static string NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) ? "whisper-1" : model.Trim();

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body) ? "" : $": {body}";
        throw new HttpRequestException(
            $"ASR HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}",
            null,
            response.StatusCode);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class TranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
