using AIVTuber.Core.Avatar;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using System.Text.Json;

namespace AIVTuber.Tests;

public class ContinuousControlTests
{
    [Theory]
    [InlineData("你好。", "Speak")]
    [InlineData("（围裙也饿了。）", "InnerThought")]
    [InlineData("【PASS】", "Pass")]
    public void StructuredReplyKeepsExistingClassification(string reply, string kind)
    {
        var parsed = AvatarReplyProtocol.Parse(JsonSerializer.Serialize(new { reply, avatar = new { targets = new { headRoll = .2 } } }), ["headRoll"]);
        Assert.Equal(reply, parsed.Reply);
        Assert.Equal(kind, ReplyClassifier.Classify(parsed.Reply).Kind.ToString());
        Assert.Equal(.2f, parsed.Intent!.Targets["headRoll"]);
        Assert.DoesNotContain("targets", parsed.Reply);
    }
    [Theory]
    [InlineData("{\"reply\":\"你好\"")]
    [InlineData("{\"reply\":123}")]
    [InlineData("这不是 JSON")]
    public void BadEnvelopeCannotBecomeSpeech(string json) => Assert.ThrowsAny<JsonException>(() => AvatarReplyProtocol.Parse(json, []));
    [Theory]
    [InlineData("{\"targets\":{\"mouthOpen\":1}}")]
    [InlineData("{\"targets\":{\"unknown\":1}}")]
    [InlineData("{\"targets\":{\"headRoll\":2}}")]
    [InlineData("{\"targets\":{\"headRoll\":0,\"headRoll\":1}}")]
    [InlineData("{\"targets\":{\"eyeOpenL\":-1}}")]
    [InlineData("123")]
    public void BadAvatarKeepsReply(string avatar)
    {
        var plan = AvatarReplyProtocol.Parse("{\"reply\":\"（想下班）\",\"avatar\":" + avatar + "}", ["headRoll", "eyeOpenL"]);
        Assert.Equal("（想下班）", plan.Reply);
        Assert.Null(plan.Intent);
        Assert.NotNull(plan.Diagnostic);
    }
    [Fact]
    public void PromptContainsOnlySemanticCapabilities()
    {
        var prompt = AvatarReplyProtocol.Prompt(["headRoll", "gazeX"]);
        Assert.Contains("headRoll", prompt);
        Assert.Contains("gazeX", prompt);
        Assert.DoesNotContain("headYaw", prompt);
        Assert.DoesNotContain("eyeOpenL", prompt);
        Assert.DoesNotContain("mouthOpen", prompt);
        Assert.DoesNotContain("AIVTuberHeadRoll", prompt);
        Assert.DoesNotContain("ParamAngleZ", prompt);
    }
    [Fact]
    public void LimitsAndUnverifiedChannels()
    {
        const string json = "{\"reply\":\"hi\",\"avatar\":{\"targets\":{\"headRoll\":0.3},\"transitionMs\":9999,\"holdMs\":-3}}";
        var plan = AvatarReplyProtocol.Parse(json, ["headRoll"]);
        Assert.Equal(2000, plan.Intent!.TransitionMs);
        Assert.Equal(0, plan.Intent.HoldMs);
        Assert.Null(AvatarReplyProtocol.Parse(json, []).Intent);
    }
    [Fact]
    public void ProfileExportImportKeepsCalibrationButRequiresLocalVerification()
    {
        var config = new AppConfig();
        config.Vts.ContinuousControl.Enabled = true;
        config.Vts.ContinuousControl.Profiles["model"] = new() { ModelId = "model", Revision = "old", Channels = [Binding("headRoll")] };
        var source = new AIVTuber.Core.ViewModels.ConfigViewModel(config, [], _ => { }, _ => Task.CompletedTask);
        var target = new AIVTuber.Core.ViewModels.ConfigViewModel(new(), [], _ => { }, _ => Task.CompletedTask);
        var exported = source.ExportContinuousProfiles();
        Assert.DoesNotContain("token", exported, StringComparison.OrdinalIgnoreCase);
        target.ImportContinuousProfiles(exported);
        Assert.False(target.Working.Vts.ContinuousControl.Enabled);
        var imported = target.Working.Vts.ContinuousControl.Profiles["model"];
        Assert.Equal("", imported.Revision);
        Assert.False(imported.Channels[0].Verified);
        Assert.Equal(30, imported.Channels[0].Maximum);
        Assert.True(target.IsDirty);
    }

