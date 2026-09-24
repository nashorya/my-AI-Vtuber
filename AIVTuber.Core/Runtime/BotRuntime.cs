using AIVTuber.Core.Cortico;
using AIVTuber.Core.Audio;
using System.Threading.Channels;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Bot.Turns;
using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Memory;
using AIVTuber.Core.Obs;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.RealtimeAsr;
using AIVTuber.Core.Vts;

namespace AIVTuber.Core.Runtime;

/// <summary>
/// Owns and wires every pipeline module. UI-agnostic: exposes events and state,
/// and supports runtime reconfiguration via <see cref="ApplyConfigAsync"/>.
/// </summary>
public sealed class BotRuntime : IAsyncDisposable
{
    private readonly string _baseDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _configApplyGate = new(1, 1);
    private readonly object _backgroundTasksLock = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly Func<RuntimeChange, Task>? _applyChangesOverride;
    private readonly AsrSidecarProcess _asrSidecar;
    private AppConfig _config;
    private AppConfig _activeConfig;
    private AppConfig _lastKnownGoodConfig;
    private AppConfig? _candidateConfig;
    private long _activeRevision = 1;
    private long _candidateRevision = 1;
    private long _lastKnownGoodRevision = 1;
    private long _nextRevision = 1;

    private MemoryDb _memoryDb = null!;
    private EmbeddingEngine? _embedding;
    private ViewerRepository _viewerRepo = null!;
    private FactRepository _factRepo = null!;
    private PkTurnRepository _pkTurnRepo = null!;
    private readonly PkMatchBuffer _pkBuffer = new();
    private PkMemoryCurator _pkCurator = null!;
    private ConversationManager _conversation = null!;
    private MemoryExtractor _memoryExtractor = null!;
    private LlmClient _memoryLlm = null!; // owned by BotRuntime so it can be disposed (MemoryExtractor is not IDisposable)
    private LlmClient _pkCuratorLlm = null!; // dedicated client: no character system prompt
    private VtsClient? _vts;
    private CorticoProcess? _cortico;
    private CorticoOptions? _corticoOptions;
    private VtsContinuousSession? _continuousVts;
    public VtsContinuousSession? ContinuousVts => _continuousVts;
    private PixelAvatarDriver? _pixelAvatar;
    private AvatarConfigWatcher? _avatarConfigWatcher;
    private EventHandler<float>? _avatarRmsHandler;
    private EventHandler<string>? _avatarEmotionHandler;
    private EventHandler<string>? _avatarPoseHandler;
    private ObsClient? _obs;
    private IAsrClient _asr = null!;   // provider-specific (whisper-HTTP or DashScope-WebSocket)
    private LlmClient _llm = null!;
    private ITtsClient _tts = null!;   // provider-specific (fish/minimax HTTP or DashScope WebSocket)
    private AudioPlayer _player = null!;
    private BotOrchestrator _orchestrator = null!;
    private AIVTuber.Core.Vision.VisionObservationWorker? _vision;
    private MicrophoneCapture? _mic;
    private VadDetector? _vad;
    private AIVTuber.Core.Audio.LoopbackCapture? _loopback;
    private AIVTuber.Core.Audio.ProcessLoopbackCapture? _processLoopback;
    private VadDetector? _loopbackVad;
    private AIVTuber.Core.Audio.VirtualMicMixer? _virtualMic;
    private BilibiliDanmakuClient? _danmaku;
    private DanmakuSelector? _selector;
    // RT-02 realtime session path: one pump per physical source, each with its own
    // session/buffer/cancellation. Null (legacy whole-segment path) unless realtime is enabled.
    private RealtimeAsrPump? _micAsrPump;
    private RealtimeAsrPump? _loopbackAsrPump;
    private bool _realtimeAsrActive;
    /// <summary>Test injection point for realtime session factories (fake transports).</summary>
    internal Func<IRealtimeAsrSessionFactory>? RealtimeSessionFactoryOverride { get; set; }
    internal RealtimeAsrPump? MicAsrPumpForTests => _micAsrPump;
    internal RealtimeAsrPump? LoopbackAsrPumpForTests => _loopbackAsrPump;
    private readonly WakeGate _wakeGate = new();
    private ConversationTurnGate? _turnGate;
    private TurnManagerV2? _turnManagerV2; // RT-04 v2, only when realtime.turn_manager_v2_enabled
    private long _pendingV2Generation;
    private readonly object _talkInputSync = new();
    private readonly HashSet<Message> _queuedInputs = [];
    private IReadOnlyList<TalkLine> _pendingTurnLines = [];
    private sealed record CapturedOpponent(string Name, string? Uid, string? MatchId);
    private CapturedOpponent? _capturedOpponent;

    private CapturedOpponent CaptureOpponent()
    {
        var (matchId, opponent) = _pkBuffer.CaptureIdentity();
        return new(IdentityPrompt.ResolveOpponentName(_config.Identity.OpponentName, opponent),
            opponent?.Uid, matchId);
    }

    private bool IsCurrentMatch(TalkLine line) =>
        line.Identity != TalkIdentity.Opponent && line.Identity != TalkIdentity.System ||
        line.MatchId == _pkBuffer.MatchId;

    private readonly PipelineStateTracker _stateTracker = new();
    private readonly AIVTuber.Core.Diagnostics.RealtimeTrace _trace;

    public PipelineStateTracker StateTracker => _stateTracker;
    /// <summary>RT-00 chain tracer (ASR→LLM→TTS→playback). Off unless
    /// realtime.trace_enabled=true; never records audio/transcript/image/secret payloads.</summary>
    public AIVTuber.Core.Diagnostics.RealtimeTrace Trace => _trace;
    /// <summary>True when the local ONNX embedding was skipped because
    /// realtime.inference_mode=cloud_only — memory retrieval runs without vector search.</summary>
    public bool MemoryVectorSearchDegraded { get; private set; }
    /// <summary>True when the last StartLocalAsrServerAsync call was rejected by the
    /// cloud-only gate (no Python sidecar was spawned).</summary>
    internal bool CloudOnlySidecarSkipped { get; private set; }
    public event EventHandler? AiStartSpeaking;
    public event EventHandler? AiStopSpeaking;
    /// <summary>Fired when Normal/PK interaction mode changes.</summary>
    public event EventHandler? InteractionModeChanged;
    /// <summary>Fired when any pipeline stage (ASR/LLM/TTS) encounters a non-cancellation error.</summary>
    public event EventHandler<string>? PipelineError;
    public event EventHandler<string>? TurnStatusChanged;

    private void ReportTurnStatus(string status)
    {
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[回合状态] {status}");
        TurnStatusChanged?.Invoke(this, status);
    }
    /// <summary>Fired per mic frame with RMS in [0,1] for a level indicator.</summary>
    public event EventHandler<float>? MicLevelUpdated;
    /// <summary>Fired per loopback frame with RMS in [0,1] for a level indicator.</summary>
    public event EventHandler<float>? LoopbackLevelUpdated;

    public bool VtsConnected => _cortico?.IsConnected ?? (_vts?.IsConnected == true);
    /// <summary>In-process PNG avatar driver when backend is pixel/both; null otherwise.</summary>
    public PixelAvatarDriver? PixelAvatar => _pixelAvatar;
    public bool ObsConnected => _obs is not null;
    /// <summary>True only when the danmaku bridge subprocess is actually alive — not merely
    /// when the client object exists.</summary>
    public bool DanmakuActive => _danmaku?.IsBridgeRunning == true;
    public bool LocalAsrActive => _asr is LocalAsrClient;

    private volatile bool _localAsrReachable;
    public bool LocalAsrReachable => _localAsrReachable;
    public event EventHandler<bool>? LocalAsrReachableChanged;

    /// <summary>Returns hotkeys in the currently loaded VTS model, or empty if not connected.</summary>
    public Task<List<VtsHotkeyInfo>> GetVtsHotkeysAsync(CancellationToken ct = default)
        => _vts?.GetHotkeyListAsync(ct) ?? Task.FromResult(new List<VtsHotkeyInfo>());

    /// <summary>Fired with the user's recognized speech / danmaku text.</summary>
    public event EventHandler<string>? UserTranscript;
    /// <summary>Fired with each completed AI sentence.</summary>
    public event EventHandler<string>? SentenceReady;
    /// <summary>Fired when the LLM emits a structured avatar action tag.</summary>
    public event EventHandler<string>? ActionDetected;
    /// <summary>Fired with a detected emotion tag from the LLM output.</summary>
    public event EventHandler<string>? EmotionDetected;
    /// <summary>Fired with the user's detected emotion from Qwen-ASR (null/neutral suppressed).</summary>
    public event EventHandler<string>? UserEmotionDetected;
    /// <summary>Fired with transcribed text from system audio (loopback/PC source).</summary>
    public event EventHandler<string>? LoopbackTranscript;
    /// <summary>Fired when the parsed PK opponent snapshot changes (name/uid/room).</summary>
    public event EventHandler<PkOpponent?>? PkOpponentChanged;

    public AppConfig CurrentConfig => ConfigManager.Clone(_activeConfig);
    public AppConfig? CandidateConfig => _candidateConfig is null ? null : ConfigManager.Clone(_candidateConfig);
    public long ActiveConfigRevision => Interlocked.Read(ref _activeRevision);
    public long CandidateConfigRevision => Interlocked.Read(ref _candidateRevision);
    public long LastKnownGoodConfigRevision => Interlocked.Read(ref _lastKnownGoodRevision);
    public bool CandidateConfigIsActive => CandidateConfigRevision == ActiveConfigRevision;
    public ViewerRepository ViewerRepository => _viewerRepo;
    public FactRepository FactRepository => _factRepo;
    public PkTurnRepository PkTurnRepository => _pkTurnRepo;
    public DanmakuSelector? Selector => _selector;

    public Task ForceExtractMemoryAsync() => _memoryExtractor.ExtractFactsAsync();

    private volatile bool _micMuted;
    private readonly object _micInputSync = new();
    public bool MicMuted => _micMuted;
    public void SetMicMuted(bool muted)
    {
        lock (_micInputSync)
        {
            _micMuted = muted;
            if (!muted) return;
            _vad?.Reset();
            // Realtime path: the mute gap breaks the capture timeline — tear the session down
            // and bump the epoch so late packets cannot leak into the next session.
            _micAsrPump?.NotifyCaptureGap();
            Interlocked.Exchange(ref _micSpeechChannel, null)?.Writer.TryComplete();
        }
    }

    /// <summary>Immediately stops any in-progress AI generation/speech: cancels the pipeline,
    /// halts playback, and closes the VTS mouth. Safe to call when idle (no-op).</summary>
    public void StopSpeaking()
    {
        lock (_talkInputSync)
        {
            _turnGate?.Clear();
            _turnManagerV2?.NoteStopCommand();
            _queuedInputs.Clear();
        }
        _orchestrator?.SetHold(false);
        _orchestrator?.Interrupt();
    }

