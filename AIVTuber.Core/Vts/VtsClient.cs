using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.Vts;

/// <summary>
/// VTube Studio WebSocket Plugin API client.
/// Handles authentication, parameter injection (lip-sync), and hotkey triggering (expressions).
/// </summary>
public sealed class VtsClient : IDisposable
{
    private readonly VtsConfig _config;
    private ClientWebSocket? _ws;
    private string? _authToken;
    private bool _disposed;
    private readonly Dictionary<string, TaskCompletionSource<VtsResponse>> _pending = new();
    private readonly object _lock = new();

    public event EventHandler? OnConnected;
    public event EventHandler<string>? OnDisconnected;
    public event EventHandler<string>? OnError;

    public VtsClient(VtsConfig config) => _config = config;

    /// <summary>Connects and authenticates with VTS. User must approve in VTS UI.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"ws://{_config.Host}:{_config.Port}"), ct).ConfigureAwait(false);
        _ = ReceiveLoopAsync(ct);

        var resp = await SendAndWaitAsync("APIUserAuthorizeRequest", new
        {
            pluginName = "AIVTuber", pluginDeveloper = "AIVTuberDev"
        }, ct).ConfigureAwait(false);

        _authToken = resp?.Data?.TryGetProperty("authenticationToken", out var t) == true
            ? t.GetString()
            : throw new InvalidOperationException("VTS authentication failed: no token");
        OnConnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Disconnects from VTS.</summary>
    public async Task DisconnectAsync()
    {
        if (_ws is not null && _ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
            catch { }
        }
        OnDisconnected?.Invoke(this, "Disconnected");
    }

    /// <summary>Injects a parameter value into VTS (e.g., ParamMouthOpenY).</summary>
    public async Task InjectParameterAsync(string paramId, float value, CancellationToken ct = default)
    {
        EnsureAuth();
        await SendAsync("InjectParameterDataRequest", DictWithAuth(new Dictionary<string, object>
        {
            ["parameterId"] = paramId, ["parameterValue"] = value,
            ["injectionMode"] = "set", ["weight"] = 1.0
        }), ct).ConfigureAwait(false);
    }

    /// <summary>Triggers a VTS hotkey by ID (expression change).</summary>
    public async Task TriggerHotkeyAsync(string hotkeyId, CancellationToken ct = default)
    {
        EnsureAuth();
        await SendAsync("HotkeyTriggerRequest", DictWithAuth(new Dictionary<string, object>
        {
            ["hotkeyID"] = hotkeyId
        }), ct).ConfigureAwait(false);
    }

    /// <summary>Gets available hotkeys from the current VTS model.</summary>
    public async Task<List<VtsHotkeyInfo>> GetHotkeyListAsync(CancellationToken ct = default)
    {
        EnsureAuth();
        var resp = await SendAndWaitAsync("HotkeysInCurrentModelRequest",
            DictWithAuth(new Dictionary<string, object>()), ct).ConfigureAwait(false);
        if (resp?.Data is null) return [];
        return JsonSerializer.Deserialize<VtsHotkeyListResponse>(
            resp.Data.Value.GetRawText(), JsonOpts)?.Hotkeys ?? [];
    }

    /// <summary>Sets mouth from RMS value, scaled by MouthScale.</summary>
    public async Task SetMouthAsync(float rms, CancellationToken ct = default)
        => await InjectParameterAsync("ParamMouthOpenY", Math.Clamp(rms * _config.MouthScale, 0f, 1f), ct).ConfigureAwait(false);

    /// <summary>Resets mouth to closed.</summary>
    public async Task CloseMouthAsync(CancellationToken ct = default)
        => await InjectParameterAsync("ParamMouthOpenY", 0f, ct).ConfigureAwait(false);

    private void EnsureAuth()
    {
        if (string.IsNullOrEmpty(_authToken))
            throw new InvalidOperationException("Not authenticated. Call ConnectAsync first.");
    }

    private Dictionary<string, object> DictWithAuth(Dictionary<string, object> data)
    {
        data["authenticationToken"] = _authToken!;
        return data;
    }

    private async Task SendAsync(string apiName, Dictionary<string, object> data, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket not connected");
        var msg = new Dictionary<string, object>
        {
            ["apiName"] = apiName, ["apiVersion"] = "1.0",
            ["requestID"] = Guid.NewGuid().ToString(), ["data"] = data
        };
        var json = JsonSerializer.Serialize(msg, JsonOpts);
        await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task<VtsResponse?> SendAndWaitAsync(string apiName, object data, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<VtsResponse>();
        lock (_lock) { _pending[apiName] = tcs; }

        // Auth request is sent without token
        if (string.IsNullOrEmpty(_authToken))
        {
            await SendAsync(apiName, ToDict(data), ct).ConfigureAwait(false);
        }
        else
        {
            var dict = data as Dictionary<string, object> ?? ToDict(data);
            dict["authenticationToken"] = _authToken!;
            await SendAsync(apiName, dict, ct).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await tcs.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) { lock (_lock) { _pending.Remove(apiName); } throw; }
    }

    private static Dictionary<string, object> ToDict(object obj)
    {
        var dict = new Dictionary<string, object>();
        foreach (var p in obj.GetType().GetProperties())
        {
            var val = p.GetValue(obj);
            if (val is not null) dict[p.Name] = val;
        }
        return dict;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && _ws is not null && _ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) { OnDisconnected?.Invoke(this, "Closed"); break; }
                if (r.MessageType == WebSocketMessageType.Text)
                    HandleMessage(Encoding.UTF8.GetString(buf, 0, r.Count));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { OnError?.Invoke(this, ex.Message); }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var resp = JsonSerializer.Deserialize<VtsResponse>(json, JsonOpts);
            if (resp is null) return;
            var reqType = resp.ApiName?.Replace("Response", "Request");
            lock (_lock)
            {
                if (reqType is not null && _pending.Remove(reqType, out var tcs))
                    tcs.TrySetResult(resp);
                else if (resp.ApiName is not null && _pending.Remove(resp.ApiName, out tcs))
                    tcs.TrySetResult(resp);
            }
            if (resp.Data?.TryGetProperty("error", out var err) == true)
                OnError?.Invoke(this, err.GetString() ?? "VTS error");
        }
        catch (JsonException) { }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _ws?.Dispose(); }
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}