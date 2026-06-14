using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class ConfigDiffTests
{
    private static AppConfig Base() => new();

    [Fact]
    public void NoChange_ReturnsNone()
    {
        Assert.Equal(RuntimeChange.None, ConfigDiff.Compute(Base(), Base()));
    }

    [Fact]
    public void LlmChange_IsLightRebuildLlm()
    {
        var b = Base();
        b.Llm.SystemPrompt = "新人设";
        var c = ConfigDiff.Compute(Base(), b);
        Assert.Equal(RuntimeChange.RebuildLlm, c);
        Assert.False(ConfigDiff.IsHeavy(c));
    }

    [Fact]
    public void VtsEmotionMapChange_IsLightUpdateParams()
    {
        var b = Base();
        b.Vts.EmotionMap["happy"] = "42";
        var c = ConfigDiff.Compute(Base(), b);
        Assert.Equal(RuntimeChange.UpdateVtsParams, c);
        Assert.False(ConfigDiff.IsHeavy(c));
    }

    [Fact]
    public void IdenticalNonEmptyEmotionMap_ReturnsNone()
    {
        var a = new AppConfig(); a.Vts.EmotionMap["happy"] = "1";
        var b = new AppConfig(); b.Vts.EmotionMap["happy"] = "1";
        Assert.Equal(RuntimeChange.None, ConfigDiff.Compute(a, b));
    }

    [Fact]
    public void VtsConnectionChange_IsHeavyReconnect()
    {
        var b = Base();
        b.Vts.Port = 9000;
        var c = ConfigDiff.Compute(Base(), b);
        Assert.Equal(RuntimeChange.ReconnectVts, c);
        Assert.True(ConfigDiff.IsHeavy(c));
    }

    [Fact]
    public void AudioDeviceChange_IsHeavyRestartAudio()
    {
        var b = Base();
        b.Audio.InputDeviceIndex = 3;
        Assert.True(ConfigDiff.IsHeavy(ConfigDiff.Compute(Base(), b)));
        Assert.Equal(RuntimeChange.RestartAudio, ConfigDiff.Compute(Base(), b));
    }

    [Fact]
    public void BilibiliIntervalChange_IsLight_ButRoomChange_IsHeavy()
    {
        var b1 = Base(); b1.Bilibili.SelectionIntervalSec = 12;
        Assert.Equal(RuntimeChange.RebuildDanmakuSelector, ConfigDiff.Compute(Base(), b1));

        var b2 = Base(); b2.Bilibili.RoomId = 123;
        Assert.Equal(RuntimeChange.RestartDanmaku, ConfigDiff.Compute(Base(), b2));
    }

    [Fact]
    public void ObsComponentName_Light_ButPassword_Heavy()
    {
        var b1 = Base(); b1.Obs.TypewriterIntervalMs = 80;
        Assert.Equal(RuntimeChange.UpdateObsParams, ConfigDiff.Compute(Base(), b1));

        var b2 = Base(); b2.Obs.Password = "secret";
        Assert.Equal(RuntimeChange.ReconnectObs, ConfigDiff.Compute(Base(), b2));
    }

    [Fact]
    public void MemoryExtractInterval_Light_ButDbPath_Heavy()
    {
        var b1 = Base(); b1.Memory.ExtractEveryNTurns = 10;
        Assert.Equal(RuntimeChange.UpdateMemoryParams, ConfigDiff.Compute(Base(), b1));

        var b2 = Base(); b2.Memory.DatabasePath = "other.db";
        Assert.Equal(RuntimeChange.ReopenMemory, ConfigDiff.Compute(Base(), b2));
    }

    [Fact]
    public void MultipleChanges_AreCombined()
    {
        var b = Base();
        b.Llm.Model = "gpt-x";
        b.Audio.InputDeviceIndex = 2;
        var c = ConfigDiff.Compute(Base(), b);
        Assert.True(c.HasFlag(RuntimeChange.RebuildLlm));
        Assert.True(c.HasFlag(RuntimeChange.RestartAudio));
        Assert.True(ConfigDiff.IsHeavy(c));
    }
}
