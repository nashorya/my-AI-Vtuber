using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Aliyun DashScope realtime ASR over WebSocket (Fun-ASR / Paraformer).
/// </summary>
/// <remarks>
/// Two connection strategies, selected by <see cref="AsrConfig.PersistConnection"/>:
/// <list type="bullet">
///   <item><c>true</c> (default): reuses a long-lived WebSocket via <see cref="DashScopeConnectionPool"/>,
///   skipping the TCP+TLS+auth handshake on each call. On socket error the connection is dropped
///   and the request is retried once on a fresh connection.</item>
///   <item><c>false</c>: opens a fresh connection per call (legacy behavior, fallback).</item>
/// </list>
/// <see cref="RecognizeAsync"/> takes the full PCM buffer and returns the final transcript.
/// <see cref="StreamRecognizeAsync"/> pushes audio chunks as they arrive and yields finalized
/// sentences incrementally (true streaming).
/// </remarks>
public sealed class DashScopeAsrClient : IAsrClient, IDisposable
{
    private const int ChunkSize = 3200; // 100ms @ 16kHz/16bit/mono
    private readonly AsrConfig _config;
    private readonly DashScopeConnectionPool? _pool;
    private bool _disposed;

    public DashScopeAsrClient(AsrConfig config, Action<Exception>? onError = null)
    {
        _config = config;
        _pool = config.PersistConnection
            ? new DashScopeConnectionPool(DashScopeProtocol.InferenceUrl, config.ApiKey, onError)
            : null;
    }

    public async Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "paraformer-realtime-v2" : _config.Model;
        if (_pool is null)
            return await RecognizeOneShotAsync(pcm16k, model, cancellationToken).ConfigureAwait(false);

        try
        {
            return await RecognizePooledAsync(pcm16k, model, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // Stale/broken pooled connection - drop it and retry once on a fresh one.
            _pool.Invalidate();
            return await RecognizePooledAsync(pcm16k, model, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Persisted-connection recognition: borrows the pool's WebSocket for one
    /// run-task/finish-task cycle, leaving the socket open for the next call.</summary>
    private async Task<AsrResult> RecognizePooledAsync(byte[] pcm16k, string model, CancellationToken ct)
    {
        var ws = await _pool!.GetOrCreateAsync(ct).ConfigureAwait(false);
        var taskId = DashScopeProtocol.NewTaskId();
        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.RunTaskAsr(taskId, model, 16000), ct).ConfigureAwait(false);
        return await RunRequestLoopAsync(ws, taskId,
            sendAudio: () => DashScopeSocket.SendAudioAsync(ws, pcm16k, ChunkSize, ct),
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>Legacy one-shot path: opens a fresh connection, completes one task, closes.</summary>
    private async Task<AsrResult> RecognizeOneShotAsync(byte[] pcm16k, string model, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"bearer {_config.ApiKey}");
        await ws.ConnectAsync(new Uri(DashScopeProtocol.InferenceUrl), ct).ConfigureAwait(false);

        var taskId = DashScopeProtocol.NewTaskId();
        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.RunTaskAsr(taskId, model, 16000), ct).ConfigureAwait(false);
        var result = await RunRequestLoopAsync(ws, taskId,
            sendAudio: () => DashScopeSocket.SendAudioAsync(ws, pcm16k, ChunkSize, ct),
            ct: ct).ConfigureAwait(false);
        await DashScopeSocket.CloseAsync(ws);
        return result;
    }

    /// <summary>Shared receive loop for the whole-buffer paths. Sends audio (via
    /// <paramref name="sendAudio"/>) once task-started arrives, then waits for
    /// task-finished/task-failed and returns the concatenated transcript.</summary>
    private static async Task<AsrResult> RunRequestLoopAsync(
        ClientWebSocket ws, string taskId, Func<Task> sendAudio, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        var audioSent = false;

        while (true)
        {
            var msg = await DashScopeSocket.ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
            if (msg is null) throw new WebSocketException("DashScope socket closed mid-recognition");
            if (msg.Value.Type != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(msg.Value.Bytes);
            var (ev, err) = DashScopeProtocol.ParseEvent(json);
            switch (ev)
            {
                case "task-started":
                    if (!audioSent)
                    {
                        audioSent = true;
                        await sendAudio().ConfigureAwait(false);
                        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.FinishTask(taskId), ct).ConfigureAwait(false);
                    }
                    break;

                case "result-generated":
                    var (sentence, end) = DashScopeProtocol.ParseAsrSentence(json);
                    if (!string.IsNullOrEmpty(sentence) && end)
                        transcript.Append(sentence);
                    break;

                case "task-finished":
                    return new AsrResult(transcript.ToString());

                case "task-failed":
                    throw new InvalidOperationException($"DashScope ASR failed: {err}");
            }
        }
    }

    /// <summary>True streaming: pushes audio chunks to the service as they arrive, while
    /// a background task drains the server's incremental result messages. Yields each
    /// finalized sentence (sentence_end=true) as an incremental <see cref="AsrResult"/>.
    /// On socket error with a pooled connection, retries once on a fresh connection.</summary>
    public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
        IAsyncEnumerable<byte[]> audioStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_config.Model) ? "paraformer-realtime-v2" : _config.Model;

        if (_pool is null)
        {
            // Legacy path: buffer then one-shot recognize.
            var buffer = new List<byte>();
            await foreach (var chunk in audioStream.WithCancellation(cancellationToken))
                buffer.AddRange(chunk);
            if (buffer.Count > 0)
            {
                var result = await RecognizeOneShotAsync(buffer.ToArray(), model, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.Text)) yield return result;
            }
            yield break;
        }

