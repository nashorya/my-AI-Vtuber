using System.Text.Json;
using AIVTuber.Core.Config;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests;

public class ProviderSecretsTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", "deepseek")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "gemini")]
    [InlineData("https://api.openai.com/v1", "openai")]
    public void LlmVendor_MapsKnownHosts(string baseUrl, string vendor)
    {
        Assert.Equal(vendor, ProviderSecrets.LlmVendor(baseUrl));
    }

    [Fact]
    public void RememberAndActivate_RestoreOtherVendor()
    {
        var keys = new Dictionary<string, string>();
        ProviderSecrets.Remember(keys, "deepseek", "sk-ds");
        ProviderSecrets.Remember(keys, "gemini", "sk-gm");

        Assert.True(ProviderSecrets.TryActivate(keys, "deepseek", out var ds));
        Assert.Equal("sk-ds", ds);
        Assert.True(ProviderSecrets.TryActivate(keys, "gemini", out var gm));
        Assert.Equal("sk-gm", gm);
    }
}

public class ProviderKeyRetentionTests
{
    private static ConfigViewModel Make(AppConfig current, Action<AppConfig>? save = null)
        => new(current, ["麦克风"], save ?? (_ => { }), _ => Task.CompletedTask);

    private static JsonElement Patch(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task SwitchingLlmToGemini_KeepsDeepSeekKey()
    {
        AppConfig? saved = null;
        var current = new AppConfig();
        current.Llm.BaseUrl = "https://api.deepseek.com";
        current.Llm.ApiKey = "sk-deepseek";
        var vm = Make(current, c => saved = c);

        vm.ApplyWebPatch(Patch("""
            {"llm":{"baseUrl":"https://generativelanguage.googleapis.com/v1beta/openai","apiKey":"sk-gemini"}}
            """));
        await vm.SaveAsync();

        Assert.Equal("sk-gemini", saved!.Llm.ApiKey);
        Assert.Equal("sk-deepseek", saved.Llm.ApiKeys["deepseek"]);
        Assert.Equal("sk-gemini", saved.Llm.ApiKeys["gemini"]);
    }

    [Fact]
    public async Task SwitchingLlmBackToDeepSeek_ReusesStoredKey()
    {
        AppConfig? saved = null;
        var current = new AppConfig();
        current.Llm.BaseUrl = "https://api.deepseek.com";
        current.Llm.ApiKey = "sk-deepseek";
        var vm = Make(current, c => saved = c);

        vm.ApplyWebPatch(Patch("""
            {"llm":{"baseUrl":"https://generativelanguage.googleapis.com/v1beta/openai","apiKey":"sk-gemini"}}
            """));
        await vm.SaveAsync();
        vm.ApplyWebPatch(Patch("""{"llm":{"baseUrl":"https://api.deepseek.com"}}"""));
        await vm.SaveAsync();

        Assert.Equal("sk-deepseek", saved!.Llm.ApiKey);
        Assert.Equal("sk-gemini", saved.Llm.ApiKeys["gemini"]);
    }

    [Fact]
    public async Task SwitchingTtsProvider_KeepsPreviousKey()
    {
        AppConfig? saved = null;
        var current = new AppConfig();
        current.Tts.Provider = "fish-audio";
        current.Tts.ApiKey = "fish-key";
        var vm = Make(current, c => saved = c);

        vm.ApplyWebPatch(Patch("""{"tts":{"provider":"minimax","apiKey":"minimax-key"}}"""));
        await vm.SaveAsync();
        vm.ApplyWebPatch(Patch("""{"tts":{"provider":"fish-audio"}}"""));
        await vm.SaveAsync();

        Assert.Equal("fish-key", saved!.Tts.ApiKey);
        Assert.Equal("minimax-key", saved.Tts.ApiKeys["minimax"]);
    }

    [Fact]
    public void Hydrate_CopiesLegacyApiKeyIntoVendorSlot()
    {
        var config = new AppConfig();
        config.Llm.BaseUrl = "https://api.deepseek.com";
        config.Llm.ApiKey = "sk-legacy";

        ConfigManager.HydrateProviderKeys(config);

        Assert.Equal("sk-legacy", config.Llm.ApiKeys["deepseek"]);
    }
}
