using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;

namespace AIVTuber.Tests.Auth;

public sealed class DistributionProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dist-{Guid.NewGuid():N}");

    public DistributionProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal static string ProfileJson(
        string profileId = "streamer-017", int revision = 2, string llmKey = "sk-test-llm-aaaa1111",
        string asrProvider = "aliyun", string ttsProvider = "minimax", string llmBaseUrl = "https://api.deepseek.com",
        string authServer = "https://auth.example.invalid/") => $$"""
        {
          "format": 1,
          "profile_id": "{{profileId}}",
          "account": "alice",
          "credential_revision": {{revision}},
          "credential_scope": "dedicated",
          "auth_server": "{{authServer}}",
          "providers": {
            "llm": { "provider": "deepseek", "base_url": "{{llmBaseUrl}}", "model": "deepseek-chat", "api_key": "{{llmKey}}" },
            "asr": { "provider": "{{asrProvider}}", "model": "paraformer-realtime-v2", "api_key": "sk-test-asr-bbbb2222" },
            "tts": { "provider": "{{ttsProvider}}", "model": "speech-2.8-hd", "voice_id": "voice-x", "api_key": "sk-test-tts-cccc3333" }
          }
        }
        """;

    private void WriteProfile(string json, string? root = null)
    {
        var dir = Path.Combine(root ?? _root, DistributionProfile.DirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DistributionProfile.FileName), json);
    }

    [Fact]
    public void PublicBuild_HasNoProfile()
    {
        Assert.Null(DistributionProfile.TryLoad(_root));
    }

    [Fact]
    public void Profile_OverridesProviderSettingsAndKeys()
    {
        WriteProfile(ProfileJson());
        var profile = DistributionProfile.TryLoad(_root)!;
        var config = new AppConfig();
        config.Llm.ApiKey = "sk-user-typed";

        profile.ApplyTo(config);

        Assert.Equal("streamer-017", profile.ProfileId);
        Assert.Equal(2, profile.CredentialRevision);
        Assert.Equal("sk-test-llm-aaaa1111", config.Llm.ApiKey);
        Assert.Equal("https://api.deepseek.com", config.Llm.BaseUrl);
        Assert.Equal("aliyun", config.Asr.Provider);
        Assert.Equal("sk-test-asr-bbbb2222", config.Asr.ApiKey);
        Assert.Equal("minimax", config.Tts.Provider);
        Assert.Equal("voice-x", config.Tts.VoiceId);
        Assert.Equal("sk-test-tts-cccc3333", config.Tts.ApiKey);
    }

    [Theory]
    [InlineData("local", "minimax", "https://api.deepseek.com", "本地 ASR")]
    [InlineData("aliyun", "dots", "https://api.deepseek.com", "自托管 TTS")]
    [InlineData("aliyun", "minimax", "http://127.0.0.1:11434/v1", "本地 LLM")]
    public void Profile_RejectsLocalInference(string asr, string tts, string llmBase, string expected)
    {
        WriteProfile(ProfileJson(asrProvider: asr, ttsProvider: tts, llmBaseUrl: llmBase));

        var ex = Assert.Throws<DistributionProfileException>(() => DistributionProfile.TryLoad(_root));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Profile_MissingAuthServer_IsExplicitError()
    {
        WriteProfile(ProfileJson(authServer: ""));

        var ex = Assert.Throws<DistributionProfileException>(() => DistributionProfile.TryLoad(_root));
        Assert.Contains("auth_server", ex.Message);
    }

    [Fact]
    public void KeyHint_ShowsOnlyTail()
    {
        Assert.Equal("已配置（…1111）", DistributionProfile.KeyHint("sk-test-llm-aaaa1111"));
        Assert.Equal("未配置", DistributionProfile.KeyHint(""));
        Assert.Equal("已配置", DistributionProfile.KeyHint("abc"));
    }

    [Fact]
    public void ConfigManager_DoesNotPersistManagedKeys_AndReloadUsesCurrentProfile()
    {
        WriteProfile(ProfileJson());
        var configPath = Path.Combine(_root, "config.json");
        var manager = new ConfigManager(configPath) { Profile = DistributionProfile.TryLoad(_root) };

        var loaded = manager.Load();
        Assert.Equal("sk-test-llm-aaaa1111", loaded.Llm.ApiKey);
        manager.Save(loaded);
        var onDisk = File.ReadAllText(configPath);
        Assert.DoesNotContain("sk-test-llm-aaaa1111", onDisk);
        Assert.DoesNotContain("sk-test-asr-bbbb2222", onDisk);
        Assert.DoesNotContain("sk-test-tts-cccc3333", onDisk);

        // Operator ships revision 3 with a rotated key: no silent fallback to the old one.
        WriteProfile(ProfileJson(revision: 3, llmKey: "sk-test-llm-rotated-9999"));
        var rotated = new ConfigManager(configPath) { Profile = DistributionProfile.TryLoad(_root) }.Load();
        Assert.Equal("sk-test-llm-rotated-9999", rotated.Llm.ApiKey);
        Assert.DoesNotContain("aaaa1111", string.Join(",", rotated.Llm.ApiKeys.Values));
    }

    [Fact]
    public void ConfigManager_WithoutProfile_KeepsExistingBehaviour()
    {
        var configPath = Path.Combine(_root, "config.json");
        var manager = new ConfigManager(configPath);
        var config = manager.Load();
        config.Llm.StoreKey("sk-own-key");

        manager.Save(config);

        Assert.Contains("sk-own-key", File.ReadAllText(configPath));
    }

    [Fact]
    public void RevisionGuard_RejectsRollbackToOlderCredentials()
    {
        var statePath = Path.Combine(_root, "distribution-state.json");
        WriteProfile(ProfileJson(revision: 3));
        CredentialRevisionGuard.Check(DistributionProfile.TryLoad(_root)!, statePath);

        WriteProfile(ProfileJson(revision: 2));
        var ex = Assert.Throws<DistributionProfileException>(() =>
            CredentialRevisionGuard.Check(DistributionProfile.TryLoad(_root)!, statePath));
        Assert.Contains("低于本机已使用过的修订号 3", ex.Message);

        WriteProfile(ProfileJson(revision: 4));
        CredentialRevisionGuard.Check(DistributionProfile.TryLoad(_root)!, statePath);
        Assert.Contains("4", File.ReadAllText(statePath));
    }

    [Fact]
    public void RevisionGuard_IsPerProfile()
    {
        var statePath = Path.Combine(_root, "distribution-state.json");
        WriteProfile(ProfileJson(profileId: "streamer-017", revision: 5));
        CredentialRevisionGuard.Check(DistributionProfile.TryLoad(_root)!, statePath);

        WriteProfile(ProfileJson(profileId: "streamer-018", revision: 1));
        var ex = Assert.Throws<DistributionProfileException>(() =>
            CredentialRevisionGuard.Check(DistributionProfile.TryLoad(_root)!, statePath));
        Assert.Contains("streamer-017", ex.Message);
    }

    [Fact]
    public void TwoStreamerPackages_CarryTheirOwnKeys()
    {
        var rootA = Path.Combine(_root, "a");
        var rootB = Path.Combine(_root, "b");
        WriteProfile(ProfileJson(profileId: "streamer-017", llmKey: "sk-test-A-0001"), rootA);
        WriteProfile(ProfileJson(profileId: "streamer-018", llmKey: "sk-test-B-0002"), rootB);

        var a = new ConfigManager(Path.Combine(rootA, "config.json")) { Profile = DistributionProfile.TryLoad(rootA) }.Load();
        var b = new ConfigManager(Path.Combine(rootB, "config.json")) { Profile = DistributionProfile.TryLoad(rootB) }.Load();

        Assert.Equal("sk-test-A-0001", a.Llm.ApiKey);
        Assert.Equal("sk-test-B-0002", b.Llm.ApiKey);
    }
}
