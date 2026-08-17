using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIVTuber.App.WebUi;

/// <summary>
/// Bridges Monitor + Config + Memory view-models to the WebView2 SPA.
/// Hot path stays in .NET; only UI state and click commands cross this bridge.
/// </summary>
public sealed class WebConsoleHost : IDisposable
{
    public const string VirtualHost = "aivtuber.local";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebView2 _webView;
    private readonly MonitorViewModel _monitor;
    private readonly ConfigViewModel _config;
    private readonly MemoryViewModel _memory;
    private readonly string _wwwroot;
    private readonly NotifyCollectionChangedEventHandler _eventsChanged;
    private readonly NotifyCollectionChangedEventHandler _memoryFactsChanged;
    private readonly NotifyCollectionChangedEventHandler _memoryViewersChanged;
    private bool _ready;
    private bool _disposed;
    private long _lastLevelPushMs;
    private const int LevelThrottleMs = 100;

    public WebConsoleHost(
        WebView2 webView,
        MonitorViewModel monitor,
        ConfigViewModel config,
        MemoryViewModel memory,
        string wwwroot)
    {
        _webView = webView;
        _monitor = monitor;
        _config = config;
        _memory = memory;
        _wwwroot = wwwroot;
        _eventsChanged = (_, _) => PushMonitorState();
        _memoryFactsChanged = (_, _) => PushMemory();
        _memoryViewersChanged = (_, _) => PushMemory();
        _monitor.PropertyChanged += OnMonitorPropertyChanged;
        _monitor.OperationalEvents.CollectionChanged += _eventsChanged;
        _config.PropertyChanged += OnConfigPropertyChanged;
        _memory.PropertyChanged += OnMemoryPropertyChanged;
        _memory.Facts.CollectionChanged += _memoryFactsChanged;
        _memory.Viewers.CollectionChanged += _memoryViewersChanged;
    }

    public async Task InitializeAsync()
    {
        await _webView.EnsureCoreWebView2Async();
        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            _wwwroot,
            CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;
            _ready = true;
            PushMonitorState();
            PushConfig();
            PushMemory();
        };
        _webView.Source = new Uri($"https://{VirtualHost}/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        _ = HandleMessageAsync(e.WebMessageAsJson);
    }

    private async Task HandleMessageAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();

            if (type == "ready")
            {
                _ready = true;
                PushMonitorState();
                PushConfig();
                PushMemory();
                return;
            }

            if (type != "command") return;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d : default;

