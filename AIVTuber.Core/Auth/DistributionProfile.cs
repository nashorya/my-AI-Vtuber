using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Auth;

public sealed class DistributionProfileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Per-streamer private configuration injected at packaging time (DIST-01/02). Present only in
/// private packages: <c>distribution/profile.json</c> under the content root. Its presence puts
/// the app in distribution mode — account login required, cloud providers only (AUTH-09).
/// Provider settings in the profile override config.json and are never written back to it.
/// <para>Two kinds of data are kept apart (A2): the operator-managed service route
/// (provider, endpoint, model, key) is always taken from the profile or an explicit vendor
/// preset, never from config.json; the streamer's own choices (persona, selected voice, speed,
/// devices) live in config.json. The voice is the one overlap: the profile names the default
/// voice and the catalog the streamer may choose from, config.json holds the choice.</para>
/// </summary>
public sealed class DistributionProfile
{
    public const string DirectoryName = "distribution";
    public const string FileName = "profile.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public int Format { get; init; }
    public string ProfileId { get; init; } = "";
    public string Account { get; init; } = "";
    public int CredentialRevision { get; init; }
    /// <summary>"dedicated" or a description of who else shares these keys; shown in diagnostics.</summary>
    public string CredentialScope { get; init; } = "";
    public string AuthServer { get; init; } = "";
    public ProviderSet Providers { get; init; } = new();

    public sealed class ProviderSet
    {
        public ProviderEntry Llm { get; init; } = new();
        public ProviderEntry Asr { get; init; } = new();
        public ProviderEntry Tts { get; init; } = new();
    }

    public sealed class ProviderEntry
    {
        public string Provider { get; init; } = "";
        public string? BaseUrl { get; init; }
        public string? Model { get; init; }
        public string ApiKey { get; init; } = "";
        public string? AppId { get; init; }
        public string? VoiceId { get; init; }
        public string? GroupId { get; init; }
        /// <summary>ASR only: Tencent SecretId (tencent_realtime).</summary>
        public string? SecretId { get; init; }
        /// <summary>ASR only: Volcano resource id (volcano_realtime).</summary>
        public string? ResourceId { get; init; }
        /// <summary>TTS only (MiniMax): legacy / streaming / bidi. Omitted = legacy.</summary>
        public string? Transport { get; init; }
        /// <summary>TTS only (MiniMax bidi): the account-region WSS host the managed key is sent to.</summary>
        public string? BidiHost { get; init; }
        /// <summary>TTS only: the voices this streamer may pick from. <see cref="VoiceId"/>
        /// is the operator default and is always selectable.</summary>
        public List<VoiceChoice> Voices { get; init; } = [];
    }

