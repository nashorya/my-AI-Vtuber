using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Memory;
using AIVTuber.Core.Obs;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Vts;

namespace AIVTuber.App;

/// <summary>Main entry point. First-run wizard, module init, main loop.</summary>
public static class Program
{
    private static AppConfig _config = null!;
    private static ConfigManager _configManager = null!;
    private static MemoryDb _memoryDb = null!;
    private static ViewerRepository? _viewerRepo;
    private static FactRepository? _factRepo;
    private static ConversationManager? _conversation;
    private static MemoryExtractor? _memoryExtractor;
    private static BotOrchestrator? _orchestrator;
    private static BilibiliDanmakuClient? _danmakuClient;
    private static DanmakuSelector? _danmakuSelector;
    private static ObsClient? _obsClient;
    private static VtsClient? _vtsClient;
    private static MicrophoneCapture? _mic;
    private static VadDetector? _vad;
    private static CancellationTokenSource _cts = new();

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== AIVTuber ===\n");
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configManager = new ConfigManager(configPath);
        _config = _configManager.Load();

        if (IsFirstRun())
        {
            ConfigWizard.Run(_config);
            _configManager.Save(_config);
            Console.WriteLine("配置已保存。请重启程序以使所有设置生效。按任意键退出...");
            Console.ReadKey();
            return;
        }

        await InitMemoryAsync();
        await InitVtsAsync();
        InitObs();
        InitPipeline();
        await InitDanmakuAsync();
        StartAudio();

