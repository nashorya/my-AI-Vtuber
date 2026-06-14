# Phase 0 — BotRuntime 重构 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `App/Program.cs` 里散落的静态模块装配收拢进一个 UI 无关、可运行时重建配置的 `BotRuntime` 对象，控制台程序照常运行。

**Architecture:** 新增 `AIVTuber.Core/Runtime/ConfigDiff.cs`（纯函数：对比新旧 `AppConfig`，输出需要执行哪些模块重建动作，并区分轻量/重型）与 `AIVTuber.Core/Runtime/BotRuntime.cs`（持有全部模块、转发事件、实现 `StartAsync` / `ApplyConfigAsync` / `DisposeAsync`）。`Program.cs` 改为薄壳，仅加载配置、创建 `BotRuntime`、跑控制台循环。

**Tech Stack:** C# / .NET 10, xUnit, 现有 Core 模块（Audio/Pipeline/Vts/Obs/LiveStream/Memory）。

参考设计文档：`docs/superpowers/specs/2026-06-14-aivtuber-wpf-dashboard-design.md`（第 3、5 节）。

---

## File Structure

- **Create** `AIVTuber.Core/Runtime/ConfigDiff.cs` — `RuntimeChange` 标志枚举 + `ConfigDiff.Compute(old, new)` 纯函数 + `IsHeavy` 掩码。唯一职责：配置差异分类。
- **Create** `AIVTuber.Core/Runtime/BotRuntime.cs` — 拥有并装配所有模块；暴露事件/状态；`StartAsync`、`ApplyConfigAsync`、`DisposeAsync`。
- **Create** `AIVTuber.Tests/ConfigDiffTests.cs` — `ConfigDiff.Compute` 的单元测试。
- **Modify** `App/Program.cs` — 删除静态模块字段与 `Init*` 方法，改用 `BotRuntime`。

各模块的构造签名取自现有 `App/Program.cs`（重构前）。

---

## Task 1: ConfigDiff —— 配置差异分类（纯逻辑，TDD）

**Files:**
- Create: `AIVTuber.Core/Runtime/ConfigDiff.cs`
- Test: `AIVTuber.Tests/ConfigDiffTests.cs`

分类规则（来自 spec 第 5 节）：

| 变更字段 | 动作 | 轻/重 |
|---|---|---|
| `Llm.*` | `RebuildLlm` | 轻 |
| `Asr.*` | `RebuildAsr` | 轻 |
| `Tts.*` | `RebuildTts` | 轻 |
| `Vts.EmotionMap` / `Vts.MouthScale` | `UpdateVtsParams` | 轻 |
| `Vts.Host` / `Vts.Port` | `ReconnectVts` | 重 |
| `Obs.AssistantTextComponent` / `UserTextComponent` / `TypewriterIntervalMs` | `UpdateObsParams` | 轻 |
| `Obs.Enable` / `Host` / `Port` / `Password` | `ReconnectObs` | 重 |
| `Bilibili.SelectionIntervalSec` | `RebuildDanmakuSelector` | 轻 |
| `Bilibili.Enable/RoomId/Sessdata/BiliJct/Buvid3/PushPort/PythonPath` | `RestartDanmaku` | 重 |
| `Memory.ExtractEveryNTurns` | `UpdateMemoryParams` | 轻 |
| `Memory.DatabasePath` / `EmbeddingModelPath` | `ReopenMemory` | 重 |
| `Audio.*` | `RestartAudio` | 重 |

- [ ] **Step 1: 写失败测试**

Create `AIVTuber.Tests/ConfigDiffTests.cs`:

