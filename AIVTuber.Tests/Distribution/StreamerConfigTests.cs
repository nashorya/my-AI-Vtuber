using System.Text.Json;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests.Distribution;

/// <summary>
/// A2-1: managed service route vs. streamer choices, through the production
/// <see cref="ConfigManager"/> → <see cref="ConfigViewModel"/> → <see cref="BotRuntime.ApplyConfigAsync"/>
/// chain. Only module rebuilding is replaced (no devices, no vendors).
/// </summary>
public sealed class StreamerConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"a2-{Guid.NewGuid():N}");

    public StreamerConfigTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    internal const string VoiceA = "vendor-voice-aaaa";
    internal const string VoiceB = "vendor-voice-bbbb";
    internal const string VoiceC = "vendor-voice-cccc";

    internal static string Profile(string llm = """{ "provider": "deepseek", "api_key": "sk-test-llm-aaaa1111" }""",
        int revision = 1, string voices = "") => $$"""
        {
          "format": 1, "profile_id": "streamer-017", "account": "alice", "credential_revision": {{revision}},
          "auth_server": "https://auth.example.invalid/",
          "providers": {
            "llm": {{llm}},
            "asr": { "provider": "aliyun", "api_key": "sk-test-asr-bbbb2222" },
            "tts": { "provider": "minimax", "voice_id": "{{VoiceA}}", "api_key": "sk-test-tts-cccc3333",
                     "voices": [ {{(voices.Length > 0 ? voices : DefaultVoices)}} ] }
          }
        }
        """;

    internal const string DefaultVoices = $$"""
        { "id": "a", "name": "温柔姐姐", "description": "默认", "voice_id": "{{VoiceA}}" },
        { "id": "b", "name": "元气少女", "voice_id": "{{VoiceB}}" }
        """;

    private void WriteProfile(string json)
    {
        var dir = Path.Combine(_root, DistributionProfile.DirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DistributionProfile.FileName), json);
    }

    private string ConfigPath => Path.Combine(_root, "config.json");

    private sealed class Stack : IAsyncDisposable
    {
        public required ConfigManager Manager;
        public required BotRuntime Runtime;
        public required ConfigViewModel Vm;
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private Stack Build(Func<AppConfig, Task>? applyOverride = null)
    {
        var profile = DistributionProfile.TryLoad(_root)!;
        var manager = new ConfigManager(ConfigPath) { Profile = profile };
        var config = manager.Load();
        var runtime = new BotRuntime(config, _root, _ => Task.CompletedTask);
        runtime.UseCloudAccess(UnrestrictedCloudAccess.Instance, profile);
        var vm = new ConfigViewModel(runtime.CurrentConfig, ["麦克风"], manager.Save,
            applyOverride ?? runtime.ApplyConfigAsync)
        {
            Profile = profile,
            ReadEffectiveConfig = () => runtime.CurrentConfig,
            ReadEffectiveRevision = () => runtime.ActiveConfigRevision,
            ReadApplyNotice = () => runtime.LastApplyNotice,
        };
        vm.VoiceNotice = manager.LastLoadNotice;
        return new Stack { Manager = manager, Runtime = runtime, Vm = vm };
    }

    private static string Relaxed(object value) => JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── U03 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingManagedEndpoint_DoesNotInheritUserEndpoint()
    {
        WriteProfile(Profile()); // deepseek, no base_url, no model
        File.WriteAllText(ConfigPath, """
            { "llm": { "provider": "custom", "base_url": "https://collector.example.invalid/v1", "model": "x" } }
            """);

        await using var s = Build();
        var loaded = s.Runtime.CurrentConfig;
        Assert.Equal("https://api.deepseek.com", loaded.Llm.BaseUrl);
        Assert.Equal("deepseek-chat", loaded.Llm.Model);
        Assert.Equal("sk-test-llm-aaaa1111", loaded.Llm.ApiKey);

        // A tampered candidate reaching the runtime directly is re-pinned as a whole.
        var tampered = s.Runtime.CurrentConfig;
        tampered.Llm.BaseUrl = "http://127.0.0.1:9/v1";
        tampered.Llm.Provider = "custom";
        tampered.Llm.Model = "other";
        await s.Runtime.ApplyConfigAsync(tampered);
        Assert.Equal("https://api.deepseek.com", s.Runtime.CurrentConfig.Llm.BaseUrl);
        Assert.Equal("deepseek-chat", s.Runtime.CurrentConfig.Llm.Model);
    }

    [Fact]
    public void CustomProviderWithoutEndpoint_IsAConfigurationError_NotASilentMerge()
    {
        WriteProfile(Profile(llm: """{ "provider": "custom", "model": "m", "api_key": "sk-test-llm-aaaa1111" }"""));

        var ex = Assert.Throws<DistributionProfileException>(() => DistributionProfile.TryLoad(_root));
        Assert.Contains("base_url", ex.Message);
    }

    [Fact]
    public void ManagedAsrAndTtsModels_DoNotInheritConfigJson()
    {
        WriteProfile(Profile());
        File.WriteAllText(ConfigPath, """
            { "asr": { "model": "user-asr-model", "app_id": "user-app" }, "tts": { "model": "user-tts", "group_id": "g" } }
            """);

        var config = new ConfigManager(ConfigPath) { Profile = DistributionProfile.TryLoad(_root) }.Load();

        Assert.Equal("", config.Asr.Model);
        Assert.Equal("", config.Asr.AppId);
        Assert.Equal("", config.Tts.Model);
        Assert.Equal("", config.Tts.GroupId);
    }

    [Fact]
    public void ManagedTtsHostAndAsrIds_AndVision_DoNotInheritConfigJson()
    {
        // Fields added by the realtime pipeline (PR #25) that route a managed key.
        WriteProfile(Profile());
        File.WriteAllText(ConfigPath, """
            { "tts": { "transport": "bidi", "bidi_host": "collector.example.invalid" },
              "asr": { "secret_id": "user-secret-id", "resource_id": "user-resource" },
              "vision": { "enabled": true, "api_key": "sk-user-vision-000000", "base_url": "https://collector.example.invalid" } }
            """);

        var config = new ConfigManager(ConfigPath) { Profile = DistributionProfile.TryLoad(_root) }.Load();

        Assert.Equal("legacy", config.Tts.Transport);
        Assert.Equal("", config.Tts.BidiHost);
        Assert.Equal("", config.Asr.SecretId);
        Assert.Equal("", config.Asr.ResourceId);
        Assert.False(config.Vision.Enabled);
    }

    [Fact]
    public void BidiTransportWithoutHost_IsAConfigurationError()
    {
        WriteProfile(Profile().Replace("\"provider\": \"minimax\",", "\"provider\": \"minimax\", \"transport\": \"bidi\","));

        var ex = Assert.Throws<DistributionProfileException>(() => DistributionProfile.TryLoad(_root));
        Assert.Contains("bidi_host", ex.Message);
    }

    // ── U01 back end: allow-listed patch ─────────────────────────────────────

    [Theory]
    [InlineData("""{ "llm": { "baseUrl": "https://collector.example.invalid" } }""", "llm")]
    [InlineData("""{ "llm": { "apiKey": "sk-other" } }""", "llm")]
    [InlineData("""{ "tts": { "voiceId": "raw-voice" } }""", "tts")]
    [InlineData("""{ "asr": { "provider": "local" } }""", "asr")]
    [InlineData("""{ "persona": { "systemPrompt": "新的人设" }, "voice": { "providerVoiceId": "x" } }""", "voice.providerVoiceId")]
    public async Task DistributionPatch_RejectsManagedServiceChanges(string patch, string expectedRejected)
    {
        WriteProfile(Profile());
        await using var s = Build();
        var before = JsonSerializer.Serialize(s.Vm.Working);

        var result = await s.Vm.SaveStreamerAsync(Json(patch));

        Assert.False(result.Ok);
        Assert.Contains(result.Rejected, r => r.StartsWith(expectedRejected, StringComparison.Ordinal));
        Assert.Equal(before, JsonSerializer.Serialize(s.Vm.Working)); // nothing from a rejected patch applies
        Assert.False(File.Exists(ConfigPath) && File.ReadAllText(ConfigPath).Contains("新的人设"));
    }

    [Fact]
    public async Task StreamerDraft_ContainsNoManagedServiceFields()
    {
        WriteProfile(Profile());
        await using var s = Build();

        var json = Relaxed(s.Vm.BuildStreamerDraft());

        foreach (var forbidden in new[] { "apiKey", "baseUrl", "base_url", "provider", "model", "groupId",
                     "localAsrUrl", "pythonPath", "sk-test", VoiceA, VoiceB, "voiceId" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("温柔姐姐", json);
    }

    // ── U02 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectedVoice_PersistsAndMatchesEffectiveTts()
    {
        WriteProfile(Profile());
        await using (var s = Build())
        {
            Assert.Equal(VoiceA, s.Runtime.CurrentConfig.Tts.VoiceId); // operator default

            var result = await s.Vm.SaveStreamerAsync(Json("""{ "voice": { "choiceId": "b" } }"""));

            Assert.True(result.Ok);
            Assert.Equal(VoiceB, s.Runtime.CurrentConfig.Tts.VoiceId); // what the next sentence uses
            Assert.Equal("b", s.Vm.SelectedVoice?.Id);
            Assert.Equal("b", s.Vm.EffectiveVoice?.Id);
            Assert.Equal(s.Runtime.ActiveConfigRevision, result.EffectiveRevision);

            // Only the persona changes: the voice must stay B.
            result = await s.Vm.SaveStreamerAsync(Json("""{ "persona": { "systemPrompt": "你是爱吐槽的猫娘。" } }"""));
            Assert.True(result.Ok);
            Assert.Equal(VoiceB, s.Runtime.CurrentConfig.Tts.VoiceId);
            Assert.Equal("你是爱吐槽的猫娘。", s.Runtime.CurrentConfig.Llm.SystemPrompt);
        }

        // Restart: still B.
        await using (var restarted = Build())
        {
            Assert.Equal(VoiceB, restarted.Runtime.CurrentConfig.Tts.VoiceId);
            Assert.Null(restarted.Manager.LastLoadNotice);
            Assert.Equal("你是爱吐槽的猫娘。", restarted.Runtime.CurrentConfig.Llm.SystemPrompt);
        }
    }

    [Fact]
    public async Task CredentialUpdate_KeepsStillOfferedVoice_AndExplainsWhenItIsGone()
    {
        WriteProfile(Profile());
        await using (var s = Build())
            Assert.True((await s.Vm.SaveStreamerAsync(Json("""{ "voice": { "choiceId": "b" } }"""))).Ok);

        // New credential revision, B still offered → kept.
        WriteProfile(Profile(revision: 2));
        await using (var s = Build())
            Assert.Equal(VoiceB, s.Runtime.CurrentConfig.Tts.VoiceId);

        // Revision 3 drops B → operator default A (not "the first item"), with a visible notice.
        WriteProfile(Profile(revision: 3, voices: $$"""
            { "id": "c", "name": "低沉大叔", "voice_id": "{{VoiceC}}" },
            { "id": "a", "name": "温柔姐姐", "voice_id": "{{VoiceA}}" }
            """));
        await using (var s = Build())
        {
            Assert.Equal(VoiceA, s.Runtime.CurrentConfig.Tts.VoiceId);
            Assert.Contains("温柔姐姐", s.Manager.LastLoadNotice);
            Assert.Contains("温柔姐姐", Relaxed(s.Vm.BuildStreamerDraft()));
        }
    }

    [Fact]
    public async Task UnknownVoiceChoice_IsRejected_NotReplacedWithSomethingElse()
    {
        WriteProfile(Profile());
        await using var s = Build();

        var result = await s.Vm.SaveStreamerAsync(Json("""{ "voice": { "choiceId": "zzz" } }"""));

        Assert.False(result.Ok);
        Assert.Contains("音色", result.Message);
        Assert.Equal(VoiceA, s.Runtime.CurrentConfig.Tts.VoiceId);
    }

    [Fact]
    public async Task SavingFailed_IsNotReportedAsApplied()
    {
        WriteProfile(Profile());
        BotRuntime? runtime = null;
        await using var s = Build(_ => throw new InvalidOperationException("device HRESULT 0x8889000A at C:\\secret\\path token=abcd1234"));
        runtime = s.Runtime;

        var result = await s.Vm.SaveStreamerAsync(Json("""{ "voice": { "choiceId": "b" } }"""));

        Assert.False(result.Ok);
        Assert.False(result.Applied);
        Assert.Equal(ConfigSaveState.SavedButApplyFailed, s.Vm.SaveState);
        Assert.Contains("暂未生效", result.Message);
        Assert.Contains("诊断编号", result.Message);
        Assert.DoesNotContain("HRESULT", result.Message);
        Assert.DoesNotContain("token", result.Message);
        Assert.Equal(VoiceA, runtime.CurrentConfig.Tts.VoiceId); // the running voice did not change
        Assert.Equal(VoiceB, s.Vm.Working.Tts.VoiceId);           // the draft is kept for a retry
    }

    [Fact]
    public async Task PersonaSave_ReachesTheRuntimePromptSource()
    {
        WriteProfile(Profile());
        await using var s = Build();
        var persona = string.Join("\n", Enumerable.Range(1, 14).Select(i => $"第{i}行设定"));

        var result = await s.Vm.SaveStreamerAsync(Json(JsonSerializer.Serialize(new { persona = new { systemPrompt = persona } })));

        Assert.True(result.Ok);
        Assert.Equal(persona, s.Runtime.CurrentConfig.Llm.SystemPrompt);
        // The system prompt the runtime builds for the next LlmClient carries the new persona,
        // followed by the internal protocol the streamer never has to write.
        var built = (string)typeof(BotRuntime)
            .GetMethod("BuildLlmSystemPrompt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(s.Runtime, null)!;
        Assert.StartsWith(persona, built);
        // Identity.SelfName (the microphone user's name) is not touched by persona edits.
        Assert.Equal("", s.Runtime.CurrentConfig.Identity.SelfName);
    }
}
