using System.Collections.ObjectModel;
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
    public const int OperationalEventCapacity = 500;

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
        _runtime.UserTranscript += (_, t) => _dispatch(() =>
        {
            UserText = t;
            LastError = "";
            AddOperationalEvent("输入", t);
        });
        _runtime.SentenceReady += (_, s) => _dispatch(() =>
        {
            AssistantText = s;
            AddOperationalEvent("回复", s);
        });
        _runtime.EmotionDetected += (_, e) => _dispatch(() => Emotion = e);
        _runtime.UserEmotionDetected += (_, e) => _dispatch(() => UserEmotion = EmotionToLabel(e));
        _runtime.LoopbackTranscript += (_, t) => _dispatch(() =>
        {
            OpponentText = t;
            AddOperationalEvent("内录", t);
        });
        _runtime.AiStartSpeaking += (_, _) => _dispatch(RefreshConnections);
        _runtime.AiStopSpeaking += (_, _) => _dispatch(RefreshDanmaku);
        _runtime.MicLevelUpdated += (_, level) => _dispatch(() => MicLevel = level);
        _runtime.LoopbackLevelUpdated += (_, level) => _dispatch(() => LoopbackLevel = level);
        _runtime.PipelineError += (_, msg) => _dispatch(() =>
        {
            LastError = msg;
            AddOperationalEvent("错误", msg, isError: true);
        });
        _runtime.LocalAsrReachableChanged += (_, reachable) => _dispatch(() =>
        {
            LocalAsrActive = _runtime.LocalAsrActive;
            LocalAsrReachable = reachable;
        });
    }

    private void OnStateChanged()
    {
        State = _runtime.StateTracker.State;
        AsrLatencyMs = _runtime.StateTracker.LastAsrLatencyMs;
        LlmLatencyMs = _runtime.StateTracker.LastLlmLatencyMs;
        TtsLatencyMs = _runtime.StateTracker.LastTtsLatencyMs;
        RefreshConnections();
        RefreshDanmaku();
        AddOperationalEvent("状态", StateToLabel(_state));
    }

    private static string StateToLabel(PipelineState state) => state switch
    {
        PipelineState.Listening => "正在监听",
        PipelineState.Thinking => "正在思考",
        PipelineState.Speaking => "正在说话",
        _ => "空闲",
    };

    /// <summary>
    /// Most recent operational records, newest first. Entries are kept deliberately
    /// small so an unattended live session cannot grow its UI memory indefinitely.
    /// </summary>
    public ObservableCollection<OperationalEvent> OperationalEvents { get; } = [];

    private bool _followLatest = true;
    public bool FollowLatest { get => _followLatest; private set => SetField(ref _followLatest, value); }

    public void PauseFollowLatest() => FollowLatest = false;

    public void ReturnToLatest() => FollowLatest = true;

    private void AddOperationalEvent(string source, string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        OperationalEvents.Insert(0, new OperationalEvent(DateTimeOffset.Now, source, message, isError));
        if (OperationalEvents.Count > OperationalEventCapacity)
            OperationalEvents.RemoveAt(OperationalEvents.Count - 1);
    }

    // Internal only to prove bounded-collection behavior without adding runtime test hooks.
    internal void RecordOperationalEventForTest(string source, string message, bool isError = false) =>
        AddOperationalEvent(source, message, isError);

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
    public PipelineState State
    {
        get => _state;
        private set { if (SetField(ref _state, value)) OnPropertyChanged(nameof(CanStop)); }
    }

    /// <summary>True while the AI is generating or speaking — gates the "截停说话" button.</summary>
    public bool CanStop => _state is PipelineState.Thinking or PipelineState.Speaking;

    private string _userText = "";
    public string UserText { get => _userText; private set => SetField(ref _userText, value); }

    private string _assistantText = "";
    public string AssistantText { get => _assistantText; private set => SetField(ref _assistantText, value); }

    private string _emotion = "";
    public string Emotion { get => _emotion; private set => SetField(ref _emotion, value); }

    private string _userEmotion = "";
    public string UserEmotion { get => _userEmotion; private set => SetField(ref _userEmotion, value); }

    private static string EmotionToLabel(string emotion) => emotion switch
    {
        "happy"     => "愉快",
        "sad"       => "悲伤",
        "angry"     => "愤怒",
        "fearful"   => "恐惧",
        "disgusted" => "厌恶",
        "surprised" => "惊讶",
        _           => emotion,
    };

    private bool _vtsConnected;
    public bool VtsConnected { get => _vtsConnected; private set => SetField(ref _vtsConnected, value); }

    private bool _obsConnected;
    public bool ObsConnected { get => _obsConnected; private set => SetField(ref _obsConnected, value); }

    private bool _danmakuActive;
    public bool DanmakuActive { get => _danmakuActive; private set => SetField(ref _danmakuActive, value); }

    private bool _localAsrActive;
    public bool LocalAsrActive { get => _localAsrActive; private set => SetField(ref _localAsrActive, value); }

    private bool _localAsrReachable;
    public bool LocalAsrReachable { get => _localAsrReachable; private set => SetField(ref _localAsrReachable, value); }

    private int _danmakuQueueCount;
    public int DanmakuQueueCount { get => _danmakuQueueCount; private set => SetField(ref _danmakuQueueCount, value); }

    private IReadOnlyList<Danmaku> _danmakuQueue = [];
    public IReadOnlyList<Danmaku> DanmakuQueue { get => _danmakuQueue; private set => SetField(ref _danmakuQueue, value); }

    private long? _asr;
    public long? AsrLatencyMs { get => _asr; private set => SetField(ref _asr, value); }

    private long? _llm;
    public long? LlmLatencyMs { get => _llm; private set => SetField(ref _llm, value); }

    private long? _tts;
    public long? TtsLatencyMs { get => _tts; private set => SetField(ref _tts, value); }

    private string _opponentText = "";
    public string OpponentText { get => _opponentText; private set => SetField(ref _opponentText, value); }

    private string _lastError = "";
    /// <summary>Last pipeline error message; cleared when a new user transcript arrives.</summary>
    public string LastError { get => _lastError; private set => SetField(ref _lastError, value); }

    private bool _micMuted;
    public bool MicMuted { get => _micMuted; private set => SetField(ref _micMuted, value); }

    private float _micLevel;
    public float MicLevel { get => _micLevel; private set => SetField(ref _micLevel, value); }

    private float _loopbackLevel;
    public float LoopbackLevel { get => _loopbackLevel; private set => SetField(ref _loopbackLevel, value); }

    public void RestartLocalAsrServer()
    {
        _dispatch(() =>
        {
            LocalAsrReachable = false;
            LastError = "[Local ASR] 正在重启服务...";
        });
        _ = _runtime.StartLocalAsrServerAsync();
    }

    public void ToggleMicMute()
    {
        var muted = !_micMuted;
        _runtime.SetMicMuted(muted);
        _dispatch(() => MicMuted = muted);
    }

    /// <summary>Interrupts the AI immediately — stops current speech/generation and playback.</summary>
    public void StopSpeaking() => _runtime.StopSpeaking();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