    /// <summary>One entry of the operator-maintained voice catalog. <see cref="Id"/> is what the
    /// UI sees; <see cref="VoiceId"/> is the provider's voice id and stays in the backend.</summary>
    public sealed class VoiceChoice
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string VoiceId { get; init; } = "";
    }

    /// <summary>Choice id used for the operator default when it is not itself in the catalog.</summary>
    public const string DefaultVoiceChoiceId = "default";

    [JsonIgnore]
    public Uri AuthServerUri => new(AuthServer.EndsWith('/') ? AuthServer : AuthServer + "/");

    /// <summary>Returns null for a public build (no profile file). Throws with an operator-readable
    /// message when the file exists but is unusable — never falls back to config.json keys.</summary>
    public static DistributionProfile? TryLoad(string contentRoot)
    {
        var path = Path.Combine(contentRoot, DirectoryName, FileName);
        return File.Exists(path) ? LoadFile(path) : null;
    }

    /// <summary>Loads and validates a profile file at an explicit path (used by the packager).</summary>
    public static DistributionProfile LoadFile(string path)
    {
        DistributionProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<DistributionProfile>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new DistributionProfileException($"专属配置 {FileName} 格式错误：{ex.Message}", ex);
        }
        if (profile is null) throw new DistributionProfileException($"专属配置 {FileName} 为空。");
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (Format != 1) throw new DistributionProfileException($"不支持的专属配置格式 {Format}，请使用与本程序匹配的专属包。");
        Require(ProfileId, "profile_id");
        Require(Account, "account");
        Require(AuthServer, "auth_server");
        if (!Uri.TryCreate(AuthServerUri.ToString(), UriKind.Absolute, out var auth) ||
            auth.Scheme is not ("https" or "http"))
            throw new DistributionProfileException("专属配置 auth_server 不是有效的 http(s) 地址。");
        if (CredentialRevision < 1) throw new DistributionProfileException("专属配置缺少 credential_revision。");

        Require(Providers.Llm.Provider, "providers.llm.provider");
        Require(Providers.Llm.ApiKey, "providers.llm.api_key");
        Require(Providers.Asr.Provider, "providers.asr.provider");
        Require(Providers.Asr.ApiKey, "providers.asr.api_key");
        Require(Providers.Tts.Provider, "providers.tts.provider");
        Require(Providers.Tts.ApiKey, "providers.tts.api_key");

        // Distribution builds use vendor APIs only (AUTH-09).
        if (Providers.Asr.Provider.Equals("local", StringComparison.OrdinalIgnoreCase))
            throw new DistributionProfileException("分发版不允许本地 ASR（providers.asr.provider=local）。");
        if (Providers.Tts.Provider.ToLowerInvariant() is "dots" or "dots-tts")
            throw new DistributionProfileException("分发版不允许自托管 TTS（dots）。");
        if (IsLocalEndpoint(Providers.Llm.BaseUrl))
            throw new DistributionProfileException("分发版不允许本地 LLM（base_url 指向本机）。");
        if (string.Equals(Providers.Tts.Transport, "bidi", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(Providers.Tts.BidiHost))
            throw new DistributionProfileException("专属配置 providers.tts.transport=bidi 时必须写 bidi_host，不能沿用本机配置。");
        if (IsLocalEndpoint(Providers.Tts.BidiHost) ||
            Providers.Tts.BidiHost is { } host &&
            (host.Contains("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.", StringComparison.Ordinal)))
            throw new DistributionProfileException("分发版不允许把语音服务指向本机（bidi_host）。");

        // The managed route must resolve on its own: an omitted endpoint or model may only come
        // from a built-in vendor preset, never from whatever config.json happens to hold (U03).
        _ = ResolveLlmRoute();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var voice in Providers.Tts.Voices)
        {
            Require(voice.Id, "providers.tts.voices[].id");
            Require(voice.Name, "providers.tts.voices[].name");
            Require(voice.VoiceId, "providers.tts.voices[].voice_id");
            if (!ids.Add(voice.Id))
                throw new DistributionProfileException($"专属配置音色 id「{voice.Id}」重复。");
        }
        if (Providers.Tts.Voices.Count > 0 && string.IsNullOrWhiteSpace(Providers.Tts.VoiceId))
            throw new DistributionProfileException("专属配置提供了音色目录时必须同时指定默认音色 providers.tts.voice_id。");
    }

    /// <summary>The LLM endpoint and model the managed key may be sent to.</summary>
    public (string BaseUrl, string Model) ResolveLlmRoute()
    {
        var llm = Providers.Llm;
        var preset = ProviderSecrets.TryPreset(llm.Provider);
        var baseUrl = !string.IsNullOrWhiteSpace(llm.BaseUrl) ? llm.BaseUrl.Trim() : preset?.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new DistributionProfileException(
                $"专属配置缺少 providers.llm.base_url：厂商「{llm.Provider}」没有内置地址，不能沿用本机配置。");
        var model = !string.IsNullOrWhiteSpace(llm.Model) ? llm.Model.Trim() : preset?.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new DistributionProfileException(
                $"专属配置缺少 providers.llm.model：厂商「{llm.Provider}」没有内置默认模型。");
        return (baseUrl, model);
    }

    /// <summary>Voices the streamer may choose from: the operator default first (when it is not
    /// already listed), then the catalog. Empty when the profile does not manage voices.</summary>
    [JsonIgnore]
    public IReadOnlyList<VoiceChoice> AvailableVoices
    {
        get
        {
            var tts = Providers.Tts;
            var list = new List<VoiceChoice>();
            if (!string.IsNullOrWhiteSpace(tts.VoiceId) &&
                !tts.Voices.Any(v => string.Equals(v.VoiceId, tts.VoiceId, StringComparison.Ordinal)))
                list.Add(new VoiceChoice { Id = DefaultVoiceChoiceId, Name = "默认音色", VoiceId = tts.VoiceId });
            list.AddRange(tts.Voices);
            return list;
        }
    }

    public VoiceChoice? FindVoiceByChoiceId(string? choiceId) =>
        AvailableVoices.FirstOrDefault(v => string.Equals(v.Id, choiceId, StringComparison.Ordinal));

    public VoiceChoice? FindVoiceByProviderId(string? voiceId) =>
        AvailableVoices.FirstOrDefault(v => string.Equals(v.VoiceId, voiceId, StringComparison.Ordinal));

    /// <summary>Keeps the streamer's voice when this profile still offers it; otherwise falls back
    /// to the operator default — never to "the first item" — and says so.</summary>
    public string ResolveVoice(string? current, out string? notice)
    {
        notice = null;
        var fallback = Providers.Tts.VoiceId;
        if (string.IsNullOrWhiteSpace(fallback)) return current ?? ""; // profile does not manage voices
        if (!string.IsNullOrWhiteSpace(current) && FindVoiceByProviderId(current) is not null) return current;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var name = FindVoiceByProviderId(fallback)?.Name ?? "默认音色";
            notice = $"之前选择的音色在当前配置中不可用，已改用「{name}」。";
        }
        return fallback;
    }

    /// <summary>Overlays the managed service route onto <paramref name="config"/>. Called on
    /// every load and every runtime apply so UI edits cannot swap in other keys or endpoints.
    /// Every managed field is written unconditionally (an omitted optional value becomes the
    /// provider default), so nothing left in config.json can pair with the managed key.
    /// The streamer's voice choice is kept when the profile offers it.</summary>
    /// <returns>A user-facing notice when the saved voice had to be replaced, otherwise null.</returns>
    public string? ApplyTo(AppConfig config)
    {
        var llm = Providers.Llm;
        var (baseUrl, model) = ResolveLlmRoute();
        config.Llm.Provider = llm.Provider;
        config.Llm.BaseUrl = baseUrl;
        config.Llm.Model = model;
        config.Llm.ApiKeys.Clear();
        config.Llm.Models.Clear();
        config.Llm.StoreKey(llm.ApiKey);

        var asr = Providers.Asr;
        config.Asr.Provider = asr.Provider;
        config.Asr.Model = asr.Model ?? "";
        config.Asr.AppId = asr.AppId ?? "";
        config.Asr.SecretId = asr.SecretId ?? "";
        config.Asr.ResourceId = asr.ResourceId ?? "";
        config.Asr.ApiKeys.Clear();
        config.Asr.StoreKey(asr.ApiKey);

        var tts = Providers.Tts;
        config.Tts.Provider = tts.Provider;
        config.Tts.Model = tts.Model ?? "";
        config.Tts.GroupId = tts.GroupId ?? "";
        config.Tts.Transport = string.IsNullOrWhiteSpace(tts.Transport) ? "legacy" : tts.Transport;
        config.Tts.BidiHost = tts.BidiHost ?? "";
        config.Tts.VoiceId = ResolveVoice(config.Tts.VoiceId, out var voiceNotice);
        config.Tts.ApiKeys.Clear();
        config.Tts.StoreKey(tts.ApiKey);

        // Vision has no operator-managed route in the profile; a key and host typed into
        // config.json must not run inside a distribution package.
        config.Vision.Enabled = false;
        return voiceNotice;
    }

    /// <summary>Removes managed secrets before config.json is written (DIST-04/05).</summary>
    public static void StripManagedSecrets(AppConfig config)
    {
        config.Llm.ApiKey = "";
        config.Llm.ApiKeys.Clear();
        config.Asr.ApiKey = "";
        config.Asr.ApiKeys.Clear();
        config.Tts.ApiKey = "";
        config.Tts.ApiKeys.Clear();
    }

    /// <summary>Diagnostics-safe description: ids and revision, key tails only.</summary>
    public string Describe() =>
        $"profile={ProfileId} account={Account} credential_revision={CredentialRevision} scope={CredentialScope} " +
        $"llm={Providers.Llm.Provider}:{KeyHint(Providers.Llm.ApiKey)} " +
        $"asr={Providers.Asr.Provider}:{KeyHint(Providers.Asr.ApiKey)} " +
        $"tts={Providers.Tts.Provider}:{KeyHint(Providers.Tts.ApiKey)}";

    public static string KeyHint(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "未配置";
        return key.Length < 12 ? "已配置" : $"已配置（…{key[^4..]}）";
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DistributionProfileException($"专属配置缺少 {name}。");
    }

    private static bool IsLocalEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Refuses a profile whose credential revision is lower than one this installation has already
