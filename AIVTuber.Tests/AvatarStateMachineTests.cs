using AIVTuber.Core.Avatar;

namespace AIVTuber.Tests;

public class AvatarStateMachineTests
{
    private static AvatarPackConfig SamplePack() => new()
    {
        Meta = new AvatarMeta { Name = "test" },
        States = new Dictionary<string, AvatarStateDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["neutral"] = new() { File = "n.png", Category = "base", Transition = new() { Type = "cut" } },
            ["blink"] = new()
            {
                File = "b.png", Category = "base", Transition = new() { Type = "cut" },
                Auto = new AutoBlinkDef { IntervalMs = [1000, 1000], DurationMs = 100, DoubleBlinkChance = 0 },
            },
            ["mouth_half"] = new() { File = "mh.png", Category = "mouth", Transition = new() { Type = "cut" } },
            ["mouth_open"] = new() { File = "mo.png", Category = "mouth", Transition = new() { Type = "cut" } },
            ["happy"] = new()
            {
                File = "h.png", Category = "emotion", Transition = new() { Type = "cut" },
                MotionOverride = new MotionOverrideDef { BounceScale = 1.6f },
            },
            ["shy"] = new()
            {
                File = "s.png", Category = "emotion",
                Transition = new() { Type = "fade", Ms = 150 },
            },
            ["angry"] = new()
            {
                File = "a.png", Category = "emotion", Transition = new() { Type = "cut" },
                MotionOverride = new MotionOverrideDef
                {
                    Shake = new ShakeDef { AmpPx = 6, FreqHz = 9, DurationMs = 600 },
                },
            },
            ["sleep"] = new()
            {
                File = "z.png", Category = "special",
                Transition = new() { Type = "fade", Ms = 450 },
            },
        },
        MouthSync = new MouthSyncConfig
        {
            HoldMs = 60,
            Levels =
            [
                new MouthLevelDef { Below = 0.06f, State = "neutral" },
                new MouthLevelDef { Below = 0.35f, State = "mouth_half" },
                new MouthLevelDef { Below = 1.01f, State = "mouth_open" },
            ],
        },
        Stickers = new StickersConfig
        {
            DefaultDurationMs = 500,
            Animation = new StickerAnimConfig { In = "pop", Out = "fade" },
            Items = new Dictionary<string, StickerItemDef>(StringComparer.OrdinalIgnoreCase)
            {
                ["sweat_laugh"] = new() { File = "stickers/sweat_laugh.png", Scale = 0.55f },
            },
        },
    };

    [Fact]
    public void Priority_SpecialBeatsEmotionAndMouth()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.SetEmotion("happy", TimeSpan.FromSeconds(5));
        sm.OnRms(0.9f);
        sm.SetIdleState("sleep");

        var frame = sm.Tick(16);
        Assert.Equal("sleep", frame.BodyState);
    }

    [Fact]
    public void Priority_EmotionBeatsMouth()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.SetEmotion("angry", TimeSpan.FromSeconds(2));
        sm.OnRms(0.9f);

        // Advance past any accidental blink window by using a state machine with long blink cooldown.
        // Force several ticks; emotion must stay.
        for (var i = 0; i < 5; i++)
        {
            var frame = sm.Tick(16);
            Assert.Equal("angry", frame.BodyState);
            Assert.True(frame.EmotionActive);
        }
    }

    [Fact]
    public void Mouth_DebouncePreventsImmediateFallback()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.OnRms(0.5f); // mouth_open region (>0.35)
        var open = sm.Tick(1);
        Assert.Equal("mouth_open", open.MouthState);

        // Brief dip to silence — hold_ms (60) should keep mouth_open.
        sm.OnRms(0.01f);
        var held = sm.Tick(30);
        Assert.Equal("mouth_open", held.MouthState);
        Assert.Equal("mouth_open", held.BodyState);

        // After hold expires, fall back to neutral.
        var after = sm.Tick(40);
        Assert.Equal("neutral", after.MouthState);
    }

    [Fact]
    public void Mouth_RaisesImmediately()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.OnRms(0.0f);
        sm.Tick(1);
        sm.OnRms(0.2f); // half
        var half = sm.Tick(1);
        Assert.Equal("mouth_half", half.MouthState);

        sm.OnRms(0.8f); // open — no debounce delay on raise
        var open = sm.Tick(1);
        Assert.Equal("mouth_open", open.MouthState);
    }

    [Fact]
    public void Emotion_LocksFace_NoMouthSwitch()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.SetEmotion("happy", TimeSpan.FromMilliseconds(500));
        sm.OnRms(0.9f);

        var frame = sm.Tick(16);
        Assert.Equal("happy", frame.BodyState);
        // Mouth tracker may still update MouthState field, but body stays emotion.
        Assert.NotEqual("mouth_open", frame.BodyState);
    }

    [Fact]
    public void Emotion_ExpiresBackToNeutralOrMouth()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.SetEmotion("happy", TimeSpan.FromMilliseconds(50));
        Assert.Equal("happy", sm.Tick(10).BodyState);
        // Expire hold
        sm.OnRms(0f);
        var after = sm.Tick(50);
        Assert.False(after.EmotionActive);
        Assert.Equal("neutral", after.BodyState);
    }

    [Fact]
    public void Blink_IntervalRespectsConfiguredRange()
    {
        // Fixed RNG + fixed interval [1000,1000] → blink after 1000ms cooldown.
        var pack = SamplePack();
        pack.States["blink"].Auto = new AutoBlinkDef
        {
            IntervalMs = [1000, 1000],
            DurationMs = 80,
            DoubleBlinkChance = 0,
        };
        var sm = new AvatarStateMachine(pack, rng: new Random(42));

        // Drain initial cooldown (constructor schedules one).
        // First schedule is also 1000ms with this pack.
        var sawBlink = false;
        for (var t = 0; t < 1200; t += 20)
        {
            var f = sm.Tick(20);
            if (f.BodyState == "blink")
            {
                sawBlink = true;
                break;
            }
        }
        Assert.True(sawBlink, "expected a blink within ~1s with interval_ms=[1000,1000]");
    }

    [Fact]
    public void Fade_TransitionReportsFadeFromAndProgress()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        // Start neutral
        sm.Tick(1);
        sm.SetEmotion("shy", TimeSpan.FromSeconds(2)); // fade 150ms

        var start = sm.Tick(0);
        Assert.Equal("shy", start.BodyState);
        Assert.Equal("neutral", start.FadeFromState);
        Assert.True(start.FadeT < 1f);

        var mid = sm.Tick(75);
        Assert.Equal("shy", mid.BodyState);
        Assert.NotNull(mid.FadeFromState);
        Assert.InRange(mid.FadeT, 0.3f, 0.8f);

        var done = sm.Tick(100);
        Assert.Equal("shy", done.BodyState);
        Assert.Null(done.FadeFromState);
        Assert.Equal(1f, done.FadeT);
    }

    [Fact]
    public void Sticker_ReplacesPrevious_AndExpires()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.ShowSticker("sweat_laugh");
        var first = sm.Tick(10);
        Assert.NotNull(first.Sticker);
        Assert.Equal("sweat_laugh", first.Sticker!.Value.Id);

        // Same sticker again resets
        sm.ShowSticker("sweat_laugh");
        var reset = sm.Tick(10);
        Assert.NotNull(reset.Sticker);

        // Advance past duration (500ms)
        StickerFrame? last = reset.Sticker;
        for (var i = 0; i < 40; i++)
            last = sm.Tick(20).Sticker;

        Assert.Null(last);
    }

    [Fact]
    public void Sticker_PopScaleAboveBaseDuringInAnimation()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.ShowSticker("sweat_laugh");
        var frame = sm.Tick(50); // mid pop (~180ms in)
        Assert.NotNull(frame.Sticker);
        // Base scale 0.55; pop goes through 0.6→1.05 factor so scale should be > 0.55*0.6
        Assert.True(frame.Sticker!.Value.Scale > 0.3f);
    }

    [Fact]
    public void UnknownEmotion_FallsBackToNeutral_NoThrow()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.SetEmotion("not_a_real_emotion", TimeSpan.FromSeconds(1));
        var frame = sm.Tick(1);
        // Unknown maps to neutral emotion which clears — body stays neutral.
        Assert.Equal("neutral", frame.BodyState);
    }

    [Fact]
    public void MotionOverride_SurfacesWhileEmotionActive()
    {
        var sm = new AvatarStateMachine(SamplePack(), rng: new Random(1));
        sm.SetEmotion("happy", TimeSpan.FromSeconds(1));
        var frame = sm.Tick(1);
        Assert.NotNull(frame.MotionOverride);
        Assert.Equal(1.6f, frame.MotionOverride!.BounceScale);
    }
}
