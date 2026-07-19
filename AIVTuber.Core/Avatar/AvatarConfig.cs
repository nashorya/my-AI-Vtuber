using System.Text.Json;
using System.Text.Json.Serialization;
using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Avatar;

/// <summary>Strongly-typed model for assets/avatar/avatar.json plus a tolerant loader.</summary>
public sealed class AvatarPackConfig
{
    [JsonPropertyName("meta")]
    public AvatarMeta Meta { get; set; } = new();

    [JsonPropertyName("states")]
    public Dictionary<string, AvatarStateDef> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("mouth_sync")]
    public MouthSyncConfig MouthSync { get; set; } = new();

    [JsonPropertyName("motion_layer")]
    public MotionLayerConfig MotionLayer { get; set; } = new();

    [JsonPropertyName("stickers")]
    public StickersConfig Stickers { get; set; } = new();
}

public sealed class AvatarMeta
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("canvas")]
    public AvatarSize Canvas { get; set; } = new() { Width = 1254, Height = 1254 };

    [JsonPropertyName("pivot")]
    public AvatarPivot Pivot { get; set; } = new() { X = 627, Y = 1180 };

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class AvatarSize
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public sealed class AvatarPivot
{
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class AvatarStateDef
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "base";

    [JsonPropertyName("transition")]
    public TransitionDef Transition { get; set; } = new();

    [JsonPropertyName("auto")]
    public AutoBlinkDef? Auto { get; set; }

    [JsonPropertyName("motion_override")]
    public MotionOverrideDef? MotionOverride { get; set; }

    [JsonPropertyName("_verify")]
    public bool Verify { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class TransitionDef
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "cut"; // cut | fade

    [JsonPropertyName("ms")]
    public int Ms { get; set; } = 0;
}

public sealed class AutoBlinkDef
{
    [JsonPropertyName("interval_ms")]
    public int[] IntervalMs { get; set; } = [2000, 6000];

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; } = 120;

    [JsonPropertyName("double_blink_chance")]
    public double DoubleBlinkChance { get; set; } = 0.15;
}

public sealed class MotionOverrideDef
{
    [JsonPropertyName("bounce_scale")]
    public float? BounceScale { get; set; }

    [JsonPropertyName("breath_rate_scale")]
    public float? BreathRateScale { get; set; }

    [JsonPropertyName("jump_px")]
    public float? JumpPx { get; set; }

    [JsonPropertyName("sink_px")]
    public float? SinkPx { get; set; }

    [JsonPropertyName("shake")]
    public ShakeDef? Shake { get; set; }
}

public sealed class ShakeDef
{
    [JsonPropertyName("amp_px")]
    public float AmpPx { get; set; } = 6;

    [JsonPropertyName("freq_hz")]
    public float FreqHz { get; set; } = 9;

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; } = 600;
}

public sealed class MouthSyncConfig
{
    [JsonPropertyName("levels")]
    public List<MouthLevelDef> Levels { get; set; } = [];

