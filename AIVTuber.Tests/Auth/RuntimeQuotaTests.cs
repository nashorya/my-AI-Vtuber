using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests.Auth;

public sealed class RuntimeQuotaTests
{
    private sealed class FakeQuotaAccess : ICloudAccess, ICompanionQuota
    {
        public bool IsAllowed => true;
        public long Epoch => 1;
        public event Action<string>? Revoked { add { } remove { } }
        public bool QuotaManaged => true;
        public int QuotaSeconds => 3600;
        public int QuotaRemainingSeconds { get; set; } = 100;
        public DateTimeOffset? QuotaResetsAt => null;
        public List<bool> ActiveCalls { get; } = [];
        public void SetCompanionActive(bool active) => ActiveCalls.Add(active);
        public event Action? QuotaChanged { add { } remove { } }
        public event Action? QuotaExhausted;
        public event Action? QuotaRestored;
        public void RaiseExhausted() => QuotaExhausted?.Invoke();
        public void RaiseRestored() => QuotaRestored?.Invoke();
    }

    private static (BotRuntime Runtime, FakeQuotaAccess Access) NewRuntime()
    {
        var runtime = new BotRuntime(new AppConfig(), Path.GetTempPath());
        var access = new FakeQuotaAccess();
        runtime.UseCloudAccess(access);
        return (runtime, access);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(20);
    }

    [Fact]
    public async Task Exhausted_WhileIdle_PausesAtOnce()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;

        access.RaiseExhausted();
        await WaitUntil(() => runtime.CompanionPaused);

        Assert.True(runtime.CompanionPaused);
        Assert.True(runtime.QuotaExhausted);
        Assert.Contains(false, access.ActiveCalls); // time stops being billed
    }

    [Fact]
    public async Task WindDown_WaitsForTheReplyToFinish_ThenPauses()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        runtime.StateTracker.SpeakingStarted(Environment.TickCount64);

        access.RaiseExhausted();
        await Task.Delay(400);
        Assert.False(runtime.CompanionPaused); // still speaking: the sentence is not cut

        runtime.StateTracker.SpeakingStopped();
        await WaitUntil(() => runtime.CompanionPaused);

        Assert.True(runtime.CompanionPaused);
        Assert.True(runtime.QuotaExhausted);
    }

    [Fact]
    public async Task WindDown_IsCappedWhenTheReplyNeverEnds()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        runtime.QuotaWindDownMax = TimeSpan.FromMilliseconds(300);
        runtime.StateTracker.SpeakingStarted(Environment.TickCount64); // never stops

        access.RaiseExhausted();
        await WaitUntil(() => runtime.CompanionPaused);

        Assert.True(runtime.CompanionPaused);
    }

    [Fact]
    public async Task Resume_IsRefusedWhileExhausted_AndAllowedAfterRestore()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        access.RaiseExhausted();
        await WaitUntil(() => runtime.CompanionPaused);

        runtime.SetCompanionPaused(false);
        Assert.True(runtime.CompanionPaused);

        access.RaiseRestored();
        Assert.False(runtime.QuotaExhausted);
        Assert.True(runtime.CompanionPaused); // restored quota never resumes by itself

        runtime.SetCompanionPaused(false);
        Assert.False(runtime.CompanionPaused);
    }

    [Fact]
    public async Task ManualPause_ThenExhausted_StillMarksExhausted()
    {
        var (runtime, access) = NewRuntime();
        await using var _ = runtime;
        runtime.SetCompanionPaused(true);

        access.RaiseExhausted();

        Assert.True(runtime.QuotaExhausted);
        runtime.SetCompanionPaused(false);
        Assert.True(runtime.CompanionPaused);
    }
}
