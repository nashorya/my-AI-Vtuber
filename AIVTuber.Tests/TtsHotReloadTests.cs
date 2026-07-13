using System.Text.Json;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class TtsHotReloadTests
{
    [Fact]
    public void ModelChange_RebuildsTtsOnly()
    {
        var candidate = new AppConfig();
        candidate.Tts.Model = "speech-2.8-hd";

        Assert.Equal(RuntimeChange.RebuildTts, ConfigDiff.Compute(new AppConfig(), candidate));
    }

    [Fact]
    public void GroupIdChange_RebuildsTtsOnly()
    {
        var candidate = new AppConfig();
        candidate.Tts.GroupId = "new-group";

        Assert.Equal(RuntimeChange.RebuildTts, ConfigDiff.Compute(new AppConfig(), candidate));
    }

    [Fact]
    public void SpeedChange_RebuildsTtsOnly()
    {
        var candidate = new AppConfig();
        candidate.Tts.Speed = 1.25;

        Assert.Equal(RuntimeChange.RebuildTts, ConfigDiff.Compute(new AppConfig(), candidate));
    }

    [Fact]
    public void NextFishRequest_UsesNewSpeed()
    {
        var before = TtsClient.BuildFishRequestJson("hello", "voice", 1.0, 24000);
        var after = TtsClient.BuildFishRequestJson("hello", "voice", 1.35, 24000);

        using var beforeJson = JsonDocument.Parse(before);
        using var afterJson = JsonDocument.Parse(after);
        Assert.Equal(1.0, beforeJson.RootElement.GetProperty("prosody").GetProperty("speed").GetDouble());
        Assert.Equal(1.35, afterJson.RootElement.GetProperty("prosody").GetProperty("speed").GetDouble());
    }

    [Fact]
    public void NextMiniMaxRequest_UsesNewModelAndSpeed()
    {
        var after = TtsClient.BuildMiniMaxRequestJson(
            "hello", "voice", "speech-2.8-hd", 1.4, 24000);

        using var json = JsonDocument.Parse(after);
        Assert.Equal("speech-2.8-hd", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(1.4, json.RootElement.GetProperty("voice_setting").GetProperty("speed").GetDouble());
    }

    [Fact]
    public void NextDashScopeRequest_UsesNewModelAndSpeed()
    {
        var after = DashScopeProtocol.RunTaskTts(
            "task", "cosyvoice-v3-plus", "voice", 24000, 1.3);

        using var json = JsonDocument.Parse(after);
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal("cosyvoice-v3-plus", payload.GetProperty("model").GetString());
        Assert.Equal(1.3, payload.GetProperty("parameters").GetProperty("rate").GetDouble());
    }
}
