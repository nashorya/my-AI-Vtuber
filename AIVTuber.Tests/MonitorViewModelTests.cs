using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests;

public class MonitorViewModelTests
{
    private static (BotRuntime rt, MonitorViewModel vm) Make()
    {
        var rt = new BotRuntime(new AppConfig(), Path.GetTempPath());
        var vm = new MonitorViewModel(rt, run => run()); // synchronous dispatch
        return (rt, vm);
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        var (_, vm) = Make();
        Assert.Equal(PipelineState.Idle, vm.State);
    }

    [Fact]
    public void StateTrackerChange_UpdatesStateAndRaisesPropertyChanged()
    {
        var (rt, vm) = Make();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        rt.StateTracker.InputStarted(1000);   // -> Thinking

        Assert.Equal(PipelineState.Thinking, vm.State);
        Assert.Contains(nameof(MonitorViewModel.State), changed);
    }

    [Fact]
    public void LatencyExposed_AfterVoiceFlow()
    {
        var (rt, vm) = Make();
        rt.StateTracker.InputStarted(1000);
        rt.StateTracker.TranscriptReady(1250);
        Assert.Equal(250, vm.AsrLatencyMs);
    }

    [Fact]
    public void ConnectionFlags_DefaultFalse_WhenNotStarted()
    {
        var (_, vm) = Make();
        Assert.False(vm.VtsConnected);
        Assert.False(vm.ObsConnected);
        Assert.False(vm.DanmakuActive);
    }
}
