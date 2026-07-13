using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class AsrHotReloadTests
{
    [Fact]
    public void ModelChange_RebuildsAsrOnly()
    {
        var candidate = new AppConfig();
        candidate.Asr.Model = "qwen3-asr-flash";

        Assert.Equal(RuntimeChange.RebuildAsr, ConfigDiff.Compute(new AppConfig(), candidate));
    }

    [Fact]
    public void LocalUrlChange_RebuildsAsrOnly()
    {
        var candidate = new AppConfig();
        candidate.Asr.LocalAsrUrl = "http://localhost:9876";

        Assert.Equal(RuntimeChange.RebuildAsr, ConfigDiff.Compute(new AppConfig(), candidate));
    }

    [Fact]
    public void PythonPathChange_RebuildsAsrOnly()
    {
        var candidate = new AppConfig();
        candidate.Asr.PythonPath = "python3";

        Assert.Equal(RuntimeChange.RebuildAsr, ConfigDiff.Compute(new AppConfig(), candidate));
    }

    [Fact]
    public void NextDashScopeRequest_UsesNewModel()
    {
        var before = DashScopeProtocol.RunTaskAsr("old", "paraformer-realtime-v2", 16000);
        var after = DashScopeProtocol.RunTaskAsr("new", "paraformer-v3", 16000);

        Assert.Contains("\"model\":\"paraformer-realtime-v2\"", before);
        Assert.Contains("\"model\":\"paraformer-v3\"", after);
        Assert.DoesNotContain("paraformer-realtime-v2", after);
    }
}
