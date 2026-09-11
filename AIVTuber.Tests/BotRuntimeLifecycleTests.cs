using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.Bot;
using System.Reflection;

namespace AIVTuber.Tests;

public sealed class BotRuntimeLifecycleTests
{
    [Fact]
    public async Task MutingDuringSpeech_ReleasesBufferedTurnWithoutMoreAudio()
    {
        await using var runtime = new BotRuntime(new AppConfig(), Path.GetTempPath());
        using var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(30));
        typeof(BotRuntime).GetField("_turnGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, gate);
        var ready = new TaskCompletionSource<IReadOnlyList<TalkLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.TurnReady += lines => ready.TrySetResult(lines);
        gate.SetMicSpeaking(true);
        gate.AddLine(new(TalkIdentity.Self, "我", "上一句话", null));
        runtime.SetMicMuted(true);
        var turn = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("上一句话", Assert.Single(turn).Text);
        Assert.True(runtime.MicMuted);
    }

    [Fact]
    public async Task DisposeAsync_waits_for_supervised_background_tasks()
    {
        var runtime = new BotRuntime(new AppConfig(), Path.GetTempPath());
        var backgroundCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SuperviseBackgroundTask(backgroundCompletion.Task);

        var disposeTask = runtime.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.False(disposeTask.IsCompleted);
        Assert.Equal(1, runtime.BackgroundTaskCount);

        backgroundCompletion.SetResult(true);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, runtime.BackgroundTaskCount);
    }
}
