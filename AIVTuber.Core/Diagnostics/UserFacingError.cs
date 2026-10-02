using System.Net;

namespace AIVTuber.Core.Diagnostics;

/// <summary>
/// What a streamer sees when something did not work (U06): the problem, what it affects and
/// what they can do, plus a diagnostic id that points at the redacted technical detail in the
/// log. Raw exception text, response bodies and stack traces never go into these fields.
/// </summary>
public sealed record UserFacingError(
    string Code,
    string Area,
    string UserMessage,
    string SuggestedAction,
    bool Retryable,
    string DiagnosticId);

/// <summary>Areas used for <see cref="UserFacingError.Area"/>.</summary>
public static class ErrorArea
{
    public const string Account = "account";
    public const string Speech = "speech";      // speech recognition
    public const string Voice = "voice";        // voice synthesis / preview
    public const string VoiceList = "voiceList";
    public const string Model = "model";        // LLM
    public const string Device = "device";
    public const string Avatar = "avatar";
    public const string Obs = "obs";
    public const string Settings = "settings";
    public const string App = "app";
}

/// <summary>One redacted technical record, kept in memory for the help page's diagnostic export.</summary>
public sealed record DiagnosticEntry(string Id, DateTimeOffset At, string Area, string Detail);

/// <summary>Bounded in-memory list of recent diagnostics (already redacted).</summary>
public static class DiagnosticJournal
{
    private const int Capacity = 50;
    private static readonly object Sync = new();
    private static readonly LinkedList<DiagnosticEntry> Entries = new();

    public static void Record(string id, string area, string detail)
    {
        var entry = new DiagnosticEntry(id, DateTimeOffset.Now, area, DiagnosticRedactor.Redact(detail));
        lock (Sync)
        {
            Entries.AddLast(entry);
            while (Entries.Count > Capacity) Entries.RemoveFirst();
        }
        DebugLog.Write($"[诊断 {id}] {area}: {entry.Detail}");
    }

    public static IReadOnlyList<DiagnosticEntry> Recent()
    {
        lock (Sync) return Entries.ToList();
    }
}

/// <summary>
/// The single place that turns exceptions and pipeline error strings into
/// <see cref="UserFacingError"/>. Status codes alone are not over-interpreted: only a 401 is
/// treated as "credentials unusable"; 403/429 and anything unrecognised stay "unknown" with a
/// diagnostic id rather than guessing at balance or bans.
/// </summary>
public static class UserErrorMapper
{
    public const string UnknownMessage = "操作未完成，请重试；若仍失败请联系支持。";

    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, UserFacingError> PipelineCache = new(StringComparer.Ordinal);

    public static string NewDiagnosticId() =>
        $"D{DateTime.Now:MMddHHmm}-{Random.Shared.Next(0x1000, 0xFFFF):X4}";

    /// <summary>Maps an exception. Returns null for a normal cancellation (user pressed stop,
    /// preview replaced, page closed) — that is not an error to show.</summary>
    public static UserFacingError? FromException(Exception ex, string area)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (IsTimeout(ex))
            return Record(ex, area, "timeout", TimeoutMessage(area), "稍后重试", retryable: true);
        if (ex is OperationCanceledException) return null;
        if (FindHttpStatus(ex) == HttpStatusCode.Unauthorized)
            return Record(ex, area, "credentials_unavailable", $"{ServiceName(area)}暂时不可用，需要管理员处理。",
                "联系支持并提供诊断编号", retryable: false);
        if (area == ErrorArea.Device)
            return Record(ex, area, "device_unavailable", "当前音频设备不可用，请重新选择设备。", "选择设备或刷新", retryable: true);
        if (area == ErrorArea.VoiceList)
            return Record(ex, area, "voice_list_unavailable", "暂时无法加载音色列表，已保留当前音色。", "重试加载", retryable: true);
        return Record(ex, area, "unknown", UnknownMessage, "重试；若仍失败请联系支持并提供诊断编号", retryable: true);
    }

    /// <summary>Maps the runtime's "[Area] detail" error strings. The same message maps to the
    /// same diagnostic id, so a repeating error is shown and logged once.</summary>
    public static UserFacingError FromPipelineMessage(string message)
    {
        lock (CacheSync)
            if (PipelineCache.TryGetValue(message, out var cached)) return cached;

        var (area, code, text, action) = Classify(message);
        var id = NewDiagnosticId();
        DiagnosticJournal.Record(id, area, message);
        var error = new UserFacingError(code, area, text, action, Retryable: true, id);
        lock (CacheSync)
        {
            if (PipelineCache.Count > 64) PipelineCache.Clear();
            PipelineCache[message] = error;
        }
        return error;
    }

    public static UserFacingError Custom(string code, string area, string userMessage, string action, bool retryable,
        string? technicalDetail = null)
    {
        var id = NewDiagnosticId();
        DiagnosticJournal.Record(id, area, technicalDetail ?? code);
        return new UserFacingError(code, area, userMessage, action, retryable, id);
    }

    private static (string Area, string Code, string Text, string Action) Classify(string m)
    {
        if (StartsWithAny(m, "[VTS]", "[Cortico]", "[Avatar]"))
            return (ErrorArea.Avatar, "avatar_unavailable", "形象连接暂时不可用，陪播语音不受影响。", "需要形象时到直播设置检查连接");
        if (StartsWithAny(m, "[OBS"))
            return (ErrorArea.Obs, "obs_unavailable", "OBS 字幕暂时没连上，陪播语音不受影响。", "需要字幕时到直播设置检查 OBS");
        if (StartsWithAny(m, "[ASR/Pipeline]", "[回合]", "[LLM"))
            return (ErrorArea.Model, "turn_failed", "这一轮回复没有完成，陪播会继续听。", "如果反复出现，请联系支持并提供诊断编号");
        if (StartsWithAny(m, "[ASR", "[Local ASR]", "[麦克风"))
            return (ErrorArea.Speech, "speech_unavailable", "语音识别暂时不可用，下一句会自动重试。", "检查网络；若持续出现请联系支持");
        if (StartsWithAny(m, "[TTS", "[语音"))
            return (ErrorArea.Voice, "voice_failed", "这次声音生成暂时没完成，请稍后重试。", "稍后重试");
        return (ErrorArea.App, "unknown", UnknownMessage, "重试；若仍失败请联系支持并提供诊断编号");
    }

    private static bool StartsWithAny(string s, params string[] prefixes) =>
        prefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static UserFacingError Record(Exception ex, string area, string code, string message, string action, bool retryable)
    {
        var id = NewDiagnosticId();
        DiagnosticJournal.Record(id, area, $"{code}: {ex}");
        return new UserFacingError(code, area, message, action, retryable, id);
    }

    private static bool IsTimeout(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is TimeoutException) return true;
        return false;
    }

    private static HttpStatusCode? FindHttpStatus(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException { StatusCode: { } code }) return code;
        return null;
    }

    private static string TimeoutMessage(string area) => area switch
    {
        ErrorArea.Voice => "这次声音生成暂时没完成，请稍后重试。",
        ErrorArea.VoiceList => "暂时无法加载音色列表，已保留当前音色。",
        ErrorArea.Account => "暂时连不上登录服务，请检查网络后重试。",
        _ => "请求暂时没有完成，请稍后重试。",
    };

    private static string ServiceName(string area) => area switch
    {
        ErrorArea.Voice or ErrorArea.VoiceList => "语音服务",
        ErrorArea.Speech => "语音识别服务",
        ErrorArea.Model => "对话服务",
        _ => "云端服务",
    };
}
