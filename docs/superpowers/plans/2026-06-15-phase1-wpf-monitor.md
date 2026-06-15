# Phase 1 — WPF 骨架 + 监控页 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 App 从控制台转为 WPF 单 exe，搭起三标签壳（监控/配置/记忆），并让「监控」页实时显示流水线状态、对话、情绪、连接、弹幕队列与延迟。

**Architecture:** 所有可测逻辑放进 Core（macOS 可测）：`PipelineStateTracker`（状态机+延迟）、`BotRuntime` 扩展（转发 speaking 事件、暴露连接状态与 tracker）、`DanmakuSelector.Snapshot`、`MonitorViewModel`（`INotifyPropertyChanged`，订阅 `BotRuntime`，经注入的 dispatch 委托切线程）。WPF 工程只含 XAML + 启动引导 + 绑定，不含逻辑。

**Tech Stack:** C# / .NET 10, WPF (`net10.0-windows`), 轻量 MVVM, xUnit。

参考：设计文档 `docs/superpowers/specs/2026-06-14-aivtuber-wpf-dashboard-design.md`（第 3、4.1 节）；Phase 0 产出的 `AIVTuber.Core/Runtime/BotRuntime.cs`。

---

## ⚠️ 平台约束（执行前必读）

- **Task 1、2（Core）**：纯 .NET，可在 macOS 用 `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj` 验证。
- **Task 3、4（WPF）**：`net10.0-windows` + `UseWPF`，**只能在 Windows 上 build/run**。在 macOS 上 `dotnet build` App 会失败，这是预期。一旦 App 转为 WPF，`dotnet build AIVTuber.slnx` 整体在 macOS 上不再可用——改用按工程构建：Core/Tests 在任意平台，App 在 Windows。
- `AIVTuber.Tests` 只引用 `AIVTuber.Core`（不引用 App），所以 Task 1/2 的测试在 macOS 仍可跑。
- 执行 Task 3/4 的子代理/人需在 Windows 环境（或由你在 Windows 上跑构建与手动验证）。

---

## File Structure

- **Create** `AIVTuber.Core/Runtime/PipelineStateTracker.cs` — 流水线状态机 + 延迟计算（纯逻辑）。
- **Modify** `AIVTuber.Core/Runtime/BotRuntime.cs` — 持有 tracker、转发 `AiStartSpeaking/AiStopSpeaking`、暴露连接状态、在事件处喂 tracker。
- **Modify** `AIVTuber.Core/LiveStream/DanmakuSelector.cs` — 加 `Snapshot()`。
- **Create** `AIVTuber.Core/ViewModels/MonitorViewModel.cs` — 监控页 ViewModel（INotifyPropertyChanged，无 WPF 依赖）。
- **Create** `AIVTuber.Tests/PipelineStateTrackerTests.cs`、`AIVTuber.Tests/MonitorViewModelTests.cs`。
- **Modify** `App/App.csproj` — 转 WPF。
- **Create** `App/App.xaml` + `App/App.xaml.cs` — 应用引导（替代 `Program.Main`）。
- **Delete** `App/Program.cs`（逻辑迁入 `App.xaml.cs`）。
- **Create** `App/MainWindow.xaml` (+ `.cs`) — 三标签壳。
- **Create** `App/Views/MonitorView.xaml` (+ `.cs`) — 监控页。

---

## Task 1: PipelineStateTracker + BotRuntime 状态/事件扩展 + DanmakuSelector.Snapshot

