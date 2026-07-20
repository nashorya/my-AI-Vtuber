using System.Net.WebSockets;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Manages a single long-lived <see cref="ClientWebSocket"/> for DashScope realtime services
/// (ASR / TTS) and the OpenAI Realtime-compatible Qwen-ASR endpoint. Reused across recognitions
/// to avoid paying the TCP+TLS+auth handshake cost (~200-600ms) on every utterance.
/// </summary>
/// <remarks>
/// Thread-safety: <see cref="GetOrCreateAsync"/> and <see cref="Invalidate"/> are thread-safe,
/// but the returned <see cref="ClientWebSocket"/> is intended for single-flight use — the
/// BotOrchestrator already serializes recognitions via RequestCoordinator, so only one
/// run-task/finish-task cycle (or session update round) is in flight at a time.
/// </remarks>
internal sealed class DashScopeConnectionPool : IDisposable
{
    private readonly string _endpoint;
    private readonly Action<ClientWebSocketOptions> _configure;
    private readonly Action<Exception>? _onError;
    private ClientWebSocket? _ws;
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="endpoint">WebSocket URL (including query string).</param>
    /// <param name="configure">Callback that sets request headers (Authorization, OpenAI-Beta, etc.)
    /// on the WebSocket options before connect. Invoked once per fresh connection.</param>
    /// <param name="onError">Optional callback invoked when connect fails; used to surface errors
    /// to BotRuntime's PipelineError event.</param>
    public DashScopeConnectionPool(
        string endpoint,
        Action<ClientWebSocketOptions> configure,
        Action<Exception>? onError = null)
    {
        _endpoint = endpoint;
        _configure = configure;
        _onError = onError;
    }

    /// <summary>Convenience overload for the common case of a single bearer token.</summary>
    public DashScopeConnectionPool(
        string endpoint, string apiKey, Action<Exception>? onError = null)
        : this(endpoint, opts => opts.SetRequestHeader("Authorization", $"bearer {apiKey}"), onError)
    {
    }

    /// <summary>Returns the live WebSocket, creating (and authenticating) one on first use
    /// or after <see cref="Invalidate"/>. Reuses the existing connection otherwise.</summary>
    public async Task<ClientWebSocket> GetOrCreateAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DashScopeConnectionPool));
            if (_ws is { State: WebSocketState.Open } open) return open;
        }

        var ws = new ClientWebSocket();
        _configure(ws.Options);
        try
        {
            await ws.ConnectAsync(new Uri(_endpoint), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
            ws.Dispose();
            throw;
        }

        lock (_gate)
        {
            if (_disposed) { ws.Dispose(); throw new ObjectDisposedException(nameof(DashScopeConnectionPool)); }
            _ws?.Dispose();
            _ws = ws;
        }
        return ws;
    }

    /// <summary>Drops the current connection so the next <see cref="GetOrCreateAsync"/>
    /// performs a fresh handshake. Call after any socket/protocol error to recover.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _ws?.Dispose();
            _ws = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _ws?.Dispose();
            _ws = null;
        }
    }
}
