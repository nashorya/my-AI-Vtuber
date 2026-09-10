using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Config;
using AIVTuber.Core.Vts;

namespace AIVTuber.Tests;

internal sealed class FakeVts : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _life = new();
    private readonly List<Task> _connections = [];
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, float> _inputs = new();
    private readonly Task _accept;
    private WebSocket? _active;
    public ConcurrentQueue<JsonElement> Requests { get; } = new();
    public string ModelId = "11111111111111111111111111111111";
    public bool DenyToken;
    public string? FailType, IgnoreType;
    public bool BigModelName, NoExpressionEvents, ActiveExpression;
    public int InjectDelay = 0;
    public VtsConfig Config => new() { Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port };
    public FakeVts() { _listener.Start(); _accept = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_life.Token);
                var task = ServeAsync(tcp);
                lock (_connections) _connections.Add(task);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ServeAsync(TcpClient tcp)
    {
        using (tcp)
        try
        {
            var stream = tcp.GetStream();
            var bytes = new List<byte>(); var one = new byte[1];
            while (bytes.Count < 16384)
            {
                if (await stream.ReadAsync(one, _life.Token) == 0) return;
                bytes.Add(one[0]);
                if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray()) == "\r\n\r\n") break;
            }
            var key = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")
                .Single(s => s.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var hash = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {hash}\r\n\r\n"), _life.Token);
            using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.Zero);
            _active = ws;
            var buffer = new byte[65536]; using var message = new MemoryStream();
            while (!_life.IsCancellationRequested)
            {
                var part = await ws.ReceiveAsync(buffer.AsMemory(), _life.Token);
                if (part.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;
                var request = JsonDocument.Parse(message.ToArray()).RootElement.Clone();
                message.SetLength(0); Requests.Enqueue(request);
                var type = request.GetProperty("messageType").GetString()!;
                var data = request.GetProperty("data");
                if (type == "EventSubscriptionRequest" && !new[] { "ModelLoadedEvent", "ModelConfigChangedEvent", "HotkeyTriggeredEvent", "ExpressionToggledEvent" }.Contains(data.GetProperty("eventName").GetString()))
                    throw new InvalidOperationException("Unrecognized official event name");
                if (type == IgnoreType) continue;
                object response;
                var responseType = type.Replace("Request", "Response");
                if (type == "EventSubscriptionRequest" && NoExpressionEvents && data.GetProperty("eventName").GetString() == "ExpressionToggledEvent")
                { responseType = "APIError"; response = new { errorID = 950, message = "event unavailable" }; }
                else if (type == FailType || (type == "AuthenticationTokenRequest" && DenyToken))
                { responseType = "APIError"; response = new { errorID = DenyToken ? 50 : 999, message = "test rejection" }; }
                else
                {
                    response = type switch
                    {
                        "AuthenticationTokenRequest" => new { authenticationToken = "test-token" },
                        "AuthenticationRequest" => new { authenticated = true },
                        "CurrentModelRequest" => new { modelLoaded = true, modelID = ModelId, modelName = BigModelName ? new string('鲸', 10000) : "Sample" },
                        "Live2DParameterListRequest" => new { modelID = ModelId, parameters = new[] { new { name = "ParamAngleZ", min = -30, max = 30, defaultValue = 0, value = _inputs.GetValueOrDefault("AIVTuberHeadRoll") } } },
                        "InputParameterListRequest" => new { defaultParameters = Array.Empty<object>(), customParameters = _inputs.Keys.Select(name => new { name }).ToArray() },
                        "ExpressionStateRequest" => new { expressions = ActiveExpression ? new object[] { new { active = true } } : Array.Empty<object>() },
                        "HotkeysInCurrentModelRequest" => new { availableHotkeys = Array.Empty<object>() },
                        _ => new { }
                    };
                    if (type == "ParameterCreationRequest") _inputs.TryAdd(data.GetProperty("parameterName").GetString()!, 0);
                    if (type == "InjectParameterDataRequest")
                    {
                        foreach (var p in data.GetProperty("parameterValues").EnumerateArray())
                            _inputs[p.GetProperty("id").GetString()!] = p.GetProperty("value").GetSingle();
                        await Task.Delay(InjectDelay, _life.Token);
                    }
                }
                await SendAsync(ws, new { messageType = responseType, requestID = request.GetProperty("requestID").GetString(), data = response });
            }
        }
        catch (Exception) when (_life.IsCancellationRequested || !tcp.Connected) { }
        catch (WebSocketException) { }
        catch (IOException) { }
    }
    private async Task SendAsync(WebSocket ws, object value)
    {
        await _sendGate.WaitAsync(_life.Token);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            // Deliberately fragment inside UTF-8 characters and across the client's 8 KiB buffer.
            for (var offset = 0; offset < bytes.Length; offset += 97)
            {
                var count = Math.Min(97, bytes.Length - offset);
                await ws.SendAsync(bytes.AsMemory(offset, count), WebSocketMessageType.Text, offset + count == bytes.Length, _life.Token);
            }
        }
        finally { _sendGate.Release(); }
    }
    public Task EventAsync(string type, object data) => SendAsync(_active!, new { messageType = type, data });
    public void Drop() => _active?.Abort();
    public int Count(string type) => Requests.Count(r => r.GetProperty("messageType").GetString() == type);
    public async ValueTask DisposeAsync()
    {
        _life.Cancel(); _listener.Stop(); _active?.Abort();
        try { await _accept; } catch (SocketException) { }
        Task[] tasks; lock (_connections) tasks = _connections.ToArray();
        await Task.WhenAll(tasks);
    }
}