    [JsonPropertyName("hold_ms")]
    public int HoldMs { get; set; } = 60;

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class MouthLevelDef
{
    [JsonPropertyName("below")]
    public float Below { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "neutral";
}

public sealed class MotionLayerConfig
{
    [JsonPropertyName("breath")]
    public BreathConfig Breath { get; set; } = new();

    [JsonPropertyName("bounce")]
    public BounceConfig Bounce { get; set; } = new();

    [JsonPropertyName("drift")]
    public DriftConfig Drift { get; set; } = new();

    [JsonPropertyName("sway")]
    public SwayConfig Sway { get; set; } = new();

    [JsonPropertyName("listening")]
    public ListeningConfig Listening { get; set; } = new();
}

public sealed class BreathConfig
{
    [JsonPropertyName("amp_px")]
    public float AmpPx { get; set; } = 5;

    [JsonPropertyName("scale_amp")]
    public float ScaleAmp { get; set; } = 0.012f;

    [JsonPropertyName("period_ms")]
    public float PeriodMs { get; set; } = 3200;
}

public sealed class BounceConfig
{
    [JsonPropertyName("max_px")]
    public float MaxPx { get; set; } = 14;

    [JsonPropertyName("attack_ms")]
    public float AttackMs { get; set; } = 40;

    [JsonPropertyName("release_ms")]
    public float ReleaseMs { get; set; } = 180;

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class DriftConfig
{
    /// <summary>Vertical micro-drift only (horizontal idle motion is not applied).</summary>
    [JsonPropertyName("amp_px")]
    public float AmpPx { get; set; } = 0f;

    [JsonPropertyName("speed")]
    public float Speed { get; set; } = 0.3f;

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class SwayConfig
{
    /// <summary>Whole-image rotation degrees. 0 = off (default). Head-tilt is not this field.</summary>
    [JsonPropertyName("amp_deg")]
    public float AmpDeg { get; set; } = 0f;

    [JsonPropertyName("period_ms")]
    public float PeriodMs { get; set; } = 5200;

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class ListeningConfig
{
    [JsonPropertyName("tilt_deg")]
    public float TiltDeg { get; set; } = 5;

    [JsonPropertyName("blink_interval_scale")]
    public float BlinkIntervalScale { get; set; } = 0.7f;

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class StickersConfig
{
    [JsonPropertyName("anchor")]
    public AvatarPivot Anchor { get; set; } = new() { X = 940, Y = 260 };

    [JsonPropertyName("default_duration_ms")]
    public int DefaultDurationMs { get; set; } = 1800;

    [JsonPropertyName("animation")]
    public StickerAnimConfig Animation { get; set; } = new();

    [JsonPropertyName("items")]
    public Dictionary<string, StickerItemDef> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class StickerAnimConfig
{
    [JsonPropertyName("in")]
    public string In { get; set; } = "pop";

    [JsonPropertyName("out")]
    public string Out { get; set; } = "fade";
}

public sealed class StickerItemDef
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("scale")]
    public float Scale { get; set; } = 1f;
}

/// <summary>Loads avatar.json and optionally falls back to a minimal neutral-only pack.</summary>
public static class AvatarConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AvatarPackConfig Load(string assetsDirectory)
    {
        var path = Path.Combine(assetsDirectory, "avatar.json");
        if (!File.Exists(path))
        {
            DebugLog.Write($"[Avatar] avatar.json missing at {path}; using empty placeholder pack");
            return CreatePlaceholderPack();
        }

        try
        {
            var json = File.ReadAllText(path);
            var pack = JsonSerializer.Deserialize<AvatarPackConfig>(json, JsonOptions) ?? CreatePlaceholderPack();
            // Dictionaries deserialize without ordinal-ignore comparer; rebuild for safe lookups.
            pack.States = new Dictionary<string, AvatarStateDef>(pack.States, StringComparer.OrdinalIgnoreCase);
            pack.Stickers.Items = new Dictionary<string, StickerItemDef>(pack.Stickers.Items, StringComparer.OrdinalIgnoreCase);
            return pack;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[Avatar] failed to parse avatar.json: {ex.Message}");
            return CreatePlaceholderPack();
        }
    }

    /// <summary>
    /// Returns which state files exist under <paramref name="assetsDirectory"/>.
    /// Missing entries are logged; the loader never throws.
    /// </summary>
    public static HashSet<string> ResolveAvailableStates(AvatarPackConfig pack, string assetsDirectory)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, def) in pack.States)
        {
            if (string.IsNullOrWhiteSpace(def.File)) continue;
            var full = Path.Combine(assetsDirectory, def.File);
            if (File.Exists(full))
                available.Add(name);
            else
                DebugLog.Write($"[Avatar] missing sprite for state '{name}': {full}");
        }

        if (available.Count == 0)
            DebugLog.Write("[Avatar] no state sprites found; renderer will use dev_placeholder if present");

        return available;
    }

    public static string? ResolveDevPlaceholderIdle(string assetsDirectory)
    {
        var path = Path.Combine(assetsDirectory, "dev_placeholder", "Maid Idle.png");
        return File.Exists(path) ? path : null;
    }

    public static AvatarPackConfig CreatePlaceholderPack() => new()
    {
        Meta = new AvatarMeta
        {
            Name = "dev_placeholder",
            Canvas = new AvatarSize { Width = 144, Height = 144 },
            Pivot = new AvatarPivot { X = 72, Y = 140 },
        },
        States = new Dictionary<string, AvatarStateDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["neutral"] = new AvatarStateDef
            {
                File = "dev_placeholder/Maid Idle.png",
                Category = "base",
                Transition = new TransitionDef { Type = "cut" },
            },
            ["blink"] = new AvatarStateDef
            {
                File = "dev_placeholder/Maid Idle.png",
                Category = "base",
                Transition = new TransitionDef { Type = "cut" },
                Auto = new AutoBlinkDef
                {
                    IntervalMs = [2000, 6000],
                    DurationMs = 120,
                    DoubleBlinkChance = 0.15,
                },
            },
        },
        MouthSync = new MouthSyncConfig
        {
            HoldMs = 60,
            Levels =
            [
                new MouthLevelDef { Below = 0.06f, State = "neutral" },
                new MouthLevelDef { Below = 0.35f, State = "neutral" },
                new MouthLevelDef { Below = 1.01f, State = "neutral" },
            ],
        },
        MotionLayer = new MotionLayerConfig(),
        Stickers = new StickersConfig(),
    };
}
