using System.Text;
using System.Text.Json;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Core.RealtimeTts;

/// <summary>
/// MiniMax 双向 TTS WebSocket 协议消息构造与解析（RT-06）。
/// 协议事件语义来自官方 /ws/v1/t2a_v2_bidi 文档描述（计划 [D2]）：
/// task_start 建任务、task_continue 投递已批准文本、task_flush 回合尾部冲刷、
/// task_cancel 打断并等确认、task_finish 正常收尾。
/// 【未实测】真实端点字段（task_id 是否必填、audio 载荷位置、ack 事件名）未用真实 Key 验证；
/// 本文件固定的字段形态以 fake-transport fixture 锁定，接入真实账号时必须先对拍，
/// 不一致处改 fixture + 本解析器，不得靠猜补齐。
/// </summary>
public static class TtsBidiProtocol
{
    /// <summary>官方文档路径；主机名是账号区域相关的配置项，不在此猜测（计划 §2.1 传输入口备忘）。</summary>
    public const string DefaultPath = "/ws/v1/t2a_v2_bidi";

    // client → server 事件名
    public const string ClientTaskStart = "task_start";
    public const string ClientTaskContinue = "task_continue";
    public const string ClientTaskFlush = "task_flush";
    public const string ClientTaskCancel = "task_cancel";
    public const string ClientTaskFinish = "task_finish";
    /// <summary>应用层空闲保活。默认关闭（BidiKeepAliveIntervalMs=0）：真实服务端是否接受该事件未验证。</summary>
    public const string ClientKeepAlive = "keepalive";

    // server → client 事件名（fixture 锁定；未实测）
    public const string ServerTaskStarted = "task_started";
    public const string ServerAudio = "audio";
    public const string ServerTaskFlushed = "task_flushed";
    public const string ServerTaskCanceled = "task_canceled";
    public const string ServerTaskFinished = "task_finished";
    public const string ServerTaskFailed = "task_failed";
    public const string ServerKeepAlive = "keepalive";

    /// <summary>bidi 主机缺失时的明确报错与回退说明（不静默切换传输）。</summary>
    public const string MissingHostMessage =
        "tts.transport=\"bidi\" 需要 tts.bidi_host（账号区域对应的 MiniMax 官方 WSS 主机名）。" +
        "计划未猜测国内/国际主机名；请按账号所属区域查阅官方文档配置。" +
        "如需回退 HTTP 流式，请显式设置 tts.transport=\"streaming\"（不会静默切换）。";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string BuildTaskStart(
        string taskId, string model, string voiceId, double speed, int sampleRate, string? emotion)
    {
        object voiceSetting = emotion is null
            ? new { voice_id = voiceId, speed, vol = 1.0, pitch = 0 }
            : new { voice_id = voiceId, speed, vol = 1.0, pitch = 0, emotion };
        return JsonSerializer.Serialize(new
        {
            type = ClientTaskStart,
            task_id = taskId,
            model,
            voice_setting = voiceSetting,
            audio_setting = new { sample_rate = sampleRate, format = "pcm", channel = 1 },
        }, JsonOptions);
    }

    public static string BuildTaskContinue(string taskId, string text) =>
        JsonSerializer.Serialize(new { type = ClientTaskContinue, task_id = taskId, text }, JsonOptions);

    public static string BuildTaskFlush(string taskId) =>
        JsonSerializer.Serialize(new { type = ClientTaskFlush, task_id = taskId }, JsonOptions);

    public static string BuildTaskCancel(string taskId) =>
        JsonSerializer.Serialize(new { type = ClientTaskCancel, task_id = taskId }, JsonOptions);

    public static string BuildTaskFinish(string taskId) =>
        JsonSerializer.Serialize(new { type = ClientTaskFinish, task_id = taskId }, JsonOptions);

    public static string BuildKeepAlive() =>
        JsonSerializer.Serialize(new { type = ClientKeepAlive }, JsonOptions);

