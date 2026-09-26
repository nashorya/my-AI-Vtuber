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
    }

    [JsonIgnore]
    public Uri AuthServerUri => new(AuthServer.EndsWith('/') ? AuthServer : AuthServer + "/");

    /// <summary>Returns null for a public build (no profile file). Throws with an operator-readable
    /// message when the file exists but is unusable — never falls back to config.json keys.</summary>
    public static DistributionProfile? TryLoad(string contentRoot)
    {
        var path = Path.Combine(contentRoot, DirectoryName, FileName);
        if (!File.Exists(path)) return null;
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
    }

    /// <summary>Overlays the managed provider settings onto <paramref name="config"/>. Called on
    /// every load and every runtime apply so UI edits cannot swap in other keys.</summary>
    public void ApplyTo(AppConfig config)
    {
        var llm = Providers.Llm;
        config.Llm.Provider = llm.Provider;
        if (!string.IsNullOrWhiteSpace(llm.BaseUrl)) config.Llm.BaseUrl = llm.BaseUrl;
        if (!string.IsNullOrWhiteSpace(llm.Model)) config.Llm.Model = llm.Model;
        config.Llm.ApiKeys.Clear();
        config.Llm.StoreKey(llm.ApiKey);

        var asr = Providers.Asr;
        config.Asr.Provider = asr.Provider;
        if (asr.Model is not null) config.Asr.Model = asr.Model;
        if (asr.AppId is not null) config.Asr.AppId = asr.AppId;
        config.Asr.ApiKeys.Clear();
        config.Asr.StoreKey(asr.ApiKey);

        var tts = Providers.Tts;
        config.Tts.Provider = tts.Provider;
        if (tts.Model is not null) config.Tts.Model = tts.Model;
        if (tts.VoiceId is not null) config.Tts.VoiceId = tts.VoiceId;
        if (tts.GroupId is not null) config.Tts.GroupId = tts.GroupId;
        config.Tts.ApiKeys.Clear();
        config.Tts.StoreKey(tts.ApiKey);
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
