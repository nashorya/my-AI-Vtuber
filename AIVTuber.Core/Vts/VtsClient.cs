using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;
using AIVTuber.Core.Avatar;

namespace AIVTuber.Core.Vts;

public sealed class VtsApiException(string message, int errorId = -1) : Exception(message)
{
    public int ErrorId { get; } = errorId;
    public bool AuthorizationDenied => ErrorId is 50;
}

/// <summary>One authenticated socket, one receive task and serialized sends. No hidden retries.</summary>
public sealed class VtsClient : IDisposable, IAvatarParameterBackend
{
    public const string MouthParameterId = "AIVTuberMouthOpen";
    private const string PluginName = "AIVTuber";
    private const string PluginDeveloper = "AIVTuberDev";
    private readonly VtsConfig _config;
    private readonly string _tokenPath;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VtsResponse>> _pending = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _session;
    private Task? _receiveTask;
    private bool _authenticated, _disposed;
    public event EventHandler? OnConnected;
    public event EventHandler<string>? OnDisconnected;
    public event EventHandler<string>? OnError;
    public event EventHandler<string>? OnStateChanged;
    public event EventHandler<VtsResponse>? OnEvent;
    public string State { get; private set; } = "未连接";
    public bool IsConnected => _authenticated && _ws?.State == WebSocketState.Open;
    internal int PendingCount => _pending.Count;