```csharp
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class ConfigDiffTests
{
    private static AppConfig Base() => new();

    [Fact]
    public void NoChange_ReturnsNone()
    {
        Assert.Equal(RuntimeChange.None, ConfigDiff.Compute(Base(), Base()));
    }

    [Fact]
    public void LlmChange_IsLightRebuildLlm()
    {
        var b = Base();
        b.Llm.SystemPrompt = "新人设";
        var c = ConfigDiff.Compute(Base(), b);
        Assert.Equal(RuntimeChange.RebuildLlm, c);
        Assert.False(ConfigDiff.IsHeavy(c));
    }

    [Fact]
    public void VtsEmotionMapChange_IsLightUpdateParams()
    {
        var b = Base();
        b.Vts.EmotionMap["happy"] = "42";
        var c = ConfigDiff.Compute(Base(), b);
        Assert.Equal(RuntimeChange.UpdateVtsParams, c);
        Assert.False(ConfigDiff.IsHeavy(c));
    }

    [Fact]
    public void VtsHostChange_IsHeavyReconnect()
    {
        var b = Base();
        b.Vts.Port = 9000;
        var c = ConfigDiff.Compute(Base(), b);
        Assert.Equal(RuntimeChange.ReconnectVts, c);
        Assert.True(ConfigDiff.IsHeavy(c));
    }

    [Fact]
    public void AudioDeviceChange_IsHeavyRestartAudio()
    {
        var b = Base();
        b.Audio.InputDeviceIndex = 3;
        Assert.True(ConfigDiff.IsHeavy(ConfigDiff.Compute(Base(), b)));
        Assert.Equal(RuntimeChange.RestartAudio, ConfigDiff.Compute(Base(), b));
    }

    [Fact]
    public void BilibiliIntervalChange_IsLight_ButRoomChange_IsHeavy()
    {
        var b1 = Base(); b1.Bilibili.SelectionIntervalSec = 12;
        Assert.Equal(RuntimeChange.RebuildDanmakuSelector, ConfigDiff.Compute(Base(), b1));

        var b2 = Base(); b2.Bilibili.RoomId = 123;
        Assert.Equal(RuntimeChange.RestartDanmaku, ConfigDiff.Compute(Base(), b2));
    }

    [Fact]
    public void ObsComponentName_Light_ButPassword_Heavy()
    {
        var b1 = Base(); b1.Obs.TypewriterIntervalMs = 80;
        Assert.Equal(RuntimeChange.UpdateObsParams, ConfigDiff.Compute(Base(), b1));

        var b2 = Base(); b2.Obs.Password = "secret";
        Assert.Equal(RuntimeChange.ReconnectObs, ConfigDiff.Compute(Base(), b2));
    }

    [Fact]
    public void MemoryExtractInterval_Light_ButDbPath_Heavy()
    {
        var b1 = Base(); b1.Memory.ExtractEveryNTurns = 10;
        Assert.Equal(RuntimeChange.UpdateMemoryParams, ConfigDiff.Compute(Base(), b1));

        var b2 = Base(); b2.Memory.DatabasePath = "other.db";
        Assert.Equal(RuntimeChange.ReopenMemory, ConfigDiff.Compute(Base(), b2));
    }

    [Fact]
    public void MultipleChanges_AreCombined()
    {
        var b = Base();
        b.Llm.Model = "gpt-x";
        b.Audio.InputDeviceIndex = 2;
        var c = ConfigDiff.Compute(Base(), b);
        Assert.True(c.HasFlag(RuntimeChange.RebuildLlm));
        Assert.True(c.HasFlag(RuntimeChange.RestartAudio));
        Assert.True(ConfigDiff.IsHeavy(c)); // 含重型项
    }
}
```

- [ ] **Step 2: 运行测试，确认编译失败**

Run: `/usr/local/share/dotnet/dotnet test AIVTuber.slnx --filter "ConfigDiffTests"`
Expected: 编译失败（`RuntimeChange` / `ConfigDiff` 未定义）。

- [ ] **Step 3: 写实现**

Create `AIVTuber.Core/Runtime/ConfigDiff.cs`:

