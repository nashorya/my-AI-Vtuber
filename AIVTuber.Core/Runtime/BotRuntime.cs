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
    private VtsClient? _vts;
    private ObsClient? _obs;
    private AsrClient _asr = null!;
    private LlmClient _llm = null!;
    private TtsClient _tts = null!;
    private AudioPlayer _player = null!;
    private BotOrchestrator _orchestrator = null!;
    private MicrophoneCapture? _mic;
    private VadDetector? _vad;
    private BilibiliDanmakuClient? _danmaku;
    private DanmakuSelector? _selector;

    /// <summary>Fired with the user's recognized speech / danmaku text.</summary>
    public event EventHandler<string>? UserTranscript;
    /// <summary>Fired with each completed AI sentence.</summary>
    public event EventHandler<string>? SentenceReady;
    /// <summary>Fired with a detected emotion tag.</summary>
    public event EventHandler<string>? EmotionDetected;

    public AppConfig CurrentConfig => _config;
    public ViewerRepository ViewerRepository => _viewerRepo;
    public FactRepository FactRepository => _factRepo;
    public DanmakuSelector? Selector => _selector;

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
        _memoryExtractor = new MemoryExtractor(
            new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Llm.SystemPrompt),
            _factRepo, _config.Memory, _conversation);
    }

    private async Task InitVtsAsync()
    {
        try
        {
            _vts = new VtsClient(_config.Vts);
            await _vts.ConnectAsync(_cts.Token);
            Console.WriteLine("[VTS] 已连接 VTube Studio");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VTS] 连接失败: {ex.Message}");
            _vts = null;
        }
    }

    private void InitObs()
    {
        if (!_config.Obs.Enable) { _obs = null; return; }
        _obs = new ObsClient(_config.Obs);
        _ = _obs.ConnectAsync(_cts.Token);
    }

    private void InitPipeline()
    {
        _orchestrator?.Dispose();
        _asr?.Dispose();
        _llm?.Dispose();
        _tts?.Dispose();

        _asr = new AsrClient(
            _config.Asr.Provider.Contains("deepseek") ? $"https://{_config.Asr.Provider}" : $"https://api.{_config.Asr.Provider}.com",
            _config.Asr.ApiKey);
        _llm = new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Llm.SystemPrompt);
        _tts = new TtsClient(_config.Tts.Provider, _config.Tts.ApiKey);
        _player ??= new AudioPlayer();
        _orchestrator = new BotOrchestrator(_asr, _llm, _tts, _player, _config.Tts, _vts, _config.Vts);

        _orchestrator.OnUserTranscript += async (_, text) =>
        {
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

        if (_obs is not null)
        {
            _orchestrator.OnSentenceReady += async (_, s)
                => await _obs.SetSubtitleTypewriterAsync(s, _config.Obs.AssistantTextComponent, _cts.Token);
            _orchestrator.OnUserTranscript += async (_, t)
                => await _obs.SetSubtitleAsync($"[用户] {t}", _config.Obs.UserTextComponent, _cts.Token);
        }
    }

    private void StartAudio()
    {
        _mic = new MicrophoneCapture(_config.Audio.InputDeviceIndex);
        _vad = new VadDetector(_config.Audio.VadAggressiveness, _config.Audio.PreSpeechPaddingMs, _config.Audio.PostSpeechSilenceMs);
        _mic.AudioFrameAvailable += (_, buf) => _vad.Feed(buf);
        _vad.SpeechDetected += async (_, seg) =>
        {
            var history = _conversation.BuildMessages();
            await _orchestrator.ProcessSpeechAsync(seg, history);
        };
        _mic.Start();
    }

    private async Task InitDanmakuAsync()
    {
        if (!_config.Bilibili.Enable) { _selector = null; _danmaku = null; return; }
        _selector = new DanmakuSelector(_config.Bilibili.SelectionIntervalSec, _viewerRepo);
        _danmaku = new BilibiliDanmakuClient(_config.Bilibili);
        _danmaku.OnDanmaku += (_, d) => { _selector.Enqueue(d); _selector.TrySelectNext(); };
        _selector.OnDanmakuSelected += async (_, d) =>
        {
            await _viewerRepo.RecordInteractionAsync(d.Uid, d.Platform, d.Username);
            var history = _conversation.BuildMessages(d.Uid);
            await _orchestrator.ProcessTextAsync(d.Content, history);
        };
        _orchestrator.OnAiStartSpeaking += (_, _) => _selector.SetSpeaking(true);
        _orchestrator.OnAiStopSpeaking += (_, _) => { _selector.SetSpeaking(false); _selector.TrySelectNext(); };
        try { await _danmaku.StartAsync(_cts.Token); }
        catch (Exception ex) { Console.WriteLine($"[弹幕] 启动失败: {ex.Message}"); }
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
        if (change.HasFlag(RuntimeChange.RestartDanmaku) || change.HasFlag(RuntimeChange.RebuildDanmakuSelector))
            await RestartDanmakuAsync();

        // Light: memory extraction cadence updated in place.
        if (change.HasFlag(RuntimeChange.UpdateMemoryParams))
            _memoryExtractor = new MemoryExtractor(
                new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Llm.SystemPrompt),
                _factRepo, _config.Memory, _conversation);

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
        StartAudio();
    }

    private async Task ReconnectVtsAsync()
    {
        if (_vts is not null) await _vts.DisconnectAsync();
        await InitVtsAsync();
        RewirePipeline();
    }

    private async Task ReconnectObsAsync()
    {
        if (_obs is not null) await _obs.DisconnectAsync();
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

    /// <summary>
    /// Rebuilds the orchestrator + pipeline clients (InitPipeline disposes the old ones and
    /// reuses the AudioPlayer) and re-attaches the danmaku speaking handlers if danmaku is active.
    /// </summary>
    private void RewirePipeline()
    {
        InitPipeline();
        if (_selector is not null)
        {
            _orchestrator.OnAiStartSpeaking += (_, _) => _selector.SetSpeaking(true);
            _orchestrator.OnAiStopSpeaking += (_, _) => { _selector.SetSpeaking(false); _selector.TrySelectNext(); };
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _mic?.Stop(); _mic?.Dispose();
        _vad?.Dispose();
        if (_danmaku is not null) { await _danmaku.StopAsync(); _danmaku.Dispose(); }
        if (_vts is not null) await _vts.DisconnectAsync();
        if (_obs is not null) await _obs.DisconnectAsync();
        _orchestrator?.Dispose();
        _tts?.Dispose();
        _llm?.Dispose();
        _asr?.Dispose();
        _player?.Dispose();
        _embedding?.Dispose();
        _memoryDb?.Dispose();
        _cts.Dispose();
    }
}
