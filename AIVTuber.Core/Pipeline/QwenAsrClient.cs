using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// DashScope Qwen-ASR over WebSocket (OpenAI Realtime-compatible protocol).
/// Endpoint: wss://dashscope.aliyuncs.com/api-ws/v1/realtime?model={model}
/// Returns transcript + 7-class emotion from conversation.item.input_audio_transcription.completed.
/// </summary>
public sealed class QwenAsrClient : IAsrClient
{
    private const string RealtimeUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/realtime";
    private const int ChunkSize = 3200; // 100 ms @ 16 kHz/16-bit/mono
    private readonly AsrConfig _config;

    public QwenAsrClient(AsrConfig config) => _config = config;

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "qwen3-asr-flash-realtime" : _config.Model;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");
        ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        await ws.ConnectAsync(new Uri($"{RealtimeUrl}?model={Uri.EscapeDataString(model)}"), cancellationToken);

        string transcript = "";
        string? emotion = null;
        bool audioSent = false;

        while (ws.State == WebSocketState.Open)
        {
            var msg = await DashScopeSocket.ReceiveMessageAsync(ws, cancellationToken);
            if (msg is null) break;
            if (msg.Value.Type != WebSocketMessageType.Text) continue;

            using var doc = JsonDocument.Parse(msg.Value.Bytes);
            var type = doc.RootElement.GetOptionalString("type");

            switch (type)
            {
                case "session.created":
                    await DashScopeSocket.SendTextAsync(ws, BuildSessionUpdate(), cancellationToken);
                    break;

                case "session.updated":
                    if (!audioSent)
                    {
                        audioSent = true;
                        await SendBase64AudioAsync(ws, pcm16k, cancellationToken);
                        await DashScopeSocket.SendTextAsync(ws,
                            """{"event_id":"finish","type":"session.finish"}""", cancellationToken);
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    transcript = doc.RootElement.GetOptionalString("transcript") ?? "";
                    emotion = doc.RootElement.GetOptionalString("emotion");
                    break;

                case "session.finished":
                    await DashScopeSocket.CloseAsync(ws);
                    return new AsrResult(transcript, NormalizeEmotion(emotion));

                case "error":
                    var errMsg = doc.RootElement.TryGetProperty("error", out var ep)
                        ? ep.GetOptionalString("message") : null;
                    await DashScopeSocket.CloseAsync(ws);
                    throw new InvalidOperationException($"Qwen-ASR error: {errMsg}");
            }
        }

        return new AsrResult(transcript, NormalizeEmotion(emotion));
    }

    public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buf = new List<byte>();
        await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
            buf.AddRange(chunk);
        if (buf.Count > 0)
        {
            var r = await RecognizeAsync(buf.ToArray(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(r.Text)) yield return r;
        }
    }

    private static async Task SendBase64AudioAsync(ClientWebSocket ws, byte[] pcm, CancellationToken ct)
    {
        for (int offset = 0; offset < pcm.Length; offset += ChunkSize)
        {
            var len = Math.Min(ChunkSize, pcm.Length - offset);
            var b64 = Convert.ToBase64String(pcm, offset, len);
            // Build JSON manually to avoid serializer overhead in a tight loop
            var json = $"{{\"event_id\":\"a{offset}\",\"type\":\"input_audio_buffer.append\",\"audio\":\"{b64}\"}}";
            await DashScopeSocket.SendTextAsync(ws, json, ct);
        }
    }

    // "neutral" emotion is noise; don't annotate it so the LLM context stays clean.
    private static string? NormalizeEmotion(string? emotion)
        => emotion is null or "neutral" ? null : emotion;

    private static string BuildSessionUpdate() =>
        // turn_detection must be explicitly null (not omitted) to disable server-side VAD.
        """
        {
            "event_id": "session_update",
            "type": "session.update",
            "session": {
                "modalities": ["text"],
                "input_audio_format": "pcm",
                "sample_rate": 16000,
                "turn_detection": null
            }
        }
        """;
}

file static class JsonElementExt
{
    internal static string? GetOptionalString(this JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