```csharp
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Runtime;

/// <summary>
/// Module-rebuild actions triggered by a config change. Light actions recreate
/// in-memory objects (instant); heavy actions reopen devices/sockets/files (~1-2s).
/// </summary>
[Flags]
public enum RuntimeChange
{
    None = 0,
    RebuildLlm = 1 << 0,
    RebuildAsr = 1 << 1,
    RebuildTts = 1 << 2,
    UpdateVtsParams = 1 << 3,
    UpdateObsParams = 1 << 4,
    RebuildDanmakuSelector = 1 << 5,
    UpdateMemoryParams = 1 << 6,
    RestartAudio = 1 << 7,
    ReconnectVts = 1 << 8,
    ReconnectObs = 1 << 9,
    RestartDanmaku = 1 << 10,
    ReopenMemory = 1 << 11,
}

/// <summary>Computes which modules must rebuild when config changes.</summary>
public static class ConfigDiff
{
    /// <summary>Mask of heavy (device/socket/file) actions.</summary>
    public const RuntimeChange HeavyMask =
        RuntimeChange.RestartAudio | RuntimeChange.ReconnectVts | RuntimeChange.ReconnectObs |
        RuntimeChange.RestartDanmaku | RuntimeChange.ReopenMemory;

    public static bool IsHeavy(RuntimeChange change) => (change & HeavyMask) != 0;

    public static RuntimeChange Compute(AppConfig a, AppConfig b)
    {
        var c = RuntimeChange.None;

        // LLM (also drives ConversationManager) — light.
        if (a.Llm.BaseUrl != b.Llm.BaseUrl || a.Llm.ApiKey != b.Llm.ApiKey ||
            a.Llm.Model != b.Llm.Model || a.Llm.SystemPrompt != b.Llm.SystemPrompt ||
            a.Llm.MaxHistoryTokens != b.Llm.MaxHistoryTokens)
            c |= RuntimeChange.RebuildLlm;

        // ASR — light.
        if (a.Asr.Provider != b.Asr.Provider || a.Asr.ApiKey != b.Asr.ApiKey || a.Asr.AppId != b.Asr.AppId)
            c |= RuntimeChange.RebuildAsr;

        // TTS — light.
        if (a.Tts.Provider != b.Tts.Provider || a.Tts.ApiKey != b.Tts.ApiKey || a.Tts.VoiceId != b.Tts.VoiceId)
            c |= RuntimeChange.RebuildTts;

        // VTS — params light, connection heavy.
        if (a.Vts.MouthScale != b.Vts.MouthScale || !DictEqual(a.Vts.EmotionMap, b.Vts.EmotionMap))
            c |= RuntimeChange.UpdateVtsParams;
        if (a.Vts.Host != b.Vts.Host || a.Vts.Port != b.Vts.Port)
            c |= RuntimeChange.ReconnectVts;

        // OBS — text params light, connection heavy.
        if (a.Obs.AssistantTextComponent != b.Obs.AssistantTextComponent ||
            a.Obs.UserTextComponent != b.Obs.UserTextComponent ||
            a.Obs.TypewriterIntervalMs != b.Obs.TypewriterIntervalMs)
            c |= RuntimeChange.UpdateObsParams;
        if (a.Obs.Enable != b.Obs.Enable || a.Obs.Host != b.Obs.Host ||
            a.Obs.Port != b.Obs.Port || a.Obs.Password != b.Obs.Password)
            c |= RuntimeChange.ReconnectObs;

        // Bilibili — interval light, connection heavy.
        if (a.Bilibili.SelectionIntervalSec != b.Bilibili.SelectionIntervalSec)
            c |= RuntimeChange.RebuildDanmakuSelector;
        if (a.Bilibili.Enable != b.Bilibili.Enable || a.Bilibili.RoomId != b.Bilibili.RoomId ||
            a.Bilibili.Sessdata != b.Bilibili.Sessdata || a.Bilibili.BiliJct != b.Bilibili.BiliJct ||
            a.Bilibili.Buvid3 != b.Bilibili.Buvid3 || a.Bilibili.PushPort != b.Bilibili.PushPort ||
            a.Bilibili.PythonPath != b.Bilibili.PythonPath)
            c |= RuntimeChange.RestartDanmaku;

        // Memory — extract interval light, db/model heavy.
        if (a.Memory.ExtractEveryNTurns != b.Memory.ExtractEveryNTurns)
            c |= RuntimeChange.UpdateMemoryParams;
        if (a.Memory.DatabasePath != b.Memory.DatabasePath ||
            a.Memory.EmbeddingModelPath != b.Memory.EmbeddingModelPath)
            c |= RuntimeChange.ReopenMemory;

        // Audio — always heavy (device / VAD restart).
        if (a.Audio.InputDeviceIndex != b.Audio.InputDeviceIndex ||
            a.Audio.UseLoopback != b.Audio.UseLoopback ||
            a.Audio.LoopbackDeviceName != b.Audio.LoopbackDeviceName ||
            a.Audio.VadAggressiveness != b.Audio.VadAggressiveness ||
            a.Audio.PreSpeechPaddingMs != b.Audio.PreSpeechPaddingMs ||
            a.Audio.PostSpeechSilenceMs != b.Audio.PostSpeechSilenceMs)
            c |= RuntimeChange.RestartAudio;

        return c;
    }

    private static bool DictEqual(Dictionary<string, string> x, Dictionary<string, string> y)
    {
        if (x.Count != y.Count) return false;
        foreach (var kv in x)
            if (!y.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `/usr/local/share/dotnet/dotnet test AIVTuber.slnx --filter "ConfigDiffTests"`
Expected: PASS（10 个测试全过）。

- [ ] **Step 5: 提交**

```bash
git add AIVTuber.Core/Runtime/ConfigDiff.cs AIVTuber.Tests/ConfigDiffTests.cs
git commit -m "feat(runtime): add ConfigDiff for config change classification

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: BotRuntime —— 模块装配 + 启动 + 事件转发

