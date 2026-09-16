using AIVTuber.Core.Avatar;
using AIVTuber.Core.Vts;

namespace AIVTuber.Tests;

public class AvatarMotionSemanticsTests
{
    private static AvatarChannelBinding Binding(string name) => new()
    {
        Channel = name, ParameterId = "P", Minimum = -30, Maximum = 30, Neutral = 0, Verified = true
    };

    [Fact]
    public async Task HeadYawKeepsSignAndZeroIsNeutralNotShake()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(), [Binding("headYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .5f }, 400, 2000));
        clock.Advance(400);
        Assert.InRange(director.Sample()["AIVTuberHeadYaw"], 14.5f, 15.5f);

        director.Submit(2, new(new Dictionary<string, float> { ["headYaw"] = -.5f }, 400, 2000));
        clock.Advance(400);
        Assert.InRange(director.Sample()["AIVTuberHeadYaw"], -15.5f, -14.5f);

        director.Submit(3, new(new Dictionary<string, float> { ["headYaw"] = 0 }, 400, 2000));
        clock.Advance(400);
        var held = director.Sample()["AIVTuberHeadYaw"];
        clock.Advance(200);
        var later = director.Sample()["AIVTuberHeadYaw"];
        Assert.InRange(held, -1.5f, 1.5f);
        Assert.InRange(later, -1.5f, 1.5f);
    }

    [Fact]
    public async Task ShakeGestureOscillatesUsingSignedAmplitude()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(), [Binding("headYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .5f }, 400, 1600, "摇头"));
        clock.Advance(300);
        var first = director.Sample()["AIVTuberHeadYaw"];
        clock.Advance(600);
        var second = director.Sample()["AIVTuberHeadYaw"];
        Assert.True(first > 8);
        Assert.True(second < -8);

        director.Submit(2, new(new Dictionary<string, float> { ["headYaw"] = -.5f }, 400, 1600, "摇头"));
        clock.Advance(300);
        Assert.True(director.Sample()["AIVTuberHeadYaw"] < -8);
    }

    [Fact]
    public void MissingBodyIsNotParsedAsZero()
    {
        var plan = AvatarReplyProtocol.Parse(
            "{\"respond\":true,\"speech\":\"好。\",\"avatar\":{\"targets\":{\"headYaw\":0.4,\"bodyYaw\":null}}}",
            ["headYaw", "bodyYaw"]);
        Assert.Equal(.4f, plan.Intent!.Targets["headYaw"]);
        Assert.False(plan.Intent.Targets.ContainsKey("bodyYaw"));
        Assert.Null(plan.Diagnostic);
    }

    [Fact]
    public void ExplicitBodyZeroIsKeptAndDistinctFromOmittedBody()
    {
        var zero = AvatarReplyProtocol.Parse(
            "{\"respond\":true,\"speech\":\"好。\",\"avatar\":{\"targets\":{\"headYaw\":0.5,\"bodyYaw\":0}}}",
            ["headYaw", "bodyYaw"]);
        Assert.True(zero.Intent!.Targets.ContainsKey("bodyYaw"));
        Assert.Equal(0, zero.Intent.Targets["bodyYaw"]);

        var omitted = AvatarReplyProtocol.Parse(
            "{\"respond\":true,\"speech\":\"好。\",\"avatar\":{\"targets\":{\"headYaw\":0.5}}}",
            ["headYaw", "bodyYaw"]);
        Assert.False(omitted.Intent!.Targets.ContainsKey("bodyYaw"));
    }

    [Fact]
    public void MotionShakeDoesNotUseBareHeadYaw()
    {
        var plan = AvatarReplyProtocol.Parse(
            "{\"respond\":true,\"speech\":\"好呀。\",\"motion\":\"摇头\"}",
            ["headYaw"]);
        Assert.Equal("摇头", plan.Intent!.Gesture);
        Assert.Equal(.5f, plan.Intent.Targets["headYaw"]);
        Assert.Equal("摇头", AvatarReplyProtocol.InferRequestedMotion("使用者（纳什）：摇摇头呗")!.Gesture);
    }

    [Fact]
    public async Task HeadOnlySubmitDoesNotWipeHeldBody()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(),
            [Binding("headYaw"), Binding("bodyYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["bodyYaw"] = .45f }, 200, 4000));
        clock.Advance(200);
        Assert.InRange(director.Sample()["AIVTuberBodyYaw"], 13f, 14f);

        director.Submit(2, new(new Dictionary<string, float> { ["headYaw"] = .3f }, 200, 4000));
        clock.Advance(200);
        var frame = director.Sample();
        Assert.InRange(frame["AIVTuberBodyYaw"], 13f, 14f);
        Assert.InRange(frame["AIVTuberHeadYaw"], 8.5f, 9.5f);
    }

    [Fact]
    public async Task ExplicitBodyZeroIsNotOverriddenByHeadFollow()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(),
            [Binding("headYaw"), Binding("bodyYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .8f, ["bodyYaw"] = 0 }, 400, 2000));
        for (var i = 0; i < 8; i++)
        {
            clock.Advance(50);
            director.Sample();
        }
        var frame = director.Sample();
        Assert.True(frame["AIVTuberHeadYaw"] > 18);
        Assert.InRange(frame["AIVTuberBodyYaw"], -1.2f, 1.2f);
    }

    [Fact]
    public async Task LargeLookMovesBodySlowerAndSmallerThanHead()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(),
            [Binding("headYaw"), Binding("bodyYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .8f }, 200, 3000));
        clock.Advance(50);
        var early = director.Sample();
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(50);
            director.Sample();
        }
        var late = director.Sample();
        Assert.True(late["AIVTuberHeadYaw"] > 20);
        Assert.True(late["AIVTuberBodyYaw"] > early["AIVTuberBodyYaw"]);
        // Body follows a large look substantially (official-rig feel, ~0.8 semantic)
        // but stays below the head and eases in over several frames, not frame-copied.
        Assert.True(late["AIVTuberBodyYaw"] > Math.Abs(late["AIVTuberHeadYaw"]) * .55f,
            $"body {late["AIVTuberBodyYaw"]} did not follow head {late["AIVTuberHeadYaw"]}");
        Assert.True(late["AIVTuberBodyYaw"] < Math.Abs(late["AIVTuberHeadYaw"]) * .95f);
    }

    [Fact]
    public async Task TinyHeadLookDoesNotCopyOntoBody()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(),
            [Binding("headYaw"), Binding("bodyYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .08f }, 200, 2000));
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(50);
            director.Sample();
        }
        var frame = director.Sample();
        Assert.InRange(frame["AIVTuberHeadYaw"], 1.5f, 3.5f);
        Assert.InRange(frame["AIVTuberBodyYaw"], -1.2f, 1.2f);
    }

    [Fact]
    public void InspectReportsMissingAndRangeMismatchWithoutRewritingOutput()
    {
        const string coupled = """
            {"ParameterSettings":[
              {"Input":"FaceAngleX","OutputLive2D":"ParamBodyAngleX","InputRangeLower":-30,"InputRangeUpper":30,"OutputRangeLower":-10,"OutputRangeUpper":10}
            ]}
            """;
        var missing = VtsModelFile.InspectBodyMappings(coupled);
        Assert.Contains(missing, r => r.Output == "ParamBodyAngleX" && r.Issue == "head-coupled");

        const string wrongRange = """
            {"ParameterSettings":[
              {"Input":"AIVTuberBodyYaw","OutputLive2D":"ParamBodyAngleX","InputRangeLower":-30,"InputRangeUpper":30,"OutputRangeLower":8,"OutputRangeUpper":-12}
            ]}
            """;
        var mismatch = VtsModelFile.InspectBodyMappings(wrongRange);
        Assert.Contains(mismatch, r => r.Issue == "range-mismatch" && r.OutputLower == 8 && r.OutputUpper == -12);

        Assert.True(VtsModelFile.PinBodyFollow(wrongRange, out var pinned));
        var settings = System.Text.Json.JsonDocument.Parse(pinned).RootElement.GetProperty("ParameterSettings")[0];
        Assert.Equal("AIVTuberBodyYaw", settings.GetProperty("Input").GetString());
        Assert.Equal(-1, settings.GetProperty("InputRangeLower").GetDouble());
        Assert.Equal(1, settings.GetProperty("InputRangeUpper").GetDouble());
        Assert.Equal(8, settings.GetProperty("OutputRangeLower").GetDouble());
        Assert.Equal(-12, settings.GetProperty("OutputRangeUpper").GetDouble());
    }

    [Fact]
    public void PromptTreatsHeadYawAsLookTarget()
    {
        var prompt = AvatarReplyProtocol.Prompt(["headYaw", "bodyYaw"]);
        Assert.Contains("motion", prompt);
        Assert.Contains("摇头", prompt);
        Assert.DoesNotContain("程序按这个幅度左右摆头", prompt);
        Assert.DoesNotContain("不必再写 bodyYaw", prompt);
        Assert.Contains("省略或写 null 的通道本次保持不动", prompt);
    }

    [Fact]
    public async Task CancelReturnsEyelidsToRestInsteadOfSlammingShut()
    {
        var clock = new ContinuousControlTests.ManualClock();
        var eye = new AvatarChannelBinding
        {
            Channel = "eyeOpenL", ParameterId = "P", Minimum = 0, Maximum = 1, Neutral = 1, Verified = true
        };
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(),
            [Binding("headYaw"), eye], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["eyeOpenL"] = .2f, ["headYaw"] = .5f }, 200, 3000));
        clock.Advance(200);
        var narrowed = director.Sample()["AIVTuberEyeOpenL"];
        Assert.InRange(narrowed, .15f, .25f);

        director.Cancel(1);
        clock.Advance(150);
        var opening = director.Sample()["AIVTuberEyeOpenL"];
        clock.Advance(750);
        var settled = director.Sample();
        // 0 is "closed" for eyelids: cancelling must reopen toward rest, never shut them.
        Assert.True(opening > narrowed + .1f, $"cancel drifted toward closed: {narrowed} -> {opening}");
        Assert.True(settled["AIVTuberEyeOpenL"] > .8f, $"eyes did not return to rest: {settled["AIVTuberEyeOpenL"]}");
        Assert.InRange(settled["AIVTuberHeadYaw"], -.05f, .05f);
    }

    [Fact]
    public async Task CancelBlendsBodyBackToNeutralWithoutSnap()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(),
            [Binding("headYaw"), Binding("bodyYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["bodyYaw"] = .45f }, 200, 3000));
        clock.Advance(200);
        // Binding maps semantic -1..1 onto ±30; assert in injected units throughout.
        var previous = director.Sample()["AIVTuberBodyYaw"];
        Assert.InRange(previous, 13f, 14f);

        director.Cancel(1);
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(50);
            var current = director.Sample()["AIVTuberBodyYaw"];
            Assert.True(Math.Abs(current - previous) <= 4.5f, $"snap of {Math.Abs(current - previous)} at frame {i}");
            previous = current;
        }
        Assert.InRange(previous, -1.5f, 1.5f);
    }

    [Fact]
    public async Task FollowResumesGraduallyAfterExplicitNeutralPoseExpires()
    {
        var clock = new ContinuousControlTests.ManualClock();
        await using var director = new AvatarMotionDirector(new ContinuousControlTests.CaptureBackend(),
            [Binding("headYaw"), Binding("bodyYaw")], clock);
        director.Submit(1, new(new Dictionary<string, float> { ["headYaw"] = .8f }, 200, 4000));
        director.Submit(1, new(new Dictionary<string, float> { ["bodyYaw"] = 0 }, 200, 800));
        clock.Advance(600);
        Assert.InRange(director.Sample()["AIVTuberBodyYaw"], -1.5f, 1.5f);

        // The explicit neutral pose lives 200+800+400ms; after it expires the default
        // follow policy may re-engage, but only as a smooth drift, never a snap.
        clock.Advance(400);
        director.Sample();
        var previous = 0f;
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(50);
            var current = director.Sample()["AIVTuberBodyYaw"];
            Assert.True(Math.Abs(current - previous) <= 3.5f, $"follow snap of {Math.Abs(current - previous)} at frame {i}");
            previous = current;
        }
        // Desired follow is headYaw(.8)*.8 = .64 semantic ≈ 19 injected units.
        Assert.InRange(previous, 3f, 19.2f);
    }
}