        Console.WriteLine("\n=== AIVTuber 已启动 ===");
        Console.WriteLine("按 Ctrl+C 或输入 quit 退出\n");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };
        while (!_cts.IsCancellationRequested)
        {
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is "quit" or "exit")
            { _cts.Cancel(); break; }
            await Task.Delay(100, _cts.Token);
        }
        await ShutdownAsync();
    }

    private static bool IsFirstRun()
        => string.IsNullOrEmpty(_config.Llm.ApiKey) && string.IsNullOrEmpty(_config.Tts.ApiKey);

    private static async Task InitMemoryAsync()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, _config.Memory.DatabasePath);
        _memoryDb = new MemoryDb(dbPath);
        await _memoryDb.InitializeAsync();
        EmbeddingEngine? embedding = null;
        try
        {
            var modelDir = Path.Combine(AppContext.BaseDirectory, _config.Memory.EmbeddingModelPath);
            var hasModel = File.Exists(Path.Combine(modelDir, "model.onnx"));
            var hasVocab = File.Exists(Path.Combine(modelDir, "vocab.txt"));
            if (Directory.Exists(modelDir) && hasModel && hasVocab)
            {
                embedding = new EmbeddingEngine(modelDir);
                Console.WriteLine($"[记忆] 向量引擎已加载: {modelDir}");
            }
            else Console.WriteLine("[记忆] 向量模型或词表(vocab.txt)未找到，使用字符串相似度兜底");
        }
        catch (Exception ex) { Console.WriteLine($"[记忆] 向量引擎加载失败: {ex.Message}"); }
        _viewerRepo = new ViewerRepository(_memoryDb);
        _factRepo = new FactRepository(_memoryDb, embedding);
        _conversation = new ConversationManager(_config.Llm);
        _conversation.SetMemory(_viewerRepo, _factRepo);
        _memoryExtractor = new MemoryExtractor(
            new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Llm.SystemPrompt),
            _factRepo, _config.Memory, _conversation);
    }

    private static async Task InitVtsAsync()
    {
        try
        {
            _vtsClient = new VtsClient(_config.Vts);
            await _vtsClient.ConnectAsync(_cts.Token);
            Console.WriteLine("[VTS] 已连接 VTube Studio");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VTS] 连接失败: {ex.Message}");
            _vtsClient = null;
        }
    }

    private static void InitObs()
    {
        if (!_config.Obs.Enable) return;
        _obsClient = new ObsClient(_config.Obs);
        _ = _obsClient.ConnectAsync(_cts.Token);
    }

    private static void InitPipeline()
    {
        var asr = new AsrClient(
            _config.Asr.Provider.Contains("deepseek") ? $"https://{_config.Asr.Provider}" : $"https://api.{_config.Asr.Provider}.com",
            _config.Asr.ApiKey);
        var llm = new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Llm.SystemPrompt);
        var tts = new TtsClient(_config.Tts.Provider, _config.Tts.ApiKey);
        var player = new AudioPlayer();
        _orchestrator = new BotOrchestrator(asr, llm, tts, player, _config.Tts, _vtsClient, _config.Vts);
        _orchestrator.OnUserTranscript += async (_, text) =>
        {
            _conversation?.AddUserMessage(text);
            await (_memoryExtractor?.OnTurnAsync() ?? Task.CompletedTask);
        };
        _orchestrator.OnSentenceReady += (_, s) => _conversation?.AddAssistantMessage(s);
        if (_obsClient is not null)
        {
            _orchestrator.OnSentenceReady += async (_, s)
                => await _obsClient.SetSubtitleTypewriterAsync(s, _config.Obs.AssistantTextComponent, _cts.Token);
            _orchestrator.OnUserTranscript += async (_, t)
                => await _obsClient.SetSubtitleAsync($"[用户] {t}", _config.Obs.UserTextComponent, _cts.Token);
        }
    }

    private static void StartAudio()
    {
        _mic = new MicrophoneCapture(_config.Audio.InputDeviceIndex);
        _vad = new VadDetector(_config.Audio.VadAggressiveness, _config.Audio.PreSpeechPaddingMs, _config.Audio.PostSpeechSilenceMs);
        Console.WriteLine("[音频] 麦克风设备列表:");
        foreach (var (idx, name) in MicrophoneCapture.ListDevices().Select((n, i) => (i, n)))
            Console.WriteLine($"  [{idx}] {name}");
        Console.WriteLine($"[音频] 使用设备 #{_config.Audio.InputDeviceIndex}");
        _mic.AudioFrameAvailable += (_, buf) => _vad.Feed(buf);
        _vad.SpeechDetected += async (_, seg) =>
        {
            Console.WriteLine($"[VAD] 检测到语音 ({seg.AudioData.Length} bytes)");
            var history = _conversation?.BuildMessages() ?? [];
            await (_orchestrator?.ProcessSpeechAsync(seg, history) ?? Task.CompletedTask);
        };
        _mic.Start();
    }

    private static async Task InitDanmakuAsync()
    {
        if (!_config.Bilibili.Enable) return;
        _danmakuSelector = new DanmakuSelector(_config.Bilibili.SelectionIntervalSec, _viewerRepo);
        _danmakuClient = new BilibiliDanmakuClient(_config.Bilibili);
        _danmakuClient.OnDanmaku += (_, d) => { _danmakuSelector.Enqueue(d); _danmakuSelector.TrySelectNext(); };
        _danmakuSelector.OnDanmakuSelected += async (_, d) =>
        {
            Console.WriteLine($"[弹幕] {d.Username}: {d.Content}");
            await (_viewerRepo?.RecordInteractionAsync(d.Uid, d.Platform, d.Username) ?? Task.CompletedTask);
            var history = _conversation?.BuildMessages(d.Uid) ?? [];
            await (_orchestrator?.ProcessTextAsync(d.Content, history) ?? Task.CompletedTask);
        };
        if (_orchestrator is not null)
        {
            _orchestrator.OnAiStartSpeaking += (_, _) => _danmakuSelector.SetSpeaking(true);
            _orchestrator.OnAiStopSpeaking += (_, _) => { _danmakuSelector.SetSpeaking(false); _danmakuSelector.TrySelectNext(); };
        }
        try { await _danmakuClient.StartAsync(_cts.Token); }
        catch (Exception ex) { Console.WriteLine($"[弹幕] 启动失败: {ex.Message}"); }
    }

    private static async Task ShutdownAsync()
    {
        Console.WriteLine("正在关闭...");
        _mic?.Stop();
        _mic?.Dispose();
        _vad?.Dispose();
        if (_danmakuClient is not null) await _danmakuClient.StopAsync();
        if (_vtsClient is not null) await _vtsClient.DisconnectAsync();
        if (_obsClient is not null) await _obsClient.DisconnectAsync();
        _memoryDb?.Dispose();
        _cts.Dispose();
        Console.WriteLine("已安全退出。");
    }
}