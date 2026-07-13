using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public sealed class BotRuntimeLifecycleTests
{
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