        List<AsrResult>? results = null;
        // IAsyncEnumerable is single-pass: buffer while streaming so a socket-error retry
        // can replay the same audio instead of getting an empty transcript.
        var buffered = new List<byte[]>();
        try
        {
            results = await StreamPooledAsync(
                BufferingTee(audioStream, buffered, cancellationToken), model, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            _pool.Invalidate();
            results = await StreamPooledAsync(
                ReplayBuffered(buffered), model, cancellationToken).ConfigureAwait(false);
        }

        foreach (var r in results)
        {
            if (!string.IsNullOrWhiteSpace(r.Text)) yield return r;
        }
    }

    private static async IAsyncEnumerable<byte[]> BufferingTee(
        IAsyncEnumerable<byte[]> source,
        List<byte[]> buffer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(chunk);
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<byte[]> ReplayBuffered(
        IReadOnlyList<byte[]> buffer,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>One streaming cycle on the pooled connection. Audio is pushed on a background
    /// task that waits for task-started, then streams chunks as they arrive and sends finish-task.
    /// The foreground receive loop collects finalized sentences until task-finished.</summary>
    private async Task<List<AsrResult>> StreamPooledAsync(
        IAsyncEnumerable<byte[]> audioStream, string model, CancellationToken ct)
    {
        var ws = await _pool!.GetOrCreateAsync(ct).ConfigureAwait(false);
        var taskId = DashScopeProtocol.NewTaskId();
        await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.RunTaskAsr(taskId, model, 16000), ct).ConfigureAwait(false);

        var results = new List<AsrResult>();
        var transcript = new StringBuilder();
        var audioSent = false;
        // Signal the background audio sender to start once we have seen task-started.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? senderError = null;

        // Background: push audio chunks then finish-task. Errors are captured for surfacing
        // after the receive loop ends (socket closed by the server is the normal termination).
        var sender = Task.Run(async () =>
        {
            try
            {
                await startGate.Task.ConfigureAwait(false);
                await foreach (var chunk in audioStream.WithCancellation(ct).ConfigureAwait(false))
                    await DashScopeSocket.SendAudioAsync(ws, chunk, ChunkSize, ct).ConfigureAwait(false);
                await DashScopeSocket.SendTextAsync(ws, DashScopeProtocol.FinishTask(taskId), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (WebSocketException wsex) { senderError = wsex; }
            catch (Exception ex) { senderError = ex; }
        });

        WebSocketException? socketError = null;
        try
        {
            while (true)
            {
                var msg = await DashScopeSocket.ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                if (msg is null) { socketError = new WebSocketException("DashScope socket closed mid-stream"); break; }
                if (msg.Value.Type != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(msg.Value.Bytes);
                var (ev, err) = DashScopeProtocol.ParseEvent(json);
                if (ev == "task-started" && !audioSent)
                {
                    audioSent = true;
                    startGate.TrySetResult();
                }
                else if (ev == "result-generated")
                {
                    var (sentence, end) = DashScopeProtocol.ParseAsrSentence(json);
                    if (!string.IsNullOrEmpty(sentence) && end)
                    {
                        transcript.Append(sentence);
                        results.Add(new AsrResult(transcript.ToString()));
                        transcript.Clear();
                    }
                    // Non-final incremental updates are dropped: orchestrator only acts on
                    // finalized sentences; yielding partials would cause duplicate processing.
                }
                else if (ev == "task-finished")
                {
                    break;
                }
                else if (ev == "task-failed")
                {
                    throw new InvalidOperationException($"DashScope ASR failed: {err}");
                }
            }
        }
        finally
        {
            // Unblock the sender if task-started never arrived (failure path).
            startGate.TrySetCanceled();
            try { await sender.ConfigureAwait(false); } catch { /* senderError captured */ }
        }

        // A socket error on either side is retried once at the caller.
        if (socketError is not null) throw socketError;
        if (senderError is WebSocketException wsErr) throw wsErr;
        if (senderError is not null) throw senderError;

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool?.Dispose();
    }
}
