using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.Ui;
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

    [Fact]
    public void SetField_SuppressesNotificationWhenValueUnchanged()
    {
        var (rt, vm) = Make();
        rt.StateTracker.InputStarted(1000); // -> Thinking (first State change)
        var stateNotifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MonitorViewModel.State)) stateNotifications++; };

        rt.StateTracker.TranscriptReady(1100); // stays Thinking -> State must NOT notify again

        Assert.Equal(0, stateNotifications);
    }

    [Fact]
    public void OperationalEvents_AreNewestFirstAndEvictOldestAtCapacity()
    {
        var (_, vm) = Make();

        for (var index = 0; index < 10_000; index++)
            vm.RecordOperationalEventForTest("输入", $"消息 {index}");

        Assert.Equal(MonitorViewModel.OperationalEventCapacity, vm.OperationalEvents.Count);
        Assert.Equal("消息 9999", vm.OperationalEvents[0].Message);
        Assert.Equal($"消息 {10_000 - MonitorViewModel.OperationalEventCapacity}", vm.OperationalEvents[^1].Message);
    }

    [Fact]
    public void TriggerAvatarEmotion_WithoutPixelBackend_LogsErrorEvent()
    {
        var (_, vm) = Make(); // default backend=vts, no PixelAvatar
        vm.TriggerAvatarEmotion("happy");
        Assert.NotEmpty(vm.OperationalEvents);
        Assert.Equal("形象", vm.OperationalEvents[0].Source);
        Assert.True(vm.OperationalEvents[0].IsError);
        Assert.Contains("happy", vm.OperationalEvents[0].Message);
    }

    [Fact]
    public void TriggerAvatarSticker_WithoutPixelBackend_LogsErrorEvent()
    {
        var (_, vm) = Make();
        vm.TriggerAvatarSticker("sweat_laugh");
        Assert.True(vm.OperationalEvents[0].IsError);
        Assert.Contains("sweat_laugh", vm.OperationalEvents[0].Message);
    }

    [Fact]
    public void TriggerAvatarListening_WithoutPixelBackend_LogsErrorEvent()
    {
        var (_, vm) = Make();
        vm.TriggerAvatarListening(true);
        Assert.True(vm.OperationalEvents[0].IsError);
        Assert.Contains("listening", vm.OperationalEvents[0].Message);
    }

    [Fact]
    public void OperationalEvents_RenderErrorsWithErrorSource()
    {
        var (_, vm) = Make();

        vm.RecordOperationalEventForTest("错误", "ASR 不可用", isError: true);

        var entry = Assert.Single(vm.OperationalEvents);
        Assert.Equal("错误", entry.Source);
        Assert.Equal("ASR 不可用", entry.Message);
        Assert.True(entry.IsError);
    }

    [Fact]
    public void FollowLatest_StaysPausedUntilUserExplicitlyReturns()
    {
        var (_, vm) = Make();

        vm.PauseFollowLatest();
        vm.RecordOperationalEventForTest("输入", "后续事件");

        Assert.False(vm.FollowLatest);
        vm.ReturnToLatest();
        Assert.True(vm.FollowLatest);
    }

    [Fact]
    public void ProgrammaticScroll_DoesNotPauseFollowing_ButLaterUserScrollDoes()
    {
        var policy = new FollowLatestScrollPolicy();

        policy.BeginProgrammaticScroll();
        Assert.False(policy.ShouldPauseFollowing(verticalChange: 24));

        policy.CompleteProgrammaticScroll();
        Assert.True(policy.ShouldPauseFollowing(verticalChange: 24));
    }

    [Fact]
    public void ProgrammaticScrollPolicy_IgnoresNonVerticalChanges()
    {
        var policy = new FollowLatestScrollPolicy();

        Assert.False(policy.ShouldPauseFollowing(verticalChange: 0));
    }

    [Fact]
    public void EventSubscription_ReattachesAfterUnloadWithoutDuplicatingHandler()
    {
        var subscription = new EventSubscription<object>();
        var source = new object();
        var subscriptions = 0;
        var unsubscriptions = 0;

        subscription.Reconcile(source, _ => subscriptions++, _ => unsubscriptions++);
        subscription.Reconcile(source, _ => subscriptions++, _ => unsubscriptions++);
        subscription.Clear(_ => unsubscriptions++);
        subscription.Reconcile(source, _ => subscriptions++, _ => unsubscriptions++);

        Assert.Equal(2, subscriptions);
        Assert.Equal(1, unsubscriptions);
    }

    [Fact]
    public void MonitorLayout_ReflowsPanelsBelowSideBySideWidth()
    {
        Assert.True(MonitorLayoutPolicy.ShouldStackPanels(728));
        Assert.False(MonitorLayoutPolicy.ShouldStackPanels(932));
        Assert.Equal(728, MonitorLayoutPolicy.ContentWidth(728));
    }
}
