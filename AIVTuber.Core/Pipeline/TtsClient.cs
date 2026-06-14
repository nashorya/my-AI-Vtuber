using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Audio;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// TTS client supporting fish-audio API format.
/// Streams audio chunks as they are generated for low-latency playback.
/// </summary>
public sealed class TtsClient : ITtsClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _provider;
    private readonly string _apiKey;

    public TtsClient(string provider, string apiKey)
    {
        _provider = provider.ToLowerInvariant();
        _apiKey = apiKey;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_provider == "fish-audio")
        {
            await foreach (var chunk in FishAudioStreamAsync(text, voiceId, cancellationToken))
            {
                yield return chunk;
            }
        }
        else
        {
            // Generic: single request, yield entire response as one chunk
            var audio = await GenericSynthesizeAsync(text, voiceId, cancellationToken);
            if (audio.Length > 0)
            {
                yield return audio;
            }
        }
    }

    /// <summary>
    /// fish-audio streaming TTS: POST and read response chunks.
    /// </summary>
    private async IAsyncEnumerable<byte[]> FishAudioStreamAsync(
        string text,
        string voiceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            text,
            reference_id = voiceId,
            format = "pcm",
            // Explicitly pin the PCM sample rate so it matches AudioPlayer's decoder
            // (AudioPlayer.DefaultSampleRate). Without this the API default could
            // differ from the player's assumed rate, causing pitch/speed distortion.
            sample_rate = AudioPlayer.DefaultSampleRate,
            bitrate = 128000,
            @params = new
            {
                speed = 1.0
            }
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.fish.audio/v1/tts")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];

        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0) break;

            var chunk = new byte[bytesRead];
            Array.Copy(buffer, chunk, bytesRead);
            yield return chunk;
        }
    }

    /// <summary>
    /// Generic non-streaming TTS synthesis (fallback).
    /// </summary>
    private async Task<byte[]> GenericSynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken)
    {
        // Placeholder for other TTS providers (e.g., Volcano Engine)
        // Each provider should be implemented as a separate method
        var requestBody = new
        {
            text,
            voice_id = voiceId,
            format = "pcm",
            sample_rate = AudioPlayer.DefaultSampleRate
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_provider}/v1/tts") { Content = content };
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}