把 `App/Program.cs` 现有的 `InitMemoryAsync / InitVtsAsync / InitObs / InitPipeline / StartAudio / InitDanmakuAsync / ShutdownAsync` 逻辑原样搬进 `BotRuntime`（静态字段→实例字段），并在管线接好处额外抛出 UI 事件。**本任务不改行为**，控制台仍照常运行。

**Files:**
- Create: `AIVTuber.Core/Runtime/BotRuntime.cs`

- [ ] **Step 1: 创建 BotRuntime 类（装配 + 启动 + 事件 + 释放）**

Create `AIVTuber.Core/Runtime/BotRuntime.cs`:

```csharp
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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _mic?.Stop(); _mic?.Dispose();
        _vad?.Dispose();
        if (_danmaku is not null) await _danmaku.StopAsync();
        if (_vts is not null) await _vts.DisconnectAsync();
        if (_obs is not null) await _obs.DisconnectAsync();
        _orchestrator?.Dispose();
        _memoryDb?.Dispose();
        _cts.Dispose();
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `/usr/local/share/dotnet/dotnet build AIVTuber.slnx`
Expected: 0 错误（`BotRuntime` 通过编译；尚未被 `Program.cs` 引用，无行为变化）。

> 注：若 `ConversationManager.BuildMessages` / `BuildMessages(uid)`、各模块构造签名与本文件不符，以现有 `App/Program.cs`（重构前）的真实调用为准并对齐。

- [ ] **Step 3: 提交**

```bash
git add AIVTuber.Core/Runtime/BotRuntime.cs
git commit -m "feat(runtime): add BotRuntime owning all pipeline modules

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: BotRuntime.ApplyConfigAsync —— 按差异选择性重建

热生效核心：对比 `_config` 与新配置，只重建变更模块并重新挂接。轻量项重建内存对象，重型项停旧→建新→重连。

**Files:**
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs`（在 `DisposeAsync` 前插入）

- [ ] **Step 1: 加入 ApplyConfigAsync 及其私有重建方法**

在 `BotRuntime` 中、`DisposeAsync` 之前插入：

```csharp
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

        // Light params updated in place.
        if (change.HasFlag(RuntimeChange.UpdateMemoryParams))
            _memoryExtractor = new MemoryExtractor(
                new LlmClient(_config.Llm.BaseUrl, _config.Llm.ApiKey, _config.Llm.Model, _config.Llm.SystemPrompt),
                _factRepo, _config.Memory, _conversation);
        if (change.HasFlag(RuntimeChange.RebuildDanmakuSelector) && _selector is not null)
            await RestartDanmakuAsync();

        // Light: recreating pipeline clients requires re-wiring the orchestrator.
        if (change.HasFlag(RuntimeChange.RebuildLlm) || change.HasFlag(RuntimeChange.RebuildAsr) ||
            change.HasFlag(RuntimeChange.RebuildTts) || change.HasFlag(RuntimeChange.UpdateVtsParams) ||
            change.HasFlag(RuntimeChange.UpdateObsParams))
        {
            RewirePipeline();
        }
    }

    private async Task RebuildMemoryAsync()
    {
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
        if (_danmaku is not null) await _danmaku.StopAsync();
        _danmaku = null; _selector = null;
        await InitDanmakuAsync();
    }

    /// <summary>Recreate the orchestrator + pipeline clients and re-attach event handlers.</summary>
    private void RewirePipeline()
    {
        _orchestrator?.Dispose();
        InitPipeline();
        // Re-attach danmaku speaking handlers if danmaku is active.
        if (_selector is not null)
        {
            _orchestrator.OnAiStartSpeaking += (_, _) => _selector.SetSpeaking(true);
            _orchestrator.OnAiStopSpeaking += (_, _) => { _selector.SetSpeaking(false); _selector.TrySelectNext(); };
        }
    }
```

> 注：`InitPipeline` 已用 `_player ??= new AudioPlayer();`，重建管线时复用同一个 `AudioPlayer`（不打断播放设备）。`StartAudio` 里的 `SpeechDetected` 闭包通过字段 `_orchestrator` 调用，重建后自动指向新实例。

- [ ] **Step 2: 编译验证**

Run: `/usr/local/share/dotnet/dotnet build AIVTuber.slnx`
Expected: 0 错误。

- [ ] **Step 3: 提交**

```bash
git add AIVTuber.Core/Runtime/BotRuntime.cs
git commit -m "feat(runtime): add ApplyConfigAsync selective module rebuild

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: 改写 Program.cs 使用 BotRuntime