            switch (name)
            {
                case "stopSpeaking":
                    _monitor.StopSpeaking();
                    break;
                case "toggleMic":
                    _monitor.ToggleMicMute();
                    break;
                case "togglePk":
                    _monitor.TogglePkMode();
                    break;
                case "newPk":
                    _monitor.StartNewPk();
                    break;
                case "restartLocalAsr":
                    _monitor.RestartLocalAsrServer();
                    break;
                case "getConfig":
                    PushConfig();
                    break;
                case "patchConfig":
                    if (data.ValueKind == JsonValueKind.Object)
                        _config.ApplyWebPatch(data);
                    PushConfigMeta();
                    break;
                case "saveConfig":
                    if (data.ValueKind == JsonValueKind.Object)
                        _config.ApplyWebPatch(data);
                    await _config.SaveAsync();
                    PushConfig();
                    PushResult("save", _config.SaveStateText, !_config.HasValidationErrors);
                    break;
                case "discardConfig":
                    _config.DiscardChanges();
                    PushConfig();
                    PushResult("discard", _config.SaveStateText, true);
                    break;
                case "refreshLoopback":
                    await _config.RefreshLoopbackSourcesAsync();
                    PushConfig();
                    break;
                case "refreshOutputs":
                    _config.RefreshOutputDevices();
                    PushConfig();
                    break;
                case "queryVtsHotkeys":
                    await _config.QueryVtsHotkeysAsync();
                    PushConfig();
                    PushResult("hotkeys", _config.Status, true);
                    break;
                case "addEmotion":
                    _config.AddEmotionRow();
                    PushConfig();
                    break;
                case "addAction":
                    _config.AddActionRow();
                    PushConfig();
                    break;
                case "importAnimations":
                    _config.ImportAnimationHotkeys();
                    PushConfig();
                    break;
                case "removeEmotion":
                    if (TryIndex(data, out var ei) && ei >= 0 && ei < _config.EmotionRows.Count)
                        _config.RemoveEmotionRow(_config.EmotionRows[ei]);
                    PushConfig();
                    break;
                case "removeAction":
                    if (TryIndex(data, out var ai) && ai >= 0 && ai < _config.ActionRows.Count)
                        _config.RemoveActionRow(_config.ActionRows[ai]);
                    PushConfig();
                    break;
                case "getMemory":
                    await _memory.LoadAsync();
                    PushMemory();
                    break;
                case "refreshMemory":
                    await _memory.LoadAsync();
                    PushMemory();
                    break;
                case "setFactSearch":
                    _memory.FactSearch = ReadString(data, "query") ?? ReadString(data) ?? "";
                    break;
                case "setViewerSearch":
                    _memory.ViewerSearch = ReadString(data, "query") ?? ReadString(data) ?? "";
                    break;
                case "memoryTab":
                {
                    var tab = ReadString(data, "tab") ?? ReadString(data) ?? "facts";
                    await _memory.ActivateTabAsync(tab == "viewers" ? MemoryTab.Viewers : MemoryTab.Facts);
                    PushMemory();
                    break;
                }
                case "deleteFact":
                {
                    var id = ReadString(data, "id") ?? ReadString(data);
                    if (!string.IsNullOrEmpty(id))
                        await _memory.DeleteFactByIdAsync(id);
                    PushMemory();
                    break;
                }
                case "extractMemory":
                    await _memory.ForceExtractAsync();
                    PushMemory();
                    PushResult("extract", _memory.StatusMessage, !_memory.Extracting);
                    break;
            }
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[WebConsole] 消息处理失败: {ex.Message}");
            PushResult("error", ex.Message, false);
        }
    }

    private static bool TryIndex(JsonElement data, out int index)
    {
        index = -1;
        if (data.ValueKind == JsonValueKind.Number && data.TryGetInt32(out index)) return true;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("index", out var i))
            return i.TryGetInt32(out index);
        return false;
    }

    private static string? ReadString(JsonElement data, string? prop = null)
    {
        if (data.ValueKind == JsonValueKind.String) return data.GetString();
        if (prop is not null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty(prop, out var el))
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        return null;
    }

    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || !_ready) return;
        if (e.PropertyName is nameof(MonitorViewModel.MicLevel) or nameof(MonitorViewModel.LoopbackLevel))
        {
            var now = Environment.TickCount64;
            if (now - _lastLevelPushMs < LevelThrottleMs) return;
            _lastLevelPushMs = now;
        }
        PushMonitorState();
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || !_ready) return;
        if (e.PropertyName is nameof(ConfigViewModel.SaveStateText)
            or nameof(ConfigViewModel.Status)
            or nameof(ConfigViewModel.IsDirty)
            or nameof(ConfigViewModel.IsSaving)
            or nameof(ConfigViewModel.ValidationMessage))
            PushConfigMeta();
    }

    private void OnMemoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || !_ready) return;
        if (e.PropertyName is nameof(MemoryViewModel.FactsLoading)
            or nameof(MemoryViewModel.ViewersLoading)
            or nameof(MemoryViewModel.FactsEmpty)
            or nameof(MemoryViewModel.ViewersEmpty)
            or nameof(MemoryViewModel.FactsError)
            or nameof(MemoryViewModel.ViewersError)
            or nameof(MemoryViewModel.Extracting)
            or nameof(MemoryViewModel.StatusMessage)
            or nameof(MemoryViewModel.FactSearch)
            or nameof(MemoryViewModel.ViewerSearch))
            PushMemory();
    }

    public void PushMonitorState()
    {
        if (_disposed || !_ready || _webView.CoreWebView2 is null) return;
        try
        {
            var payload = new
            {
                type = "state",
                data = new
                {
                    state = _monitor.State.ToString(),
                    stateLabel = StateLabel(_monitor.State),
                    canStop = _monitor.CanStop,
                    isPkMode = _monitor.IsPkMode,
                    micMuted = _monitor.MicMuted,
                    micLevel = _monitor.MicLevel,
                    loopbackLevel = _monitor.LoopbackLevel,
                    userText = _monitor.UserText,
                    assistantText = _monitor.AssistantText,
                    opponentText = _monitor.OpponentText,
                    emotion = _monitor.Emotion,
                    userEmotion = _monitor.UserEmotion,
                    lastError = _monitor.LastError,
                    vtsConnected = _monitor.VtsConnected,
                    obsConnected = _monitor.ObsConnected,
                    danmakuActive = _monitor.DanmakuActive,
                    localAsrActive = _monitor.LocalAsrActive,
                    localAsrReachable = _monitor.LocalAsrReachable,
                    asrLatencyMs = _monitor.AsrLatencyMs,
                    llmLatencyMs = _monitor.LlmLatencyMs,
                    ttsLatencyMs = _monitor.TtsLatencyMs,
                    danmakuQueueCount = _monitor.DanmakuQueueCount,
                    events = _monitor.OperationalEvents.Take(80).Select(ev => new
                    {
                        time = ev.Timestamp.ToString("HH:mm:ss"),
                        source = ev.Source,
                        message = ev.Message,
                        isError = ev.IsError,
                    }).ToList(),
                },
            };
            Post(payload);
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[WebConsole] 推送状态失败: {ex.Message}");
        }
    }

    public void PushConfig()
    {
        if (_disposed || !_ready || _webView.CoreWebView2 is null) return;
        try
        {
            Post(new { type = "config", data = _config.BuildWebDraft() });
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[WebConsole] 推送配置失败: {ex.Message}");
        }
    }

    public void PushMemory()
    {
        if (_disposed || !_ready || _webView.CoreWebView2 is null) return;
        try
        {
            Post(new { type = "memory", data = _memory.BuildWebSnapshot() });
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[WebConsole] 推送记忆失败: {ex.Message}");
        }
    }

    private void PushConfigMeta()
    {
        if (_disposed || !_ready || _webView.CoreWebView2 is null) return;
        try
        {
            Post(new
            {
                type = "result",
                data = new
                {
                    kind = "meta",
                    saveStateText = _config.SaveStateText,
                    status = _config.Status,
                    isDirty = _config.IsDirty,
                    isSaving = _config.IsSaving,
                    validationMessage = _config.ValidationMessage,
                    ok = !_config.HasValidationErrors,
                },
            });
        }
        catch { /* ignore */ }
    }

    private void PushResult(string kind, string message, bool ok)
    {
        if (_disposed || !_ready || _webView.CoreWebView2 is null) return;
        Post(new { type = "result", data = new { kind, message, ok, saveStateText = _config.SaveStateText, status = _config.Status } });
    }

    private void Post(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _webView.Dispatcher.BeginInvoke(() =>
        {
            try { _webView.CoreWebView2?.PostWebMessageAsJson(json); }
            catch { /* disposed */ }
        });
    }

    private static string StateLabel(AIVTuber.Core.Runtime.PipelineState state) => state switch
    {
        AIVTuber.Core.Runtime.PipelineState.Listening => "正在监听",
        AIVTuber.Core.Runtime.PipelineState.Thinking => "正在思考",
        AIVTuber.Core.Runtime.PipelineState.Speaking => "正在说话",
        _ => "空闲",
    };

    public static string? ResolveWwwroot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "WebUi", "wwwroot"),
            Path.Combine(AIVTuber.Core.AppPaths.ContentRoot, "WebUi", "wwwroot"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(Path.Combine(path, "index.html")))
                return path;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.PropertyChanged -= OnMonitorPropertyChanged;
        _monitor.OperationalEvents.CollectionChanged -= _eventsChanged;
        _config.PropertyChanged -= OnConfigPropertyChanged;
        _memory.PropertyChanged -= OnMemoryPropertyChanged;
        _memory.Facts.CollectionChanged -= _memoryFactsChanged;
        _memory.Viewers.CollectionChanged -= _memoryViewersChanged;
        if (_webView.CoreWebView2 is not null)
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
    }
}