**Files:**
- Create: `AIVTuber.Core/Runtime/PipelineStateTracker.cs`
- Test: `AIVTuber.Tests/PipelineStateTrackerTests.cs`
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs`, `AIVTuber.Core/LiveStream/DanmakuSelector.cs`

- [ ] **Step 1: 写 PipelineStateTracker 失败测试**

Create `AIVTuber.Tests/PipelineStateTrackerTests.cs`:

```csharp
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class PipelineStateTrackerTests
{
    [Fact]
    public void Started_GoesListening()
    {
        var t = new PipelineStateTracker();
        t.Started();
        Assert.Equal(PipelineState.Listening, t.State);
    }

    [Fact]
    public void VoiceFlow_ComputesAsrAndFirstSentenceLatency()
    {
        var t = new PipelineStateTracker();
        t.InputStarted(1000);            // VAD detected speech at t=1000ms
        Assert.Equal(PipelineState.Thinking, t.State);
        t.TranscriptReady(1300);         // transcript at t=1300 -> ASR 300ms
        Assert.Equal(300, t.LastAsrLatencyMs);
        t.SpeakingStarted(2100);         // first audio at t=2100 -> first sentence 800ms
        Assert.Equal(PipelineState.Speaking, t.State);
        Assert.Equal(800, t.LastFirstSentenceMs);
        t.SpeakingStopped();
        Assert.Equal(PipelineState.Listening, t.State);
    }

    [Fact]
    public void TextFlow_HasNoAsrLatency()
    {
        var t = new PipelineStateTracker();
        t.TextInputStarted(500);
        Assert.Null(t.LastAsrLatencyMs);
        Assert.Equal(PipelineState.Thinking, t.State);
        t.SpeakingStarted(1200);
        Assert.Equal(700, t.LastFirstSentenceMs);
    }

    [Fact]
    public void Changed_FiresOnEachTransition()
    {
        var t = new PipelineStateTracker();
        int n = 0; t.Changed += (_, _) => n++;
        t.Started();
        t.InputStarted(0);
        t.SpeakingStopped();
        Assert.Equal(3, n);
    }
}
```

- [ ] **Step 2: 跑测试确认编译失败**

Run: `/usr/local/share/dotnet/dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "PipelineStateTrackerTests"`
Expected: 编译失败（`PipelineStateTracker`/`PipelineState` 未定义）。

- [ ] **Step 3: 写 PipelineStateTracker**

Create `AIVTuber.Core/Runtime/PipelineStateTracker.cs`:

```csharp
namespace AIVTuber.Core.Runtime;

/// <summary>High-level pipeline state shown on the monitor.</summary>
public enum PipelineState { Idle, Listening, Thinking, Speaking }

/// <summary>
/// Tracks the pipeline's high-level state and computes ASR / first-sentence latency.
/// Pure logic: callers pass monotonic millisecond timestamps (e.g. Environment.TickCount64),
/// so it is fully unit-testable.
/// </summary>
public sealed class PipelineStateTracker
{
    private long _inputStartMs;   // 0 = none
    private long _transcriptMs;   // 0 = none

    public PipelineState State { get; private set; } = PipelineState.Idle;
    public long? LastAsrLatencyMs { get; private set; }
    public long? LastFirstSentenceMs { get; private set; }

    public event EventHandler? Changed;

    public void Started() => Set(PipelineState.Listening);

    /// <summary>Voice input detected by VAD; start the ASR timer.</summary>
    public void InputStarted(long nowMs)
    {
        _inputStartMs = nowMs;
        Set(PipelineState.Thinking);
    }

    /// <summary>ASR transcript ready; record ASR latency, start the first-sentence timer.</summary>
    public void TranscriptReady(long nowMs)
    {
        if (_inputStartMs != 0) LastAsrLatencyMs = nowMs - _inputStartMs;
        _transcriptMs = nowMs;
        Set(PipelineState.Thinking);
    }

    /// <summary>Text (danmaku) input; no ASR step, start the first-sentence timer.</summary>
    public void TextInputStarted(long nowMs)
    {
        _inputStartMs = 0;
        LastAsrLatencyMs = null;
        _transcriptMs = nowMs;
        Set(PipelineState.Thinking);
    }

    /// <summary>AI audio playback started; record first-sentence latency.</summary>
    public void SpeakingStarted(long nowMs)
    {
        if (_transcriptMs != 0) { LastFirstSentenceMs = nowMs - _transcriptMs; _transcriptMs = 0; }
        Set(PipelineState.Speaking);
    }

    public void SpeakingStopped() => Set(PipelineState.Listening);

