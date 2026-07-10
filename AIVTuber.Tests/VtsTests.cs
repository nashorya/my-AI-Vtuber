using AIVTuber.Core.Vts;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public class VtsClientTests
{
    [Fact]
    public void Constructor_SetsConfig()
    {
        var config = new VtsConfig { Host = "127.0.0.1", Port = 8001 };
        using var client = new VtsClient(config);
        Assert.NotNull(client);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var config = new VtsConfig();
        var client = new VtsClient(config);
        client.Dispose();
        client.Dispose(); // double dispose should be safe
    }

    [Fact]
    public async Task InjectParameter_ThrowsWhenNotAuthenticated()
    {
        var config = new VtsConfig();
        using var client = new VtsClient(config);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.InjectParameterAsync("ParamMouthOpenY", 0.5f));
    }

    [Fact]
    public async Task TriggerHotkey_ThrowsWhenNotAuthenticated()
    {
        var config = new VtsConfig();
        using var client = new VtsClient(config);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.TriggerHotkeyAsync("test-hotkey-id"));
    }

    [Fact]
    public async Task GetHotkeyList_ThrowsWhenNotAuthenticated()
    {
        var config = new VtsConfig();
        using var client = new VtsClient(config);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetHotkeyListAsync());
    }
}

public class VtsConfigTests
{
    [Fact]
    public void VtsConfig_DefaultValues()
    {
        var config = new VtsConfig();
        Assert.Equal("localhost", config.Host);
        Assert.Equal(8001, config.Port);
        Assert.Equal(1.5f, config.MouthScale);
        Assert.NotNull(config.EmotionMap);
        Assert.Empty(config.EmotionMap);
        Assert.NotNull(config.ActionMap);
        Assert.Empty(config.ActionMap);
    }

    [Fact]
    public void VtsConfig_EmotionMap_Works()
    {
        var config = new VtsConfig
        {
            EmotionMap = new Dictionary<string, string>
            {
                ["happy"] = "hotkey_abc",
                ["sad"] = "hotkey_def",
                ["angry"] = "hotkey_ghi"
            }
        };
        Assert.Equal(3, config.EmotionMap.Count);
        Assert.Equal("hotkey_abc", config.EmotionMap["happy"]);
        Assert.Equal("hotkey_def", config.EmotionMap["sad"]);
    }

    [Fact]
    public void VtsConfig_CustomHost()
    {
        var config = new VtsConfig { Host = "192.168.1.100", Port = 8002 };
        Assert.Equal("192.168.1.100", config.Host);
        Assert.Equal(8002, config.Port);
    }

    [Fact]
    public void BuildSystemPrompt_AdvertisesOnlyConfiguredActions()
    {
        var config = new VtsConfig
        {
            ActionMap = new Dictionary<string, string>
            {
                ["head_shake"] = "hotkey-1",
                ["wave"] = "hotkey-2",
            }
        };

        var prompt = config.BuildSystemPrompt("base prompt");

        Assert.Contains("[action:动作名]", prompt);
        Assert.Contains("head_shake", prompt);
        Assert.Contains("wave", prompt);
        Assert.DoesNotContain("hotkey-1", prompt);
    }
}

public class VtsModelsTests
{
    [Fact]
    public void VtsHotkeyInfo_DefaultValues()
    {
        var info = new VtsHotkeyInfo();
        Assert.Equal(string.Empty, info.HotkeyId);
        Assert.Equal(string.Empty, info.HotkeyName);
        Assert.Equal(string.Empty, info.Type);
        Assert.Equal(string.Empty, info.File);
    }

    [Fact]
    public void VtsResponse_DefaultValues()
    {
        var resp = new VtsResponse();
        Assert.Null(resp.ApiName);
        Assert.Null(resp.Data);
    }
}
