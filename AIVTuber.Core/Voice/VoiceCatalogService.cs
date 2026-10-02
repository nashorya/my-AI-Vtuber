using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Core.Voice;

/// <summary>Asks a vendor which voice ids the account can use.</summary>
public interface IVoiceAvailabilitySource
{
    Task<IReadOnlySet<string>> GetUsableVoiceIdsAsync(TtsConfig tts, CancellationToken ct);
}

/// <summary>
/// MiniMax <c>POST /v1/get_voice</c> (voice_type=all) with the managed Bearer key, on the same
/// host the TTS WebSocket uses. Only voice ids are read: system voices carry names, cloned and
/// generated voices do not have to, and the account-wide list is not what a streamer should see —
/// the operator catalog decides that. No preview URL is assumed.
/// </summary>
public sealed class MiniMaxVoiceAvailabilitySource(HttpClient http) : IVoiceAvailabilitySource
{
    public static readonly Uri Endpoint = new("https://api.minimaxi.com/v1/get_voice");

    public async Task<IReadOnlySet<string>> GetUsableVoiceIdsAsync(TtsConfig tts, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { voice_type = "all" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tts.ApiKey);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (root.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.TryGetProperty("status_code", out var code) && code.ValueKind == JsonValueKind.Number &&
            code.GetInt32() != 0)
            throw new InvalidOperationException($"MiniMax get_voice status_code={code.GetInt32()}");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in new[] { "system_voice", "voice_cloning", "voice_generation" })
        {
            if (!root.TryGetProperty(group, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("voice_id", out var id) && id.GetString() is { Length: > 0 } voiceId)
                    ids.Add(voiceId);
        }
        return ids;
    }
}

/// <summary>Result of refreshing the catalog's availability.</summary>
public sealed record VoiceCatalogRefresh(bool Checked, UserFacingError? Error);

/// <summary>
/// Checks the operator voice catalog against the vendor (V01). The catalog itself always comes
/// from the private profile; the vendor only marks entries usable or not. A failed check keeps
/// the previous marks and the current selection.
/// </summary>
public sealed class VoiceCatalogService(
    Func<DistributionProfile?> profile,
    Func<TtsConfig> tts,
    Func<ICloudAccess> cloud,
    Func<string, IVoiceAvailabilitySource?> sourceForProvider)
{
    public static IVoiceAvailabilitySource? DefaultSource(string provider, HttpClient http) =>
        provider.Trim().ToLowerInvariant() == "minimax" ? new MiniMaxVoiceAvailabilitySource(http) : null;

    public async Task<VoiceCatalogRefresh> RefreshAsync(ConfigViewModel target, CancellationToken ct = default)
    {
        var p = profile();
        if (p is null || p.AvailableVoices.Count == 0) return new(false, null);
        var config = tts();
        var source = sourceForProvider(config.Provider);
        if (source is null || !cloud().IsAllowed) return new(false, null); // stays "unknown"
        try
        {
            var usable = await source.GetUsableVoiceIdsAsync(config, ct).ConfigureAwait(false);
            target.SetVoiceAvailability(p.AvailableVoices.ToDictionary(
                v => v.Id,
                v => usable.Contains(v.VoiceId) ? VoiceAvailability.Available : VoiceAvailability.Unavailable));
            return new(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(false, UserErrorMapper.FromException(ex, ErrorArea.VoiceList)
                ?? UserErrorMapper.Custom("voice_list_unavailable", ErrorArea.VoiceList,
                    "暂时无法加载音色列表，已保留当前音色。", "重试加载", true));
        }
    }
}
