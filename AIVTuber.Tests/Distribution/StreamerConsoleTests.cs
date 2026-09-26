using System.Text.Json;
using System.Text.RegularExpressions;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;
using AIVTuber.Core.Voice;
using AIVTuber.Tests.Auth;

namespace AIVTuber.Tests.Distribution;

/// <summary>
/// U01/U06/U08 at the boundary the WebView host uses: the streamer page files and
/// <see cref="StreamerConsoleController"/> wired to a real runtime, config view-model and
/// account/license. WPF layout (U04) is not covered here — it needs a Windows GUI check.
/// </summary>
public sealed class StreamerConsoleTests : IAsyncDisposable
{
    private static readonly DateTimeOffset ServerStart = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"a2c-{Guid.NewGuid():N}");
    private readonly FakeAuthApi _api = new();
    private readonly ManualClock _clock = new(ServerStart);
    private readonly CloudLicense _license;
    private readonly AccountViewModel _account;
    private readonly BotRuntime _runtime;
    private readonly ConfigViewModel _config;
    private readonly MonitorViewModel _monitor;
    private readonly StreamerConsoleController _controller;
    private readonly List<string> _posted = [];
    private string _clipboard = "";

    public StreamerConsoleTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, DistributionProfile.DirectoryName));
        File.WriteAllText(Path.Combine(_root, DistributionProfile.DirectoryName, DistributionProfile.FileName),
            StreamerConfigTests.Profile());
        var profile = DistributionProfile.TryLoad(_root)!;
        var manager = new ConfigManager(Path.Combine(_root, "config.json")) { Profile = profile };
        var config = manager.Load();
        config.Bilibili.Sessdata = "SESSDATA-cookie-value-123456";
        _runtime = new BotRuntime(config, _root, _ => Task.CompletedTask);
        _license = new CloudLicense(_api, profile.ProfileId, profile.CredentialRevision, "0.36.0", _clock, startBackgroundLoop: false);
        _runtime.UseCloudAccess(_license, profile);
        _account = new AccountViewModel(_license, profile.ProfileId, profile.Account, run => run());
        _config = new ConfigViewModel(_runtime.CurrentConfig, ["USB 麦克风"], manager.Save, _runtime.ApplyConfigAsync)
        {
            Profile = profile,
            ReadEffectiveConfig = () => _runtime.CurrentConfig,
            ReadEffectiveRevision = () => _runtime.ActiveConfigRevision,
            ReadApplyNotice = () => _runtime.LastApplyNotice,
        };
        _monitor = new MonitorViewModel(_runtime, run => run());
        _controller = new StreamerConsoleController(_runtime, _monitor, _config, _account,
            _runtime.CreateVoicePreview(_ => throw new InvalidOperationException("no device in tests")),
            _runtime.CreateVoiceCatalog(_ => null),
            payload => { lock (_posted) _posted.Add(Serialize(payload)); },
            text => _clipboard = text, "v0.36.0-rc.2");
    }

    public async ValueTask DisposeAsync()
    {
        _controller.Dispose();
        await _runtime.DisposeAsync();
        await _license.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task SignInAsync()
    {
        _api.Login = (_, _) => Task.FromResult(new AuthReply(AuthCode.Ok, "tok", "acc", ServerStart,
            ServerStart.AddSeconds(180), ServerStart.AddDays(30), 60));
        await _account.LoginAsync("pw");
    }

    // ── Page contract ──────────────────────────────────────────────────────

    private static string Wwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIVTuber.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "App", "WebUi", "wwwroot");
    }

    [Fact]
    public void DistributionUi_ContainsNoManagedProviderFields()
    {
        var html = File.ReadAllText(Path.Combine(Wwwroot(), "streamer.html"));
        var js = File.ReadAllText(Path.Combine(Wwwroot(), "streamer.js"));

        foreach (var forbidden in new[] { "apiKey", "api_key", "API Key", "baseUrl", "Base URL", "provider", "厂商",
                     "localAsr", "pythonPath", "Python", "voiceId", "Voice ID", "groupId", "Group ID", "模型",
                     "dots", "sampleRate", "patchConfig", "saveConfig", "restartLocalAsr", "profile", "专属包" })
        {
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, js, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StreamerPage_SendsOnlyCommandsTheControllerOffers()
    {
        var js = File.ReadAllText(Path.Combine(Wwwroot(), "streamer.js"));
        var sent = Regex.Matches(js, @"send\(\s*""(\w+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        var ternary = Regex.Matches(js, @"send\([^()]*\?\s*""([A-Za-z]+)""\s*:\s*""([A-Za-z]+)""")
            .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }).ToList();
        var hostOnly = new[] { "startBiliQrLogin", "cancelBiliQrLogin" };

        Assert.NotEmpty(sent);
        Assert.Contains("pauseCompanion", ternary);
        foreach (var name in sent.Concat(ternary))
            Assert.True(StreamerConsoleController.Commands.Contains(name) || hostOnly.Contains(name), $"page sends unknown command {name}");
    }

    [Fact]
    public void PersonaEditor_IsAFirstLevelPage_WithALargeEditor()
    {
        var html = File.ReadAllText(Path.Combine(Wwwroot(), "streamer.html"));

        Assert.Contains("data-page=\"ai\"", html);
        Assert.Matches(@"<textarea id=""persona"" rows=""1[2-6]""", html);
        Assert.Contains("data-focus=\"persona\"", html); // one click from the home page
        Assert.Contains("data-focus=\"voiceSelect\"", html);
    }

    // ── Controller boundary ────────────────────────────────────────────────

    [Theory]
    [InlineData("patchConfig", """{ "llm": { "baseUrl": "https://collector.example.invalid" } }""")]
    [InlineData("saveConfig", """{ "tts": { "apiKey": "sk-other-key-000000" } }""")]
    [InlineData("restartLocalAsr", "{}")]
    [InlineData("continuousVts", """{ "operation": "prepare" }""")]
    [InlineData("getMemory", "{}")]
    public async Task DeveloperCommands_AreRefusedOnTheStreamerPage(string command, string data)
    {
        var before = Serialize(_config.Working);

        await _controller.HandleAsync(command, Json(data));

        Assert.Equal(before, Serialize(_config.Working));
        Assert.Contains(_posted, p => p.Contains("\"kind\":\"rejected\"") && p.Contains(command));
        Assert.Equal("https://api.deepseek.com", _runtime.CurrentConfig.Llm.BaseUrl);
    }

    [Fact]
    public async Task SaveSettings_EchoesRequestIdAndEffectiveRevision()
    {
        await _controller.HandleAsync("saveSettings", Json("""{ "requestId": 7, "patch": { "voice": { "choiceId": "b" } } }"""));

        var reply = _posted.Single(p => p.Contains("\"type\":\"saveResult\""));
        Assert.Contains("\"requestId\":7", reply);
        Assert.Contains("\"ok\":true", reply);
        Assert.Contains($"\"effectiveRevision\":{_runtime.ActiveConfigRevision}", reply);
        Assert.Equal(StreamerConfigTests.VoiceB, _runtime.CurrentConfig.Tts.VoiceId);
        Assert.Contains(_posted, p => p.Contains("\"type\":\"settings\"") && p.Contains("\"effectiveChoiceId\":\"b\""));
    }

    [Fact]
    public async Task Settings_AndState_NeverCarrySecrets()
    {
        await SignInAsync();
        await _controller.HandleAsync("getSettings", default);
        await _controller.HandleAsync("getState", default);

        var all = string.Join("\n", _posted);
        Assert.DoesNotContain("sk-test", all);
        Assert.DoesNotContain("SESSDATA-cookie", all);
        Assert.DoesNotContain(StreamerConfigTests.VoiceA, all);
        Assert.DoesNotContain("streamer-017", all); // package id stays in diagnostics
    }

    [Fact]
    public async Task PauseAndResume_ControlTheRuntime_NotTheAccount()
    {
        await SignInAsync();

        await _controller.HandleAsync("pauseCompanion", default);
        Assert.True(_runtime.CompanionPaused);
        Assert.True(_account.IsSignedIn);
        Assert.Contains(_posted, p => p.Contains("\"activity\":\"已暂停\""));

        await _controller.HandleAsync("resumeCompanion", default);
        Assert.False(_runtime.CompanionPaused);
    }

    [Fact]
    public async Task AccountEnded_IsShownAsRenewal_NotNetwork()
    {
        _api.Login = (_, _) => Task.FromResult(new AuthReply(AuthCode.Ok, "tok", "acc", ServerStart,
            ServerStart.AddSeconds(30), ServerStart.AddSeconds(30), 60));
        await _account.LoginAsync("pw");
        _clock.Advance(TimeSpan.FromSeconds(31));
        _license.EnforceExpiry();

        var state = Serialize(_controller.BuildState());

        Assert.Contains(CloudLicense.AccountEndedMessage, state);
        Assert.DoesNotContain("网络", state);
        Assert.Contains("联系发放者续期", state);
    }

    [Fact]
    public async Task PipelineErrors_AreShownMappedWithDiagnosticId()
    {
        await SignInAsync();
        RaisePipelineError("[ASR连接] WebSocketException: 401 Unauthorized {\"header\":{\"message\":\"Invalid API-key sk-abcdef0123456789\"}}");

        var state = Serialize(_controller.BuildState());

        Assert.Contains("语音识别暂时不可用", state);
        Assert.Matches("诊断编号|\"diagnosticId\":\"D\\d{8}-[0-9A-F]{4}\"", state);
        Assert.DoesNotContain("WebSocketException", state);
        Assert.DoesNotContain("sk-abcdef", state);
        Assert.DoesNotContain("API-key", state);
    }

    [Fact]
    public async Task Diagnostics_AreRedacted()
    {
        await SignInAsync();
        RaisePipelineError("[TTS] POST https://api.minimaxi.com/v1/t2a?sig=abc123&token=zzz failed; Authorization: Bearer sk-live-000000000000 Cookie: SESSDATA=abcdef");

        await _controller.HandleAsync("copyDiagnostics", default);

        Assert.Contains("streamer-017", _clipboard);   // useful for support
        Assert.Contains("凭据修订 1", _clipboard);
        Assert.DoesNotContain("sk-test", _clipboard);    // managed keys, not even tails
        Assert.DoesNotContain("sk-live", _clipboard);
        Assert.DoesNotContain("sig=abc123", _clipboard);
        Assert.DoesNotContain("SESSDATA=abcdef", _clipboard);
        Assert.DoesNotContain("SESSDATA-cookie", _clipboard);
    }

    [Fact]
    public async Task Preview_WithoutSignIn_IsRefusedWithAFriendlyMessage()
    {
        await _controller.HandleAsync("previewVoice", Json("""{ "choiceId": "b" }"""));
        await Task.Delay(100);

        var preview = _posted.Single(p => p.Contains("\"type\":\"preview\""));
        Assert.Contains("\"state\":\"rejected\"", preview);
        Assert.Contains("登录后才能试听", preview);
    }

    private void RaisePipelineError(string message)
    {
        var field = typeof(BotRuntime).GetField("PipelineError",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((EventHandler<string>?)field.GetValue(_runtime))?.Invoke(_runtime, message);
    }
}

public sealed class UserErrorMappingTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("header api_key=sk-1234567890abcdef", "sk-1234567890abcdef")]
    [InlineData("{\"api_key\": \"plain-secret-value\"}", "plain-secret-value")]
    [InlineData("Cookie: SESSDATA=abc%2C123; bili_jct=xyz", "abc%2C123")]
    [InlineData("GET https://oss.example.com/a.wav?Signature=QWERTY&Expires=1", "QWERTY")]
    [InlineData("token 9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c", "9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c")]
    [InlineData("password=hunter2", "hunter2")]
    public void Redactor_RemovesSecrets(string input, string secret)
    {
        var redacted = DiagnosticRedactor.Redact(input);
        Assert.DoesNotContain(secret, redacted);
    }

    [Fact]
    public void Redactor_KeepsTheShapeOfTheMessage()
    {
        Assert.Equal("[TTS] 连接 https://api.minimaxi.com/ws/v1/t2a_v2 失败: timeout",
            DiagnosticRedactor.Redact("[TTS] 连接 https://api.minimaxi.com/ws/v1/t2a_v2 失败: timeout"));
    }

    public static TheoryData<Exception, string, string?> Cases => new()
    {
        { new TaskCanceledException("t", new TimeoutException()), ErrorArea.Voice, "timeout" },
        { new HttpRequestException("x", null, System.Net.HttpStatusCode.Unauthorized), ErrorArea.Voice, "credentials_unavailable" },
        { new HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden), ErrorArea.Voice, "unknown" },
        { new HttpRequestException("x", null, System.Net.HttpStatusCode.TooManyRequests), ErrorArea.Model, "unknown" },
        { new InvalidOperationException("no mic"), ErrorArea.Device, "device_unavailable" },
        { new OperationCanceledException(), ErrorArea.Voice, null },
        { new FormatException("bad json at C:\\Users\\me\\config.json"), ErrorArea.Settings, "unknown" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void UserErrors_DoNotContainRawPayloadsOrSecrets(Exception ex, string area, string? expectedCode)
    {
        var error = UserErrorMapper.FromException(ex, area);

        if (expectedCode is null) { Assert.Null(error); return; } // a normal cancel is not an error
        Assert.NotNull(error);
        Assert.Equal(expectedCode, error.Code);
        Assert.DoesNotContain(ex.Message, error.UserMessage);
        Assert.DoesNotContain(ex.GetType().Name, error.UserMessage);
        Assert.DoesNotContain("Key", error.UserMessage, StringComparison.OrdinalIgnoreCase); // never "fill in your key"
        Assert.Matches(@"^D\d{8}-[0-9A-F]{4}$", error.DiagnosticId);
        Assert.Contains(DiagnosticJournal.Recent(), e => e.Id == error.DiagnosticId);
    }

    [Fact]
    public void SamePipelineError_MapsToOneDiagnosticId()
    {
        var a = UserErrorMapper.FromPipelineMessage("[VTS] 连接失败: refused");
        var b = UserErrorMapper.FromPipelineMessage("[VTS] 连接失败: refused");

        Assert.Equal(a.DiagnosticId, b.DiagnosticId);
        Assert.Equal(ErrorArea.Avatar, a.Area);
        Assert.Contains("陪播语音不受影响", a.UserMessage);
    }
}