/// used, so rolling back to an old package cannot quietly bring a rotated key back (DIST-05).
/// The state file lives outside <c>distribution/</c> so replacing the profile does not reset it.
/// </summary>
public static class CredentialRevisionGuard
{
    public const string StateFileName = "distribution-state.json";

    private sealed record State(string ProfileId, int HighestRevision);

    public static void Check(DistributionProfile profile, string statePath)
    {
        State? state = null;
        if (File.Exists(statePath))
        {
            try { state = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)); }
            catch (JsonException) { state = null; }
        }

        if (state is not null)
        {
            if (!string.Equals(state.ProfileId, profile.ProfileId, StringComparison.Ordinal))
                throw new DistributionProfileException(
                    $"这台电脑之前使用的是 {state.ProfileId} 的专属包，现在的包属于 {profile.ProfileId}。" +
                    $"如确需更换主播，请先删除 {StateFileName}。");
            if (profile.CredentialRevision < state.HighestRevision)
                throw new DistributionProfileException(
                    $"专属包凭据修订号 {profile.CredentialRevision} 低于本机已使用过的修订号 {state.HighestRevision}，" +
                    "可能回滚到了已作废的包。请使用运营发给你的最新专属包。");
            if (profile.CredentialRevision == state.HighestRevision) return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(statePath))!;
        Directory.CreateDirectory(directory);
        var temp = statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new State(profile.ProfileId, profile.CredentialRevision)));
        File.Move(temp, statePath, overwrite: true);
    }
}
