using System.Diagnostics;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Memory;
using AIVTuber.Core.Obs;
using AIVTuber.Core.Pipeline;
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
    // Mutable (not readonly) because ApplyConfigAsync replaces it at runtime; contrast with readonly _baseDir.
    private AppConfig _config;

    private MemoryDb _memoryDb = null!;
    private EmbeddingEngine? _embedding;
    private ViewerRepository _viewerRepo = null!;
    private FactRepository _factRepo = null!;
    private ConversationManager _conversation = null!;
    private MemoryExtractor _memoryExtractor = null!;
    private LlmClient _memoryLlm = null!; // owned by BotRuntime so it can be disposed (MemoryExtractor is not IDisposable)
    private VtsClient? _vts;
    private ObsClient? _obs;
    private IAsrClient _asr = null!;   // provider-specific (whisper-HTTP or DashScope-WebSocket)
    private LlmClient _llm = null!;
    private ITtsClient _tts = null!;   // provider-specific (fish/minimax HTTP or DashScope WebSocket)
    private AudioPlayer _player = null!;
    private BotOrchestrator _orchestrator = null!;
    private MicrophoneCapture? _mic;
    private VadDetector? _vad;
    private AIVTuber.Core.Audio.LoopbackCapture? _loopback;
    private AIVTuber.Core.Audio.ProcessLoopbackCapture? _processLoopback;
    private VadDetector? _loopbackVad;
    private AIVTuber.Core.Audio.VirtualMicMixer? _virtualMic;
    private BilibiliDanmakuClient? _danmaku;
    private DanmakuSelector? _selector;

    private readonly PipelineStateTracker _stateTracker = new();

    public PipelineStateTracker StateTracker => _stateTracker;
    public event EventHandler? AiStartSpeaking;
    public event EventHandler? AiStopSpeaking;
    /// <summary>Fired when any pipeline stage (ASR/LLM/TTS) encounters a non-cancellation error.</summary>
    public event EventHandler<string>? PipelineError;
    /// <summary>Fired per mic frame with RMS in [0,1] for a level indicator.</summary>
    public event EventHandler<float>? MicLevelUpdated;
    /// <summary>Fired per loopback frame with RMS in [0,1] for a level indicator.</summary>
    public event EventHandler<float>? LoopbackLevelUpdated;

    public bool VtsConnected => _vts?.IsConnected == true;
    public bool ObsConnected => _obs is not null;
    public bool DanmakuActive => _danmaku is not null;
    public bool LocalAsrActive => _asr is LocalAsrClient;

    private volatile bool _localAsrReachable;
    public bool LocalAsrReachable => _localAsrReachable;
    public event EventHandler<bool>? LocalAsrReachableChanged;

    private Process? _asrServerProcess;

    /// <summary>Returns hotkeys in the currently loaded VTS model, or empty if not connected.</summary>
    public Task<List<VtsHotkeyInfo>> GetVtsHotkeysAsync(CancellationToken ct = default)
        => _vts?.GetHotkeyListAsync(ct) ?? Task.FromResult(new List<VtsHotkeyInfo>());

    /// <summary>Fired with the user's recognized speech / danmaku text.</summary>
    public event EventHandler<string>? UserTranscript;
    /// <summary>Fired with each completed AI sentence.</summary>
    public event EventHandler<string>? SentenceReady;
    /// <summary>Fired with a detected emotion tag from the LLM output.</summary>
    public event EventHandler<string>? EmotionDetected;
    /// <summary>Fired with the user's detected emotion from Qwen-ASR (null/neutral suppressed).</summary>
    public event EventHandler<string>? UserEmotionDetected;
    /// <summary>Fired with transcribed text from system audio (loopback/PC source).</summary>
    public event EventHandler<string>? LoopbackTranscript;

    public AppConfig CurrentConfig => _config;
    public ViewerRepository ViewerRepository => _viewerRepo;
    public FactRepository FactRepository => _factRepo;
    public DanmakuSelector? Selector => _selector;

    private volatile bool _micMuted;
    public bool MicMuted => _micMuted;
    public void SetMicMuted(bool muted) => _micMuted = muted;

    // True while AI is speaking — loopback VAD feed is paused to prevent self-hearing.
    private volatile bool _loopbackVadMuted;

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
    {
        _config = config;
        _baseDir = baseDir;
    }

    /// <summary>Initializes all modules. Equivalent to the old Program startup sequence.</summary>
    public async Task StartAsync()
    {
        await InitMemoryAsync();
        await InitVtsAsync();
        InitObs();
        InitPipeline();
        await InitDanmakuAsync();
        StartAudio();
        _stateTracker.Started();
        _ = StartLocalAsrServerAsync();
    }

    private async Task InitMemoryAsync()
    {
        var dbPath = Path.Combine(_baseDir, _config.Memory.DatabasePath);
        _memoryDb = new MemoryDb(dbPath);
        await _memoryDb.InitializeAsync();
        _embedding = null;
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
        _conversation = new ConversationManager(_config.Llm);
        _conversation.SetMemory(_viewerRepo, _factRepo);
        BuildMemoryExtractor();
    }

    /// <summary>(Re)creates the memory extractor and its owned LlmClient, disposing the previous one.</summary>
    private void BuildMemoryExtractor()
    {
        _memoryLlm?.Dispose();
        _memoryLlm = new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Vts.BuildSystemPrompt(_config.Llm.SystemPrompt));
        _memoryExtractor = new MemoryExtractor(_memoryLlm, _factRepo, _config.Memory, _conversation);
    }

    private async Task InitVtsAsync()
    {
        try
        {
            _vts = new VtsClient(_config.Vts);
            _vts.OnError += (_, msg) => PipelineError?.Invoke(this, $"[VTS] {msg}");
            _vts.OnDisconnected += (_, reason) => PipelineError?.Invoke(this, $"[VTS] 已断开: {reason}");
            await _vts.ConnectAsync(_cts.Token);
            Console.WriteLine("[VTS] 已连接 VTube Studio");
        }
        catch (Exception ex)
        {
            var msg = $"[VTS] 连接失败: {ex.Message}";
            Console.WriteLine(msg);
            PipelineError?.Invoke(this, msg);
            _vts = null;
        }
    }

    private void InitObs()
    {
        if (!_config.Obs.Enable) { _obs = null; return; }
        _obs = new ObsClient(_config.Obs);
        _ = _obs.ConnectAsync(_cts.Token);
    }

    private static IAsrClient CreateAsrClient(AsrConfig asr)
    {
        if (asr.Provider.ToLowerInvariant() is "local")
            return new LocalAsrClient(asr.LocalAsrUrl);

        if (asr.Provider.ToLowerInvariant() is "aliyun" or "dashscope")
        {
            // Qwen-ASR uses a different WebSocket protocol (OpenAI Realtime-style) at /realtime
            return !string.IsNullOrWhiteSpace(asr.Model) && asr.Model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
                ? new QwenAsrClient(asr)
                : new DashScopeAsrClient(asr);
        }
        return new AsrClient(
            asr.Provider.Contains("deepseek") ? $"https://{asr.Provider}" : $"https://api.{asr.Provider}.com",
            asr.ApiKey);
    }

    private static ITtsClient CreateTtsClient(TtsConfig tts)
        => tts.Provider.ToLowerInvariant() is "aliyun" or "cosyvoice" or "dashscope"
            ? new DashScopeTtsClient(tts)
            : new TtsClient(tts);

    private void InitPipeline()
    {
        _orchestrator?.Dispose();
        (_asr as IDisposable)?.Dispose();
        _llm?.Dispose();
        (_tts as IDisposable)?.Dispose();

        _asr = CreateAsrClient(_config.Asr);
        _llm = new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Vts.BuildSystemPrompt(_config.Llm.SystemPrompt));
        _tts = CreateTtsClient(_config.Tts);
        if (_player is null)
        {
            _player = new AudioPlayer();
            // Single stable tap: reads _virtualMic field, so mixer restarts pick up automatically.
            _player.PcmChunkPlayed += (_, chunk) => _virtualMic?.WriteTts(chunk);
        }
        _orchestrator = new BotOrchestrator(_asr, _llm, _tts, _player, _config.Tts, _vts, _config.Vts);

        _orchestrator.OnError += (_, msg) => PipelineError?.Invoke(this, msg);

        _orchestrator.OnUserTranscript += async (_, text) =>
        {
            _stateTracker.TranscriptReady(Environment.TickCount64);
            _conversation.AddUserMessage(text);
            UserTranscript?.Invoke(this, text);
            await _memoryExtractor.OnTurnAsync();
        };
        _orchestrator.OnSentenceReady += (_, s) =>
        {
            _conversation.AddAssistantMessage(s);
            SentenceReady?.Invoke(this, s);
        };
        _orchestrator.OnEmotionDetected += (_, e) => EmotionDetected?.Invoke(this, e);
        _orchestrator.OnUserEmotionDetected += (_, e) => UserEmotionDetected?.Invoke(this, e);
        _orchestrator.OnLoopbackTranscript += (_, t) => LoopbackTranscript?.Invoke(this, t);

        if (_obs is not null)
        {
            _orchestrator.OnSentenceReady += async (_, s)
                => await _obs.SetSubtitleTypewriterAsync(s, _config.Obs.AssistantTextComponent, _cts.Token);
            _orchestrator.OnUserTranscript += async (_, t)
                => await _obs.SetSubtitleAsync($"[用户] {t}", _config.Obs.UserTextComponent, _cts.Token);
        }

        _orchestrator.OnFirstSentenceToTts += (_, _) =>
            _stateTracker.LlmFirstSentenceReady(Environment.TickCount64);

        // Mute loopback VAD while AI is playing so it doesn't hear its own TTS output.
        _orchestrator.OnAiStartSpeaking += (_, _) => _loopbackVadMuted = true;
        _orchestrator.OnAiStopSpeaking  += (_, _) => _loopbackVadMuted = false;

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
    }

    /// <summary>
    /// Starts asr_server.py as a hidden background process, then polls /health until ready.
    /// Fires LocalAsrReachableChanged when the server comes online or fails to start.
    /// </summary>
    public async Task StartLocalAsrServerAsync()
    {
        if (_asr is not LocalAsrClient localAsr) return;

        // Kill any previously owned process first
        StopLocalAsrServer();

        var scriptPath = Path.Combine(_baseDir, "asr_server.py");
        var si = new ProcessStartInfo
        {
            FileName = _config.Asr.PythonPath,
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _asrServerProcess = new Process { StartInfo = si, EnableRaisingEvents = true };
        _asrServerProcess.Exited += (_, _) =>
        {
            _localAsrReachable = false;
            LocalAsrReachableChanged?.Invoke(this, false);
            PipelineError?.Invoke(this, "[Local ASR] 服务进程已退出");
        };

        try { _asrServerProcess.Start(); }
        catch (Exception ex)
        {
            PipelineError?.Invoke(this, $"[Local ASR] 启动失败: {ex.Message}");
            return;
        }

        // Poll /health until the model finishes loading (can take a few seconds)
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !_cts.Token.IsCancellationRequested)
        {
            if (await localAsr.PingAsync(_cts.Token))
            {
                _localAsrReachable = true;
                LocalAsrReachableChanged?.Invoke(this, true);
                return;
            }
            await Task.Delay(500, _cts.Token).ConfigureAwait(false);
        }

        _localAsrReachable = false;
        LocalAsrReachableChanged?.Invoke(this, false);
        PipelineError?.Invoke(this, "[Local ASR] 服务启动超时（60s），请检查 Python 环境");
    }

    public void StopLocalAsrServer()
    {
        try { _asrServerProcess?.Kill(entireProcessTree: true); }
        catch { /* already dead */ }
        _asrServerProcess?.Dispose();
        _asrServerProcess = null;
        _localAsrReachable = false;
    }

    private void StartAudio()
    {
        _mic = new MicrophoneCapture(_config.Audio.InputDeviceIndex);
        _vad = new VadDetector(_config.Audio.VadAggressiveness, _config.Audio.PreSpeechPaddingMs, _config.Audio.PostSpeechSilenceMs);
        _mic.AudioFrameAvailable += (_, buf) => _vad.Feed(buf);
        _mic.LevelUpdated += (_, level) => MicLevelUpdated?.Invoke(this, level);
        _mic.ErrorOccurred += (_, ex) => PipelineError?.Invoke(this, $"[麦克风] {ex.Message}");
        _vad.SpeechDetected += async (_, seg) =>
        {
            if (_micMuted) return;
            _stateTracker.InputStarted(Environment.TickCount64);
            var history = _conversation.BuildMessages();
            // Mic has highest priority: always interrupts (including any loopback processing)
            await _orchestrator.ProcessSpeechAsync(seg, history, _config.Input.MicTemplate);
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
                _virtualMic = new AIVTuber.Core.Audio.VirtualMicMixer(_config.Audio.VirtualMicDeviceName);
                _virtualMic.Start();
                // Feed real mic frames into the mixer — not gated by _micMuted (mute only stops AI listening)
                _mic.AudioFrameAvailable += (_, buf) => _virtualMic?.WriteMic(buf);
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
                _loopbackVad.SpeechDetected += async (_, seg) =>
                {
                    var tagged = new AIVTuber.Core.Audio.SpeechSegment
                    {
                        AudioData = seg.AudioData, StartTime = seg.StartTime, EndTime = seg.EndTime,
                        Source = AIVTuber.Core.Audio.AudioSource.Loopback,
                    };
                    await _orchestrator.ProcessLoopbackSpeechAsync(tagged, _conversation.BuildMessages(), _config.Input.LoopbackTemplate);
                };

                if (!string.IsNullOrWhiteSpace(_config.Audio.LoopbackProcessName))
                {
                    // Per-process capture — Windows 10 2004+ / Windows 11, no virtual sound card
                    _processLoopback = new AIVTuber.Core.Audio.ProcessLoopbackCapture(_config.Audio.LoopbackProcessName);
                    _processLoopback.AudioFrameAvailable += (_, buf) => { if (!_loopbackVadMuted) _loopbackVad!.Feed(buf); };
                    _processLoopback.LevelUpdated += (_, level) => LoopbackLevelUpdated?.Invoke(this, level);
                    _processLoopback.ErrorOccurred += (_, ex) => PipelineError?.Invoke(this, $"[内录-进程] {ex.Message}");
                    _processLoopback.Start();
                    Console.WriteLine($"[音频] 进程内录已启动 → {_config.Audio.LoopbackProcessName}");
                }
                else
                {
                    // Whole-system loopback. The VAD feed is muted while the AI is speaking
                    // (via _loopbackVadMuted) so the AI never reacts to its own TTS output.
                    _loopback = new AIVTuber.Core.Audio.LoopbackCapture(16000,
                        string.IsNullOrWhiteSpace(_config.Audio.LoopbackDeviceName) ? null : _config.Audio.LoopbackDeviceName);
                    _loopback.AudioFrameAvailable += (_, buf) => { if (!_loopbackVadMuted) _loopbackVad!.Feed(buf); };
                    _loopback.AudioFrameAvailable += (_, buf) => LoopbackLevelUpdated?.Invoke(this,
                        buf.Length >= 2 ? ComputeRms(buf) : 0f);
                    _loopback.Start();
                    Console.WriteLine("[音频] 全局内录已启动（PK 模式）");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[音频] 内录启动失败: {ex.Message}");
                _loopback?.Dispose(); _loopback = null;
                _processLoopback?.Dispose(); _processLoopback = null;
                _loopbackVad?.Dispose(); _loopbackVad = null;
            }
        }
    }

    private async Task InitDanmakuAsync()
    {
        if (!_config.Bilibili.Enable) { _selector = null; _danmaku = null; return; }
        _selector = CreateSelector();
        _danmaku = new BilibiliDanmakuClient(_config.Bilibili);
        _danmaku.OnDanmaku += (_, d) => { _selector?.Enqueue(d); _selector?.TrySelectNext(); };
        try { await _danmaku.StartAsync(_cts.Token); }
        catch (Exception ex) { Console.WriteLine($"[弹幕] 启动失败: {ex.Message}"); }
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
            var history = _conversation.BuildMessages(d.Uid);
            var text = _config.Input.DanmakuTemplate
                .Replace("{username}", d.Username)
                .Replace("{content}", d.Content);
            await _orchestrator.ProcessTextAsync(text, history);
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
        var change = ConfigDiff.Compute(_config, newConfig);
        _config = newConfig;
        if (change == RuntimeChange.None) return;

        // Heavy: memory store / audio / connections.
        if (change.HasFlag(RuntimeChange.ReopenMemory)) await RebuildMemoryAsync();
        if (change.HasFlag(RuntimeChange.RestartAudio)) RestartAudio();
        if (change.HasFlag(RuntimeChange.ReconnectVts)) await ReconnectVtsAsync();
        if (change.HasFlag(RuntimeChange.ReconnectObs)) await ReconnectObsAsync();
        if (change.HasFlag(RuntimeChange.RestartDanmaku)) await RestartDanmakuAsync();
        else if (change.HasFlag(RuntimeChange.RebuildDanmakuSelector)) RebuildSelector(); // light: in-memory only

        // Light: memory extraction cadence updated in place.
        if (change.HasFlag(RuntimeChange.UpdateMemoryParams)) BuildMemoryExtractor();

        // Recreating any pipeline client, or changing the VTS/OBS params the orchestrator
        // and subtitle handlers capture, requires rebuilding + re-wiring the orchestrator.
        if (change.HasFlag(RuntimeChange.RebuildLlm) || change.HasFlag(RuntimeChange.RebuildAsr) ||
            change.HasFlag(RuntimeChange.RebuildTts) || change.HasFlag(RuntimeChange.UpdateVtsParams) ||
            change.HasFlag(RuntimeChange.UpdateObsParams))
        {
            RewirePipeline();
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
        if (_vts is not null) { await _vts.DisconnectAsync(); _vts.Dispose(); }
        await InitVtsAsync();
        RewirePipeline();
    }

    private async Task ReconnectObsAsync()
    {
        if (_obs is not null) { await _obs.DisconnectAsync(); _obs.Dispose(); }
        InitObs();
        RewirePipeline();
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
    private void RewirePipeline() => InitPipeline();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _mic?.Stop(); _mic?.Dispose();
        _vad?.Dispose();
        _loopback?.Stop(); _loopback?.Dispose();
        _processLoopback?.Stop(); _processLoopback?.Dispose();
        _loopbackVad?.Dispose();
        _virtualMic?.Stop(); _virtualMic?.Dispose(); _virtualMic = null;
        if (_danmaku is not null) { await _danmaku.StopAsync(); _danmaku.Dispose(); }
        if (_vts is not null) { await _vts.DisconnectAsync(); _vts.Dispose(); }
        if (_obs is not null) { await _obs.DisconnectAsync(); _obs.Dispose(); }
        StopLocalAsrServer();
        _orchestrator?.Dispose();
        (_tts as IDisposable)?.Dispose();
        _llm?.Dispose();
        (_asr as IDisposable)?.Dispose();
        _player?.Dispose();
        _memoryLlm?.Dispose();
        _embedding?.Dispose();
        _memoryDb?.Dispose();
        _cts.Dispose();
    }
}