    private void Set(PipelineState s)
    {
        State = s;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `/usr/local/share/dotnet/dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "PipelineStateTrackerTests"`
Expected: PASS（4 个）。

- [ ] **Step 5: 给 DanmakuSelector 加 Snapshot（供监控页读队列）**

在 `AIVTuber.Core/LiveStream/DanmakuSelector.cs` 的 `QueueCount` 属性后加：

```csharp
    /// <summary>Snapshot of the current backlog (oldest first), for display.</summary>
    public IReadOnlyList<Danmaku> Snapshot()
    {
        lock (_lock) { return _items.ToArray(); }
    }
```

- [ ] **Step 6: 扩展 BotRuntime —— tracker、speaking 事件、连接状态**

在 `AIVTuber.Core/Runtime/BotRuntime.cs` 中做以下改动：

(a) 加字段与公开成员（放在现有事件声明附近）：

```csharp
    private readonly PipelineStateTracker _stateTracker = new();

    /// <summary>Pipeline state + latency, for the monitor.</summary>
    public PipelineStateTracker StateTracker => _stateTracker;
    /// <summary>Fired when the AI starts / stops speaking.</summary>
    public event EventHandler? AiStartSpeaking;
    public event EventHandler? AiStopSpeaking;

    public bool VtsConnected => _vts is not null;
    public bool ObsConnected => _obs is not null;
    public bool DanmakuActive => _danmaku is not null;
```

(b) 在 `StartAsync()` 末尾（`StartAudio();` 之后）加：

```csharp
        _stateTracker.Started();
```

(c) 在 `InitPipeline()` 里，现有的 `OnUserTranscript`/speaking 桥接处接入 tracker 与新事件。把现有的 OnUserTranscript 处理器改为同时喂 tracker，并在稳定 speaking 桥接处转发事件 + 喂 tracker：

将 `_orchestrator.OnUserTranscript += async (_, text) => { ... }` 内首行后加 `_stateTracker.TranscriptReady(Environment.TickCount64);`，即：

```csharp
        _orchestrator.OnUserTranscript += async (_, text) =>
        {
            _stateTracker.TranscriptReady(Environment.TickCount64);
            _conversation.AddUserMessage(text);
            UserTranscript?.Invoke(this, text);
            await _memoryExtractor.OnTurnAsync();
        };
```

将稳定 speaking 桥接那两行（Phase 0 加的 `_selector?.SetSpeaking(...)`）扩展为同时转发事件并喂 tracker：

```csharp
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
```

(d) 在 `StartAudio()` 的 `SpeechDetected` 处理器首行加 `_stateTracker.InputStarted(Environment.TickCount64);`：

```csharp
        _vad.SpeechDetected += async (_, seg) =>
        {
            _stateTracker.InputStarted(Environment.TickCount64);
            var history = _conversation.BuildMessages();
            await _orchestrator.ProcessSpeechAsync(seg, history);
        };
```

(e) 在 `CreateSelector()` 的 `OnDanmakuSelected` 处理器首行加 `_stateTracker.TextInputStarted(Environment.TickCount64);`：

```csharp
        selector.OnDanmakuSelected += async (_, d) =>
        {
            _stateTracker.TextInputStarted(Environment.TickCount64);
            await _viewerRepo.RecordInteractionAsync(d.Uid, d.Platform, d.Username);
            var history = _conversation.BuildMessages(d.Uid);
            await _orchestrator.ProcessTextAsync(d.Content, history);
        };
```

- [ ] **Step 7: 构建 + 全量测试（Core/Tests，macOS 可跑）**

Run: `/usr/local/share/dotnet/dotnet build AIVTuber.Core/AIVTuber.Core.csproj`
Run: `/usr/local/share/dotnet/dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj`
Expected: 0 错误；既有测试 + 新增 PipelineStateTracker 测试全过。

- [ ] **Step 8: 提交**

```bash
git add AIVTuber.Core/Runtime/PipelineStateTracker.cs AIVTuber.Tests/PipelineStateTrackerTests.cs \
        AIVTuber.Core/Runtime/BotRuntime.cs AIVTuber.Core/LiveStream/DanmakuSelector.cs
git commit -m "feat(runtime): pipeline state tracker, speaking events, danmaku snapshot

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: MonitorViewModel（Core，TDD）

UI 无关的监控页 ViewModel：订阅 `BotRuntime`，把事件映射成可绑定属性；通过注入的 `Action<Action> dispatch` 切到 UI 线程（测试传同步实现）。

**Files:**
- Create: `AIVTuber.Core/ViewModels/MonitorViewModel.cs`
- Test: `AIVTuber.Tests/MonitorViewModelTests.cs`

> 依赖：`MonitorViewModel` 需要一个已构造的 `BotRuntime`。测试中不调用 `StartAsync`（那会开真实设备），而是构造 `new BotRuntime(new AppConfig(), tempDir)` 并直接触发其事件来断言。为此 `BotRuntime` 的三个 UI 事件（`UserTranscript`/`SentenceReady`/`EmotionDetected`）已是 public，可在测试中 `Invoke`——但它们由内部 orchestrator 触发，测试无法直接 raise。**因此 ViewModel 必须只依赖 public 事件/属性**，测试通过 `StateTracker`（public，可直接调用其方法触发 `Changed`）和直接读属性来验证。对 `UserTranscript` 等无法在测试中 raise 的事件，仅验证初始绑定与 `StateTracker` 驱动的属性。

- [ ] **Step 1: 写失败测试**

Create `AIVTuber.Tests/MonitorViewModelTests.cs`:

```csharp
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
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
}
```

- [ ] **Step 2: 跑测试确认编译失败**

Run: `/usr/local/share/dotnet/dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "MonitorViewModelTests"`
Expected: 编译失败（`MonitorViewModel` 未定义）。

- [ ] **Step 3: 写 MonitorViewModel**

Create `AIVTuber.Core/ViewModels/MonitorViewModel.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Core.ViewModels;

/// <summary>
/// Monitor tab view-model. UI-agnostic (no WPF types): subscribes to BotRuntime and
/// exposes bindable properties. All updates are marshalled through the injected
/// <paramref name="dispatch"/> delegate (the WPF host passes Dispatcher.Invoke; tests
/// pass a synchronous run-now delegate).
/// </summary>
public sealed class MonitorViewModel : INotifyPropertyChanged
{
    private readonly BotRuntime _runtime;
    private readonly Action<Action> _dispatch;

