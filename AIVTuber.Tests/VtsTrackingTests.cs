using System.Text.Json;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;
using AIVTuber.Core.Vts;

namespace AIVTuber.Tests;

public sealed class VtsTrackingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vts-tracking-" + Guid.NewGuid().ToString("N"));
    private string Token => Path.Combine(_directory, "token");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private static VtsParameter P(string id, float min = 0, float max = 1) => new(id, min, max, min, min);
    private sealed class Capture : IAvatarParameterBackend
    {
        public IReadOnlyDictionary<string, float> Frame = new Dictionary<string, float>();
        public Task InjectAsync(IReadOnlyDictionary<string, float> values, CancellationToken ct) { Frame = values; return Task.CompletedTask; }
    }

    [Fact]
    public async Task ReusesStandardInputsWithoutProfileOrParameterCreation()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleZ", -30, 30), P("MouthOpen")], FailType = "ParameterCreationRequest" };
        using var client = new VtsClient(server.Config, Token);
        var config = new ContinuousControlConfig { Enabled = true };
        await using var session = new VtsContinuousSession(client, config);
        await session.ConnectAsync();
        Assert.Equal(new[] { "headRoll" }, session.AllowedChannels);
        Assert.Empty(config.Profiles);
        Assert.Equal(0, server.Count("ParameterCreationRequest"));
        session.BeginTurn(1);
        session.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = 1 }, 100, 5000));
        session.OnRms(.8f);
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f => f["FaceAngleZ"] > 10 && f["MouthOpen"] > .5));
        Assert.All(Frames(server), f => Assert.All(f.Keys, id => Assert.DoesNotContain("AIVTuber", id)));
        await session.StopAsync();
        var count = server.Count("InjectParameterDataRequest");
        await Task.Delay(150);
        Assert.Equal(count, server.Count("InjectParameterDataRequest"));
        Assert.Empty(session.AllowedChannels);
    }

    private static Dictionary<string, float>[] Frames(FakeVts server) => server.Requests
        .Where(r => r.GetProperty("messageType").GetString() == "InjectParameterDataRequest")
        .Select(r => r.GetProperty("data").GetProperty("parameterValues").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("value").GetSingle())).ToArray();

    [Fact]
    public async Task MatchingLive2DOutputDoesNotInventTrackingSupport()
    {
        await using var server = new FakeVts(); // ParamAngleZ exists, but no standard inputs.
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        Assert.Empty(session.AllowedChannels);
        Assert.Contains("没有可用", session.Status);
        Assert.Empty(Frames(server));
        Assert.Equal(0, server.Count("ParameterCreationRequest"));
    }

    [Fact]
    public async Task ReconnectRestoresOnlyIdleAndActiveExpressionBlocksResume()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleZ", -30, 30)] };
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        session.BeginTurn(1);
        session.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = 1 }, 100, 5000));
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f => f["FaceAngleZ"] > 10));
        server.Drop();
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => server.Count("AuthenticationRequest") >= 2 && session.Status.Contains("运行中"));
        var count = Frames(server).Length;
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Length > count + 2);
        Assert.Equal(0, Frames(server).Last()["FaceAngleZ"]);
        Assert.Equal(0, server.Count("ParameterCreationRequest"));
        server.ActiveExpression = true;
        await session.ResumeAsync();
        Assert.Empty(session.AllowedChannels);
        Assert.Contains("活动表情", session.Status);
    }

    [Fact]
    public async Task ModeSwitchReplacesWriterAndDisablingRestoresLegacyMouth()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleZ", -30, 30)] };
        using var client = new VtsClient(server.Config, Token);
        var config = new ContinuousControlConfig { Enabled = true };
        await using var session = new VtsContinuousSession(client, config);
        await session.ConnectAsync();
        for (var i = 0; i < 20; i++) await session.ApplyAsync(config);
        var count = server.Count("InjectParameterDataRequest");
        await Task.Delay(400);
        Assert.InRange(server.Count("InjectParameterDataRequest") - count, 5, 18);
        config.UseBuiltInTracking = false;
        await session.ApplyAsync(config);
        Assert.Empty(session.AllowedChannels);
        var profile = session.CreateDraft(config);
        var head = profile.Channels.Single(b => b.Channel == "headRoll");
        await session.PrepareInputsAsync(profile);
        head.Verified = true;
        config.Profiles[profile.ModelId] = profile;
        await session.ApplyAsync(config);
        count = Frames(server).Length;
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Length > count);
        Assert.Contains("AIVTuberHeadRoll", Frames(server).Last().Keys);
        config.UseBuiltInTracking = true;
        await session.ApplyAsync(config);
        count = Frames(server).Length;
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Length > count);
        Assert.Equal(new[] { "FaceAngleZ" }, Frames(server).Last().Keys);
        config.Enabled = false;
        await session.ApplyAsync(config);
        Assert.Empty(session.AllowedChannels);
        await client.SetMouthAsync(.5f);
        Assert.Contains(server.Requests, r => r.GetProperty("messageType").GetString() == "ParameterCreationRequest" &&
            r.GetProperty("data").GetProperty("parameterName").GetString() == "AIVTuberMouthOpen");
    }

    [Fact]
    public async Task ManualTakeoverAndModelChangeRequireResumeWithoutOldIntent()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleZ", -30, 30)] };
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        session.BeginTurn(1);
        await server.EventAsync("HotkeyTriggeredEvent", new { hotkeyID = "manual" });
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => session.Status.Contains("人工操作"));
        Assert.Empty(session.AllowedChannels);
        await session.ResumeAsync();
        session.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = 1 }));
        var count = Frames(server).Length;
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Length > count + 3);
        Assert.Equal(0, Frames(server).Last()["FaceAngleZ"]);
        server.ModelId = "22222222222222222222222222222222";
        server.DefaultInputs = [P("EyeOpenLeft")];
        await server.EventAsync("ModelLoadedEvent", new { modelID = server.ModelId });
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => session.Model?.Id == server.ModelId);
        Assert.Empty(session.AllowedChannels);
        await session.ResumeAsync();
        Assert.Equal(new[] { "eyeOpenL" }, session.AllowedChannels);
        server.ModelLoaded = false;
        await session.RefreshAsync();
        Assert.Null(session.Model);
        Assert.Empty(session.AllowedChannels);
    }

    [Fact]
    public async Task FansOutEyesAndAudioAliasesAndCombinesBrowsInOneBatch()
    {
        var capture = new Capture();
        var backend = new VtsTrackingBackend(capture, [P("EyeLeftX", -1, 1), P("EyeRightX", -1, 1),
            P("BrowLeftY"), P("BrowRightY"), P("Brows"), P("MouthOpen"), P("VoiceVolume"), P("VoiceVolumePlusMouthOpen")]);
        await backend.InjectAsync(new Dictionary<string, float>
        {
            ["AIVTuberGazeX"] = .6f, ["AIVTuberBrowHeightL"] = 1, ["AIVTuberBrowHeightR"] = -1,
            ["AIVTuberMouthOpen"] = .7f, ["AIVTuberBodyYaw"] = 1
        }, default);
        Assert.Equal(capture.Frame["EyeLeftX"], capture.Frame["EyeRightX"]);
        Assert.Equal(.5f, capture.Frame["Brows"]);
        Assert.True(capture.Frame["BrowLeftY"] > capture.Frame["BrowRightY"]);
        Assert.Equal(.7f, capture.Frame["MouthOpen"]);
        Assert.Equal(.7f, capture.Frame["VoiceVolumePlusMouthOpen"]);
        Assert.Equal(.7f, capture.Frame["VoiceVolume"]);
        Assert.DoesNotContain("bodyYaw", backend.Channels);
        Assert.DoesNotContain("browFormL", backend.Channels);
        Assert.DoesNotContain("breath", backend.Channels);
    }

    [Fact]
    public async Task RespectsReportedRangesAndRejectsUnknownOrInvalidInputs()
    {
        var capture = new Capture();
        var backend = new VtsTrackingBackend(capture, [P("FaceAngleZ", -5, 6), P("EyeOpenLeft"),
            P("EyeOpenRight", 1, 1), P("MouthSmile", float.NaN, 1), P("ArbitraryInput")]);
        Assert.Equal(new[] { "headRoll", "eyeOpenL" }, backend.Channels);
        var values = new List<float>();
        foreach (var level in new[] { .2f, .4f, .7f, 10f })
        {
            await backend.InjectAsync(new Dictionary<string, float> { ["AIVTuberHeadRoll"] = level, ["AIVTuberEyeOpenL"] = 1 }, default);
            values.Add(capture.Frame["FaceAngleZ"]);
            Assert.InRange(values.Last(), -5, 6);
            Assert.Equal(1, capture.Frame["EyeOpenLeft"]);
        }
        Assert.True(values[0] < values[1] && values[1] < values[2]);
        Assert.Equal(6, values[3]);
    }

    [Fact]
    public void DefaultModeAndWebPatchRoundTripAndDiff()
    {
        var original = new AppConfig();
        Assert.True(original.Vts.ContinuousControl.UseBuiltInTracking);
        Assert.False(original.Vts.ContinuousControl.Enabled);
        var vm = new ConfigViewModel(original, [], _ => { }, _ => Task.CompletedTask);
        vm.ApplyWebPatch(JsonDocument.Parse("{\"vts\":{\"continuousControl\":{\"useBuiltInTracking\":false}}}").RootElement);
        var changed = ConfigManager.Clone(vm.Working);
        Assert.False(changed.Vts.ContinuousControl.UseBuiltInTracking);
        var diff = ConfigDiff.Compute(original, changed);
        Assert.True(diff.HasFlag(RuntimeChange.UpdateVtsParams));
        Assert.True(diff.HasFlag(RuntimeChange.RebuildLlm));
        vm.ImportContinuousProfiles("{\"use_built_in_tracking\":true,\"profiles\":{}}");
        Assert.False(vm.Working.Vts.ContinuousControl.UseBuiltInTracking);
    }
}
