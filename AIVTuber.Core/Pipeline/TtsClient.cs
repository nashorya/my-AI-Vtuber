using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// TTS client. Dispatches by provider:
///  - "fish-audio"/"fish": fish.audio streaming /v1/tts (raw PCM stream)
///  - "minimax": MiniMax t2a_v2 (JSON response, hex-encoded PCM)
/// All providers are asked for PCM at <see cref="AudioPlayer.DefaultSampleRate"/> so the audio
/// can be played directly (AudioPlayer decodes raw PCM at that rate; it does not decode MP3).
/// Aliyun/DashScope (WebSocket) is handled by <see cref="DashScopeTtsClient"/>.
/// </summary>
public sealed class TtsClient : ITtsClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TtsConfig _config;
    private readonly string _provider;

    public TtsClient(TtsConfig config)
    {
        _config = config;
        _provider = config.Provider.ToLowerInvariant();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        switch (_provider)
        {
            case "minimax":
                var audio = await MiniMaxSynthesizeAsync(text, voiceId, emotion, cancellationToken);
                if (audio.Length > 0) yield return audio;
                break;

            case "fish-audio":
            case "fish":
            default:
                await foreach (var chunk in FishStreamAsync(text, voiceId, emotion, cancellationToken))
                    yield return chunk;
                break;
        }
    }

    // ---- fish.audio ----

    private async IAsyncEnumerable<byte[]> FishStreamAsync(
        string text, string voiceId, string? emotion, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "s1" : _config.Model;
        // S2 supports inline emotion tags; S1 ignores them harmlessly.
        var textWithEmotion = ApplyFishEmotionTag(text, emotion);
        var json = BuildFishRequestJson(textWithEmotion, voiceId, _config.Speed, AudioPlayer.DefaultSampleRate);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.fish.audio/v1/tts")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Headers.TryAddWithoutValidation("model", model); // fish selects the model via this header (s1 / s2-pro)

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

    /// <summary>Builds the fish.audio /v1/tts request body. Speed goes under `prosody` (not `params`),
    /// and PCM is pinned to the given sample rate. No bogus `bitrate` field.</summary>
    internal static string BuildFishRequestJson(string text, string voiceId, double speed, int sampleRate)
        => JsonSerializer.Serialize(new
        {
            text,
            reference_id = voiceId,
            format = "pcm",
            sample_rate = sampleRate,
            prosody = new { speed },
            normalize = true,
            latency = "normal",
        }, JsonOptions);

    // ---- MiniMax t2a_v2 ----

    private async Task<byte[]> MiniMaxSynthesizeAsync(string text, string voiceId, string? emotion, CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "speech-2.8-hd" : _config.Model;
        // New platform: api.minimaxi.com — no GroupId query param, just Bearer token.
        const string url = "https://api.minimaxi.com/v1/t2a_v2";

        var miniMaxEmotion = MapToMiniMaxEmotion(emotion);
        var json = BuildMiniMaxRequestJson(text, voiceId, model, _config.Speed, AudioPlayer.DefaultSampleRate, miniMaxEmotion);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseMiniMaxAudio(body);
    }

    /// <summary>Builds the MiniMax t2a_v2 request body (non-streaming, PCM at the given rate).
    /// <paramref name="emotion"/> is the MiniMax emotion value or null to let the model choose.</summary>
    internal static string BuildMiniMaxRequestJson(string text, string voiceId, string model, double speed, int sampleRate, string? emotion = null)
    {
        // emotion must be omitted (not null) from JSON when not set, so we build two anonymous types.
        object voiceSetting = emotion is null
            ? new { voice_id = voiceId, speed, vol = 1.0, pitch = 0 }
            : new { voice_id = voiceId, speed, vol = 1.0, pitch = 0, emotion };

        return JsonSerializer.Serialize(new
        {
            model,
            text,
            stream = false,
            voice_setting = voiceSetting,
            audio_setting = new { sample_rate = sampleRate, format = "pcm", channel = 1 },
        }, JsonOptions);
    }

    /// <summary>Maps our internal emotion name to a MiniMax emotion value. Returns null for unknown/neutral.</summary>
    private static string? MapToMiniMaxEmotion(string? emotion) => emotion?.ToLowerInvariant() switch
    {
        "happy"     => "happy",
        "sad"       => "sad",
        "angry"     => "angry",
        "fearful"   => "fearful",
        "disgusted" => "disgusted",
        "surprised" => "surprised",
        "calm"      => "calm",
        "neutral"   => "calm",
        "whisper"   => "whisper",
        _           => null,
    };

    /// <summary>Prepends an inline emotion tag to text for Fish Audio S2. S1 ignores unknown tags.</summary>
    private static string ApplyFishEmotionTag(string text, string? emotion)
    {
        var tag = emotion?.ToLowerInvariant() switch
        {
            "happy"     => "[happy]",
            "sad"       => "[sad]",
            "angry"     => "[angry]",
            "fearful"   => "[fearful]",
            "disgusted" => "[disgusted]",
            "surprised" => "[surprised]",
            "whisper"   => "[whispers]",
            _           => null,
        };
        return tag is null ? text : $"{tag} {text}";
    }

    /// <summary>Parses a MiniMax t2a_v2 JSON response: checks base_resp.status_code, then hex-decodes
    /// data.audio into raw PCM bytes. Throws on a non-zero status code.</summary>
    internal static byte[] ParseMiniMaxAudio(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.TryGetProperty("status_code", out var statusCode) &&
            statusCode.GetInt32() != 0)
        {
            var msg = baseResp.TryGetProperty("status_msg", out var m) ? m.GetString() : "unknown error";
            throw new InvalidOperationException($"MiniMax TTS error {statusCode.GetInt32()}: {msg}");
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("audio", out var audioEl) ||
            audioEl.GetString() is not { Length: > 0 } hex)
            return [];

        return Convert.FromHexString(hex);
    }

    public void Dispose() => _httpClient.Dispose();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
