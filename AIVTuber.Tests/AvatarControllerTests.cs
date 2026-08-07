using AIVTuber.Core.Avatar;
using AIVTuber.Core.Config;
using AIVTuber.Core.Vts;

namespace AIVTuber.Tests;

public sealed class AvatarControllerTests
{
    private sealed class FakeAvatar : IAvatarController
    {
        public List<string> Calls { get; } = [];
        private readonly string _name;
        public FakeAvatar(string name = "fake") => _name = name;

        public Task StartAsync(CancellationToken ct = default)
        {
            Calls.Add($"{_name}:start");
            return Task.CompletedTask;
        }

        public void OnRms(float rms) => Calls.Add($"{_name}:rms:{rms:F2}");

        public Task SetEmotionAsync(string emotion, TimeSpan? hold = null, CancellationToken ct = default)
        {
            Calls.Add($"{_name}:emotion:{emotion}");
            return Task.CompletedTask;
        }

        public Task TriggerActionAsync(string action, CancellationToken ct = default)
        {
            Calls.Add($"{_name}:action:{action}");
            return Task.CompletedTask;
        }

        public void SetPose(string pose) => Calls.Add($"{_name}:pose:{pose}");

        public Task CloseMouthAsync(CancellationToken ct = default)
        {
            Calls.Add($"{_name}:closemouth");
            return Task.CompletedTask;
        }

        public void SetListening(bool userSpeaking) => Calls.Add($"{_name}:listening:{userSpeaking}");
        public void ShowSticker(string stickerId) => Calls.Add($"{_name}:sticker:{stickerId}");
        public void SetIdleState(string state) => Calls.Add($"{_name}:idle:{state}");

        public ValueTask DisposeAsync()
        {
            Calls.Add($"{_name}:dispose");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Swappable_WithoutInner_AllMembersNoOp()
    {
        var facade = new SwappableAvatarController();
        facade.OnRms(0.5f);
        facade.SetPose("front");
        await facade.SetEmotionAsync("happy");
        await facade.TriggerActionAsync("nod");
        await facade.CloseMouthAsync();
        await facade.DisposeAsync(); // nothing throws
    }

    [Fact]
    public async Task Swappable_RoutesToInner_AndSwapReturnsOld()
    {
        var facade = new SwappableAvatarController();
        var first = new FakeAvatar("a");
        var second = new FakeAvatar("b");

        Assert.Null(facade.Swap(first));
        await facade.SetEmotionAsync("happy");
        Assert.Contains("a:emotion:happy", first.Calls);

        var old = facade.Swap(second);
        Assert.Same(first, old);
        await facade.TriggerActionAsync("nod");
        facade.SetPose("tilt_left");
        Assert.DoesNotContain(first.Calls, c => c.Contains(":action:") || c.Contains(":pose:"));
        Assert.Contains("b:action:nod", second.Calls);
        Assert.Contains("b:pose:tilt_left", second.Calls);
    }

    [Fact]
    public async Task Swappable_Dispose_DetachesAndDisposesInner()
    {
        var facade = new SwappableAvatarController();
        var inner = new FakeAvatar("a");
        facade.Swap(inner);
        await facade.DisposeAsync();
        Assert.Contains("a:dispose", inner.Calls);
        Assert.Null(facade.Inner);
    }

    [Fact]
    public async Task Composite_FansOut_InConstructorOrder()
    {
        var pixel = new FakeAvatar("pixel");
        var vts = new FakeAvatar("vts");
        var composite = new CompositeAvatarController(pixel, vts);

        await composite.SetEmotionAsync("happy");
        await composite.TriggerActionAsync("nod");
        composite.OnRms(0.25f);
        composite.SetPose("side_left");
        await composite.CloseMouthAsync();

        Assert.Equal(
            ["pixel:emotion:happy", "pixel:action:nod", "pixel:rms:0.25", "pixel:pose:side_left", "pixel:closemouth"],
            pixel.Calls);
        Assert.Equal(
            ["vts:emotion:happy", "vts:action:nod", "vts:rms:0.25", "vts:pose:side_left", "vts:closemouth"],
            vts.Calls);
    }

    [Fact]
    public async Task VtsAdapter_UnknownEmotionAndAction_ThrowWithoutTouchingClient()
    {
        using var client = new VtsClient(new VtsConfig());
        var adapter = new VtsAvatarAdapter(client, new VtsConfig
        {
            EmotionMap = new Dictionary<string, string> { ["happy"] = "hk-happy" },
            ActionMap = new Dictionary<string, string> { ["nod"] = "hk-nod" },
        });

        var emotionEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.SetEmotionAsync("angry"));
        Assert.Contains("unknown emotion", emotionEx.Message);

        var actionEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.TriggerActionAsync("head_spin"));
        Assert.Contains("unknown action", actionEx.Message);
    }

    [Fact]
    public async Task VtsAdapter_MappedNames_ResolveCaseInsensitively_AndReachClient()
    {
        using var client = new VtsClient(new VtsConfig());
        var adapter = new VtsAvatarAdapter(client, new VtsConfig
        {
            EmotionMap = new Dictionary<string, string> { ["Happy"] = "hk-happy" },
        });

        // Mapping resolves ("HAPPY" → hk-happy) and the call reaches the unauthenticated
        // client, whose error message is about auth — not an unknown-mapping error.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.SetEmotionAsync("HAPPY"));
        Assert.DoesNotContain("unknown emotion", ex.Message);
    }
}
