using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.Voice;

namespace AIVTuber.Core.ViewModels;

/// <summary>
/// Everything the streamer page (distribution builds) may ask the app to do, in one UI-agnostic
/// place (U01/U06/U08, V01–V03). The WebView host only forwards messages and posts the payloads
/// this class builds; it never falls back to the developer console's commands.
/// <para>Only the commands in <see cref="Commands"/> exist here. Developer-console commands
/// (patchConfig, saveConfig, restartLocalAsr, VTS editing, …) are refused, so a crafted
/// message cannot reach the full configuration.</para>
/// </summary>
public sealed class StreamerConsoleController : IDisposable
{
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "getState", "getSettings",
        "pauseCompanion", "resumeCompanion", "stopSpeaking", "toggleMic", "togglePk", "newPk",
        "saveSettings", "discardSettings", "refreshDevices",
        "refreshVoices", "previewVoice", "stopPreview",
        "signIn", "signOut", "copyDiagnostics", "dismissIssue",
    };

    private readonly BotRuntime _runtime;
    private readonly MonitorViewModel _monitor;
    private readonly ConfigViewModel _config;
    private readonly AccountViewModel? _account;
    private readonly VoicePreviewService _preview;
    private readonly VoiceCatalogService _catalog;
    private readonly Action<object> _post;
    private readonly Action<string> _copyToClipboard;
    private readonly string _versionText;
    private readonly List<UserFacingError> _extraIssues = [];
    private readonly object _issuesSync = new();
    private string _dismissedError = "";
    private UserFacingError? _voiceListError;

    public StreamerConsoleController(
        BotRuntime runtime,
        MonitorViewModel monitor,
        ConfigViewModel config,
        AccountViewModel? account,
        VoicePreviewService preview,
        VoiceCatalogService catalog,
        Action<object> post,
        Action<string> copyToClipboard,
        string versionText)
    {
        _runtime = runtime;
        _monitor = monitor;
        _config = config;
        _account = account;
        _preview = preview;
        _catalog = catalog;
        _post = post;
        _copyToClipboard = copyToClipboard;
        _versionText = versionText;

        _runtime.AsrHealthChanged += OnRuntimeChanged;
        _runtime.CompanionPausedChanged += OnRuntimeChangedPlain;
        _runtime.CloudAccessRevoked += OnRevoked;
        if (_account is not null) _account.PropertyChanged += OnAccountChanged;
        _preview.StatusChanged += OnPreviewStatus;
    }

    /// <summary>Raised when the home state should be re-pushed (the host throttles it).</summary>
    public event EventHandler? StateInvalidated;

    public bool Handles(string? name) => name is not null && Commands.Contains(name);

    public async Task HandleAsync(string? name, JsonElement data)
    {
        if (!Handles(name))
        {
            // Not an error for the streamer; a developer command reached the streamer page.
            DebugLog.Write($"[主播界面] 拒绝未开放的命令: {name}");
            _post(new { type = "result", data = new { kind = "rejected", command = name ?? "", ok = false } });
            return;
        }

        switch (name)
        {
            case "getState": PushState(); break;
            case "getSettings": PushSettings(); break;
            case "pauseCompanion": _runtime.SetCompanionPaused(true); PushState(); break;
            case "resumeCompanion": _runtime.SetCompanionPaused(false); PushState(); break;
            case "stopSpeaking": _monitor.StopSpeaking(); break;
            case "toggleMic": _monitor.ToggleMicMute(); break;
            case "togglePk": _monitor.TogglePkMode(); break;
            case "newPk": _monitor.StartNewPk(); break;
            case "saveSettings": await SaveAsync(data).ConfigureAwait(true); break;
            case "discardSettings":
                _config.DiscardChanges();
                PushSettings();
                break;
            case "refreshDevices":
                await _config.RefreshLoopbackSourcesAsync().ConfigureAwait(true);
                _config.RefreshOutputDevices();
                PushSettings();
                break;
            case "refreshVoices": await RefreshVoicesAsync().ConfigureAwait(true); break;
            case "previewVoice":
                _ = RunPreviewAsync(ReadString(data, "choiceId") ?? "");
                break;
            case "stopPreview": _preview.Stop(); break;
            case "signIn": _account?.ReopenLogin(); break;
            case "signOut":
                _preview.Stop();
                if (_account is not null) await _account.LogoutAsync().ConfigureAwait(true);
                break;
            case "copyDiagnostics":
                _copyToClipboard(BuildDiagnostics());
                _post(new { type = "result", data = new { kind = "diagnostics", ok = true, message = "诊断信息已复制（已去除密钥和个人凭据）" } });
                break;
            case "dismissIssue":
                _dismissedError = _monitor.LastError;
                lock (_issuesSync) _extraIssues.Clear();
                _voiceListError = null;
                PushState();
                break;
        }
    }

    /// <summary>Records a mapped error for display on the home page (e.g. a failed command).</summary>
    public void ReportIssue(UserFacingError error)
    {
        lock (_issuesSync)
        {
            _extraIssues.RemoveAll(e => e.Code == error.Code && e.Area == error.Area);
            _extraIssues.Add(error);
            if (_extraIssues.Count > 5) _extraIssues.RemoveAt(0);
        }
        StateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAsync(JsonElement data)
    {
        var requestId = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("requestId", out var r) &&
                        r.TryGetInt64(out var rid) ? rid : 0;
        var patch = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("patch", out var p) ? p : default;
        var result = await _config.SaveStreamerAsync(patch).ConfigureAwait(true);
        _post(new
        {
            type = "saveResult",
            data = new
            {
                requestId,
                ok = result.Ok,
                applied = result.Applied,
                stateText = result.StateText,
                message = result.Message,
                effectiveRevision = result.EffectiveRevision,
                rejected = result.Rejected,
            },
        });
        PushSettings();
        PushState();
    }

    private async Task RefreshVoicesAsync()
    {
        var result = await _catalog.RefreshAsync(_config).ConfigureAwait(true);
        _voiceListError = result.Error;
        PushSettings();
        _post(new
        {
            type = "result",
            data = new
            {
                kind = "voices",
                ok = result.Error is null,
                message = result.Error?.UserMessage ?? (result.Checked ? "音色列表已更新" : "已显示当前可选音色"),
                diagnosticId = result.Error?.DiagnosticId,
            },
        });
    }

    private async Task RunPreviewAsync(string choiceId)
    {
        try { await _preview.PreviewAsync(choiceId).ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (UserErrorMapper.FromException(ex, ErrorArea.Voice) is { } error)
                _post(new { type = "preview", data = new { requestId = 0L, choiceId, state = "failed", message = error.UserMessage, diagnosticId = error.DiagnosticId } });
        }
    }

    public void PushState() => _post(new { type = "state", data = BuildState() });

    public void PushSettings() => _post(new { type = "settings", data = _config.BuildStreamerDraft() });

    public object BuildState()
    {
        var signedIn = _account?.IsSignedIn ?? true;
        var paused = _runtime.CompanionPaused;
        var health = _runtime.CurrentAsrHealth;
        var config = _runtime.CurrentConfig;
        var effectiveVoice = _config.EffectiveVoice;
        return new
        {
            companion = new
            {
                signedIn,
                paused,
                running = signedIn && !paused,
                activity = !signedIn ? "未登录" : paused ? "已暂停" : ActivityLabel(_monitor.State),
                canStop = _monitor.CanStop,
            },
            listening = new
            {
                micEnabled = !_monitor.MicMuted,
                micLevel = _monitor.MicLevel,
                micName = config.Audio.InputDeviceIndex >= 0 && config.Audio.InputDeviceIndex < _config.InputDevices.Count
                    ? _config.InputDevices[config.Audio.InputDeviceIndex] : "",
                loopbackEnabled = config.Audio.EnableLoopbackListen,
                loopbackLevel = _monitor.LoopbackLevel,
                isPkMode = _monitor.IsPkMode,
                pkOpponent = _monitor.PkOpponentSummary,
            },
            speech = new { health = health.ToString().ToLowerInvariant(), label = AsrLabel(health) },
            voice = new { name = effectiveVoice?.Name ?? "默认音色" },
            services = new
            {
                danmaku = config.Bilibili.Enable ? (_monitor.DanmakuActive ? "connected" : "connecting") : "off",
                obs = config.Obs.Enable ? (_monitor.ObsConnected ? "connected" : "notConnected") : "off",
                avatar = config.Avatar.UsesVts ? (_monitor.VtsConnected ? "connected" : "notConnected") : "off",
            },
            conversation = _monitor.OperationalEvents
                .Where(e => e.Source is "输入" or "回复" or "内录")
                .Take(12)
                .Select(e => new
                {
                    time = e.Timestamp.ToString("HH:mm:ss"),
                    who = e.Source switch { "回复" => "ai", "内录" => "opponent", _ => "input" },
                    text = e.Message,
                })
                .ToList(),
            issues = BuildIssues(signedIn),
            account = BuildAccount(),
        };
    }

    private object BuildAccount() => new
    {
        managed = _account is not null,
        signedIn = _account?.IsSignedIn ?? true,
        username = _account?.Username ?? "",
        validUntil = _account?.ValidUntilText ?? "",
        message = _account is { IsSignedIn: false } a ? a.ErrorText : "",
        reason = (_account?.StopReason ?? LicenseStopReason.None).ToString(),
        version = _versionText,
    };

    private List<object> BuildIssues(bool signedIn)
    {
        var issues = new List<object>();
        if (_account is { IsSignedIn: false } account)
        {
            var (text, action) = account.StopReason switch
            {
                LicenseStopReason.AccountExpired => (CloudLicense.AccountEndedMessage, "联系发放者续期"),
                LicenseStopReason.VerificationLost => (CloudLicense.VerificationLostMessage, "检查连接后重新登录"),
                LicenseStopReason.Denied when account.ErrorText.Length > 0 => (account.ErrorText, "联系发放者"),
                _ => ("还没有登录，陪播不会连接任何云端服务。", "登录"),
            };
            issues.Add(new { area = ErrorArea.Account, code = "account_" + account.StopReason.ToString().ToLowerInvariant(), message = text, action, diagnosticId = "" });
        }
        if (signedIn && _monitor.LastError is { Length: > 0 } raw && raw != _dismissedError)
            issues.Add(Shape(UserErrorMapper.FromPipelineMessage(raw)));
        if (_voiceListError is not null) issues.Add(Shape(_voiceListError));
        lock (_issuesSync) issues.AddRange(_extraIssues.Select(Shape));
        return issues;
    }

    private static object Shape(UserFacingError e) =>
        new { area = e.Area, code = e.Code, message = e.UserMessage, action = e.SuggestedAction, diagnosticId = e.DiagnosticId };

    /// <summary>Redacted technical summary for "帮助 → 复制诊断信息". No keys, tokens, cookies,
    /// response bodies or signed URLs.</summary>
    public string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("AIVTuber 诊断信息（已脱敏）");
        sb.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"版本: {_versionText}");
        if (_runtime.Profile is { } profile)
            sb.AppendLine($"专属配置: {profile.ProfileId} · 凭据修订 {profile.CredentialRevision} · 语音厂商 {profile.Providers.Tts.Provider} · 识别厂商 {profile.Providers.Asr.Provider} · 对话厂商 {profile.Providers.Llm.Provider}");
        if (_account is not null)
            sb.AppendLine($"账号: {(_account.IsSignedIn ? "已登录" : "未登录")} · {_account.StopReason} · {_account.ValidUntilText}");
        sb.AppendLine($"陪播: {(_runtime.CompanionPaused ? "已暂停" : "运行")} · 状态 {_monitor.State} · 语音识别 {_runtime.CurrentAsrHealth}");
        sb.AppendLine($"配置修订: 生效 {_runtime.ActiveConfigRevision}");
        sb.AppendLine("最近问题:");
        foreach (var entry in DiagnosticJournal.Recent().TakeLast(20))
            sb.AppendLine($"  [{entry.Id}] {entry.At:HH:mm:ss} {entry.Area}: {Truncate(entry.Detail, 600)}");
        return DiagnosticRedactor.Redact(sb.ToString());
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void OnPreviewStatus(PreviewStatus status) => _post(new
    {
        type = "preview",
        data = new
        {
            requestId = status.RequestId,
            choiceId = status.ChoiceId,
            state = status.State.ToString().ToLowerInvariant(),
            message = status.Message,
            diagnosticId = status.Error?.DiagnosticId,
        },
    });

    private void OnRuntimeChanged(object? sender, AsrHealth _) => StateInvalidated?.Invoke(this, EventArgs.Empty);
    private void OnRuntimeChangedPlain(object? sender, EventArgs _) => StateInvalidated?.Invoke(this, EventArgs.Empty);

    private void OnRevoked(object? sender, string _)
    {
        _preview.Stop();
        StateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.IsSignedIn) && _account is { IsSignedIn: false }) _preview.Stop();
        StateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private static string ActivityLabel(PipelineState state) => state switch
    {
        PipelineState.Listening => "正在听",
        PipelineState.Thinking => "正在想",
        PipelineState.Speaking => "正在说话",
        _ => "空闲",
    };

    public static string AsrLabel(AsrHealth health) => health switch
    {
        AsrHealth.Unknown => "未检测（说句话就会检测）",
        AsrHealth.Ready => "可用",
        AsrHealth.Recognizing => "正在识别",
        AsrHealth.Unavailable => "暂时不可用",
        AsrHealth.Paused => "已暂停",
        _ => "",
    };

    private static string? ReadString(JsonElement data, string prop) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    public void Dispose()
    {
        _runtime.AsrHealthChanged -= OnRuntimeChanged;
        _runtime.CompanionPausedChanged -= OnRuntimeChangedPlain;
        _runtime.CloudAccessRevoked -= OnRevoked;
        if (_account is not null) _account.PropertyChanged -= OnAccountChanged;
        _preview.StatusChanged -= OnPreviewStatus;
        _preview.Dispose();
    }
}
