using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class ConfigSnapshotApplyTests
{
    [Fact]
    public void Constructor_IsolatesRuntimeSnapshotFromCaller()
    {
        var source = Config("initial");
        var runtime = Runtime(source);

        source.Llm.Model = "mutated";
        var exposed = runtime.CurrentConfig;
        exposed.Llm.Model = "also-mutated";

        Assert.Equal("initial", runtime.CurrentConfig.Llm.Model);
        Assert.NotSame(source, runtime.CurrentConfig);
    }

    [Fact]
    public async Task Apply_ClonesCandidateAndAdvancesActiveRevisionOnlyAfterSuccess()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = Runtime(Config("one"), async _ => { entered.SetResult(); await release.Task; });
        var candidate = Config("two");

        var applying = runtime.ApplyConfigAsync(candidate);
        await entered.Task;
        candidate.Llm.Model = "mutated";

        Assert.Equal(1, runtime.ActiveConfigRevision);
        Assert.Equal("one", runtime.CurrentConfig.Llm.Model);
        Assert.Equal("two", runtime.CandidateConfig!.Llm.Model);
        Assert.False(runtime.CandidateConfigIsActive);

        release.SetResult();
        await applying;

        Assert.Equal(2, runtime.ActiveConfigRevision);
        Assert.Equal("two", runtime.CurrentConfig.Llm.Model);
        Assert.True(runtime.CandidateConfigIsActive);
    }

    [Fact]
    public async Task ConsecutiveApplies_ObserveDistinctSnapshotsAndRevisions()
    {
        var changes = new List<RuntimeChange>();
        var runtime = Runtime(Config("one"), change => { changes.Add(change); return Task.CompletedTask; });

        await runtime.ApplyConfigAsync(Config("two"));
        await runtime.ApplyConfigAsync(Config("three"));

        Assert.Equal(3, runtime.ActiveConfigRevision);
        Assert.Equal("three", runtime.CurrentConfig.Llm.Model);
        Assert.Equal([RuntimeChange.RebuildLlm, RuntimeChange.RebuildLlm], changes);
    }

    [Fact]
    public async Task ConsecutiveActionMapApplies_UseSecondMapAndPrompt()
    {
        var changes = new List<RuntimeChange>();
        var initial = Config("model");
        initial.Vts.ActionMap["old"] = "hotkey-0";
        var runtime = Runtime(initial, change => { changes.Add(change); return Task.CompletedTask; });

        var first = Config("model");
        first.Vts.ActionMap["wave"] = "hotkey-1";
        await runtime.ApplyConfigAsync(first);

        var second = Config("model");
        second.Vts.ActionMap["dance"] = "hotkey-2";
        await runtime.ApplyConfigAsync(second);

        var active = runtime.CurrentConfig;
        var prompt = active.Vts.BuildSystemPrompt(active.Llm.SystemPrompt);
        Assert.Equal([RuntimeChange.UpdateVtsParams, RuntimeChange.UpdateVtsParams], changes);
        Assert.Equal("hotkey-2", active.Vts.ActionMap["dance"]);
        Assert.DoesNotContain("wave", active.Vts.ActionMap.Keys);
        Assert.Contains("dance", prompt);
        Assert.DoesNotContain("wave", prompt);
    }

    [Fact]
    public async Task ApplyFailure_RestoresModulesAndKeepsActiveRevision()
    {
        var calls = 0;
        var runtime = Runtime(Config("one"), _ =>
        {
            if (++calls == 1) throw new InvalidOperationException("apply failed");
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ApplyConfigAsync(Config("two")));

        Assert.Equal("apply failed", error.Message);
        Assert.Equal(2, calls);
        Assert.Equal(1, runtime.ActiveConfigRevision);
        Assert.Equal("one", runtime.CurrentConfig.Llm.Model);
        Assert.Equal("two", runtime.CandidateConfig!.Llm.Model);
        Assert.False(runtime.CandidateConfigIsActive);
    }

    [Fact]
    public async Task Rollback_ReappliesLastKnownGoodAsNewRevision()
    {
        var runtime = Runtime(Config("one"));
        await runtime.ApplyConfigAsync(Config("two"));

        Assert.Equal(1, runtime.LastKnownGoodConfigRevision);
        await runtime.RollbackConfigAsync();

        Assert.Equal(3, runtime.ActiveConfigRevision);
        Assert.Equal("one", runtime.CurrentConfig.Llm.Model);
        Assert.True(runtime.CandidateConfigIsActive);
    }

    [Fact]
    public async Task ConcurrentApplies_AreSerialized()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runtime = Runtime(Config("one"), async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            }
        });

        var first = runtime.ApplyConfigAsync(Config("two"));
        await firstEntered.Task;
        var second = runtime.ApplyConfigAsync(Config("three"));

        Assert.Equal(1, calls);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, calls);
        Assert.Equal(3, runtime.ActiveConfigRevision);
        Assert.Equal("three", runtime.CurrentConfig.Llm.Model);
    }

    private static BotRuntime Runtime(AppConfig config, Func<RuntimeChange, Task>? apply = null)
        => new(config, Path.GetTempPath(), apply ?? (_ => Task.CompletedTask));

    private static AppConfig Config(string model)
    {
        var config = new AppConfig();
        config.Llm.Model = model;
        return config;
    }
}
