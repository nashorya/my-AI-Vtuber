using System.Net.WebSockets;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>A received message from the transport.</summary>
public readonly record struct WebSocketTransportResult(byte[] Data, bool EndOfMessage);

/// <summary>
/// Minimal injectable WebSocket transport so every vendor session can be contract-tested
/// against fake fixtures without real network access or provider keys (plan §RT-03).
/// </summary>
public interface IWebSocketTransport : IDisposable
{
    /// <summary>Connects using the given URI and optional handshake headers (auth).</summary>
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken);

    /// <summary>Sends one message. Vendors decide text vs binary via <paramref name="asText"/>.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> buffer, bool asText, CancellationToken cancellationToken);

    /// <summary>Receives the next complete message (aggregates fragments).</summary>
    ValueTask<WebSocketTransportResult> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Performs the close handshake.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken);
}

/// <summary>Real network transport over <see cref="ClientWebSocket"/>. 未实测 against live
/// provider endpoints in this change — all contract tests run on fakes.</summary>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private ClientWebSocket? _ws;

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        _ws?.Dispose();
        var ws = new ClientWebSocket();
        if (headers is not null)
            foreach (var (name, value) in headers)
                ws.Options.SetRequestHeader(name, value);
        _ws = ws;
        return ws.ConnectAsync(uri, cancellationToken);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, bool asText, CancellationToken cancellationToken)
    {
        ObjectDisposedThrowHelper();
        await _ws!.SendAsync(buffer, asText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WebSocketTransportResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedThrowHelper();
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _ws!.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return new(Array.Empty<byte>(), true);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return new(message.ToArray(), true);
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_ws is null) return;
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client close", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ws.Dispose();
            _ws = null;
        }
    }

    private void ObjectDisposedThrowHelper()
    {
        if (_ws is null) throw new ObjectDisposedException(nameof(ClientWebSocketTransport));
    }

    public void Dispose()
    {
        _ws?.Dispose();
        _ws = null;
    }
}
