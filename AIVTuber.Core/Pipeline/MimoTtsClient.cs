using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Xiaomi MiMo V2.5 TTS via OpenAI-compatible <c>/v1/chat/completions</c>.
/// Streams base64 <c>pcm16</c> chunks (24 kHz mono LE) from SSE <c>delta.audio.data</c>.
/// Docs: https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5
/// </summary>
public sealed class MimoTtsClient : ITtsClient, IDisposable
{
    public const string DefaultBaseUrl = "https://api.xiaomimimo.com/v1";
    public const string DefaultModel = "mimo-v2.5-tts";
    public const string DefaultVoice = "mimo_default";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TtsConfig _config;
    private readonly string _baseUrl;

    public MimoTtsClient(TtsConfig config, HttpClient? httpClient = null, string? baseUrl = null)
    {
        _config = config;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var model = string.IsNullOrWhiteSpace(_config.Model) ? DefaultModel : _config.Model.Trim();
        var voice = string.IsNullOrWhiteSpace(voiceId) ? DefaultVoice : voiceId.Trim();
        var json = BuildRequestJson(text, voice, model, emotion, _config.Speed);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        // MiMo curl samples use `api-key` (not Bearer).
        request.Headers.TryAddWithoutValidation("api-key", _config.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"MiMo TTS HTTP {(int)response.StatusCode}: {Truncate(errBody, 400)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;

            if (!TryParseSseDataLine(line, out var payload))
                continue;
            if (payload == "[DONE]")
                yield break;

            if (TryExtractAudioPcm(payload, out var pcm) && pcm.Length > 0)
                yield return pcm;
        }
    }

    /// <summary>Builds the chat.completions body for streaming pcm16 TTS.</summary>
    internal static string BuildRequestJson(
        string text, string voice, string model, string? emotion, double speed)
    {
        var assistantText = ApplyStyleTag(text, emotion);
        var userInstruction = BuildUserInstruction(emotion, speed);

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(userInstruction))
            messages.Add(new { role = "user", content = userInstruction });
        messages.Add(new { role = "assistant", content = assistantText });

        return JsonSerializer.Serialize(new
        {
            model,
            messages,
            audio = new { format = "pcm16", voice },
            stream = true,
        }, JsonOptions);
    }

    /// <summary>Maps emotion to a MiMo audio style tag on the assistant text, e.g. <c>(开心)你好</c>.</summary>
    internal static string ApplyStyleTag(string text, string? emotion)
    {
        var tag = MapToStyleTag(emotion);
        if (tag is null) return text;
        // Avoid double-tagging if caller already embedded a style bracket.
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('(') || trimmed.StartsWith('（') || trimmed.StartsWith('['))
            return text;
        return $"({tag}){text}";
    }

    internal static string? MapToStyleTag(string? emotion) => emotion?.Trim() switch
    {
        null or "" => null,
        "happy" or "开心" or "高兴" or "愉快" or "兴奋" => "开心",
        "sad" or "难过" or "悲伤" or "委屈" => "悲伤",
        "angry" or "生气" or "愤怒" => "愤怒",
        "fearful" or "恐惧" or "害怕" => "恐惧",
        "surprised" or "惊讶" => "惊讶",
        "disgusted" or "厌恶" => "冷漠",
        "shy" or "害羞" => "俏皮",
        "calm" or "neutral" or "平静" => "平静",
        "whisper" or "低语" => "温柔",
        "upset" or "无语" => "无奈",
        _ => null,
    };

    /// <summary>Optional user-role NL instruction (speed / soft emotion hint). Empty when unused.</summary>
    internal static string BuildUserInstruction(string? emotion, double speed)
    {
        var parts = new List<string>();
        if (speed >= 1.15)
            parts.Add("语速稍快");
        else if (speed > 0 && speed <= 0.85)
            parts.Add("语速稍慢");

        // Soft NL backup when we have an emotion but MapToStyleTag returned null.
        if (MapToStyleTag(emotion) is null && !string.IsNullOrWhiteSpace(emotion))
            parts.Add($"用自然的「{emotion}」语气说话");

        return parts.Count == 0 ? "" : string.Join("，", parts) + "。";
    }

    /// <summary>Accepts a raw SSE line; returns payload after <c>data:</c> prefix.</summary>
    internal static bool TryParseSseDataLine(string line, out string payload)
    {
        payload = "";
        if (string.IsNullOrEmpty(line)) return false;
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        payload = line["data:".Length..].TrimStart();
        return payload.Length > 0;
    }

    /// <summary>Extracts base64 PCM from an OpenAI-style streaming chunk JSON.</summary>
    internal static bool TryExtractAudioPcm(string json, out byte[] pcm)
    {
        pcm = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return false;

            var choice = choices[0];
            // Prefer delta.audio (streaming); fall back to message.audio (compat dump).
            JsonElement audioEl = default;
            var hasAudio = false;
            if (choice.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("audio", out audioEl))
                hasAudio = true;
            else if (choice.TryGetProperty("message", out var message)
                     && message.TryGetProperty("audio", out audioEl))
                hasAudio = true;

            if (!hasAudio) return false;

            string? b64 = null;
            if (audioEl.ValueKind == JsonValueKind.Object
                && audioEl.TryGetProperty("data", out var dataEl)
                && dataEl.ValueKind == JsonValueKind.String)
                b64 = dataEl.GetString();
            else if (audioEl.ValueKind == JsonValueKind.String)
                b64 = audioEl.GetString();

            if (string.IsNullOrEmpty(b64)) return false;
            pcm = Convert.FromBase64String(b64);
            return pcm.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
