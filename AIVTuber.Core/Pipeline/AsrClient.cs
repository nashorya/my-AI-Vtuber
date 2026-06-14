using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// ASR client using OpenAI-compatible Whisper API format.
/// Supports both single-shot and streaming recognition.
/// </summary>
public sealed class AsrClient : IAsrClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public AsrClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<string> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        // Convert PCM 16kHz 16-bit mono to WAV format for API compatibility
        var wavData = PcmToWav(pcm16k, 16000, 1, 16);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavData);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("zh"), "language");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/audio/transcriptions")
        {
            Content = content
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<TranscriptionResponse>(json);
        return result?.Text ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // For streaming ASR, accumulate audio and then recognize
        // Most ASR APIs don't support true streaming; we buffer and do single-shot
        var audioBuffer = new List<byte>();

        await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
        {
            audioBuffer.AddRange(chunk);
        }

        if (audioBuffer.Count > 0)
        {
            var result = await RecognizeAsync(audioBuffer.ToArray(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(result))
            {
                yield return result;
            }
        }
    }

    /// <summary>
    /// Converts raw PCM data to WAV format byte array.
    /// </summary>
    internal static byte[] PcmToWav(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length); // File size - 8
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8); // Byte rate
        writer.Write((short)(channels * bitsPerSample / 8)); // Block align
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        writer.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class TranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}