    public VtsClient(VtsConfig config) : this(config, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIVTuber", "vts",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{config.Host}:{config.Port}")))[..16] + ".token")) { }
    internal VtsClient(VtsConfig config, string tokenPath) { _config = config; _tokenPath = tokenPath; }
    private void SetState(string value) { State = value; OnStateChanged?.Invoke(this, value); }

    public async Task ConnectAsync(CancellationToken ct = default, bool createLegacyMouth = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
            SetState("连接中");
            _session = new CancellationTokenSource();
            var socket = new ClientWebSocket();
            _ws = socket;
            await socket.ConnectAsync(new Uri($"ws://{_config.Host}:{_config.Port}"), ct).ConfigureAwait(false);
            _receiveTask = ReceiveLoopAsync(socket, _session.Token);
            string? token = File.Exists(_tokenPath) ? (await File.ReadAllTextAsync(_tokenPath, ct).ConfigureAwait(false)).Trim() : null;
            var accepted = !string.IsNullOrEmpty(token) && await AuthenticateAsync(token, ct).ConfigureAwait(false);
            if (!accepted)
            {
                SetState("等待 VTS 授权");
                var response = await RequestAsync("AuthenticationTokenRequest", new()
                {
                    ["pluginName"] = PluginName, ["pluginDeveloper"] = PluginDeveloper
                }, ct, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
                token = response.Data!.Value.GetProperty("authenticationToken").GetString()!;
                if (!await AuthenticateAsync(token, ct).ConfigureAwait(false))
                    throw new VtsApiException("VTS 拒绝授权", 50);
                Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
                var temp = _tokenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllTextAsync(temp, token, ct).ConfigureAwait(false); File.Move(temp, _tokenPath, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            _authenticated = true;
            if (createLegacyMouth) await CreateParameterAsync(MouthParameterId, 0, 1, 0, ct).ConfigureAwait(false);
            SetState("已连接");
            OnConnected?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            await DisconnectAsync().ConfigureAwait(false);
            SetState("连接失败 / 授权未完成");
            throw;
        }
        finally { _connectLock.Release(); }
    }

    private async Task<bool> AuthenticateAsync(string token, CancellationToken ct)
    {
        var response = await RequestAsync("AuthenticationRequest", new()
        {
            ["pluginName"] = PluginName, ["pluginDeveloper"] = PluginDeveloper, ["authenticationToken"] = token
        }, ct).ConfigureAwait(false);
        return response.Data!.Value.GetProperty("authenticated").GetBoolean();
    }

    public async Task DisconnectAsync()
    {
        _authenticated = false;
        _session?.Cancel();
        _ws?.Abort();
        FailPending(new IOException("VTS connection closed"));
        if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
        _receiveTask = null;
        _ws?.Dispose(); _ws = null;
        _session?.Dispose(); _session = null;
    }

    public Task CreateParameterAsync(string id, float min, float max, float neutral, CancellationToken ct = default)
        => RequestAsync("ParameterCreationRequest", new()
        {
            ["parameterName"] = id, ["explanation"] = "AIVTuber continuous control: map input/output to identical calibrated ranges",
            ["min"] = min, ["max"] = max, ["defaultValue"] = neutral
        }, ct);
    public Task InjectAsync(IReadOnlyDictionary<string, float> values, CancellationToken ct)
        => RequestAuthenticatedAsync("InjectParameterDataRequest", new()
        {
            ["faceFound"] = true, ["mode"] = "set",
            ["parameterValues"] = values.Select(p => new { id = p.Key, value = p.Value, weight = 1f }).ToArray()
        }, ct);
    public Task InjectParameterAsync(string paramId, float value, CancellationToken ct = default)
        => InjectAsync(new Dictionary<string, float> { [paramId] = value }, ct);
    public Task SetMouthAsync(float rms, CancellationToken ct = default)
        => InjectParameterAsync(MouthParameterId, Math.Clamp(rms * _config.MouthScale, 0, 1), ct);
    public Task CloseMouthAsync(CancellationToken ct = default) => InjectParameterAsync(MouthParameterId, 0, ct);
    public Task TriggerHotkeyAsync(string hotkeyId, CancellationToken ct = default)
        => RequestAuthenticatedAsync("HotkeyTriggerRequest", new() { ["hotkeyID"] = hotkeyId }, ct);
    public async Task<List<VtsHotkeyInfo>> GetHotkeyListAsync(CancellationToken ct = default)
    {
        var response = await RequestAuthenticatedAsync("HotkeysInCurrentModelRequest", new(), ct).ConfigureAwait(false);
        return response.Data!.Value.Deserialize<VtsHotkeyListResponse>()?.Hotkeys ?? [];
    }
    public async Task<JsonElement> QueryAsync(string type, CancellationToken ct = default)
        => (await RequestAuthenticatedAsync(type, new(), ct).ConfigureAwait(false)).Data!.Value;
    public Task SubscribeAsync(string eventName, CancellationToken ct = default)
        => RequestAuthenticatedAsync("EventSubscriptionRequest", new()
        { ["eventName"] = eventName, ["subscribe"] = true, ["config"] = new { } }, ct);

    private Task<VtsResponse> RequestAuthenticatedAsync(string type, Dictionary<string, object> data, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("VTS 未连接或未授权");
        return RequestAsync(type, data, ct);
    }
    private async Task<VtsResponse> RequestAsync(string type, Dictionary<string, object> data, CancellationToken ct, TimeSpan? timeout = null)
    {
        var socket = _ws;
        if (socket is null || socket.State != WebSocketState.Open) throw new IOException("VTS socket closed");
        var id = Guid.NewGuid().ToString("N");
        var source = new TaskCompletionSource<VtsResponse>(type, TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = source;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _session?.Token ?? CancellationToken.None);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await _sendLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(VtsProtocol.BuildMessage(type, id, data));
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
            return await source.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _session?.IsCancellationRequested != true)
        { throw new TimeoutException($"VTS {type} 等待响应超时"); }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        string reason = "VTS 已断开";
        try
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var part = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, part.Count);
                if (message.Length > 4 * 1024 * 1024) throw new IOException("VTS 响应超过 4 MiB");
                if (!part.EndOfMessage) continue;
                var response = JsonSerializer.Deserialize<VtsResponse>(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
                if (response is null) continue;
                if (response.RequestId is not null && _pending.TryRemove(response.RequestId, out var pending))
                {
                    if (response.MessageType == "APIError")
                    {
                        var body = response.Data!.Value;
                        var error = new VtsApiException(body.TryGetProperty("message", out var m) ? m.GetString()! : "VTS API error",
                            body.TryGetProperty("errorID", out var number) ? number.GetInt32() : -1);
                        pending.TrySetException(error);
                        OnError?.Invoke(this, $"{pending.Task.AsyncState} request={response.RequestId}: {error.Message}");
                    }
                    else pending.TrySetResult(response);
                }
                else if (response.MessageType?.EndsWith("Event", StringComparison.Ordinal) == true)
                    OnEvent?.Invoke(this, response);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { reason = ex.Message; }
        finally
        {
            _authenticated = false;
            FailPending(new IOException(reason));
            if (!ct.IsCancellationRequested) { SetState("已断开"); OnDisconnected?.Invoke(this, reason); }
        }
    }
    private void FailPending(Exception error)
    {
        foreach (var pair in _pending)
            if (_pending.TryRemove(pair.Key, out var pending)) pending.TrySetException(error);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _authenticated = false;
        _session?.Cancel(); _ws?.Abort();
        FailPending(new ObjectDisposedException(nameof(VtsClient)));
        _ws?.Dispose();
    }
}