    /// <summary>Builds the WSS URI for the account-region host. Host must be configured — never guessed.</summary>
    public static Uri BuildUri(string host, string path = DefaultPath)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException(MissingHostMessage);
        return new Uri($"wss://{host.Trim().TrimEnd('/')}/{path.TrimStart('/')}");
    }

    /// <summary>Defensive parse of one server message. Unknown shapes parse to Ignored, never throw.</summary>
    public static bool TryParseServer(ReadOnlyMemory<byte> utf8, out TtsBidiServerMessage message)
    {
        message = default;
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            return TryParse(doc.RootElement, out message);
        }
        catch (JsonException)
        {
            return false; // 半包/非 JSON：丢弃，不断会话
        }
    }

    internal static bool TryParse(JsonElement root, out TtsBidiServerMessage message)
    {
        message = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeEl) ||
            typeEl.GetString() is not { } type)
            return false;

        message = new TtsBidiServerMessage { Kind = TtsBidiServerMessageKind.Ignored };
        if (root.TryGetProperty("task_id", out var taskEl))
            message.TaskId = taskEl.GetString();

        switch (type)
        {
            case ServerTaskStarted:
                message.Kind = TtsBidiServerMessageKind.TaskStarted;
                return true;

            case ServerAudio:
                message.Kind = TtsBidiServerMessageKind.Audio;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("audio", out var audioEl) && audioEl.GetString() is { Length: > 0 } encoded)
                        message.Audio = DecodeAudioString(encoded);
                    if (data.TryGetProperty("audio_format", out var fmtEl) && fmtEl.GetString() is { Length: > 0 } fmt)
                        message.AudioFormat = fmt;
                    if (data.TryGetProperty("is_final", out var finEl) && finEl.ValueKind == JsonValueKind.True)
                        message.IsFinal = true;
                }
                return true;

            case ServerTaskFlushed:
                message.Kind = TtsBidiServerMessageKind.TaskFlushed;
                return true;

            case ServerTaskCanceled:
            case "task_cancelled": // 英式拼写防御，未实测
                message.Kind = TtsBidiServerMessageKind.TaskCanceled;
                return true;

            case ServerTaskFinished:
                message.Kind = TtsBidiServerMessageKind.TaskFinished;
                return true;

            case ServerTaskFailed:
                message.Kind = TtsBidiServerMessageKind.TaskFailed;
                if (root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var code))
                    message.ErrorCode = code;
                if (root.TryGetProperty("message", out var msgEl))
                    message.ErrorMessage = msgEl.GetString();
                return true;

            case ServerKeepAlive:
                message.Kind = TtsBidiServerMessageKind.KeepAlive;
                return true;

            default:
                return true; // parsed but ignored
        }
    }

    /// <summary>MiniMax 音频字符串：文档为 hex；非合法 hex 时回退 base64（与 RT-01 相同策略）。</summary>
    internal static byte[] DecodeAudioString(string s)
    {
        try
        {
            return Convert.FromHexString(s);
        }
        catch (FormatException)
        {
            try
            {
                return Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                return [];
            }
        }
    }
}

public enum TtsBidiServerMessageKind
{
    Ignored,
    TaskStarted,
    Audio,
    TaskFlushed,
    TaskCanceled,
    TaskFinished,
    TaskFailed,
    KeepAlive,
}

/// <summary>解析后的服务端消息（可变字段，接收循环内本地使用）。</summary>
public struct TtsBidiServerMessage
{
    public TtsBidiServerMessageKind Kind { get; set; }
    public byte[]? Audio { get; set; }
    public string? AudioFormat { get; set; }
    public bool IsFinal { get; set; }
    public int ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TaskId { get; set; }
}

/// <summary>创建可注入的 WebSocket 传输（连接 epoch 重建时每次新建一个实例）。</summary>
public interface IWebSocketTransportFactory
{
    IWebSocketTransport Create();
}

/// <summary>真实网络传输工厂。未实测：本改造所有测试均在 fake transport 上运行。</summary>
public sealed class ClientWebSocketTransportFactory : IWebSocketTransportFactory
{
    public IWebSocketTransport Create() => new ClientWebSocketTransport();
}