    public MonitorViewModel(BotRuntime runtime, Action<Action> dispatch)
    {
        _runtime = runtime;
        _dispatch = dispatch;

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

    private bool _vts;
    public bool VtsConnected { get => _vts; private set => SetField(ref _vts, value); }

    private bool _obs;
    public bool ObsConnected { get => _obs; private set => SetField(ref _obs, value); }

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
```

- [ ] **Step 4: 跑测试确认通过**

Run: `/usr/local/share/dotnet/dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter "MonitorViewModelTests"`
Expected: PASS（4 个）。

- [ ] **Step 5: 全量测试 + 提交**

Run: `/usr/local/share/dotnet/dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj`
Expected: 全过。

```bash
git add AIVTuber.Core/ViewModels/MonitorViewModel.cs AIVTuber.Tests/MonitorViewModelTests.cs
git commit -m "feat(viewmodels): add MonitorViewModel bound to BotRuntime

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: App → WPF 转换 + 启动引导 ⚠️ Windows-only build

把 App 工程转为 WPF，启动逻辑从 `Program.Main` 迁到 `App.xaml.cs`，首次运行仍走 `ConfigWizard`（控制台向导暂保留；Phase 2 的配置页会取代它）。

**Files:**
- Modify: `App/App.csproj`
- Create: `App/App.xaml`, `App/App.xaml.cs`
- Delete: `App/Program.cs`

- [ ] **Step 1: 改 App.csproj 为 WPF**

将 `App/App.csproj` 的 `<PropertyGroup>` 改为（保留单 exe/自包含/win-x64）：

```xml
  <PropertyGroup>
    <AssemblyName>AIVTuber</AssemblyName>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AIVTuber</RootNamespace>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>
```

`<ItemGroup>`（ProjectReference 与 None Include 的 config/python/readme 拷贝）保持不变。

- [ ] **Step 2: 删除 Program.cs，创建 App.xaml**

```bash
git rm App/Program.cs
```

Create `App/App.xaml`:

```xml
<Application x:Class="AIVTuber.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
```

- [ ] **Step 3: 创建 App.xaml.cs（启动引导）**

Create `App/App.xaml.cs`:

```csharp
using System.Windows;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.App;

public partial class App : Application
{
    private BotRuntime? _runtime;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
        var configManager = new ConfigManager(configPath);
        var config = configManager.Load();

        if (IsFirstRun(config))
        {
            MessageBox.Show("尚未配置 API key，请先编辑 config.json（Phase 2 将提供配置页）。",
                "AIVTuber", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _runtime = new BotRuntime(config, AppContext.BaseDirectory);
        await _runtime.StartAsync();

        var window = new MainWindow(_runtime);
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_runtime is not null) await _runtime.DisposeAsync();
        base.OnExit(e);
    }

    private static bool IsFirstRun(AppConfig config)
        => string.IsNullOrEmpty(config.Llm.ApiKey) && string.IsNullOrEmpty(config.Tts.ApiKey);
}
```

> 说明：首次运行原走控制台 `ConfigWizard`；WPF 下暂以提示框替代（Phase 2 配置页接管）。`ConfigWizard.cs` 暂保留在工程中不引用，Phase 2 删除。

- [ ] **Step 4: 在 Windows 上构建（macOS 跳过，预期失败）**

On Windows: `dotnet build App/App.csproj`
Expected: 0 错误。MainWindow 在 Task 4 创建——本步会因缺 `MainWindow` 报错，故 **Task 3 与 Task 4 一起在 Windows 构建验证**（先建文件，最后统一 build）。

- [ ] **Step 5: 提交**

```bash
git add App/App.csproj App/App.xaml App/App.xaml.cs
git commit -m "feat(app): convert App to WPF, bootstrap BotRuntime from App.OnStartup

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: MainWindow 三标签壳 + 监控页 ⚠️ Windows-only build/run

**Files:**
- Create: `App/MainWindow.xaml` (+ `.cs`)
- Create: `App/Views/MonitorView.xaml` (+ `.cs`)

- [ ] **Step 1: MainWindow.xaml（TabControl：监控/配置/记忆）**

Create `App/MainWindow.xaml`:

```xml
<Window x:Class="AIVTuber.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:AIVTuber.App.Views"
        Title="AIVTuber 控制台" Height="600" Width="900">
    <TabControl Margin="6">
        <TabItem Header="监控">
            <views:MonitorView x:Name="MonitorView" />
        </TabItem>
        <TabItem Header="配置">
            <TextBlock Text="配置页（Phase 2）" Margin="16" Foreground="Gray" />
        </TabItem>
        <TabItem Header="记忆">
            <TextBlock Text="记忆页（Phase 3）" Margin="16" Foreground="Gray" />
        </TabItem>
    </TabControl>
</Window>
```

Create `App/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App;

public partial class MainWindow : Window
{
    public MainWindow(BotRuntime runtime)
    {
        InitializeComponent();
        // Marshal VM updates onto the UI thread via the window's Dispatcher.
        var vm = new MonitorViewModel(runtime, action => Dispatcher.Invoke(action));
        MonitorView.DataContext = vm;
    }
}
```

- [ ] **Step 2: MonitorView.xaml（状态/对话/情绪/连接/队列/延迟）**

Create `App/Views/MonitorView.xaml`:

```xml
<UserControl x:Class="AIVTuber.App.Views.MonitorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="2*" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <StackPanel Grid.Column="0" Margin="0,0,12,0">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="状态: " FontWeight="Bold" />
                <TextBlock Text="{Binding State}" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                <TextBlock Text="VTS:" /><TextBlock Text="{Binding VtsConnected}" Margin="4,0,12,0" />
                <TextBlock Text="OBS:" /><TextBlock Text="{Binding ObsConnected}" Margin="4,0,12,0" />
                <TextBlock Text="弹幕:" /><TextBlock Text="{Binding DanmakuActive}" Margin="4,0,0,0" />
            </StackPanel>
            <Border BorderBrush="#DDD" BorderThickness="1" Margin="0,12,0,0" Padding="8" MinHeight="160">
                <StackPanel>
                    <TextBlock Text="实时对话" Foreground="Gray" />
                    <TextBlock Text="用户" Foreground="Gray" Margin="0,8,0,0" />
                    <TextBlock Text="{Binding UserText}" TextWrapping="Wrap" />
                    <TextBlock Text="AI" Foreground="#3B6" Margin="0,8,0,0" />
                    <TextBlock Text="{Binding AssistantText}" TextWrapping="Wrap" />
                </StackPanel>
            </Border>
        </StackPanel>

        <StackPanel Grid.Column="1">
            <Border BorderBrush="#DDD" BorderThickness="1" Padding="8">
                <StackPanel>
                    <TextBlock Text="当前情绪" Foreground="Gray" />
                    <TextBlock Text="{Binding Emotion}" FontSize="16" />
                </StackPanel>
            </Border>
            <Border BorderBrush="#DDD" BorderThickness="1" Padding="8" Margin="0,8,0,0">
                <StackPanel>
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="弹幕队列 " Foreground="Gray" />
                        <TextBlock Text="{Binding DanmakuQueueCount}" Foreground="Gray" />
                    </StackPanel>
                    <ItemsControl ItemsSource="{Binding DanmakuQueue}" Margin="0,6,0,0">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <StackPanel Orientation="Horizontal">
                                    <TextBlock Text="{Binding Username}" Foreground="Gray" Margin="0,0,6,0" />
                                    <TextBlock Text="{Binding Content}" />
                                </StackPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>
            </Border>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                <TextBlock Text="ASR " /><TextBlock Text="{Binding AsrLatencyMs}" /><TextBlock Text="ms  " />
                <TextBlock Text="首句 " /><TextBlock Text="{Binding FirstSentenceMs}" /><TextBlock Text="ms" />
            </StackPanel>
        </StackPanel>
    </Grid>
</UserControl>
```

Create `App/Views/MonitorView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace AIVTuber.App.Views;

public partial class MonitorView : UserControl
{
    public MonitorView() => InitializeComponent();
}
```

- [ ] **Step 3: 在 Windows 上构建 + 运行（macOS 不可执行）**

On Windows:
- `dotnet build App/App.csproj` → 0 错误。
- 准备好已配置的 `config.json`（含 LLM/TTS key），`dotnet run --project App` 或运行生成的 exe。
- **手动验证清单**：窗口打开、三标签可切换；对麦克风说话后，监控页「状态」从 Listening→Thinking→Speaking 变化，「实时对话」显示转写与 AI 回复，「ASR/首句」显示数字；若启用 VTS/OBS/弹幕，对应状态为 True、弹幕队列有内容。

- [ ] **Step 4: 提交**

```bash
git add App/MainWindow.xaml App/MainWindow.xaml.cs App/Views/MonitorView.xaml App/Views/MonitorView.xaml.cs
git commit -m "feat(app): add WPF main window shell + live monitor view

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage（设计文档 4.1 监控页）：**
- 状态灯（在听/在想/在说）→ `PipelineStateTracker` + VM.State（Task 1、2、4）✅
- 流水线阶段高亮 → State 已覆盖「在听/在想/在说」三态；逐模块阶段条（麦/VAD/ASR/LLM/TTS）未做，属可选增强，**本期以 State 三态呈现**（spec 监控页核心是状态，逐模块条是 mockup 细节）。⚠️ 已知缩减。
- 实时对话（转写+流式回复）→ UserText/AssistantText（Task 2、4）✅
- 当前情绪 → Emotion ✅
- 弹幕队列（老粉星标/置灰）→ DanmakuQueue 列表（Task 1 Snapshot + Task 4）；星标/TTL 置灰为视觉细节，本期仅列表+计数。⚠️ 已知缩减。
- 延迟（ASR/首句）→ tracker 计算 + VM 暴露（Task 1、2、4）✅
- WebSearching 预留态 → `PipelineState` 暂未含；tool calling 独立特性落地时再加枚举值（spec 已定为预留）。✅ 按计划预留

**Placeholder scan：** 无 TBD；Core 任务含完整代码与真实断言测试；WPF 任务含完整 XAML/代码 + 手动验证清单（XAML 无法 TDD）。

**Type consistency：** `PipelineState`/`PipelineStateTracker` 在 Task 1 定义，Task 2 VM 与 Task 4 XAML 绑定一致；`BotRuntime` 新增 `StateTracker`/`AiStartSpeaking`/`AiStopSpeaking`/`VtsConnected`/`ObsConnected`/`DanmakuActive` 在 Task 1 定义，Task 2 引用一致；`MonitorViewModel(BotRuntime, Action<Action>)` 构造签名在 Task 2 定义，Task 4 MainWindow 按此调用；`DanmakuSelector.Snapshot()` 在 Task 1 定义，VM 使用。

**已知执行风险：**
- Task 3/4 仅能在 Windows build/run；macOS 环境只能验证 Task 1/2。
- `MonitorViewModelTests` 无法 raise `BotRuntime` 的 orchestrator 驱动事件（UserTranscript 等），故对那部分仅验证初值；`StateTracker` 路径有完整断言。
- WPF 标量属性的跨线程 PropertyChanged 由注入的 Dispatcher.Invoke 保证在 UI 线程触发；`DanmakuQueue` 整体替换为新列表（而非 ObservableCollection 增量），避免集合跨线程问题。
