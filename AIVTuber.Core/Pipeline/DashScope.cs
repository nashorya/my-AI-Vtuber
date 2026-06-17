using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// DashScope (Aliyun Bailian) WebSocket protocol helpers — run-task / continue-task / finish-task
/// framing and event parsing. Pure functions, unit-testable without a socket.
/// Endpoint: wss://dashscope.aliyuncs.com/api-ws/v1/inference/ (Beijing region).
/// Property names are written in the exact snake_case the protocol expects (no naming policy).
/// </summary>
internal static class DashScopeProtocol
{
    public const string InferenceUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/inference/";

    // Relaxed encoder so CJK text is emitted literally (valid UTF-8, matches the API examples)
    // instead of \uXXXX escapes.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string NewTaskId() => Guid.NewGuid().ToString("N");

    /// <summary>run-task for realtime ASR (Fun-ASR / Paraformer), PCM input at the given rate.</summary>
    public static string RunTaskAsr(string taskId, string model, int sampleRate) =>
        JsonSerializer.Serialize(new
        {
            header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
            payload = new
            {
                task_group = "audio",
                task = "asr",
                function = "recognition",
                model,
                parameters = new { format = "pcm", sample_rate = sampleRate },
                input = new { },
            },
        }, Json);

    /// <summary>run-task for realtime TTS (CosyVoice), PCM output at the given rate.
    /// <paramref name="instructions"/> is an optional natural-language style directive (e.g. "用开心的语气说话").
    /// CosyVoice v3+ models support this; older models ignore it.</summary>
    public static string RunTaskTts(string taskId, string model, string voice, int sampleRate, double rate, string? instructions = null) =>
        JsonSerializer.Serialize(new
        {
            header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
            payload = new
            {
                task_group = "audio",
                task = "tts",
                function = "SpeechSynthesizer",
                model,
                parameters = instructions is null
                    ? (object)new
                    {
                        text_type = "PlainText",
                        voice,
                        format = "pcm",
                        sample_rate = sampleRate,
                        volume = 50,
                        rate,
                        pitch = 1,
                        enable_ssml = false,
                    }
                    : new
                    {
                        text_type = "PlainText",
                        voice,
                        format = "pcm",
                        sample_rate = sampleRate,
                        volume = 50,
                        rate,
                        pitch = 1,
                        enable_ssml = false,
                        instructions,
                    },
                input = new { },
            },
        }, Json);

    /// <summary>continue-task carrying TTS text to synthesize.</summary>
    public static string ContinueTask(string taskId, string text) =>
        JsonSerializer.Serialize(new
        {
            header = new { action = "continue-task", task_id = taskId, streaming = "duplex" },
            payload = new { input = new { text } },
        }, Json);

    public static string FinishTask(string taskId) =>
        JsonSerializer.Serialize(new
        {
            header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
            payload = new { input = new { } },
        }, Json);

    /// <summary>Reads header.event and header.error_message from a server text frame.</summary>
    public static (string Event, string? ErrorMessage) ParseEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("header", out var header)) return ("", null);
        var ev = header.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
        var err = header.TryGetProperty("error_message", out var m) ? m.GetString() : null;
        return (ev, err);
    }

    /// <summary>Reads payload.output.sentence (text + sentence_end) from a result-generated frame.
    /// Returns ("", false) when the path is absent.</summary>
    public static (string Text, bool SentenceEnd) ParseAsrSentence(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("payload", out var p) &&
            p.TryGetProperty("output", out var o) &&
            o.TryGetProperty("sentence", out var s))
        {
            var text = s.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var end = s.TryGetProperty("sentence_end", out var se) && se.ValueKind == JsonValueKind.True;
            return (text, end);
        }
        return ("", false);
    }
}

/// <summary>Thin WebSocket send/receive helpers shared by the DashScope ASR/TTS clients.</summary>
internal static class DashScopeSocket
{
    public static Task SendTextAsync(ClientWebSocket ws, string json, CancellationToken ct)
        => ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);

    /// <summary>Sends raw PCM as binary frames of <paramref name="chunkSize"/> bytes.</summary>
    public static async Task SendAudioAsync(ClientWebSocket ws, byte[] pcm, int chunkSize, CancellationToken ct)
    {
        for (int offset = 0; offset < pcm.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, pcm.Length - offset);
            await ws.SendAsync(new ArraySegment<byte>(pcm, offset, len), WebSocketMessageType.Binary, true, ct);
        }
    }

    /// <summary>Receives one full message, accumulating fragments. Returns null when the socket closes.</summary>
    public static async Task<(WebSocketMessageType Type, byte[] Bytes)?> ReceiveMessageAsync(
        ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, ms.ToArray());
    }

    public static async Task CloseAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* ignore close errors */ }
    }
}
