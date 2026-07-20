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
/// Two connection strategies, selected by AsrConfig.PersistConnection (default true: pooled).
/// </summary>
public sealed class QwenAsrClient : IAsrClient, IDisposable
{
    private const string RealtimeUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/realtime";
    private const int ChunkSize = 3200; // 100 ms @ 16 kHz/16-bit/mono
    private readonly AsrConfig _config;
    private readonly DashScopeConnectionPool? _pool;
    private bool _disposed;

    public QwenAsrClient(AsrConfig config, Action<Exception>? onError = null)
    {
        _config = config;
        if (config.PersistConnection)
        {
            var model = string.IsNullOrWhiteSpace(config.Model) ? "qwen3-asr-flash-realtime" : config.Model;
            var endpoint = $"{RealtimeUrl}?model={Uri.EscapeDataString(model)}";
            _pool = new DashScopeConnectionPool(endpoint,
                opts =>
                {
                    opts.SetRequestHeader("Authorization", $"Bearer {config.ApiKey}");
                    opts.SetRequestHeader("OpenAI-Beta", "realtime=v1");
                },
                onError);
        }
    }

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        if (_pool is null)
            return await RecognizeOneShotAsync(pcm16k, cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecognizePooledAsync(pcm16k, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            _pool.Invalidate();
            return await RecognizePooledAsync(pcm16k, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AsrResult> RecognizePooledAsync(byte[] pcm16k, CancellationToken ct)
    {
        var ws = await _pool!.GetOrCreateAsync(ct).ConfigureAwait(false);
        return await RunSessionAsync(ws, () => SendBase64AudioAsync(ws, pcm16k, ct), ct).ConfigureAwait(false);
    }

    private async Task<AsrResult> RecognizeOneShotAsync(byte[] pcm16k, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "qwen3-asr-flash-realtime" : _config.Model;
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");
        ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        await ws.ConnectAsync(new Uri($"{RealtimeUrl}?model={Uri.EscapeDataString(model)}"), ct).ConfigureAwait(false);
        var result = await RunSessionAsync(ws, () => SendBase64AudioAsync(ws, pcm16k, ct), ct).ConfigureAwait(false);
        await DashScopeSocket.CloseAsync(ws);
        return result;
    }

    /// <summary>Runs one full session cycle: session.created -> session.update ->
    /// session.updated -> push audio -> session.finish -> transcription.completed -> session.finished.</summary>
    private static async Task<AsrResult> RunSessionAsync(
        ClientWebSocket ws, Func<Task> sendAudio, CancellationToken ct)
    {
        string transcript = "";
        string? emotion = null;
        var audioSent = false;

        while (ws.State == WebSocketState.Open)
        {
            var msg = await DashScopeSocket.ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
            if (msg is null) throw new WebSocketException("Qwen-ASR socket closed mid-session");
            if (msg.Value.Type != WebSocketMessageType.Text) continue;

            using var doc = JsonDocument.Parse(msg.Value.Bytes);
            var type = doc.RootElement.GetOptionalString("type");

            switch (type)
            {
                case "session.created":
                    await DashScopeSocket.SendTextAsync(ws, BuildSessionUpdate(), ct).ConfigureAwait(false);
                    break;
                case "session.updated":
                    if (!audioSent)
                    {
                        audioSent = true;
                        await sendAudio().ConfigureAwait(false);
                        await DashScopeSocket.SendTextAsync(ws, SessionFinishJson, ct).ConfigureAwait(false);
                    }
                    break;
                case "conversation.item.input_audio_transcription.completed":
                    transcript = doc.RootElement.GetOptionalString("transcript") ?? "";
                    emotion = doc.RootElement.GetOptionalString("emotion");
                    break;
                case "session.finished":
                    return new AsrResult(transcript, NormalizeEmotion(emotion));
                case "error":
                    var errMsg = doc.RootElement.TryGetProperty("error", out var ep)
                        ? ep.GetOptionalString("message") : null;
                    throw new InvalidOperationException($"Qwen-ASR error: {errMsg}");
            }
        }
        throw new WebSocketException("Qwen-ASR socket closed before session.finished");
    }

    public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Qwen Realtime emits one final transcription per session; streaming yields at most one
        // result. The latency win comes from sending audio as it arrives rather than buffered.
        AsrResult? result = null;
        if (_pool is null)
        {
            var buffer = new List<byte>();
            await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
                buffer.AddRange(chunk);
            if (buffer.Count > 0)
                result = await RecognizeOneShotAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                result = await StreamPooledAsync(audioStream, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                _pool.Invalidate();
                result = await StreamPooledAsync(audioStream, cancellationToken).ConfigureAwait(false);
            }
        }

        if (result is { Text: not null } && !string.IsNullOrWhiteSpace(result.Text))
            yield return result;
    }

    private async Task<AsrResult?> StreamPooledAsync(
        IAsyncEnumerable<byte[]> audioStream, CancellationToken ct)
    {
        var ws = await _pool!.GetOrCreateAsync(ct).ConfigureAwait(false);

        string transcript = "";
        string? emotion = null;
        var sessionReady = false;
        var audioDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? senderError = null;

        var sender = Task.Run(async () =>
        {
            try
            {
                await audioDone.Task.ConfigureAwait(false);
                await foreach (var chunk in audioStream.WithCancellation(ct).ConfigureAwait(false))
                    await SendBase64AudioChunkAsync(ws, chunk, ct).ConfigureAwait(false);
                await DashScopeSocket.SendTextAsync(ws, SessionFinishJson, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException wsex) { senderError = wsex; }
            catch (Exception ex) { senderError = ex; }
        });

        WebSocketException? socketError = null;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = await DashScopeSocket.ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                if (msg is null) { socketError = new WebSocketException("Qwen-ASR socket closed mid-stream"); break; }
                if (msg.Value.Type != WebSocketMessageType.Text) continue;

                using var doc = JsonDocument.Parse(msg.Value.Bytes);
                var type = doc.RootElement.GetOptionalString("type");
                if (type == "session.created")
                {
                    await DashScopeSocket.SendTextAsync(ws, BuildSessionUpdate(), ct).ConfigureAwait(false);
                }
                else if (type == "session.updated" && !sessionReady)
                {
                    sessionReady = true;
                    audioDone.TrySetResult();
                }
                else if (type == "conversation.item.input_audio_transcription.completed")
                {
                    transcript = doc.RootElement.GetOptionalString("transcript") ?? "";
                    emotion = doc.RootElement.GetOptionalString("emotion");
                }
                else if (type == "session.finished")
                {
                    break;
                }
                else if (type == "error")
                {
                    audioDone.TrySetCanceled();
                    var errMsg = doc.RootElement.TryGetProperty("error", out var ep)
                        ? ep.GetOptionalString("message") : null;
                    throw new InvalidOperationException($"Qwen-ASR error: {errMsg}");
                }
            }
        }
        finally
        {
            audioDone.TrySetCanceled();
            try { await sender.ConfigureAwait(false); } catch { }
        }

        if (socketError is not null) throw socketError;
        if (senderError is WebSocketException wsErr) throw wsErr;
        if (senderError is not null) throw senderError;

        return new AsrResult(transcript, NormalizeEmotion(emotion));
    }

    private static async Task SendBase64AudioAsync(ClientWebSocket ws, byte[] pcm, CancellationToken ct)
    {
        for (int offset = 0; offset < pcm.Length; offset += ChunkSize)
        {
            var len = Math.Min(ChunkSize, pcm.Length - offset);
            await SendBase64AudioChunkAsync(ws, pcm, offset, len, ct).ConfigureAwait(false);
        }
    }

    private static Task SendBase64AudioChunkAsync(ClientWebSocket ws, byte[] chunk, CancellationToken ct)
        => SendBase64AudioChunkAsync(ws, chunk, 0, chunk.Length, ct);

    private static async Task SendBase64AudioChunkAsync(
        ClientWebSocket ws, byte[] pcm, int offset, int len, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(pcm, offset, len);
        var json = $"{{\"event_id\":\"a{offset}\",\"type\":\"input_audio_buffer.append\",\"audio\":\"{b64}\"}}";
        await DashScopeSocket.SendTextAsync(ws, json, ct).ConfigureAwait(false);
    }

    private static string? NormalizeEmotion(string? emotion)
        => emotion is null or "neutral" ? null : emotion;

    private const string SessionFinishJson = "{\"event_id\":\"finish\",\"type\":\"session.finish\"}";

    private static string BuildSessionUpdate() =>
        // turn_detection must be explicitly null (not omitted) to disable server-side VAD.
        "{\"event_id\": \"session_update\", \"type\": \"session.update\", \"session\": {\"modalities\": [\"text\"], \"input_audio_format\": \"pcm\", \"sample_rate\": 16000, \"turn_detection\": null}}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool?.Dispose();
    }
}

file static class JsonElementExt
{
    internal static string? GetOptionalString(this JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
