using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Vts;

/// <summary>
/// VTube Studio Plugin API client (ws://host:port, default 8001). Implements the real protocol:
/// the "VTubeStudioPublicAPI" envelope with a messageType, the two-step auth flow
/// (AuthenticationTokenRequest → AuthenticationRequest), parameter injection via parameterValues,
/// and hotkey triggering. Responses are routed by requestID. High-frequency injects (lip-sync) are
/// fire-and-forget; all sends are serialized (ClientWebSocket allows only one outstanding send).
/// </summary>
public sealed class VtsClient : IDisposable
{
    private const string PluginName = "AIVTuber";
    private const string PluginDeveloper = "AIVTuberDev";

    /// <summary>
    /// VTS rejects InjectParameterDataRequest for built-in Live2D parameters like
    /// ParamMouthOpenY ("only for tracking parameters"). We create our own custom
    /// tracking parameter and inject into that instead. The user must map it to the
    /// model's mouth-open parameter once in VTS (Settings → Live2D Parameter Mapping,
    /// or drag it onto ParamMouthOpenY in the model's parameter list).
    /// </summary>
    public const string MouthParameterId = "AIVTuberMouthOpen";

    private readonly VtsConfig _config;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<VtsResponse>> _pending = new();
    private readonly object _lock = new();
    private ClientWebSocket? _ws;
    private string? _authToken;
    private bool _disposed;

    public event EventHandler? OnConnected;
    public event EventHandler<string>? OnDisconnected;
    public event EventHandler<string>? OnError;

    public VtsClient(VtsConfig config) => _config = config;

    /// <summary>True only while the WebSocket is actually open (not just "ConnectAsync was called once").</summary>
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Connects and authenticates. The user must approve the plugin in the VTS UI the first
    /// time (the token request). Each connect fetches a fresh token.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"ws://{_config.Host}:{_config.Port}"), ct).ConfigureAwait(false);
        _ = ReceiveLoopAsync(ct);

        // Step 1: request an auth token (prompts the user to allow the plugin in VTS).
        var tokenResp = await RequestAsync("AuthenticationTokenRequest", new Dictionary<string, object>
        {
            ["pluginName"] = PluginName,
            ["pluginDeveloper"] = PluginDeveloper,
        }, ct).ConfigureAwait(false);

        _authToken = tokenResp?.Data?.TryGetProperty("authenticationToken", out var t) == true
            ? t.GetString()
            : throw new InvalidOperationException("VTS did not return an authentication token (was the plugin denied?)");

        // Step 2: authenticate the session with the token.
        var authResp = await RequestAsync("AuthenticationRequest", new Dictionary<string, object>
        {
            ["pluginName"] = PluginName,
            ["pluginDeveloper"] = PluginDeveloper,
            ["authenticationToken"] = _authToken!,
        }, ct).ConfigureAwait(false);

        var authenticated = authResp?.Data?.TryGetProperty("authenticated", out var a) == true && a.GetBoolean();
        if (!authenticated)
        {
            var reason = authResp?.Data?.TryGetProperty("reason", out var r) == true ? r.GetString() : "unknown";
            throw new InvalidOperationException($"VTS authentication failed: {reason}");
        }

        // Built-in Live2D parameters (e.g. ParamMouthOpenY) can't be injected directly —
        // VTS only allows injection into custom tracking parameters. Create ours once;
        // if it already exists VTS returns an APIError, which we ignore.
        await RequestAsync("ParameterCreationRequest", new Dictionary<string, object>
        {
            ["parameterName"] = MouthParameterId,
            ["explanation"] = "AIVTuber lip-sync (RMS-driven mouth open)",
            ["min"] = 0f,
            ["max"] = 1f,
            ["defaultValue"] = 0f,
        }, ct).ConfigureAwait(false);

        OnConnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        if (_ws is not null && _ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
            catch { }
        }
        OnDisconnected?.Invoke(this, "Disconnected");
    }

    /// <summary>Injects a parameter value (e.g. ParamMouthOpenY). Fire-and-forget — we don't await the
    /// VTS response, so the ~30ms lip-sync loop never blocks on a round-trip.</summary>
    public Task InjectParameterAsync(string paramId, float value, CancellationToken ct = default)
        => FireAsync("InjectParameterDataRequest", VtsProtocol.InjectParameterData(paramId, value), ct);

    /// <summary>Triggers a VTS hotkey by ID (expression change). Fire-and-forget.</summary>
    public Task TriggerHotkeyAsync(string hotkeyId, CancellationToken ct = default)
        => FireAsync("HotkeyTriggerRequest", new Dictionary<string, object> { ["hotkeyID"] = hotkeyId }, ct);

    /// <summary>Lists hotkeys in the current model.</summary>
    public async Task<List<VtsHotkeyInfo>> GetHotkeyListAsync(CancellationToken ct = default)
    {
        var resp = await RequestAsync("HotkeysInCurrentModelRequest", new Dictionary<string, object>(), ct).ConfigureAwait(false);
        if (resp?.Data is null) return [];
        return JsonSerializer.Deserialize<VtsHotkeyListResponse>(resp.Data.Value.GetRawText())?.Hotkeys ?? [];
    }

    public Task SetMouthAsync(float rms, CancellationToken ct = default)
        => InjectParameterAsync(MouthParameterId, Math.Clamp(rms * _config.MouthScale, 0f, 1f), ct);

    public Task CloseMouthAsync(CancellationToken ct = default)
        => InjectParameterAsync(MouthParameterId, 0f, ct);

    /// <summary>Send a request and wait for the matching response (by requestID), with a 30s timeout.</summary>
    private async Task<VtsResponse?> RequestAsync(string messageType, Dictionary<string, object> data, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<VtsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) { _pending[requestId] = tcs; }

        await SendRawAsync(VtsProtocol.BuildMessage(messageType, requestId, data), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var reg = timeout.Token.Register(() =>
        {
            lock (_lock) { _pending.Remove(requestId); }
            tcs.TrySetCanceled();
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Send a request without awaiting a response (for high-frequency / don't-care messages).</summary>
    private Task FireAsync(string messageType, Dictionary<string, object> data, CancellationToken ct)
        => SendRawAsync(VtsProtocol.BuildMessage(messageType, Guid.NewGuid().ToString("N"), data), ct);

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("VTS WebSocket not connected");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _ws is not null && _ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) { OnDisconnected?.Invoke(this, "Closed"); break; }
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                HandleMessage(sb.ToString());
                sb.Clear();
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { OnError?.Invoke(this, ex.Message); }
    }

    private void HandleMessage(string json)
    {
        VtsResponse? resp;
        try { resp = JsonSerializer.Deserialize<VtsResponse>(json); }
        catch (JsonException) { return; }
        if (resp is null) return;

        if (resp.RequestId is not null)
        {
            TaskCompletionSource<VtsResponse>? tcs;
            lock (_lock) { _pending.Remove(resp.RequestId, out tcs); }
            tcs?.TrySetResult(resp);
        }

        if (resp.MessageType == "APIError" && resp.Data?.TryGetProperty("message", out var m) == true)
            OnError?.Invoke(this, m.GetString() ?? "VTS API error");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sendLock.Dispose();
        _ws?.Dispose();
    }
}