把 `Program.cs` 瘦身为：加载配置 → 首次运行向导 → 创建并启动 `BotRuntime` → 控制台循环 → 释放。删除所有静态模块字段与 `Init*` / `Shutdown*` 方法（已迁入 `BotRuntime`）。保留 `IsFirstRun` 与 `ConfigWizard` 调用。

**Files:**
- Modify: `App/Program.cs`（整体替换 `Main` 之后的私有方法）

- [ ] **Step 1: 替换 Program.cs 内容**

将 `App/Program.cs` 改为：

```csharp
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.App;

/// <summary>Main entry point: first-run wizard, then run the BotRuntime with a console loop.</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== AIVTuber ===\n");

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var configManager = new ConfigManager(configPath);
        var config = configManager.Load();

        if (IsFirstRun(config))
        {
            ConfigWizard.Run(config);
            configManager.Save(config);
            Console.WriteLine("配置已保存。请重启程序以使所有设置生效。按任意键退出...");
            Console.ReadKey();
            return;
        }

        await using var runtime = new BotRuntime(config, AppContext.BaseDirectory);
        await runtime.StartAsync();

        Console.WriteLine("\n=== AIVTuber 已启动 ===");
        Console.WriteLine("按 Ctrl+C 或输入 quit 退出\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        while (!cts.IsCancellationRequested)
        {
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is "quit" or "exit")
            { cts.Cancel(); break; }
        }
        Console.WriteLine("正在关闭...");
    }

    private static bool IsFirstRun(AppConfig config)
        => string.IsNullOrEmpty(config.Llm.ApiKey) && string.IsNullOrEmpty(config.Tts.ApiKey);
}
```

> 注：`ListDevices` 的打印若想保留，可在 `BotRuntime.StartAudio` 内保留原有 `MicrophoneCapture.ListDevices()` 输出；本计划已省略该日志，不影响功能。

- [ ] **Step 2: 编译 + 全量测试**

Run: `/usr/local/share/dotnet/dotnet build AIVTuber.slnx`
Run: `/usr/local/share/dotnet/dotnet test AIVTuber.slnx`
Expected: 0 编译错误；测试全过（既有 + 新增 `ConfigDiffTests`，失败 0）。

- [ ] **Step 3: 冒烟运行（人工）**

Run: `/usr/local/share/dotnet/dotnet run --project App`
Expected: 若 `config.json` 已配置，打印 `=== AIVTuber 已启动 ===` 并进入循环；输入 `quit` 干净退出。（无 key 时进入配置向导属正常。）

- [ ] **Step 4: 提交**

```bash
git add App/Program.cs
git commit -m "refactor(app): drive console from BotRuntime, remove static init

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage（针对 spec 第 3、5 节）：**
- 3.1 BotRuntime（持有模块/事件/ApplyConfigAsync）→ Task 2、3 ✅
- 3.3 现有模块不动 → Task 2 仅搬运装配，不改模块接口 ✅
- 5 配置热生效分类表 → Task 1（分类）+ Task 3（执行）✅，逐行对应
- `FactRepository.DeleteAsync` → 属 Phase 3（记忆页），不在 Phase 0 范围 ✅
- 延迟埋点、监控页 → Phase 1，不在本计划 ✅

**Placeholder scan：** 无 TBD/TODO；每个代码步骤含完整代码；测试含真实断言。两处 `> 注` 是为执行者标注的对齐提醒（构造签名以现有 Program.cs 为准），非占位。

**Type consistency：** `RuntimeChange` 枚举成员、`ConfigDiff.Compute` / `IsHeavy` / `HeavyMask` 在 Task 1 定义，Task 3 一致引用；`BotRuntime` 字段在 Task 2 定义，Task 3 复用同名字段与 `Init*` 方法；`ApplyConfigAsync` 调用的 `RewirePipeline / RestartAudio / ReconnectVtsAsync / ReconnectObsAsync / RestartDanmakuAsync / RebuildMemoryAsync` 均在 Task 3 定义。

**已知执行风险（执行时核对，非计划缺陷）：**
- 各 Core 模块构造签名/方法名以重构前 `App/Program.cs` 真实调用为准；若有出入按真实签名对齐。
- `ApplyConfigAsync` 的重建在生产中应避免与正在进行的 `ProcessSpeechAsync` 竞争；Phase 2 接 UI 时若发现并发问题，再在 runtime 内加一道 gate（超出 Phase 0 范围）。
