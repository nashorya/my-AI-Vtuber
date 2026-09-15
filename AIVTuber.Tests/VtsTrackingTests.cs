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
        Assert.Contains("headRoll", session.AllowedChannels);
        Assert.DoesNotContain("bodyYaw", session.AllowedChannels);
        Assert.Empty(config.Profiles);
        Assert.DoesNotContain(server.Requests, r => r.GetProperty("messageType").GetString() == "ParameterCreationRequest" &&
            r.GetProperty("data").GetProperty("parameterName").GetString() == "AIVTuberMouthOpen");
        session.BeginTurn(1);
        session.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = 1 }, 100, 5000));
        session.OnRms(.8f);
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f =>
            f.TryGetValue("FaceAngleZ", out var z) && z > 10 && f.TryGetValue("MouthOpen", out var m) && m > .5));
        Assert.All(Frames(server), f => Assert.All(f.Keys, id =>
        {
            if (id.StartsWith("AIVTuberBody", StringComparison.Ordinal)) return;
            Assert.DoesNotContain("AIVTuber", id);
        }));
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
    public async Task ReconnectRestoresOnlyIdleAndActiveExpressionDoesNotBlockResume()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleZ", -30, 30)] };
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        session.BeginTurn(1);
        session.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = 1 }, 100, 5000));
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f =>
            f.TryGetValue("FaceAngleZ", out var z) && z > 10));
        server.Drop();
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => server.Count("AuthenticationRequest") >= 2 && session.Status.Contains("运行中"));
        var count = Frames(server).Length;
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Length > count + 2);
        Assert.Equal(0, Frames(server).Last()["FaceAngleZ"]);
        Assert.DoesNotContain(server.Requests, r => r.GetProperty("messageType").GetString() == "ParameterCreationRequest" &&
            r.GetProperty("data").GetProperty("parameterName").GetString() == "AIVTuberMouthOpen");
        server.ActiveExpression = true;
        await session.ResumeAsync();
        Assert.Contains("headRoll", session.AllowedChannels);
        Assert.DoesNotContain("bodyYaw", session.AllowedChannels);
        Assert.Contains("运行中", session.Status);
    }

    [Fact]
    public async Task ModeSwitchReplacesWriterAndDisablingStopsInjection()
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
        Assert.Contains("FaceAngleZ", Frames(server).Last().Keys);
        config.Enabled = false;
        await session.ApplyAsync(config);
        Assert.Empty(session.AllowedChannels);
        Assert.DoesNotContain(server.Requests, r => r.GetProperty("messageType").GetString() == "ParameterCreationRequest" &&
            r.GetProperty("data").GetProperty("parameterName").GetString() == "AIVTuberMouthOpen");
    }

    [Fact]
    public async Task SameModelReloadKeepsInjectingHeadYaw()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleX", -30, 30)] };
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        Assert.Contains("运行中", session.Status);
        await server.EventAsync("ModelConfigChangedEvent", new { modelID = server.ModelId });
        await server.EventAsync("ModelLoadedEvent", new { modelLoaded = true, modelID = server.ModelId });
        await Task.Delay(200);
        Assert.Contains("运行中", session.Status);
        session.BeginTurn(1);
        session.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .5f }, 100, 5000));
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f => f.ContainsKey("FaceAngleX") && f["FaceAngleX"] != 0));
    }

    [Fact]
    public async Task PinReloadStartsInjectionAndIgnoresOwnLoadEvents()
    {
        var pinned = 0;
        VtsModelFile.TryPinOverride.Value = _ => Interlocked.Increment(ref pinned) == 1;
        try
        {
            await using var server = new FakeVts { DefaultInputs = [P("FaceAngleX", -30, 30)] };
            using var client = new VtsClient(server.Config, Token);
            await using var session = new VtsContinuousSession(client, new() { Enabled = true, AllowModelFilePatch = true });
            await session.ConnectAsync();
            Assert.Equal(1, server.Count("ModelLoadRequest"));
            Assert.Contains("运行中", session.Status);
            Assert.Contains("headYaw", session.AllowedChannels);
            await server.EventAsync("ModelConfigChangedEvent", new { modelID = server.ModelId });
            await server.EventAsync("ModelLoadedEvent", new { modelLoaded = true, modelID = server.ModelId });
            await Task.Delay(200);
            Assert.Contains("运行中", session.Status);
            session.BeginTurn(1);
            session.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .5f }, 100, 5000));
            await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f => f.ContainsKey("FaceAngleX") && f["FaceAngleX"] != 0));
            await session.ApplyAsync(new() { Enabled = true });
            Assert.Equal(1, server.Count("ModelLoadRequest"));
        }
        finally { VtsModelFile.TryPinOverride.Value = null; }
    }

    [Fact]
    public async Task HotkeyDoesNotPauseAndModelChangeRequiresResumeWithoutOldIntent()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleZ", -30, 30)] };
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        session.BeginTurn(1);
        await server.EventAsync("HotkeyTriggeredEvent", new { hotkeyID = "manual" });
        await Task.Delay(150);
        Assert.DoesNotContain("人工操作", session.Status);
        Assert.Contains("headRoll", session.AllowedChannels);
        session.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = 1 }));
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f =>
            f.TryGetValue("FaceAngleZ", out var z) && z > 10));
        server.ModelId = "22222222222222222222222222222222";
        server.DefaultInputs = [P("EyeOpenLeft")];
        await server.EventAsync("ModelLoadedEvent", new { modelID = server.ModelId });
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => session.Model?.Id == server.ModelId &&
            session.AllowedChannels.Contains("eyeOpenL"));
        server.ModelLoaded = false;
        await session.RefreshAsync();
        Assert.Null(session.Model);
        Assert.Empty(session.AllowedChannels);
    }

    [Fact]
    public async Task PinsFacePositionWhenHeadMoves()
    {
        var capture = new Capture();
        var backend = new VtsTrackingBackend(capture,
            [P("FaceAngleX", -30, 30), P("FacePositionX", -10, 10), P("FacePositionY", -10, 10)]);
        await backend.InjectAsync(new Dictionary<string, float> { ["AIVTuberHeadYaw"] = 1 }, default);
        Assert.Equal(0, capture.Frame["FacePositionX"]);
        Assert.Equal(0, capture.Frame["FacePositionY"]);
        Assert.False(capture.Frame.ContainsKey("FacePositionZ"));
    }

    [Fact]
    public async Task BodyChannelStaysClosedUntilMappingIsVerified()
    {
        await using var server = new FakeVts { DefaultInputs = [P("FaceAngleX", -30, 30)] };
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        Assert.Equal(0, server.Count("ModelLoadRequest"));
        Assert.DoesNotContain("bodyYaw", session.AllowedChannels);
        Assert.Contains(session.BodyMappings, r => r.Channel == "bodyYaw" && r.State is "missing" or "unknown");
        Assert.DoesNotContain("复用已有映射，未验证视觉效果", session.Status);
    }

    [Fact]
    public async Task VerifiedBodyProbeOpensBodyChannel()
    {
        await using var server = new FakeVts
        {
            DefaultInputs = [P("FaceAngleX", -30, 30)],
            Live2DParameters = [new("ParamAngleZ", -30, 30, 0, 0), new("ParamBodyAngleX", -10, 10, 0, 0)]
        };
        server.Live2DFromInput["AIVTuberBodyYaw"] = "ParamBodyAngleX";
        using var client = new VtsClient(server.Config, Token);
        await using var session = new VtsContinuousSession(client, new() { Enabled = true });
        await session.ConnectAsync();
        Assert.Contains("bodyYaw", session.AllowedChannels);
        Assert.Contains(session.BodyMappings, r => r.Channel == "bodyYaw" && r.State == "verified" && r.Readback is not null);
        session.BeginTurn(1);
        session.Submit(1, new(new Dictionary<string, float> { ["bodyYaw"] = .6f }, 100, 5000));
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => Frames(server).Any(f =>
            f.TryGetValue("AIVTuberBodyYaw", out var value) && value > .2f));
        await session.TestTrackingAxisAsync("bodyYaw", .45f);
        var axis = Frames(server).Where(f => f.ContainsKey("AIVTuberBodyYaw")).ToArray();
        Assert.Contains(axis, f => f.Count == 1);
        Assert.DoesNotContain(axis.TakeLast(8), f => f.ContainsKey("FaceAngleX") && Math.Abs(f["FaceAngleX"]) > 1);
    }

    [Fact]
    public void PinBodyFollowRewritesOnlyBodyAndStepMappings()
    {
        const string json = """
            {"ParameterSettings":[
              {"Input":"FaceAngleX","OutputLive2D":"ParamAngleX"},
              {"Input":"FaceAngleX","OutputLive2D":"ParamBodyAngleX","OutputRangeLower":-10,"OutputRangeUpper":10},
              {"Input":"FaceAngleX","OutputLive2D":"ParamStep"},
              {"Input":"MouthOpen","OutputLive2D":"ParamMouthOpenY"}
            ]}
            """;
        Assert.True(VtsModelFile.PinBodyFollow(json, out var updated));
        var settings = JsonDocument.Parse(updated).RootElement.GetProperty("ParameterSettings").EnumerateArray().ToArray();
        Assert.Equal("FaceAngleX", settings[0].GetProperty("Input").GetString());
        Assert.Equal("AIVTuberBodyYaw", settings[1].GetProperty("Input").GetString());
        Assert.Equal(-10, settings[1].GetProperty("OutputRangeLower").GetDouble());
        Assert.Equal(10, settings[1].GetProperty("OutputRangeUpper").GetDouble());
        Assert.Equal("", settings[2].GetProperty("Input").GetString());
        Assert.Equal("MouthOpen", settings[3].GetProperty("Input").GetString());
        Assert.False(VtsModelFile.PinBodyFollow(updated, out _));
    }

    [Fact]
    public void PinBodyFollowRetargetsLegacyMouthInput()
    {
        const string json = """
            {"ParameterSettings":[
              {"Input":"AIVTuberMouthOpen","OutputLive2D":"ParamMouthOpenY"},
              {"Input":"MouthSmile","OutputLive2D":"ParamMouthForm"}
            ]}
            """;
        Assert.True(VtsModelFile.PinBodyFollow(json, out var updated));
        var settings = JsonDocument.Parse(updated).RootElement.GetProperty("ParameterSettings").EnumerateArray().ToArray();
        Assert.Equal("MouthOpen", settings[0].GetProperty("Input").GetString());
        Assert.Equal("MouthSmile", settings[1].GetProperty("Input").GetString());
        Assert.False(VtsModelFile.PinBodyFollow(updated, out _));
    }

    [Fact]
    public void PinBodyFollowInvertsClearedBodyAndKeepsStepPlanted()
    {
        const string json = """
            {"ParameterSettings":[
              {"Name":"Body Rotation X","Input":"","OutputLive2D":"ParamBodyAngleX","OutputRangeLower":-10,"OutputRangeUpper":10},
              {"Name":"Step left/right","Input":"","OutputLive2D":"ParamStep"}
            ]}
            """;
        Assert.True(VtsModelFile.PinBodyFollow(json, out var updated));
        var settings = JsonDocument.Parse(updated).RootElement.GetProperty("ParameterSettings").EnumerateArray().ToArray();
        Assert.Equal("AIVTuberBodyYaw", settings[0].GetProperty("Input").GetString());
        Assert.True(settings[0].GetProperty("OutputRangeLower").GetDouble() <
                    settings[0].GetProperty("OutputRangeUpper").GetDouble());
        Assert.Equal("", settings[1].GetProperty("Input").GetString());
        Assert.Equal("AIVTuberBodyPitch", VtsModelFile.BodyInputFor("ParamBodyAngleY", "Body Rotation Y"));
        Assert.Equal("AIVTuberBodyRoll", VtsModelFile.BodyInputFor("ParamBodyAngleZ", "Body Rotation Z"));
    }

    [Fact]
    public async Task HeadYawUsesVisibleFaceAngleRange()
    {
        var capture = new Capture();
        var backend = new VtsTrackingBackend(capture, [P("FaceAngleX", -30, 30)]);
        await backend.InjectAsync(new Dictionary<string, float> { ["AIVTuberHeadYaw"] = 1 }, default);
        Assert.InRange(capture.Frame["FaceAngleX"], 24, 30);
    }

    [Fact]
    public async Task FansOutEyesAndAudioAliasesAndCombinesBrowsInOneBatch()
    {
        var capture = new Capture();
        var backend = new VtsTrackingBackend(capture,             [P("EyeLeftX", -1, 1), P("EyeRightX", -1, 1),
            P("BrowLeftY"), P("BrowRightY"), P("Brows"), P("MouthOpen"), P("VoiceVolume"),
            P("VoiceVolumePlusMouthOpen"), P("AIVTuberBodyYaw", -1, 1)]);
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
        Assert.DoesNotContain("AIVTuberMouthOpen", capture.Frame.Keys);
        Assert.Equal(1, capture.Frame["AIVTuberBodyYaw"]);
        Assert.Contains("bodyYaw", backend.Channels);
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
