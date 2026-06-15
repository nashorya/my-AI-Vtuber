using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Core.ViewModels;

/// <summary>
/// Monitor tab view-model. UI-agnostic (no WPF types): subscribes to BotRuntime and
/// exposes bindable properties. All updates are marshalled through the injected
/// dispatch delegate (the WPF host passes Dispatcher.Invoke; tests pass a synchronous
/// run-now delegate).
/// </summary>
public sealed class MonitorViewModel : INotifyPropertyChanged
{
    private readonly BotRuntime _runtime;
    private readonly Action<Action> _dispatch;

    public MonitorViewModel(BotRuntime runtime, Action<Action> dispatch)
    {
        _runtime = runtime;
        _dispatch = dispatch;

        // No unsubscription: the runtime and this VM are both app-lifetime singletons
        // (one window). If the VM ever becomes shorter-lived than the runtime, make this
        // IDisposable and detach these handlers.
        _runtime.StateTracker.Changed += (_, _) => _dispatch(OnStateChanged);
        _runtime.UserTranscript += (_, t) => _dispatch(() => UserText = t);
        _runtime.SentenceReady += (_, s) => _dispatch(() => AssistantText = s);
        _runtime.EmotionDetected += (_, e) => _dispatch(() => Emotion = e);
        _runtime.AiStartSpeaking += (_, _) => _dispatch(RefreshConnections);
        _runtime.AiStopSpeaking += (_, _) => _dispatch(RefreshDanmaku);
    }

    private void OnStateChanged()
    {
        State = _runtime.StateTracker.State;
        AsrLatencyMs = _runtime.StateTracker.LastAsrLatencyMs;
        FirstSentenceMs = _runtime.StateTracker.LastFirstSentenceMs;
        RefreshConnections();
        RefreshDanmaku();
    }

    private void RefreshConnections()
    {
        VtsConnected = _runtime.VtsConnected;
        ObsConnected = _runtime.ObsConnected;
        DanmakuActive = _runtime.DanmakuActive;
    }

    private void RefreshDanmaku()
    {
        var snap = _runtime.Selector?.Snapshot() ?? [];
        DanmakuQueueCount = snap.Count;
        DanmakuQueue = snap;
    }

    private PipelineState _state = PipelineState.Idle;
    public PipelineState State { get => _state; private set => SetField(ref _state, value); }

    private string _userText = "";
    public string UserText { get => _userText; private set => SetField(ref _userText, value); }

    private string _assistantText = "";
    public string AssistantText { get => _assistantText; private set => SetField(ref _assistantText, value); }

    private string _emotion = "";
    public string Emotion { get => _emotion; private set => SetField(ref _emotion, value); }

    private bool _vtsConnected;
    public bool VtsConnected { get => _vtsConnected; private set => SetField(ref _vtsConnected, value); }

    private bool _obsConnected;
    public bool ObsConnected { get => _obsConnected; private set => SetField(ref _obsConnected, value); }

    private bool _danmakuActive;
    public bool DanmakuActive { get => _danmakuActive; private set => SetField(ref _danmakuActive, value); }

    private int _danmakuQueueCount;
    public int DanmakuQueueCount { get => _danmakuQueueCount; private set => SetField(ref _danmakuQueueCount, value); }

    private IReadOnlyList<Danmaku> _danmakuQueue = [];
    public IReadOnlyList<Danmaku> DanmakuQueue { get => _danmakuQueue; private set => SetField(ref _danmakuQueue, value); }

    private long? _asr;
    public long? AsrLatencyMs { get => _asr; private set => SetField(ref _asr, value); }

    private long? _firstSentence;
    public long? FirstSentenceMs { get => _firstSentence; private set => SetField(ref _firstSentence, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
