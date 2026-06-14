using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Obs;

/// <summary>
/// OBS WebSocket client (obswebsocket v5 protocol).
/// Handles authentication handshake and SetInputSettings for subtitle display.
/// Provides typewriter effect by sending text character-by-character at a configurable interval.
/// Silently degrades on connection failure (subtitles don't block the main pipeline).
/// </summary>
public sealed class ObsClient : IDisposable
{
    private readonly ObsConfig _config;
    private ClientWebSocket? _ws;
    private bool _disposed;
    private bool _connected;
    private readonly object _lock = new();

    /// <summary>Fires when connected and authenticated with OBS.</summary>
    public event EventHandler? OnConnected;
    public event EventHandler<string>? OnDisconnected;
    public event EventHandler<string>? OnError;

    public ObsClient(ObsConfig config) => _config = config;

    /// <summary>Connects to OBS WebSocket and performs the v5 authentication handshake.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!_config.Enable) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri($"ws://{_config.Host}:{_config.Port}"), ct).ConfigureAwait(false);

            // Step 1: Receive Hello (op=0) with challenge and salt
            var hello = await ReceiveMessageAsync(ct).ConfigureAwait(false);
            if (hello is null || hello.Value.TryGetProperty("op", out var opEl) && opEl.GetInt32() != 0)
                throw new InvalidOperationException("Expected Hello message from OBS");

            var helloData = hello.Value.GetProperty("d");
            var challenge = helloData.GetProperty("challenge").GetString()!;
            var salt = helloData.GetProperty("salt").GetString()!;

            // Step 2: Compute auth = Base64(SHA256(Base64(SHA256(password + salt)) + challenge))
            var auth = ComputeAuth(_config.Password, salt, challenge);

            // Step 3: Send Identify (op=1) with auth
            var identify = new Dictionary<string, object>
            {
                ["op"] = 1,
                ["d"] = new Dictionary<string, object>
                {
                    ["rpcVersion"] = 1,
                    ["authentication"] = auth,
                    ["eventSubscriptions"] = 0
                }
            };
            await SendAsync(identify, ct).ConfigureAwait(false);

            // Step 4: Receive Identified (op=2)
            var identified = await ReceiveMessageAsync(ct).ConfigureAwait(false);
            if (identified is null) throw new InvalidOperationException("No Identified response from OBS");

            lock (_lock) { _connected = true; }
            OnConnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"OBS connection failed: {ex.Message}");
            // Silent degradation - don't throw, subtitles are optional
        }
    }

    /// <summary>Sets subtitle text on an OBS text source. Instant update (no typewriter effect).</summary>
    public async Task SetSubtitleAsync(string text, string? component = null, CancellationToken ct = default)
    {
        if (!_config.Enable) return;
        if (!EnsureConnected()) return;

        var inputName = component ?? _config.AssistantTextComponent;
        await SetInputSettingsAsync(inputName, new Dictionary<string, object> { ["text"] = text }, ct).ConfigureAwait(false);
    }

    /// <summary>Sets subtitle with typewriter effect: sends text character-by-character.</summary>
    public async Task SetSubtitleTypewriterAsync(string text, string? component = null, CancellationToken ct = default)
    {
        if (!_config.Enable) return;
        if (_config.TypewriterIntervalMs <= 0)
        {
            await SetSubtitleAsync(text, component, ct).ConfigureAwait(false);
            return;
        }

        if (!EnsureConnected()) return;
        var inputName = component ?? _config.AssistantTextComponent;
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            current.Append(ch);
            await SetInputSettingsAsync(inputName, new Dictionary<string, object> { ["text"] = current.ToString() }, ct).ConfigureAwait(false);
            await Task.Delay(_config.TypewriterIntervalMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Clears the subtitle text source.</summary>
    public async Task ClearSubtitleAsync(CancellationToken ct = default)
        => await SetSubtitleAsync("", ct: ct).ConfigureAwait(false);

    private bool EnsureConnected()
    {
        lock (_lock) { return _connected && _ws is not null && _ws.State == WebSocketState.Open; }
    }

    private async Task SetInputSettingsAsync(string inputName, Dictionary<string, object> settings, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;

        var request = new Dictionary<string, object>
        {
            ["op"] = 6,
            ["d"] = new Dictionary<string, object>
            {
                ["requestType"] = "SetInputSettings",
                ["requestId"] = Guid.NewGuid().ToString(),
                ["requestData"] = new Dictionary<string, object>
                {
                    ["inputName"] = inputName,
                    ["inputSettings"] = settings
                }
            }
        };
        await SendAsync(request, ct).ConfigureAwait(false);
    }

    private static string ComputeAuth(string password, string salt, string challenge)
    {
        // auth = Base64(SHA256(Base64(SHA256(password + salt)) + challenge))
        var innerHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var innerB64 = Convert.ToBase64String(innerHash);
        var outerHash = SHA256.HashData(Encoding.UTF8.GetBytes(innerB64 + challenge));
        return Convert.ToBase64String(outerHash);
    }

    private async Task SendAsync(object data, CancellationToken ct)
    {
        if (_ws is null) return;
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> ReceiveMessageAsync(CancellationToken ct)
    {
        if (_ws is null) return null;
        var buf = new byte[8192];
        var sb = new StringBuilder();

        while (true)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
            if (result.EndOfMessage) break;
        }

        try { return JsonSerializer.Deserialize<JsonElement>(sb.ToString()); }
        catch { return null; }
    }

    /// <summary>Disconnects from OBS WebSocket.</summary>
    public async Task DisconnectAsync()
    {
        lock (_lock) { _connected = false; }
        if (_ws is not null && _ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
            catch { }
        }
        OnDisconnected?.Invoke(this, "Disconnected");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ws?.Dispose();
    }
}