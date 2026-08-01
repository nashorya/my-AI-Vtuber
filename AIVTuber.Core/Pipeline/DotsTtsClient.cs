using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Self-hosted dots.tts over HTTP. The service must return raw 16-bit mono PCM at
/// <see cref="AudioPlayer.DefaultSampleRate"/>; dots.tts generates 48kHz natively, so the
/// resampling belongs on the server side.
/// Single-speaker fine-tunes carry no voice id, and dots.tts exposes no emotion control, so
/// both of those parameters are ignored here.
/// </summary>
public sealed class DotsTtsClient : ITtsClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TtsConfig _config;

    public DotsTtsClient(TtsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new ArgumentException("dots.tts base_url 不能为空", nameof(config));

        _config = config;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/"),
            // The first request after startup can wait on model warm-up.
            Timeout = TimeSpan.FromMinutes(3),
        };

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = voiceId;
        _ = emotion;

        if (string.IsNullOrWhiteSpace(text)) yield break;

        var body = JsonSerializer.Serialize(new
        {
            text,
            language = string.IsNullOrWhiteSpace(_config.Language) ? "ZH" : _config.Language,
            seed = _config.Seed,
            num_steps = _config.NumSteps,
            guidance_scale = _config.GuidanceScale,
            sample_rate = AudioPlayer.DefaultSampleRate,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/tts")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(
                $"dots.tts 请求失败：{(int)response.StatusCode} {error}");
        }

        EnsureSampleRateMatches(response);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            yield return buffer.AsSpan(0, read).ToArray();
        }
    }

    /// <summary>
    /// A server left at another rate produces audio that plays at the wrong speed and pitch
    /// — audible but easy to misdiagnose. When the service advertises its rate, hold it to
    /// the one the player decodes at.
    /// </summary>
    private static void EnsureSampleRateMatches(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Sample-Rate", out var values) &&
            !response.Content.Headers.TryGetValues("X-Sample-Rate", out values))
            return;

        var advertised = values.FirstOrDefault();
        if (!int.TryParse(advertised, out var rate) || rate == AudioPlayer.DefaultSampleRate)
            return;

        throw new InvalidOperationException(
            $"dots.tts 返回 {rate}Hz，播放器要求 {AudioPlayer.DefaultSampleRate}Hz。" +
            $"请把服务端 OUTPUT_SAMPLE_RATE 改为 {AudioPlayer.DefaultSampleRate}。");
    }

    public void Dispose() => _httpClient.Dispose();
}