    [Fact]
    public void ConfigRoundTripAndDiffUseIndependentProfiles()
    {
        var a = new AppConfig();
        Assert.False(a.Vts.ContinuousControl.Enabled);
        a.Vts.ContinuousControl.Profiles["model"] = new() { ModelId = "model", Channels = [Binding("headRoll")] };
        var b = ConfigManager.Clone(a);
        b.Vts.ContinuousControl.Enabled = true;
        b.Vts.ContinuousControl.Profiles["model"].Channels[0].Maximum = 17;
        Assert.Equal(30, a.Vts.ContinuousControl.Profiles["model"].Channels[0].Maximum);
        var diff = ConfigDiff.Compute(a, b);
        Assert.True(diff.HasFlag(RuntimeChange.UpdateVtsParams));
        Assert.True(diff.HasFlag(RuntimeChange.RebuildLlm));
    }
    internal static AvatarChannelBinding Binding(string name) => new()
    {
        Channel = name, ParameterId = "ParamAngleZ", Minimum = -30, Maximum = 30, Neutral = 0, Verified = true
    };
    internal sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void Advance(int ms) => _ticks += ms;
    }
    internal sealed class CaptureBackend : IAvatarParameterBackend
    {
        public int Active, MaxActive, Count;
        public int DelayMs;
        public async Task InjectAsync(IReadOnlyDictionary<string, float> values, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref Active);
            MaxActive = Math.Max(current, MaxActive);
            try { Interlocked.Increment(ref Count); await Task.Delay(DelayMs, ct); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    [Fact]
    public async Task TargetsInterpolateHoldAndReturnWithoutOvershoot()
    {
        var clock = new ManualClock();
        await using var director = new AvatarMotionDirector(new CaptureBackend(), [Binding("headRoll")], clock);
        director.Sample();
        director.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = .4f }, 400, 1000));
        clock.Advance(200); Assert.InRange(director.Sample()["AIVTuberHeadRoll"], 5.9f, 6.1f);
        clock.Advance(200); Assert.Equal(12, director.Sample()["AIVTuberHeadRoll"]);
        clock.Advance(1000); Assert.Equal(12, director.Sample()["AIVTuberHeadRoll"]);
        clock.Advance(200); Assert.InRange(director.Sample()["AIVTuberHeadRoll"], 5.9f, 6.1f);
        clock.Advance(200); Assert.Equal(0, director.Sample()["AIVTuberHeadRoll"]);
    }
    [Fact]
    public async Task StrengthsAreContinuousAndOldGenerationCannotOverwrite()
    {
        var clock = new ManualClock();
        await using var director = new AvatarMotionDirector(new CaptureBackend(), [Binding("headRoll")], clock);
        foreach (var strength in new[] { .2f, .4f, .7f })
        {
            director.Submit(10, new(new Dictionary<string, float> { ["headRoll"] = strength }));
            clock.Advance(400);
            Assert.Equal(30 * strength, director.Sample()["AIVTuberHeadRoll"], 3);
        }
        director.Submit(9, new(new Dictionary<string, float> { ["headRoll"] = -1 }));
        clock.Advance(100); Assert.True(director.Sample()["AIVTuberHeadRoll"] > 0);
        director.Cancel(10); clock.Advance(300); Assert.Equal(0, director.Sample()["AIVTuberHeadRoll"]);
    }
    [Fact]
    public async Task BlinkMultipliesEyelidAndAudioOwnsMouth()
    {
        var clock = new ManualClock();
        var eye = Binding("eyeOpenL"); eye.Minimum = 0; eye.Maximum = 1; eye.Neutral = 1;
        var mouth = Binding("mouthOpen"); mouth.Minimum = 0; mouth.Maximum = 1;
        await using var director = new AvatarMotionDirector(new CaptureBackend(), [eye, mouth], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["eyeOpenL"] = .5f, ["mouthOpen"] = 1 }));
        clock.Advance(400); Assert.Equal(.5f, director.Sample()[eye.InputId]);
        Assert.Equal(0, director.Sample()[mouth.InputId]);
        director.OnRms(.8f); clock.Advance(100); Assert.InRange(director.Sample()[mouth.InputId], .5f, .8f);
        clock.Advance(4290); Assert.InRange(director.Sample()[eye.InputId], 0, .001f);
    }
    [Fact]
    public async Task SlowBackendHasOneWriterAndBoundedLatestFrames()
    {
        var backend = new CaptureBackend { DelayMs = Timeout.Infinite };
        await using var director = new AvatarMotionDirector(backend, [Binding("headRoll")]);
        director.Start();
        await VtsContinuousIntegrationTests.WaitUntilAsync(() => director.ProducedFrames >= 8);
        await director.DisposeAsync();
        Assert.Equal(1, backend.MaxActive);
        Assert.True(director.ProducedFrames > backend.Count * 2);
        Assert.True(director.SupersededFrames > 0);
        var count = backend.Count;
        await Task.Delay(100); Assert.Equal(count, backend.Count);
    }
    [Fact]
    public async Task RunningBindingsDoNotAliasUiDraft()
    {
        var clock = new ManualClock(); var binding = Binding("headRoll");
        await using var director = new AvatarMotionDirector(new CaptureBackend(), [binding], clock);
        binding.Maximum = 1000;
        director.Submit(1, new(new Dictionary<string, float> { ["headRoll"] = 1 }));
        clock.Advance(400); Assert.Equal(30, director.Sample()[binding.InputId]);
    }
}