    // True while AI is speaking — loopback VAD feed is paused to prevent self-hearing.
    private volatile bool _loopbackVadMuted;

    // Streaming-ASR channels: when AsrConfig.Streaming is true, each in-progress speech segment
    // pushes its frames into one of these channels; the ASR client reads them as an
    // IAsyncEnumerable. Lazily created on the first SpeechFrame, completed on SpeechDetected.
    // Unbounded: VAD segments are finite (ended by post-speech silence). A small bounded
    // DropOldest channel previously truncated utterances longer than ~2s.
    private Channel<byte[]>? _micSpeechChannel;
    private Channel<byte[]>? _loopbackSpeechChannel;
    private static Channel<byte[]> NewSpeechChannel()
        => Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

    // Minimum segment peak (0..1) to send loopback audio to ASR. Below this it's silence/noise,
    // and the local ASR fabricates phantom 对面 lines from it. Real speech is well above this.
    private const float LoopbackAsrMinPeak = 0.05f;
    private const float MicAsrMinPeak = 0.03f;

    /// <summary>Abandon an in-progress loopback speech channel so muted/reset gaps cannot
    /// leak stale frames into a later ASR session.</summary>
    private void AbandonLoopbackSpeechChannel()
    {
        var channel = Interlocked.Exchange(ref _loopbackSpeechChannel, null);
        channel?.Writer.TryComplete();
    }

    /// <summary>Feeds a loopback frame to its VAD unless the AI itself is speaking (which would
    /// otherwise feed the AI's own TTS back into the 对面 channel).</summary>
    private void FeedLoopback(byte[] buf)
    {
        if (_loopbackVad is null) return;
        if (_loopbackVadMuted)
        {
            _loopbackVad.Reset(); // drop any half-open segment so it can't merge across the gap
            _turnManagerV2?.NoteVoiceActivity(TurnSource.Loopback, false);
            AbandonLoopbackSpeechChannel();
            _loopbackAsrPump?.NotifyCaptureGap();
            return;
        }
        _loopbackVad.Feed(buf);
        _loopbackAsrPump?.OnCapturedFrame(buf, _loopbackVad.IsSpeaking);
    }