public sealed class VtsContinuousIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vts-tests-" + Guid.NewGuid().ToString("N"));
    private string Token => Path.Combine(_directory, "auth.token");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    [Fact]
    public async Task AuthTokenIsReusedAndLargeFragmentedResponseIsIntact()
    {
        await using var server = new FakeVts { BigModelName = true };
        using var client = new VtsClient(server.Config, Token);
        await client.ConnectAsync();
        Assert.True(client.IsConnected);
        var model = await client.QueryAsync("CurrentModelRequest");
        Assert.Equal(new string('鲸', 10000), model.GetProperty("modelName").GetString());
        await client.ConnectAsync();
        Assert.Equal(1, server.Count("AuthenticationTokenRequest"));
        Assert.Equal(2, server.Count("AuthenticationRequest"));
        Assert.Equal(0, client.PendingCount);
        await client.DisconnectAsync();
    }
    [Fact]
    public async Task DeniedAuthAndFailedParameterCreationAreNotSuccess()
    {
        await using var server = new FakeVts { DenyToken = true };
        using var client = new VtsClient(server.Config, Token);
        var error = await Assert.ThrowsAsync<VtsApiException>(() => client.ConnectAsync());
        Assert.Equal(50, error.ErrorId); Assert.False(client.IsConnected);
        Assert.False(File.Exists(Token));
        server.DenyToken = false; server.FailType = "ParameterCreationRequest";
        await Assert.ThrowsAsync<VtsApiException>(() => client.ConnectAsync());
        Assert.False(client.IsConnected); Assert.Equal(0, client.PendingCount);
    }
    [Fact]
    public async Task DisconnectFailsPendingAndApiErrorsReachCaller()
    {
        await using var server = new FakeVts();
        using var client = new VtsClient(server.Config, Token);
        await client.ConnectAsync();
        server.FailType = "HotkeyTriggerRequest";
        await Assert.ThrowsAsync<VtsApiException>(() => client.TriggerHotkeyAsync("bad"));
        server.IgnoreType = "CurrentModelRequest";
        var pending = client.QueryAsync("CurrentModelRequest");
        await Task.Delay(50); await client.DisconnectAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.Equal(0, client.PendingCount);
    }
    [Fact]
    public async Task PreviewRequiresVisualConfirmationAndModelChangeInvalidatesIt()
    {
        await using var server = new FakeVts();
        using var client = new VtsClient(server.Config, Token);
        var config = new ContinuousControlConfig { Enabled = true };
        await using var session = new VtsContinuousSession(client, config);
        await session.ConnectAsync();
        var profile = session.CreateDraft(config);
        config.Profiles[profile.ModelId] = profile;
        var head = profile.Channels.Single(b => b.Channel == "headRoll");
        await session.PrepareInputsAsync(profile);
        Assert.Throws<InvalidOperationException>(() => session.ConfirmTrial(profile, head));
        await session.TestAsync(profile, head, .4f);
        Assert.False(head.Verified);
        session.ConfirmTrial(profile, head);
        Assert.True(head.Verified);
        await session.ApplyAsync(config);
        Assert.Contains("headRoll", session.AllowedChannels);
        var epoch = session.Model!.Revision;
        await server.EventAsync("ModelConfigChangedEvent", new { modelID = profile.ModelId });
        await WaitUntilAsync(() => session.Model?.Revision != epoch);
        Assert.Empty(session.AllowedChannels);
        Assert.Throws<InvalidOperationException>(() => session.ConfirmTrial(profile, head));
        await session.ApplyAsync(config); Assert.Empty(session.AllowedChannels);
        server.ModelId = "22222222222222222222222222222222";
        await server.EventAsync("ModelLoadedEvent", new { modelLoaded = true, modelID = server.ModelId });
        await WaitUntilAsync(() => session.Model?.Id == server.ModelId);
        await session.ApplyAsync(config);
        Assert.Empty(session.AllowedChannels);
        Assert.All(session.CreateDraft(config).Channels, b => Assert.False(b.Verified));
        await client.DisconnectAsync();
    }
    [Fact]
    public async Task RepeatedApplyHasSingleWriterAndManualEventPauses()
    {
        await using var server = new FakeVts();
        using var client = new VtsClient(server.Config, Token);
        var config = new ContinuousControlConfig { Enabled = true };
        await using var session = new VtsContinuousSession(client, config);
        await session.ConnectAsync();
        var profile = session.CreateDraft(config);
        var head = profile.Channels.Single(b => b.Channel == "headRoll");
        await session.PrepareInputsAsync(profile); head.Verified = true;
        config.Profiles[profile.ModelId] = profile;
        for (var i = 0; i < 20; i++) await session.ApplyAsync(config);
        var before = server.Count("InjectParameterDataRequest");
        await Task.Delay(400);
        Assert.InRange(server.Count("InjectParameterDataRequest") - before, 5, 18);
        await server.EventAsync("HotkeyTriggeredEvent", new { hotkeyID = "manual" });
        await WaitUntilAsync(() => session.Status.Contains("人工操作"));
        before = server.Count("InjectParameterDataRequest"); await Task.Delay(150);
        Assert.Equal(before, server.Count("InjectParameterDataRequest"));
        Assert.Empty(session.AllowedChannels);
        await client.DisconnectAsync();
    }
    [Fact]
    public async Task StableVtsWithoutBetaExpressionEventUsesPolling()
    {
        await using var server = new FakeVts { NoExpressionEvents = true };
        using var client = new VtsClient(server.Config, Token);
        var config = new ContinuousControlConfig { Enabled = true };
        await using var session = new VtsContinuousSession(client, config);
        await session.ConnectAsync();
        var profile = session.CreateDraft(config);
        await session.PrepareInputsAsync(profile);
        profile.Channels.Single(b => b.Channel == "headRoll").Verified = true;
        config.Profiles[profile.ModelId] = profile;
        await session.ApplyAsync(config);
        server.ActiveExpression = true;
        await WaitUntilAsync(() => session.Status.Contains("人工操作"));
        Assert.Empty(session.AllowedChannels);
        await client.DisconnectAsync();
    }
    [Fact]
    public async Task LateIntentAfterManualTakeoverAndResumeCannotStartOnNewController()
    {
        await using var server = new FakeVts();
        using var client = new VtsClient(server.Config, Token);
        var config = new ContinuousControlConfig { Enabled = true };
        await using var session = new VtsContinuousSession(client, config);
        await session.ConnectAsync();
        var profile = session.CreateDraft(config);
        await session.PrepareInputsAsync(profile);
        profile.Channels.Single(b => b.Channel == "headRoll").Verified = true;
        config.Profiles[profile.ModelId] = profile;
        await session.ApplyAsync(config);
        session.BeginTurn(100);
        await server.EventAsync("HotkeyTriggeredEvent", new { hotkeyID = "manual" });
        await WaitUntilAsync(() => session.Status.Contains("人工操作"));
        await session.ResumeAsync();
        session.Submit(100, new(new Dictionary<string, float> { ["headRoll"] = 1 }, 100, 5000));
        var before = server.Count("InjectParameterDataRequest");
        await WaitUntilAsync(() => server.Count("InjectParameterDataRequest") > before + 5);
        var frame = server.Requests.Last(r => r.GetProperty("messageType").GetString() == "InjectParameterDataRequest");
        Assert.Equal(0, frame.GetProperty("data").GetProperty("parameterValues")[0].GetProperty("value").GetSingle());
        session.BeginTurn(101);
        session.Submit(101, new(new Dictionary<string, float> { ["headRoll"] = 1 }, 100, 5000));
        await WaitUntilAsync(() => server.Requests.Last(r => r.GetProperty("messageType").GetString() == "InjectParameterDataRequest")
            .GetProperty("data").GetProperty("parameterValues")[0].GetProperty("value").GetSingle() > 20);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task StopDuringPreviewCancelsTrialAndLeavesNoWriter()
    {
        await using var server = new FakeVts();
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new());
        await session.ConnectAsync();
        var profile = session.CreateDraft(new());
        var head = profile.Channels.Single(b => b.Channel == "headRoll");
        await session.PrepareInputsAsync(profile);
        var trial = session.TestAsync(profile, head, .4f);
        await WaitUntilAsync(() => server.Count("InjectParameterDataRequest") > 0);
        await session.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trial);
        Assert.Throws<InvalidOperationException>(() => session.ConfirmTrial(profile, head));
        var count = server.Count("InjectParameterDataRequest");
        await Task.Delay(150);
        Assert.Equal(count, server.Count("InjectParameterDataRequest"));
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SocketRestartReusesTokenAndDoesNotReplayIntent()
    {
        await using var server = new FakeVts();
        using var client = new VtsClient(server.Config, Token);
        var config = new ContinuousControlConfig { Enabled = true };
        await using var session = new VtsContinuousSession(client, config);
        await session.ConnectAsync();
        var profile = session.CreateDraft(config);
        await session.PrepareInputsAsync(profile);
        profile.Channels.Single(b => b.Channel == "headRoll").Verified = true;
        config.Profiles[profile.ModelId] = profile;
        await session.ApplyAsync(config);
        session.BeginTurn(100);
        session.Submit(100, new(new Dictionary<string, float> { ["headRoll"] = .7f }));
        await Task.Delay(80); server.Drop();
        await WaitUntilAsync(() => server.Count("AuthenticationRequest") >= 2 && session.Status.Contains("运行中"));
        var before = server.Count("InjectParameterDataRequest");
        await WaitUntilAsync(() => server.Count("InjectParameterDataRequest") > before + 1);
        var latest = server.Requests.Last(r => r.GetProperty("messageType").GetString() == "InjectParameterDataRequest");
        var head = latest.GetProperty("data").GetProperty("parameterValues").EnumerateArray().First(p => p.GetProperty("id").GetString() == "AIVTuberHeadRoll");
        Assert.Equal(0, head.GetProperty("value").GetSingle());
        Assert.Equal(1, server.Count("AuthenticationTokenRequest"));
        await client.DisconnectAsync();
    }
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(4000);
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