    private static float ComputeRms(byte[] pcm16)
    {
        int samples = pcm16.Length / 2;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            float s = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
            sum += s * s;
        }
        return (float)Math.Sqrt(sum / Math.Max(samples, 1));
    }

    public BotRuntime(AppConfig config, string baseDir)
        : this(config, baseDir, null)
    {
    }

    internal BotRuntime(AppConfig config, string baseDir, Func<RuntimeChange, Task>? applyChangesOverride)
    {
        _config = ConfigManager.Clone(config);
        _activeConfig = ConfigManager.Clone(_config);
        _lastKnownGoodConfig = ConfigManager.Clone(_config);
        _candidateConfig = ConfigManager.Clone(_config);
        _baseDir = baseDir;
        _applyChangesOverride = applyChangesOverride;
        _trace = new AIVTuber.Core.Diagnostics.RealtimeTrace(
            _config.Realtime.TraceEnabled, AIVTuber.Core.Diagnostics.SystemRealtimeClock.Instance);
        foreach (var violation in CloudOnlyGate.Validate(_config.Realtime, _config.Asr))
            PipelineError?.Invoke(this, violation);
        _asrSidecar = new AsrSidecarProcess(baseDir);
        _asrSidecar.Diagnostic += (_, line) =>
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[Local ASR] {line}");
        _asrSidecar.UnexpectedExit += (_, _) =>
        {
            SetLocalAsrReachable(false);
            PipelineError?.Invoke(this, "[Local ASR] 服务进程意外退出");
        };
    }

    /// <summary>Initializes all modules. Equivalent to the old Program startup sequence.</summary>
    public async Task StartAsync()
    {
        await InitMemoryAsync();
        if (_config.Avatar.UsesVts)
            await InitVtsAsync();
        else
            Console.WriteLine($"[Avatar] backend={_config.Avatar.Backend} — skip VTS connect");
        InitObs();
        InitPipeline();
        InitPixelAvatar();
        await InitDanmakuAsync();
        StartAudio();
        _stateTracker.Started();
        SuperviseBackgroundTask(StartLocalAsrServerAsync());
    }

    /// <summary>Show a sticker on the pixel avatar if active (no-op for VTS-only).</summary>
    public void ShowAvatarSticker(string stickerId) => _pixelAvatar?.ShowSticker(stickerId);

    /// <summary>Set idle/special state on the pixel avatar if active.</summary>
    public void SetAvatarIdleState(string state) => _pixelAvatar?.SetIdleState(state);

    internal async Task InitMemoryAsync()
    {
        var dbPath = Path.Combine(_baseDir, _config.Memory.DatabasePath);
        _memoryDb = new MemoryDb(dbPath);
        await _memoryDb.InitializeAsync();
        _embedding = null;
        MemoryVectorSearchDegraded = false;
        if (!CloudOnlyGate.ShouldLoadLocalEmbedding(_config.Realtime))
        {
            // Explicit, visible degradation — the gate must not silently start the ONNX
            // runtime in cloud-only mode. Existing databases and memories are untouched.
            MemoryVectorSearchDegraded = true;
            Console.WriteLine("[记忆] cloud_only 模式：本地向量模型(bge-small-zh ONNX)已禁用，降级为非向量检索");
            AIVTuber.Core.Diagnostics.DebugLog.Write("[记忆] cloud_only gate: local embedding skipped (non-vector search)");
        }
        else
        try
        {
            var modelDir = Path.Combine(_baseDir, _config.Memory.EmbeddingModelPath);
            var hasModel = File.Exists(Path.Combine(modelDir, "model.onnx"));
            var hasVocab = File.Exists(Path.Combine(modelDir, "vocab.txt"));
            if (Directory.Exists(modelDir) && hasModel && hasVocab)
            {
                _embedding = new EmbeddingEngine(modelDir);
                Console.WriteLine($"[记忆] 向量引擎已加载: {modelDir}");
            }
            else Console.WriteLine("[记忆] 向量模型或词表(vocab.txt)未找到，使用字符串相似度兜底");
        }
        catch (Exception ex) { Console.WriteLine($"[记忆] 向量引擎加载失败: {ex.Message}"); }
        _viewerRepo = new ViewerRepository(_memoryDb);
        _factRepo = new FactRepository(_memoryDb, _embedding);
        _pkTurnRepo = new PkTurnRepository(_memoryDb);
        _conversation = new ConversationManager(_config.Llm);
        _conversation.SetMemory(_viewerRepo, _factRepo, _pkTurnRepo);
        _conversation.SetIdentity(_config.Identity);
        if (CurrentPkOpponent is not null)
            _conversation.SetLivePkOpponent(CurrentPkOpponent);
        BuildMemoryExtractor();
    }

    /// <summary>(Re)creates the memory extractor and its owned LlmClient, disposing the previous one.</summary>
    private void BuildMemoryExtractor()
    {
        _memoryLlm?.Dispose();
        _pkCuratorLlm?.Dispose();
        _memoryLlm = new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, systemPrompt: "");
        // Curator must not inherit the VTuber prompt — that fights JSON keep-index output.
        _pkCuratorLlm = new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, systemPrompt: "");
        _memoryExtractor = new MemoryExtractor(_memoryLlm, _factRepo, _config.Memory, _conversation);
        _pkCurator = new PkMemoryCurator(_pkCuratorLlm, _pkTurnRepo);
    }

    public Task ConnectContinuousVtsAsync() => _continuousVts is not null
        ? _continuousVts.ConnectAsync(_cts.Token)
        : Task.FromException(new InvalidOperationException("请在 config.json 将 avatar.backend 设为 vts 或 both，再重启应用"));

    private async Task InitVtsAsync()
    {
        _corticoOptions ??= CorticoOptions.Load(_baseDir);
        if (_corticoOptions.Enabled)
        {
            _cortico = await CorticoProcess.StartAsync(_corticoOptions, _baseDir, _config.Vts,
                () => _tts, () => _config.Tts,
                message => AIVTuber.Core.Diagnostics.DebugLog.Write($"[Cortico] {message}"), _cts.Token);
            return;
        }
        _vts = new VtsClient(_config.Vts);
        _vts.OnError += (_, msg) => PipelineError?.Invoke(this, $"[VTS] {msg}");
        _continuousVts = new VtsContinuousSession(_vts, _config.Vts.ContinuousControl);
        try
        {
            if (_config.Vts.ContinuousControl.Enabled) await _continuousVts.ConnectAsync(_cts.Token);
            else await _vts.ConnectAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            PipelineError?.Invoke(this, $"[VTS] 连接失败: {ex.Message}");
            // Keep the session so the settings panel can retry without restarting the app.
        }
    }

    private void InitPixelAvatar()
    {
        UnwirePixelAvatar();
        _avatarConfigWatcher?.Dispose();
        _avatarConfigWatcher = null;
        _pixelAvatar = null;

        if (!_config.Avatar.UsesPixel)
        {
            Console.WriteLine($"[Avatar] backend={_config.Avatar.Backend} — pixel renderer off");
            return;
        }

        try
        {
            var assetsDir = AppPaths.ResolveAvatarAssetsDirectory(_config.Avatar.AssetsPath);

            if (!Directory.Exists(assetsDir))
            {
                var msg = $"[Avatar] assets directory missing: {assetsDir}";
                Console.WriteLine(msg);
                PipelineError?.Invoke(this, msg);
                // Still create a driver with placeholder pack so the window can show something.
                assetsDir = Path.Combine(_baseDir, "assets", "avatar");
            }

            Console.WriteLine($"[Avatar] resolving pack under: {assetsDir}");
            var pack = AvatarConfigLoader.Load(assetsDir);
            var available = AvatarConfigLoader.ResolveAvailableStates(pack, assetsDir);
            if (available.Count == 0 && AvatarConfigLoader.ResolveDevPlaceholderIdle(assetsDir) is not null)
            {
                var missingHint = pack.States.Count > 0
                    ? $"avatar.json loaded ({pack.Meta.Name}) but 0/{pack.States.Count} sprite files found under {assetsDir}"
                    : $"no usable sprites under {assetsDir}";
                Console.WriteLine($"[Avatar] falling back to dev_placeholder pack — {missingHint}");
                PipelineError?.Invoke(this, $"[Avatar] {missingHint}");
                pack = AvatarConfigLoader.CreatePlaceholderPack();
                available = AvatarConfigLoader.ResolveAvailableStates(pack, assetsDir);
            }

            _pixelAvatar = new PixelAvatarDriver(pack, assetsDir, available, _config.Avatar.EmotionMap);
            // Wire after InitPipeline creates the orchestrator; StartAsync calls InitPipeline first.
            if (_orchestrator is not null)
                WirePixelAvatar();
            StartAvatarConfigWatcher(assetsDir);
            Console.WriteLine($"[Avatar] pixel driver ready ({pack.Meta.Name}, states={available.Count}) @ {assetsDir}");
        }
        catch (Exception ex)
        {
            var msg = $"[Avatar] pixel init failed: {ex.Message}";
            Console.WriteLine(msg);
            PipelineError?.Invoke(this, msg);
            _pixelAvatar = null;
        }
    }

    private void StartAvatarConfigWatcher(string assetsDir)
    {
        _avatarConfigWatcher?.Dispose();
        _avatarConfigWatcher = null;
        if (!Directory.Exists(assetsDir)) return;

        var capturedDir = assetsDir;
        _avatarConfigWatcher = new AvatarConfigWatcher(
            capturedDir,
            () =>
            {
                try
                {
                    if (!AvatarConfigLoader.TryLoad(capturedDir, out var pack))
                    {
                        Console.WriteLine("[Avatar] hot-reload skipped (parse failed / placeholder)");
                        return Task.CompletedTask;
                    }

                    var available = AvatarConfigLoader.ResolveAvailableStates(pack, capturedDir);
                    _pixelAvatar?.ReloadConfig(pack, available);
                    Console.WriteLine($"[Avatar] hot-reloaded ({pack.Meta.Name})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Avatar] hot-reload error: {ex.Message}");
                }
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(300));
    }

    private void WirePixelAvatar()
    {
        if (_pixelAvatar is null || _player is null) return;

        _avatarRmsHandler = (_, rms) => _pixelAvatar?.OnRms(rms);
        _player.RmsUpdated += _avatarRmsHandler;

        _avatarEmotionHandler = (_, emotion) => _pixelAvatar?.SetEmotion(emotion);
        _orchestrator.OnEmotionDetected += _avatarEmotionHandler;

        _avatarPoseHandler = (_, pose) => _pixelAvatar?.SetPose(pose);
        _orchestrator.OnPoseDetected += _avatarPoseHandler;
    }

    private void UnwirePixelAvatar()
    {
        if (_avatarRmsHandler is not null && _player is not null)
            _player.RmsUpdated -= _avatarRmsHandler;
        if (_avatarEmotionHandler is not null && _orchestrator is not null)
            _orchestrator.OnEmotionDetected -= _avatarEmotionHandler;
        if (_avatarPoseHandler is not null && _orchestrator is not null)
            _orchestrator.OnPoseDetected -= _avatarPoseHandler;
        _avatarRmsHandler = null;
        _avatarEmotionHandler = null;
        _avatarPoseHandler = null;
    }

    private void InitObs()
    {
        if (!_config.Obs.Enable) { _obs = null; return; }
        _obs = new ObsClient(_config.Obs);
        SuperviseBackgroundTask(_obs.ConnectAsync(_cts.Token));
    }

    internal int BackgroundTaskCount
    {
        get { lock (_backgroundTasksLock) return _backgroundTasks.Count; }
    }

    internal void SuperviseBackgroundTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_backgroundTasksLock) _backgroundTasks.Add(task);
        _ = ObserveBackgroundTaskAsync(task);
    }

    private async Task ObserveBackgroundTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected during runtime shutdown.
        }
        catch (Exception ex)
        {
            PipelineError?.Invoke(this, $"[Background] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_backgroundTasksLock) _backgroundTasks.Remove(task);
        }
    }

    private async Task DrainBackgroundTasksAsync()
    {
        Task[] pending;
        lock (_backgroundTasksLock) pending = [.. _backgroundTasks];
        if (pending.Length == 0) return;

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Observed by ObserveBackgroundTaskAsync; cancellation is normal on shutdown.
        }
        catch
        {
            // Individual failures are observed and surfaced by ObserveBackgroundTaskAsync.
        }
        finally
        {
            lock (_backgroundTasksLock)
            {
                foreach (var task in pending)
                    _backgroundTasks.Remove(task);
            }
        }
    }

    private static IAsrClient CreateAsrClient(AsrConfig asr, Action<Exception>? onError = null)
    {
        if (asr.Provider.ToLowerInvariant() is "local")
            return new LocalAsrClient(asr.LocalAsrUrl);

        // Realtime-only providers: no legacy whole-segment implementation — fail loudly if the
        // legacy path is reached instead of silently mis-routing realtime audio.
        if (asr.Provider.ToLowerInvariant() is "tencent_realtime" or "volcano_realtime")
            return new RealtimeOnlyAsrClient(asr.Provider);

        if (asr.Provider.ToLowerInvariant() is "minimax")
        {
            asr.ActivateStoredKey();
            return new MiniMaxAsrClient(asr.ApiKey, asr.Model);
        }

        if (asr.Provider.ToLowerInvariant() is "aliyun" or "dashscope")
        {
            // Qwen-ASR uses a different WebSocket protocol (OpenAI Realtime-style) at /realtime
            return !string.IsNullOrWhiteSpace(asr.Model) && asr.Model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
                ? new QwenAsrClient(asr, onError)
                : new DashScopeAsrClient(asr, onError);
        }
        return new AsrClient(
            asr.Provider.Contains("deepseek") ? $"https://{asr.Provider}" : $"https://api.{asr.Provider}.com",
            asr.ApiKey,
            asr.Model);
    }

    private static ITtsClient CreateTtsClient(TtsConfig tts, AIVTuber.Core.Diagnostics.RealtimeTrace? trace = null)
        => tts.Provider.ToLowerInvariant() switch
        {
            "aliyun" or "cosyvoice" or "dashscope" => new DashScopeTtsClient(tts),
            "minimax" => tts.Transport.Equals("bidi", StringComparison.OrdinalIgnoreCase)
                ? new AIVTuber.Core.RealtimeTts.MiniMaxBidiTtsClient(tts, trace) // RT-06: bidirectional WS session (opt-in, host must be configured)
                : tts.Transport.Equals("streaming", StringComparison.OrdinalIgnoreCase)
                    ? new MiniMaxHttpStreamingTtsClient(tts) // RT-01: opt-in HTTP streaming
                    : new MiniMaxWsTtsClient(tts),           // legacy: previous per-sentence WS path
            "dots" or "dots-tts" => new DotsTtsClient(tts),
            "mimo" or "xiaomi" or "xiaomimimo" => new MimoTtsClient(tts),
            _ => new TtsClient(tts),
        };

    private void InitPipeline(RuntimeChange rebuild =
        RuntimeChange.RebuildAsr | RuntimeChange.RebuildLlm | RuntimeChange.RebuildTts)
    {
        _orchestrator?.Dispose();

        if (_asr is null || rebuild.HasFlag(RuntimeChange.RebuildAsr))
        {
            (_asr as IDisposable)?.Dispose();
            _asr = CreateAsrClient(_config.Asr, ex => PipelineError?.Invoke(this, $"[ASR连接] {ex.GetType().Name}: {ex.Message}"));
        }
        if (_llm is null || rebuild.HasFlag(RuntimeChange.RebuildLlm))
        {
            _llm?.Dispose();
            _llm = new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model,
                BuildLlmSystemPrompt(),
                _config.Avatar.UsesVts && _config.Vts.ContinuousControl.Enabled
                    ? () => _continuousVts is { } vts && vts.DeclaredChannels.Length > 0
                        ? vts.DeclaredChannels
                        : [] : null,
                _config.Llm.ReplyProtocol);
        }
        if (_tts is null || rebuild.HasFlag(RuntimeChange.RebuildTts))
        {
            (_tts as IDisposable)?.Dispose();
            _tts = CreateTtsClient(_config.Tts, _trace);
        }
        if (_player is null)
        {
            _player = new AudioPlayer(sampleRate: _config.Tts.SampleRate,
                deviceIndex: _config.Audio.OutputDeviceIndex);
            // Single stable tap: reads _virtualMic field, so mixer restarts pick up automatically.
            _player.PcmChunkPlayed += (_, chunk) => _virtualMic?.WriteTts(chunk);
        }
        EnsureTurnGate();
        _conversation?.SetIdentity(_config.Identity);
        _orchestrator = new BotOrchestrator(
            _asr, _llm, _tts, _player, _config.Tts, _vts, _config.Vts,
            ttsEmotionMap: _config.Avatar.EmotionMap);
        _orchestrator.Cortico = _cortico;
        _orchestrator.Trace = _trace;
        if (_config.Avatar.UsesVts && _config.Vts.ContinuousControl.Enabled && _continuousVts is not null)
            _orchestrator.ConfigureContinuousControl(_continuousVts);
        _orchestrator.ShouldSpeak = probe => _wakeGate.ShouldSpeak(
            _config.Interaction.IsPkMode,
            _config.Interaction.WakeKeywords,
            _config.Interaction.WakeHoldSec,
            probe,
            Environment.TickCount64);

        _orchestrator.OnError += (_, msg) => PipelineError?.Invoke(this, msg);

        _orchestrator.OnUserTranscript += (_, text) =>
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[麦克风识别] 「{text}」");
            _stateTracker.TranscriptReady(Environment.TickCount64);
            UserTranscript?.Invoke(this, text);
        };
        _orchestrator.OnReplyCommitted += (_, reply) => CommitReply(reply);
        _orchestrator.OnSentenceReady += (_, s) => SentenceReady?.Invoke(this, s);
        _orchestrator.OnEmotionDetected += (_, e) => EmotionDetected?.Invoke(this, e);
        _orchestrator.OnActionDetected += (_, a) => ActionDetected?.Invoke(this, a);
        _orchestrator.OnUserEmotionDetected += (_, e) => UserEmotionDetected?.Invoke(this, e);
        _orchestrator.OnLoopbackTranscript += (_, t) =>
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[内录识别→对面] 「{t}」");
            if (CurrentPkOpponent is not null)
                _pkBuffer.NoteOpponentSpeech(t, "loopback");
            LoopbackTranscript?.Invoke(this, t);
        };

        _orchestrator.ConfigureOutputCommands(
            _obs is null
                ? null
                : (text, ct) => _obs.SetSubtitleTypewriterAsync(
                    text, _config.Obs.AssistantTextComponent, ct),
            _obs is null
                ? null
                : (text, ct) => _obs.SetSubtitleAsync(
                    $"[用户] {text}", _config.Obs.UserTextComponent, ct));

        _orchestrator.OnFirstSentenceToTts += (_, _) =>
            _stateTracker.LlmFirstSentenceReady(Environment.TickCount64);

        // Mute loopback VAD while AI is playing so it doesn't hear its own TTS output.
        // Also abandon any in-flight streaming channel so mute gaps cannot mix into later ASR.
        _orchestrator.OnAiStartSpeaking += (_, _) =>
        {
            _loopbackVadMuted = true;
            _loopbackVad?.Reset();
            Interlocked.Exchange(ref _capturedOpponent, null);
            AbandonLoopbackSpeechChannel();
        };
        _orchestrator.OnAiStopSpeaking += (_, _) =>
        {
            _loopbackVadMuted = false;
            _loopbackVad?.Reset();
            Interlocked.Exchange(ref _capturedOpponent, null);
            AbandonLoopbackSpeechChannel();
            if (_pkBuffer.IsActive)
                _pkBuffer.EndAssistantReply();
        };

        // Stable speaking->selector bridge, added once per orchestrator. Null-safe so it works
        // whether or not danmaku is active, and reads the current _selector field after rebuilds —
        // so it never needs re-adding (which would accumulate handlers).
        _orchestrator.OnAiStartSpeaking += (_, _) =>
        {
            _selector?.SetSpeaking(true);
            _stateTracker.SpeakingStarted(Environment.TickCount64);
            AiStartSpeaking?.Invoke(this, EventArgs.Empty);
        };
        _orchestrator.OnAiStopSpeaking += (_, _) =>
        {
            _selector?.SetSpeaking(false);
            _selector?.TrySelectNext();
            _stateTracker.SpeakingStopped();
            AiStopSpeaking?.Invoke(this, EventArgs.Empty);
        };

        // Orchestrator is recreated here — re-attach pixel avatar hooks if active.
        if (_pixelAvatar is not null)
        {
            if (_avatarEmotionHandler is not null)
                _orchestrator.OnEmotionDetected -= _avatarEmotionHandler;
            if (_avatarPoseHandler is not null)
                _orchestrator.OnPoseDetected -= _avatarPoseHandler;
            _avatarEmotionHandler = (_, emotion) => _pixelAvatar?.SetEmotion(emotion);
            _avatarPoseHandler = (_, pose) => _pixelAvatar?.SetPose(pose);
            _orchestrator.OnEmotionDetected += _avatarEmotionHandler;
            _orchestrator.OnPoseDetected += _avatarPoseHandler;
        }

        StartVisionIfNeeded();
    }

    /// <summary>VIS-02 wiring. Kept strictly optional and non-blocking: failures here never
    /// affect the voice pipeline, and the worker is never awaited by it.</summary>
    private void StartVisionIfNeeded()
    {
        if (_vision is not null) return;
        if (_config.Vision is not { Enabled: true }) return;
        try
        {
            var captureSource = new AIVTuber.Core.Vision.GdiWindowCaptureSource();
            var capture = new AIVTuber.Core.Vision.WindowCaptureService(captureSource, _config.Vision);
            var worker = new AIVTuber.Core.Vision.VisionObservationWorker(
                _config.Vision, capture,
                AIVTuber.Core.Vision.VisionClientFactory.Create(_config.Vision));
            worker.VisionError += msg => PipelineError?.Invoke(this, msg);
            // The operator still selects the concrete window (with preview + consent) before
            // any capture happens; without a selected target the service only reports
            // "no target" and uploads nothing.
            worker.Start();
            _vision = worker;
        }
        catch (Exception ex)
        {
            PipelineError?.Invoke(this, $"[视觉] 未启动（保持关闭）：{ex.Message}");
        }
    }

    /// <summary>Merges VTS + pixel avatar emotion/pose allow-lists into the LLM system prompt.</summary>
    private string BuildLlmSystemPrompt()
    {
        var extraEmotions = _config.Avatar.EmotionMap.Keys;
        var basePrompt = _config.Vts.BuildSystemPrompt(
            _config.Llm.SystemPrompt,
            extraEmotions,
            ResolvePoseIdsForPrompt());
        var self = IdentityPrompt.ResolveSelfName(_config.Identity.SelfName);
        var opponent = IdentityPrompt.ResolveOpponentName(_config.Identity.OpponentName, CurrentPkOpponent);
        var danmaku = IdentityPrompt.ResolveDanmakuLabel(_config.Identity.DanmakuLabel);
        var protocol = IdentityPrompt.ProtocolAppendix(self, opponent, danmaku, _config.Interaction.WakeKeywords);
        var extra = string.IsNullOrWhiteSpace(_config.Identity.ExtraNotes)
            ? ""
            : "\n" + _config.Identity.ExtraNotes.Trim();
        if (_cortico is not null)
            return _config.Llm.SystemPrompt + "\n\n" + protocol + extra + "\n\n" + _cortico.Prompt;
        return string.IsNullOrWhiteSpace(basePrompt) ? protocol + extra : basePrompt + "\n\n" + protocol + extra;
    }

    private void EnsureTurnGate()
    {
        if (_turnGate is null)
        {
            _turnGate = new ConversationTurnGate();
            _turnGate.StatusChanged += status => TurnStatusChanged?.Invoke(this, status);
            _turnGate.TurnReady += lines => SuperviseBackgroundTask(HandleTurnReadyAsync(lines));
        }
        // RT-04 v2 is created lazily behind its flag; the legacy gate above is untouched and
        // remains the only path while the flag is false (default).
        if (_turnManagerV2 is null && _config.Realtime.TurnManagerV2Enabled)
        {
            _turnManagerV2 = new TurnManagerV2(new TurnManagerOptions
            {
                SelfNames = [_config.Identity.SelfName, .. _config.Interaction.WakeKeywords],
                SpeculativeGenerationEnabled = _config.Realtime.SpeculativeGenerationEnabled,
            });
            _turnManagerV2.StatusChanged += status => ReportTurnStatus(status);
            _turnManagerV2.TurnReady += (ctx, lines) => SuperviseBackgroundTask(HandleTurnReadyV2Async(ctx, lines));
            _turnManagerV2.TurnCancelled += (_, reason) =>
                AIVTuber.Core.Diagnostics.DebugLog.Write($"[回合v2] 已取消 generation，原因={reason}");
            _turnManagerV2.DecisionRecorded += d =>
                AIVTuber.Core.Diagnostics.DebugLog.Write(
                    $"[回合v2] 决策 respond={d.Respond} level={d.Level} reason={d.ReasonCode}");
        }
    }

    /// <summary>RT-04 v2 turn dispatch. Mirrors the legacy path, with CanCommit additionally
    /// bound to the v2 generation (voice recovery / match change / retraction / stop).</summary>
    private async Task HandleTurnReadyV2Async(TurnContextV2 context, IReadOnlyList<TalkLine> lines)
    {
        var manager = _turnManagerV2;
        if (manager is null) return;
        var turnMatchId = _pkBuffer.MatchId;
        var orchestrator = _orchestrator;
        try
        {
            if (orchestrator is null) return;
            lines = lines.Where(IsCurrentMatch).ToArray();
            if (lines.Count == 0) return;
            var formatted = IdentityPrompt.FormatTurn(lines);
            if (string.IsNullOrWhiteSpace(formatted)) return;

            var history = BuildTurnHistory(lines, formatted);

            bool CanCommit()
            {
                return ReferenceEquals(orchestrator, _orchestrator) && ReferenceEquals(manager, _turnManagerV2) &&
                    manager.CanCommit(context.GenerationId) &&
                    turnMatchId == _pkBuffer.MatchId && lines.All(IsCurrentMatch);
            }
            lock (_talkInputSync) _pendingV2Generation = context.GenerationId;
            _stateTracker.TextInputStarted(Environment.TickCount64);
            UserTranscript?.Invoke(this, formatted);
            await orchestrator.ProcessTextAsync(formatted, history, bypassWake: true, canCommit: CanCommit,
                requireStructuredReply: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PipelineError?.Invoke(this, $"[回合v2] {ex.Message}");
        }
        finally
        {
            lock (_talkInputSync)
                foreach (var line in lines)
                    if (line.HistoryMessage is { } message) _queuedInputs.Remove(message);
            _stateTracker.SpeakingStopped();
            manager.CompleteTurn(context.GenerationId);
        }
    }

    internal void AcceptTalkLine(TalkLine line)
    {
        if (_cts.IsCancellationRequested || string.IsNullOrWhiteSpace(line.Text) || !IsCurrentMatch(line)) return;
        var stop = line.Identity == TalkIdentity.Self &&
            IdentityPrompt.IsStopRequest(line.Text, _config.Interaction.WakeKeywords);
        lock (_talkInputSync)
        {
            if (_conversation is null) return;
            var message = _conversation.ObserveUserMessage(IdentityPrompt.FormatTurn([line]));
            if (!stop)
            {
                _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.TurnCandidate);
                _queuedInputs.Add(message);
                var lineWithHistory = line with { HistoryMessage = message };
                if (_turnManagerV2 is not null && _config.Realtime.TurnManagerV2Enabled)
                    _turnManagerV2.AddFinal(lineWithHistory, SourceOf(line.Identity));
                else
                    _turnGate?.AddLine(lineWithHistory);
            }
        }
        if (stop)
        {
            ReportTurnStatus("收到停止请求，回到旁听");
            StopSpeaking();
        }
    }

    private static TurnSource SourceOf(TalkIdentity identity) => identity switch
    {
        TalkIdentity.Self => TurnSource.Microphone,
        TalkIdentity.Opponent => TurnSource.Loopback,
        TalkIdentity.Danmaku => TurnSource.Danmaku,
        _ => TurnSource.System,
    };

    internal List<Message> BuildTurnHistory(IReadOnlyList<TalkLine> lines, string formatted)
    {
        lock (_talkInputSync)
        {
            var subjects = lines.Select(l => l.SubjectUid)
                .Where(uid => !string.IsNullOrEmpty(uid)).Cast<string>()
                .Distinct(StringComparer.Ordinal).ToList();
            var viewerUid = lines.LastOrDefault(l => l.Identity == TalkIdentity.Danmaku).SubjectUid
                ?? lines.LastOrDefault(l => l.Identity == TalkIdentity.Self).SubjectUid;
            var history = _conversation.BuildMessages(viewerUid, formatted, subjects);
            // Current input is supplied separately. Inputs arriving during this turn
            // are already retained, but belong to the next decision, not this snapshot.
            history.RemoveAll(_queuedInputs.Contains);
            foreach (var line in lines)
                if (line.HistoryMessage is { } message) _queuedInputs.Remove(message);
            history.Add(new Message { Role = MessageRole.System, Content = IdentityPrompt.InvitationPolicyFor(_config.Llm.ReplyProtocol) });
            // VIS-02: synchronous read of the in-memory observation snapshot. Never awaits the
            // VLM; when no (valid) snapshot exists the turn proceeds exactly as before.
            if (_vision is { Enabled: true })
            {
                var note = _vision.BuildUntrustedSnapshotNote();
                if (note is not null)
                    history.Add(new Message { Role = MessageRole.System, Content = note });
            }
            return history;
        }
    }

    private async Task HandleTurnReadyAsync(IReadOnlyList<TalkLine> lines)
    {
        var gate = _turnGate;
        if (gate is null) return;
        var revision = gate.ActiveTurnRevision;
        var turnMatchId = _pkBuffer.MatchId;
        var orchestrator = _orchestrator;
        try
        {
            if (orchestrator is null) return;
            lock (_talkInputSync)
                foreach (var stale in lines.Where(l => !IsCurrentMatch(l)))
                    if (stale.HistoryMessage is { } message) _queuedInputs.Remove(message);
            lines = lines.Where(IsCurrentMatch).ToArray();
            if (lines.Count == 0) return;
            var formatted = IdentityPrompt.FormatTurn(lines);
            if (string.IsNullOrWhiteSpace(formatted)) return;

            _pendingTurnLines = lines;
            var history = BuildTurnHistory(lines, formatted);

            bool CanCommit()
            {
                return ReferenceEquals(orchestrator, _orchestrator) && ReferenceEquals(gate, _turnGate) && gate.CanCommit(revision) &&
                    turnMatchId == _pkBuffer.MatchId && lines.All(IsCurrentMatch);
            }
            _stateTracker.TextInputStarted(Environment.TickCount64);
            _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.TurnCommitReady);
            UserTranscript?.Invoke(this, formatted);
            await orchestrator.ProcessTextAsync(formatted, history, bypassWake: true, canCommit: CanCommit,
                requireStructuredReply: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PipelineError?.Invoke(this, $"[回合] {ex.Message}");
        }
        finally
        {
            lock (_talkInputSync)
                foreach (var line in lines)
                    if (line.HistoryMessage is { } message) _queuedInputs.Remove(message);
            _pendingTurnLines = [];
            _stateTracker.SpeakingStopped();
            gate.CompleteTurn(revision);
        }
    }

    private void CommitReply(ClassifiedReply reply)
    {
        var lines = _pendingTurnLines;
        _pendingTurnLines = [];

        switch (reply.Kind)
        {
            case ReplyKind.Speak:
                ReportTurnStatus("回答已生成，开始播放");
                _conversation.MarkInputsPersistable(lines.Select(l => l.HistoryMessage).OfType<Message>());
                _conversation.AddAssistantMessage(reply.Spoken);
                _turnManagerV2?.NoteAssistantMessage(reply.Spoken);
                // PK curation also reads assistant text; it must not provide a
                // second persistence route for a paraphrased PASS observation.
                if (!_conversation.HasTransientContext)
                    NotePkPair(lines, reply.Spoken);
                SuperviseBackgroundTask(_memoryExtractor.OnTurnAsync(true));
                break;
            case ReplyKind.InnerThought:
                ReportTurnStatus("模型选择心里话，本轮不播放语音");
                SuperviseBackgroundTask(_memoryExtractor.OnTurnAsync(true));
                break;
            case ReplyKind.Pass:
                ReportTurnStatus("继续旁听，对话已保留");
                break;
            default:
                break;
        }
    }

    private void NotePkPair(IReadOnlyList<TalkLine> lines, string spoken)
    {
        var opponents = lines.Where(l => l.Identity == TalkIdentity.Opponent).ToArray();
        if (opponents.Length == 0) return;
        var matchId = opponents[0].MatchId;
        if (opponents.Any(l => l.MatchId != matchId)) return;
        _pkBuffer.TryAddPair(matchId, string.Join("\n", opponents.Select(l => l.Text)), spoken);
    }

    private string SelfDisplayName() => IdentityPrompt.ResolveSelfName(_config.Identity.SelfName);

    private string OpponentDisplayName() =>
        IdentityPrompt.ResolveOpponentName(_config.Identity.OpponentName, CurrentPkOpponent);

    private string? SelfUidOrNull() =>
        string.IsNullOrWhiteSpace(_config.Identity.SelfUid) ? null : _config.Identity.SelfUid.Trim();

    private async Task ObserveMicSegmentAsync(SpeechSegment seg)
    {
        var peak = AIVTuber.Core.Diagnostics.DebugLog.PeakRms(seg.AudioData);
        AIVTuber.Core.Diagnostics.DebugLog.Write(
            $"[麦克风段] 时长={(seg.EndTime - seg.StartTime).TotalMilliseconds:F0}ms " +
            $"峰值={peak:F3} micMuted={_micMuted} streaming={_config.Asr.Streaming}");
        var channel = Interlocked.Exchange(ref _micSpeechChannel, null);
        channel?.Writer.TryComplete();
        if (_realtimeAsrActive && _micAsrPump is not null)
        {
            // Realtime path: transcription is committed by the session pump on provider finals;
            // the VAD segment is only a local activity-boundary report here.
            AIVTuber.Core.Diagnostics.DebugLog.Write("[麦克风段] 实时会话模式：VAD 仅报告活动边界，本段不另起 ASR");
            return;
        }
        if (_micMuted || peak < MicAsrMinPeak)
        {
            if (peak < MicAsrMinPeak)
                AIVTuber.Core.Diagnostics.DebugLog.Write($"[麦克风段] 能量过低(<{MicAsrMinPeak})，跳过ASR");
            return;
        }
        _trace.BeginTurn();
        // VAD closed the segment here: this is the local end-of-speech boundary on the capture
        // timeline (plan RT-00 input_last_voiced), not the ASR request time.
        _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.InputLastVoiced);
        _stateTracker.InputStarted(Environment.TickCount64);
        AsrResult result;
        if (_config.Asr.Streaming)
        {
            // Consumer of the buffered frames starts only now (after SpeechDetected) — see
            // docs/realtime/baseline.md; RT-02 moves it to first voiced frame.
            _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.AsrFirstAudioSent);
            var stream = channel is null
                ? System.Linq.AsyncEnumerable.Empty<byte[]>()
                : channel.Reader.ReadAllAsync();
            result = await _orchestrator.TranscribeStreamAsync(stream, _cts.Token).ConfigureAwait(false);
        }
        else
        {
            _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.AsrFirstAudioSent);
            result = await _orchestrator.TranscribeAsync(seg.AudioData, _cts.Token).ConfigureAwait(false);
        }
        _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.AsrSegmentFinal);

        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            var text = BotOrchestrator.AnnotateWithUserEmotion(result.Text, result.Emotion);
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[麦克风识别] 「{result.Text}」");
            _stateTracker.TranscriptReady(Environment.TickCount64);
            if (result.Emotion is not null)
                UserEmotionDetected?.Invoke(this, result.Emotion);
            UserTranscript?.Invoke(this, result.Text);
            ReportTurnStatus("麦克风已识别，记录对话");
            AcceptTalkLine(new TalkLine(TalkIdentity.Self, SelfDisplayName(), text, SelfUidOrNull(), StartedAt: seg.StartTime));
        }
    }

    private async Task ObserveLoopbackSegmentAsync(SpeechSegment seg)
    {
        var opponent = Interlocked.Exchange(ref _capturedOpponent, null) ?? CaptureOpponent();
        var peak = AIVTuber.Core.Diagnostics.DebugLog.PeakRms(seg.AudioData);
        AIVTuber.Core.Diagnostics.DebugLog.Write(
            $"[内录段] 时长={(seg.EndTime - seg.StartTime).TotalMilliseconds:F0}ms " +
            $"峰值={peak:F3} loopbackMuted={_loopbackVadMuted} streaming={_config.Asr.Streaming}");
        var channel = Interlocked.Exchange(ref _loopbackSpeechChannel, null);
        channel?.Writer.TryComplete();
        if (_realtimeAsrActive && _loopbackAsrPump is not null)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write("[内录段] 实时会话模式：VAD 仅报告活动边界，本段不另起 ASR");
            return;
        }
        if (peak < LoopbackAsrMinPeak)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[内录段] 能量过低(<{LoopbackAsrMinPeak})，跳过ASR");
            return;
        }
        _trace.BeginTurn();
        _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.InputLastVoiced);
        AsrResult result;
        if (_config.Asr.Streaming)
        {
            _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.AsrFirstAudioSent);
            var stream = channel is null
                ? System.Linq.AsyncEnumerable.Empty<byte[]>()
                : channel.Reader.ReadAllAsync();
            result = await _orchestrator.TranscribeStreamAsync(stream, _cts.Token).ConfigureAwait(false);
        }
        else
        {
            _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.AsrFirstAudioSent);
            result = await _orchestrator.TranscribeAsync(seg.AudioData, _cts.Token).ConfigureAwait(false);
        }
        _trace.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.AsrSegmentFinal);

        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[内录识别→对面] 「{result.Text}」");
            LoopbackTranscript?.Invoke(this, result.Text);
            AcceptTalkLine(new TalkLine(
                TalkIdentity.Opponent,
                opponent.Name,
                result.Text,
                opponent.Uid, opponent.MatchId, seg.StartTime));
        }
    }

    private IEnumerable<string> ResolvePoseIdsForPrompt()
    {
        if (_pixelAvatar?.Pack.Poses?.List is { Count: > 0 } live)
            return live.Keys;

        if (!_config.Avatar.UsesPixel)
            return [];

        try
        {
            var dir = AppPaths.ResolveAvatarAssetsDirectory(_config.Avatar.AssetsPath);
            if (AvatarConfigLoader.TryLoad(dir, out var pack)
                && pack.Poses?.List is { Count: > 0 } poses)
                return poses.Keys;
        }
        catch
        {
            // Prompt enrichment is best-effort.
        }

        return [];
    }

    /// <summary>
    /// Starts the managed local ASR sidecar and waits for an explicit ready health state.
    /// </summary>
    public async Task StartLocalAsrServerAsync()
    {
        CloudOnlySidecarSkipped = false;
        if (_asr is not LocalAsrClient localAsr) return;
        if (!CloudOnlyGate.ShouldStartAsrSidecar(_config.Realtime, _config.Asr.Provider))
        {
            // RT-00 cloud-only gate: never spawn the Python ASR sidecar in this mode.
            CloudOnlySidecarSkipped = true;
            SetLocalAsrReachable(false);
            const string message = "[Local ASR] cloud_only 模式：已阻止本地 ASR sidecar 启动（请改用云端 ASR provider）";
            AIVTuber.Core.Diagnostics.DebugLog.Write(message);
            PipelineError?.Invoke(this, message);
            return;
        }
        SetLocalAsrReachable(false);
        try
        {
            var health = await _asrSidecar.StartAsync(
                _config.Asr,
                localAsr,
                TimeSpan.FromSeconds(60),
                _cts.Token).ConfigureAwait(false);
            SetLocalAsrReachable(health.Status == LocalAsrHealthStatus.Ready);
        }
        catch (OperationCanceledException)
        {
            SetLocalAsrReachable(false);
        }
        catch (Exception ex)
        {
            SetLocalAsrReachable(false);
            PipelineError?.Invoke(this, $"[Local ASR] 启动失败: {ex.Message}");
            throw;
        }
    }

    private void SetLocalAsrReachable(bool reachable)
    {
        if (_localAsrReachable == reachable) return;
        _localAsrReachable = reachable;
        LocalAsrReachableChanged?.Invoke(this, reachable);
    }

    /// <summary>Builds the realtime session factory for the configured provider. Real network
    /// transports are wired here but remain 未实测 (no real keys); tests inject fakes via
    /// <see cref="RealtimeSessionFactoryOverride"/>.</summary>
    private IRealtimeAsrSessionFactory CreateRealtimeSessionFactory()
    {
        var asr = _config.Asr;
        switch (asr.Provider.Trim().ToLowerInvariant())
        {
            case "tencent_realtime":
                asr.ActivateStoredKey();
                return new TencentRealtimeAsrSessionFactory(
                    asr.AppId, asr.SecretId, asr.ApiKey,
                    string.IsNullOrWhiteSpace(asr.Model) ? "16k_zh" : asr.Model,
                    transportFactory: null,
                    _config.Realtime.SendPacketMs);
            case "volcano_realtime":
                asr.ActivateStoredKey();
                return new VolcanoRealtimeAsrSessionFactory(
                    asr.AppId, asr.ApiKey, asr.ResourceId,
                    string.IsNullOrWhiteSpace(asr.Model) ? "bigmodel" : asr.Model,
                    transportFactory: null,
                    _config.Realtime.SendPacketMs);
            default:
                throw new InvalidOperationException(
                    $"realtime.streaming_asr_enabled=true 但 asr.provider={asr.Provider} 不是实时 provider" +
                    "（支持 tencent_realtime / volcano_realtime），已按配置错误处理");
        }
    }

    private static DateTime ToWallClock(long tickCountMs)
        => DateTime.UtcNow.AddMilliseconds(tickCountMs - Environment.TickCount64);

    /// <summary>Commits a realtime final segment into the conversation, mirroring the legacy
    /// Observe*SegmentAsync result handling. Client-endpoint snapshots carry their lower
    /// confidence in the log line — they are never presented as vendor finals.</summary>
    private void HandleRealtimeFinal(TranscriptUpdate update)
    {
        try
        {
            var text = update.TextSnapshot?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            var snapshotNote = update.FinalKind == TranscriptFinalKind.ClientEndpointSnapshot
                ? "（客户端断点快照，非厂商 final）"
                : string.Empty;
            var startedAt = ToWallClock(update.AudioStartMs);

            if (update.Source == AIVTuber.Core.Audio.AudioSource.Microphone)
            {
                AIVTuber.Core.Diagnostics.DebugLog.Write($"[麦克风识别·实时] 「{text}」{snapshotNote}");
                _stateTracker.TranscriptReady(Environment.TickCount64);
                UserTranscript?.Invoke(this, text);
                ReportTurnStatus("麦克风已识别，记录对话");
                AcceptTalkLine(new TalkLine(TalkIdentity.Self, SelfDisplayName(), text, SelfUidOrNull(),
                    StartedAt: startedAt));
            }
            else
            {
                AIVTuber.Core.Diagnostics.DebugLog.Write($"[内录识别→对面·实时] 「{text}」{snapshotNote}");
                if (update.OpponentSnapshot is { MatchId: { } snapMatch } && snapMatch == _pkBuffer.MatchId)
                    _pkBuffer.NoteOpponentSpeech(text, "loopback");
                LoopbackTranscript?.Invoke(this, text);
                AcceptTalkLine(new TalkLine(
                    TalkIdentity.Opponent,
                    string.IsNullOrWhiteSpace(update.OpponentSnapshot?.Name)
                        ? OpponentDisplayName()
                        : update.OpponentSnapshot!.Name,
                    text,
                    update.OpponentSnapshot?.Uid,
                    update.OpponentSnapshot?.MatchId,
                    startedAt));
            }
        }
        catch (Exception ex)
        {
            PipelineError?.Invoke(this, $"[实时ASR] 提交转写失败: {ex.Message}");
        }
    }

    private void StartAudio()
    {
        // RT-02: optionally create per-source realtime pumps. Capture callbacks only hand
        // frames to the pump's bounded channel — no network await on the audio thread.
        _realtimeAsrActive = false;
        _micAsrPump = null;
        _loopbackAsrPump = null;
        var realtimeOptions = new RealtimeAsrOptions
        {
            BufferCapacityMs = _config.Realtime.BufferCapacityMs,
            PrerollMs = _config.Realtime.PrerollMs,
            IdleDisconnectMs = _config.Realtime.IdleDisconnectMs,
        };
        IRealtimeAsrSessionFactory? realtimeFactory = null;
        if (_config.Realtime.StreamingAsrEnabled)
        {
            try
            {
                realtimeFactory = RealtimeSessionFactoryOverride?.Invoke() ?? CreateRealtimeSessionFactory();
            }
            catch (Exception ex)
            {
                var msg = $"[实时ASR] 配置错误，实时会话未启用（继续 legacy 路径）: {ex.Message}";
                Console.WriteLine(msg);
                PipelineError?.Invoke(this, msg);
            }
        }

        _mic = new MicrophoneCapture(_config.Audio.InputDeviceIndex);
        _vad = new VadDetector(_config.Audio.VadAggressiveness, _config.Audio.PreSpeechPaddingMs, _config.Audio.PostSpeechSilenceMs);
        if (realtimeFactory is not null)
        {
            _micAsrPump = new RealtimeAsrPump(AIVTuber.Core.Audio.AudioSource.Microphone,
                realtimeFactory, opponentProvider: null, realtimeOptions);
            _micAsrPump.PartialUpdate += (_, u) =>
                AIVTuber.Core.Diagnostics.DebugLog.Write(
                    $"[麦克风识别·实时partial] seg={u.SegmentId} rev={u.Revision} 「{u.TextSnapshot}」");
            _micAsrPump.FinalCommitted += (_, u) => HandleRealtimeFinal(u);
            _micAsrPump.StreamBroken += (_, reason) => PipelineError?.Invoke(this, reason);
            _realtimeAsrActive = true;
        }
        _mic.AudioFrameAvailable += (_, buf) =>
        {
            lock (_micInputSync)
            {
                if (_micMuted) return;
                _vad.Feed(buf);
                // Parallel observation of the same frames (realtime session feed). The VAD
                // verdict for this frame becomes the voiced hint driving session lifecycle.
                _micAsrPump?.OnCapturedFrame(buf, _vad.IsSpeaking);
            }
        };
        _mic.LevelUpdated += (_, level) => MicLevelUpdated?.Invoke(this, level);
        _mic.ErrorOccurred += (_, ex) => PipelineError?.Invoke(this, $"[麦克风] {ex.Message}");

        _vad.SpeechFrame += (_, frame) =>
        {
            if (_micMuted) return;
            _turnManagerV2?.NoteVoiceActivity(TurnSource.Microphone, true);
            if (_realtimeAsrActive) return; // realtime path feeds frames directly
            _continuousVts?.NoteListening();
            if (!_config.Asr.Streaming) return;
            _micSpeechChannel ??= NewSpeechChannel();
            _micSpeechChannel.Writer.TryWrite(frame);
        };
        _vad.SpeechDetected += async (_, seg) =>
        {
            _turnManagerV2?.NoteVoiceActivity(TurnSource.Microphone, false);
            try { await ObserveMicSegmentAsync(seg).ConfigureAwait(false); }
            catch (Exception ex) { PipelineError?.Invoke(this, $"[麦克风] {ex.Message}"); }
        };
        try
        {
            _mic.Start();
        }
        catch (Exception ex)
        {
            var msg = $"[麦克风] 启动失败: {ex.Message}";
            Console.WriteLine(msg);
            PipelineError?.Invoke(this, msg);
            _stateTracker.Started();
        }

        // Optional virtual mic mixer: routes mic + AI TTS into a virtual render device
        // so streaming software (直播姬) can pick up both voices from CABLE Output.
        if (_config.Audio.EnableVirtualMic)
        {
            try
            {
                _virtualMic = new AIVTuber.Core.Audio.VirtualMicMixer(_config.Audio.VirtualMicDeviceName,
                    _config.Tts.SampleRate);
                _virtualMic.Start();
                // Real mic is intentionally not injected into the virtual cable here. The
                // cable carries AI TTS only; mic capture remains available for AI listening.
                Console.WriteLine($"[虚拟麦克风] 混音器已启动 → {_config.Audio.VirtualMicDeviceName}");
            }
            catch (Exception ex)
            {
                var msg = $"[虚拟麦克风] 启动失败: {ex.Message}";
                Console.WriteLine(msg);
                PipelineError?.Invoke(this, msg);
                _virtualMic?.Dispose();
                _virtualMic = null;
            }
        }

        // Optional second channel: system audio for PK / co-streaming scenarios
        if (_config.Audio.EnableLoopbackListen)
        {
            try
            {
                _loopbackVad = new VadDetector(_config.Audio.VadAggressiveness, _config.Audio.PreSpeechPaddingMs, _config.Audio.PostSpeechSilenceMs);
                if (realtimeFactory is not null)
                {
                    // Independent socket/session/buffer/cancellation from the mic pump; the
                    // opponent snapshot is taken at session creation (audio-time), not commit time.
                    _loopbackAsrPump = new RealtimeAsrPump(AIVTuber.Core.Audio.AudioSource.Loopback,
                        realtimeFactory,
                        () =>
                        {
                            var captured = CaptureOpponent();
                            return new OpponentSnapshot(captured.Name, captured.Uid, captured.MatchId);
                        },
                        realtimeOptions);
                    _loopbackAsrPump.PartialUpdate += (_, u) =>
                        AIVTuber.Core.Diagnostics.DebugLog.Write(
                            $"[内录识别·实时partial] seg={u.SegmentId} rev={u.Revision} 「{u.TextSnapshot}」");
                    _loopbackAsrPump.FinalCommitted += (_, u) => HandleRealtimeFinal(u);
                    _loopbackAsrPump.StreamBroken += (_, reason) => PipelineError?.Invoke(this, reason);
                }

                _loopbackVad.SpeechFrame += (_, frame) =>
                {
                    if (_loopbackVadMuted) return;
                    _turnManagerV2?.NoteVoiceActivity(TurnSource.Loopback, true);
                    if (_realtimeAsrActive) return; // realtime path feeds frames directly
                    _capturedOpponent ??= CaptureOpponent();
                    _continuousVts?.NoteListening();
                    if (!_config.Asr.Streaming) return;
                    _loopbackSpeechChannel ??= NewSpeechChannel();
                    _loopbackSpeechChannel.Writer.TryWrite(frame);
                };
                _loopbackVad.SpeechDetected += async (_, seg) =>
                {
                    _turnManagerV2?.NoteVoiceActivity(TurnSource.Loopback, false);
                    try { await ObserveLoopbackSegmentAsync(seg).ConfigureAwait(false); }
                    catch (Exception ex) { PipelineError?.Invoke(this, $"[内录] {ex.Message}"); }
                };

                if (!string.IsNullOrWhiteSpace(_config.Audio.LoopbackProcessName))
                {
                    // Per-process capture — Windows 10 2004+ / Windows 11, no virtual sound card
                    _processLoopback = new AIVTuber.Core.Audio.ProcessLoopbackCapture(_config.Audio.LoopbackProcessName);
                    _processLoopback.AudioFrameAvailable += (_, buf) => FeedLoopback(buf);
                    _processLoopback.LevelUpdated += (_, level) => LoopbackLevelUpdated?.Invoke(this, level);
                    _processLoopback.ErrorOccurred += (_, ex) => PipelineError?.Invoke(this, $"[内录-进程] {ex.Message}");
                    _processLoopback.Start();
                    AIVTuber.Core.Diagnostics.DebugLog.Write(
                        $"[内录] 模式=进程内录 目标进程='{_config.Audio.LoopbackProcessName}'");
                    Console.WriteLine($"[音频] 进程内录已启动 → {_config.Audio.LoopbackProcessName}");
                }
                else
                {
                    // Whole-system loopback. This path uses the selected Windows render
                    // endpoint, which is more predictable for browser tests such as Edge.
                    _loopback = new AIVTuber.Core.Audio.LoopbackCapture(16000,
                        string.IsNullOrWhiteSpace(_config.Audio.LoopbackDeviceName) ? null : _config.Audio.LoopbackDeviceName);
                    _loopback.AudioFrameAvailable += (_, buf) => FeedLoopback(buf);
                    _loopback.AudioFrameAvailable += (_, buf) => LoopbackLevelUpdated?.Invoke(this,
                        buf.Length >= 2 ? ComputeRms(buf) : 0f);
                    _loopback.ErrorOccurred += (_, ex) => PipelineError?.Invoke(this, $"[内录] {ex.Message}");
                    _loopback.Start();
                    AIVTuber.Core.Diagnostics.DebugLog.Write("[内录] 模式=全局内录(WASAPI)");
                    Console.WriteLine("[音频] 全局内录已启动（全部音源）");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[音频] 内录启动失败: {ex.Message}");
                _loopback?.Dispose(); _loopback = null;
                _processLoopback?.Stop(); _processLoopback?.Dispose(); _processLoopback = null;
                _loopbackVad?.Dispose(); _loopbackVad = null;
                if (_loopbackAsrPump is not null) { _loopbackAsrPump.DisposeAsync().AsTask().GetAwaiter().GetResult(); _loopbackAsrPump = null; }
            }
        }
    }

    private async Task InitDanmakuAsync()
    {
        if (!_config.Bilibili.Enable) { _selector = null; _danmaku = null; return; }
        _selector = CreateSelector();
        _danmaku = new BilibiliDanmakuClient(_config.Bilibili);
        _danmaku.OnDanmaku += (_, d) => { _selector?.Enqueue(d); _selector?.TrySelectNext(); };
        _danmaku.OnPkStarted += (_, pk) => AnnouncePkOpponent(pk);
        _danmaku.OnPkEnded += (_, _) => HandlePkEnded();
        // Surface bridge health to the UI error banner, and pipe its stdout/stderr to the debug log.
        _danmaku.OnError += (_, msg) => PipelineError?.Invoke(this, $"[弹幕] {msg}");
        _danmaku.OnProcessExited += (_, msg) => PipelineError?.Invoke(this, $"[弹幕] {msg}");
        _danmaku.OnBridgeOutput += (_, line) => AIVTuber.Core.Diagnostics.DebugLog.Write($"[弹幕桥] {line}");
        try
        {
            await _danmaku.StartAsync(_cts.Token);
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[弹幕] 已启动，监听端口 {_config.Bilibili.PushPort}，房间 {_config.Bilibili.RoomId}");
        }
        catch (Exception ex)
        {
            var msg = $"[弹幕] 启动失败: {ex.Message}";
            Console.WriteLine(msg);
            PipelineError?.Invoke(this, msg);
        }
    }

    /// <summary>The opponent of the PK match currently in progress, or null when no match is
    /// running or the opponent could not be resolved.</summary>
    public PkOpponent? CurrentPkOpponent { get; private set; }

    /// <summary>
    /// Announces the opposing streamer when a PK match starts. Deliberately bypasses
    /// DanmakuSelector: that queue exists to pick one message out of a backlog on a
    /// SelectionIntervalSec throttle, which would delay or drop a one-shot, time-critical
    /// event. Fire-and-forget so the HTTP accept loop is never blocked by the pipeline.
    /// </summary>
    public void AnnouncePkOpponent(PkOpponent pk)
    {
        ArgumentNullException.ThrowIfNull(pk);
        // If a previous match was still open (missed end event), curate it before starting anew.
        if (_pkBuffer.IsActive)
            HandlePkEnded();

        CurrentPkOpponent = pk;
        _conversation?.SetLivePkOpponent(pk);
        PkOpponentChanged?.Invoke(this, pk);
        AIVTuber.Core.Diagnostics.DebugLog.Write(
            $"[PK] 对手 {pk.Username}（{pk.FollowerCount} 粉，房间 {pk.RoomId}）");

        var matchId = _pkBuffer.Start(pk);
        _turnManagerV2?.NoteMatchChanged(matchId);
        SuperviseBackgroundTask(StartPkMatchAsync(pk, matchId));

        var summary = $"PK 开始，对手是 {pk.Username}";
        if (pk.FollowerCount > 0) summary += $"，粉丝 {pk.FollowerCount}";
        if (pk.RoomId != 0) summary += $"，房间 {pk.RoomId}";
        if (!string.IsNullOrEmpty(pk.Uid)) summary += $"，UID {pk.Uid}";
        AcceptTalkLine(new TalkLine(TalkIdentity.System, "系统", summary, pk.Uid, matchId));
    }

    private async Task StartPkMatchAsync(PkOpponent pk, string matchId)
    {
        try
        {
            await _viewerRepo.RecordInteractionAsync(pk.Uid, "bilibili", pk.Username).ConfigureAwait(false);
            await _pkTurnRepo.InsertMatchAsync(new PkMatch
            {
                MatchId = matchId,
                OpponentUid = pk.Uid,
                OpponentName = pk.Username,
                StartedAt = DateTime.UtcNow.ToString("o"),
                Status = "active",
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[PK记忆] 开场落库失败: {ex.Message}");
        }
    }

    /// <summary>Ends the current PK match: agentic-curate buffered original pairs, then clear state.</summary>
    public void HandlePkEnded()
    {
        var matchId = _pkBuffer.MatchId;
        var opponent = _pkBuffer.Opponent ?? CurrentPkOpponent;
        _pkBuffer.EndAssistantReply();
        var turns = _pkBuffer.Snapshot();
        _pkBuffer.Clear();
        _turnManagerV2?.NoteMatchChanged(null);
        CurrentPkOpponent = null;
        _conversation?.SetLivePkOpponent(null);
        PkOpponentChanged?.Invoke(this, null);
        AIVTuber.Core.Diagnostics.DebugLog.Write(
            $"[PK] 结束（缓冲 {turns.Count} 对，对手 {opponent?.Username ?? "?"}）");

        if (matchId is null) return;
        SuperviseBackgroundTask(CurateAndPersistPkAsync(matchId, opponent, turns));
    }

    private async Task CurateAndPersistPkAsync(
        string matchId,
        PkOpponent? opponent,
        IReadOnlyList<BufferedPkTurn> turns)
    {
        try
        {
            await _pkCurator.PersistMatchAsync(
                matchId,
                opponent?.Uid,
                opponent?.Username,
                turns,
                _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[PK记忆] 结束落盘失败: {ex.Message}");
            try
            {
                await _pkTurnRepo.EndMatchAsync(matchId, DateTime.UtcNow.ToString("o")).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>True when live interaction mode is PK (wake-keyword gate).</summary>
    public bool IsPkMode => _config.Interaction.IsPkMode;

    /// <summary>
    /// Hot-switch Normal ↔ PK without rebuilding the pipeline. Resets the wake hold window.
    /// Does not write config.json; save from the Config tab to persist.
    /// </summary>
    public void SetPkMode(bool pk)
    {
        var modeChanged = _config.Interaction.IsPkMode != pk;
        var enableCapture = pk && _config.Bilibili.Enable && !_config.Bilibili.PkNotice;
        if (!modeChanged && !enableCapture) return;
        if (modeChanged)
        {
            _config.Interaction.SetPkMode(pk);
            _activeConfig.Interaction.SetPkMode(pk);
            _wakeGate.Reset();
            AIVTuber.Core.Diagnostics.DebugLog.Write(pk
                ? "[模式] PK（默认静默；麦/内录/弹幕/开场播报均需关键词或保持窗）"
                : "[模式] 正常（有输入就回）");
            InteractionModeChanged?.Invoke(this, EventArgs.Empty);
        }

        if (enableCapture)
        {
            _config.Bilibili.PkNotice = true;
            _activeConfig.Bilibili.PkNotice = true;
            AIVTuber.Core.Diagnostics.DebugLog.Write("[PK] 已打开对手抓取");
            if (_danmaku is not null)
            {
                AIVTuber.Core.Diagnostics.DebugLog.Write("[PK] 重启弹幕桥以监听对手");
                SuperviseBackgroundTask(RestartDanmakuAsync());
            }
        }
    }

    /// <summary>Manually marks a new PK match, for when the opponent could not be
    /// auto-detected. Clears the previous opponent and tells the AI a fresh match began.</summary>
    public void StartNewPk()
    {
        if (_pkBuffer.IsActive)
            HandlePkEnded();
        _turnManagerV2?.NoteMatchChanged(null);
        CurrentPkOpponent = null;
        _conversation?.SetLivePkOpponent(null);
        PkOpponentChanged?.Invoke(this, null);
        AIVTuber.Core.Diagnostics.DebugLog.Write("[PK] 手动标记新一场 PK");
        AcceptTalkLine(new TalkLine(TalkIdentity.System, "系统", "新的一场 PK 开始了，还不知道对手是谁", null));
    }

    /// <summary>Creates a DanmakuSelector with its selection handler wired. Speaking state is
    /// bridged from the orchestrator (see InitPipeline), and OnDanmaku reads the _selector field,
    /// so a rebuilt selector is picked up without re-wiring the danmaku client.</summary>
    private DanmakuSelector CreateSelector()
    {
        var selector = new DanmakuSelector(_config.Bilibili.SelectionIntervalSec, _viewerRepo);
        selector.OnDanmakuSelected += async (_, d) =>
        {
            _stateTracker.TextInputStarted(Environment.TickCount64);
            await _viewerRepo.RecordInteractionAsync(d.Uid, d.Platform, d.Username);
            var label = IdentityPrompt.ResolveDanmakuLabel(_config.Identity.DanmakuLabel);
            AcceptTalkLine(new TalkLine(
                TalkIdentity.Danmaku,
                string.IsNullOrWhiteSpace(d.Username) ? label : d.Username,
                d.Content,
                string.IsNullOrEmpty(d.Uid) ? null : d.Uid));
        };
        return selector;
    }

    /// <summary>
    /// Applies a new config at runtime, rebuilding only the modules whose settings changed.
    /// Light changes are instant; heavy changes reconnect a module (~1-2s). Changes take
    /// effect from the next interaction.
    /// </summary>
    public async Task ApplyConfigAsync(AppConfig newConfig)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        var candidate = ConfigManager.Clone(newConfig);

        await _configApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var candidateRevision = Interlocked.Increment(ref _nextRevision);
            _candidateConfig = ConfigManager.Clone(candidate);
            Interlocked.Exchange(ref _candidateRevision, candidateRevision);

            var previousConfig = _config;
            var previousActive = _activeConfig;
            var previousRevision = ActiveConfigRevision;
            var change = ConfigDiff.Compute(previousActive, candidate);

            AIVTuber.Core.Diagnostics.DebugLog.Write($"[配置] 开始应用 revision={candidateRevision} change={change}");
            _config = candidate;
            try
            {
                await ApplyChangesAsync(change).ConfigureAwait(false);
            }
            catch (Exception applyError)
            {
                AIVTuber.Core.Diagnostics.DebugLog.Write($"[配置] 应用失败，回滚: {applyError.Message}");
                _config = previousConfig;
                try
                {
                    await ApplyChangesAsync(change).ConfigureAwait(false);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("Configuration apply and rollback both failed.", applyError, rollbackError);
                }

                throw;
            }

            _lastKnownGoodConfig = ConfigManager.Clone(previousActive);
            Interlocked.Exchange(ref _lastKnownGoodRevision, previousRevision);
            _activeConfig = ConfigManager.Clone(candidate);
            _config = candidate;
            Interlocked.Exchange(ref _activeRevision, candidateRevision);
            _wakeGate.Reset();
            InteractionModeChanged?.Invoke(this, EventArgs.Empty);
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[配置] 应用完成 revision={candidateRevision}");
        }
        finally
        {
            _configApplyGate.Release();
        }
    }

    /// <summary>Applies the last successfully replaced snapshot as a new revision.</summary>
    public Task RollbackConfigAsync()
        => ApplyConfigAsync(ConfigManager.Clone(_lastKnownGoodConfig));

    private async Task ApplyChangesAsync(RuntimeChange change)
    {
        if (change == RuntimeChange.None) return;

        if (_applyChangesOverride is not null)
        {
            await _applyChangesOverride(change);
            return;
        }

        // Heavy: memory store / audio / connections.
        if (change.HasFlag(RuntimeChange.ReopenMemory)) await RebuildMemoryAsync();
        if (change.HasFlag(RuntimeChange.RestartAudio)) RestartAudio();
        if (change.HasFlag(RuntimeChange.ReconnectVts)) await ReconnectVtsAsync();
        if (change.HasFlag(RuntimeChange.ReconnectObs)) await ReconnectObsAsync();
        if (change.HasFlag(RuntimeChange.RestartDanmaku)) await RestartDanmakuAsync();
        else if (change.HasFlag(RuntimeChange.RebuildDanmakuSelector)) RebuildSelector(); // light: in-memory only

        if (change.HasFlag(RuntimeChange.UpdateVtsParams) && _continuousVts is not null)
        {
            _orchestrator?.Dispose();
            // Subscribe/discover on first enable. Applying profiles must not create a second writer.
            if (_config.Vts.ContinuousControl.Enabled && _continuousVts.Model is null)
                await _continuousVts.ConnectAsync(_cts.Token);
            await _continuousVts.ApplyAsync(_config.Vts.ContinuousControl, _cts.Token);
        }

        // Light: memory extraction cadence updated in place.
        if (change.HasFlag(RuntimeChange.UpdateMemoryParams)) BuildMemoryExtractor();

        // Recreating any pipeline client, or changing the VTS/OBS params the orchestrator
        // and subtitle handlers capture, requires rebuilding + re-wiring the orchestrator.
        if (change.HasFlag(RuntimeChange.RebuildLlm) || change.HasFlag(RuntimeChange.RebuildAsr) ||
            change.HasFlag(RuntimeChange.RebuildTts) || change.HasFlag(RuntimeChange.UpdateVtsParams) ||
            change.HasFlag(RuntimeChange.UpdateObsParams) || change.HasFlag(RuntimeChange.ReconnectVts) ||
            change.HasFlag(RuntimeChange.ReconnectObs))
        {
            RewirePipeline(change);
        }

        if (change.HasFlag(RuntimeChange.RebuildAsr))
        {
            if (_asr is LocalAsrClient)
            {
                if (CloudOnlySidecarSkipped || !CloudOnlyGate.ShouldStartAsrSidecar(_config.Realtime, _config.Asr.Provider))
                    throw new InvalidOperationException(
                        "cloud_only 模式禁止启动本地 ASR sidecar：请将 asr.provider 切换为云端 provider");
                await StartLocalAsrServerAsync();
                if (!LocalAsrReachable)
                    throw new InvalidOperationException("Local ASR sidecar failed to become ready.");
            }
        }
    }

    private async Task RebuildMemoryAsync()
    {
        _embedding?.Dispose();
        _memoryDb?.Dispose();
        await InitMemoryAsync();
    }

    private void RestartAudio()
    {
        if (_micAsrPump is not null) { _micAsrPump.DisposeAsync().AsTask().GetAwaiter().GetResult(); _micAsrPump = null; }
        if (_loopbackAsrPump is not null) { _loopbackAsrPump.DisposeAsync().AsTask().GetAwaiter().GetResult(); _loopbackAsrPump = null; }
        _realtimeAsrActive = false;
        _mic?.Stop(); _mic?.Dispose();
        _vad?.Dispose();
        _loopback?.Stop(); _loopback?.Dispose(); _loopback = null;
        _processLoopback?.Stop(); _processLoopback?.Dispose(); _processLoopback = null;
        _loopbackVad?.Dispose(); _loopbackVad = null;
        _virtualMic?.Stop(); _virtualMic?.Dispose(); _virtualMic = null;
        StartAudio();
    }

    private async Task ReconnectVtsAsync()
    {
        if (_cortico is not null)
        {
            await _cortico.DisposeAsync();
            _cortico = null;
        }
        if (_continuousVts is not null) { await _continuousVts.DisposeAsync(); _continuousVts = null; }
        if (!_config.Avatar.UsesVts)
        {
            if (_vts is not null) { await _vts.DisconnectAsync(); _vts.Dispose(); _vts = null; }
            return;
        }
        if (_vts is not null) { await _vts.DisconnectAsync(); _vts.Dispose(); }
        await InitVtsAsync();
    }

    private async Task ReconnectObsAsync()
    {
        if (_obs is not null) { await _obs.DisconnectAsync(); _obs.Dispose(); }
        InitObs();
    }

    private async Task RestartDanmakuAsync()
    {
        if (_danmaku is not null) { await _danmaku.StopAsync(); _danmaku.Dispose(); }
        _danmaku = null;
        _selector = null;
        await InitDanmakuAsync();
    }

    /// <summary>Light path: rebuild only the in-memory selector (e.g. selection interval changed),
    /// leaving the danmaku client/connection alone. The danmaku client's OnDanmaku and the
    /// orchestrator's speaking bridge both read the _selector field, so no re-wiring is needed.</summary>
    private void RebuildSelector()
    {
        if (_selector is null) return; // danmaku not active
        _selector = CreateSelector();
    }

    /// <summary>Rebuilds the orchestrator + pipeline clients. InitPipeline disposes the old ones,
    /// reuses the AudioPlayer, and re-adds the stable speaking bridge — so nothing else is needed here.</summary>
    private void RewirePipeline(RuntimeChange change) => InitPipeline(change);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _turnGate?.Dispose();
        _turnManagerV2?.Dispose();
        _turnManagerV2 = null;
        _orchestrator?.Interrupt();
        await DrainBackgroundTasksAsync();
        UnwirePixelAvatar();
        _avatarConfigWatcher?.Dispose();
        _avatarConfigWatcher = null;
        if (_pixelAvatar is not null)
        {
            await _pixelAvatar.DisposeAsync();
            _pixelAvatar = null;
        }
        _mic?.Stop(); _mic?.Dispose();
        if (_micAsrPump is not null) { await _micAsrPump.DisposeAsync().ConfigureAwait(false); _micAsrPump = null; }
        if (_loopbackAsrPump is not null) { await _loopbackAsrPump.DisposeAsync().ConfigureAwait(false); _loopbackAsrPump = null; }
        _vad?.Dispose();
        _loopback?.Stop(); _loopback?.Dispose();
        _processLoopback?.Stop(); _processLoopback?.Dispose();
        _loopbackVad?.Dispose();
        _virtualMic?.Stop(); _virtualMic?.Dispose(); _virtualMic = null;
        if (_danmaku is not null) { await _danmaku.StopAsync(); _danmaku.Dispose(); }
        if (_continuousVts is not null) { await _continuousVts.DisposeAsync(); _continuousVts = null; }
        if (_vts is not null) { await _vts.DisconnectAsync(); _vts.Dispose(); }
        if (_obs is not null) { await _obs.DisconnectAsync(); _obs.Dispose(); }
        if (_cortico is not null) { await _cortico.DisposeAsync(); _cortico = null; }
        await _asrSidecar.DisposeAsync();
        _turnGate?.Dispose();
        _turnGate = null;
        _orchestrator?.Dispose();
        _vision?.Dispose();
        _vision = null;
        (_tts as IDisposable)?.Dispose();
        _llm?.Dispose();
        (_asr as IDisposable)?.Dispose();
        _player?.Dispose();
        _memoryLlm?.Dispose();
        _pkCuratorLlm?.Dispose();
        _embedding?.Dispose();
        _memoryDb?.Dispose();
        _configApplyGate.Dispose();
        _cts.Dispose();
    }
}
