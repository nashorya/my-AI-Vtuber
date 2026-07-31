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

    /// <summary>Optional head/body layer split (deprecated). Prefer <see cref="Poses"/>.</summary>
    [JsonPropertyName("layers")]
    public LayersConfig? Layers { get; set; }

    /// <summary>Whole-image pose switch (v0.6+). Replaces layered head-tilt.</summary>
    [JsonPropertyName("poses")]
    public PosesConfig? Poses { get; set; }
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

    /// <summary>Optional head-tilt angle (degrees) applied while this emotion is active.
    /// v0.2+. Null = leave tilt unchanged.</summary>
    [JsonPropertyName("tilt_deg")]
    public float? TiltDeg { get; set; }
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

    /// <summary>Head-tilt spring parameters. Drives the head layer's rotation target
    /// (SetListening, emotion override). v0.2+.</summary>
    [JsonPropertyName("tilt")]
    public TiltConfig Tilt { get; set; } = new();
}

public sealed class BreathConfig
{
    /// <summary>DEPRECATED (v0.2). Breath is now pure ScaleY oscillation; any Y translation
    /// would cause overlap/gaps. Kept for backward-compat in older avatar.json files but
    /// MotionEngine forces this to 0. Use <see cref="ScaleAmp"/> instead.</summary>
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

    /// <summary>RMS-to-bounce multiplier. Higher = more bounce for the same RMS.
    /// Replaces the previously-hardcoded 4.0 constant (v0.2: externally configurable).</summary>
    [JsonPropertyName("gain")]
    public float Gain { get; set; } = 4.0f;

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
    public float TiltDeg { get; set; } = 12;

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

/// <summary>Head/body layer split configuration (v0.2+). When Enabled and assets exist,
/// the renderer composites a single body.png with per-expression head sprites and applies
/// head-tilt rotation about <see cref="NeckPivot"/>. Falls back to single-sprite mode otherwise.</summary>
public sealed class LayersConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Body sprite path relative to assets dir (e.g. "layered/body.png"). Single image,
    /// shared across all expressions; never changes during expression switches.</summary>
    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    /// <summary>Directory (relative to assets dir) containing head sprites named identically to
    /// the base names in <see cref="AvatarStateDef.File"/> (e.g. gen_00.png). Trailing slash optional.</summary>
    [JsonPropertyName("head_dir")]
    public string HeadDir { get; set; } = "layered/head/";

    /// <summary>Y coordinate (in pack canvas space) of the head/body cut.
    /// Used with <c>meta.pivot.y</c> for breath follow:
    /// <c>HeadTranslate.Y = -(pivot_y - cut_y) × (scaleY - 1)</c>.
    /// Soft edge / feather lives in the head PNG (<c>feather_to_y</c> is documentation only).</summary>
    [JsonPropertyName("cut_y")]
    public int CutY { get; set; }

    /// <summary>Optional pack note: head PNG alpha fades through this Y (inclusive soft edge).
    /// Not consumed by the renderer — feathering must already be baked into the PNG.</summary>
    [JsonPropertyName("feather_to_y")]
    public int FeatherToY { get; set; }

    /// <summary>Lowest canvas Y included in the rotating head (exclusive bottom). Rows at/below
    /// this stay on the static body so shoulder wings do not tilt. 0 = use cut_y (legacy).
    /// Whale maid v0.5: shoulder flare starts ~516 → use 515.</summary>
    [JsonPropertyName("head_rotate_bottom_y")]
    public int HeadRotateBottomY { get; set; }

    /// <summary>Pivot (pack canvas space) the head rotates about. Typically the neck center.</summary>
    [JsonPropertyName("neck_pivot")]
    public AvatarPivot NeckPivot { get; set; } = new() { X = 627, Y = 500 };

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>Whole-image pose switch (v0.6). Distorts nothing — each pose is a full standee.</summary>
public sealed class PosesConfig
{
    [JsonPropertyName("dir")]
    public string Dir { get; set; } = "poses/";

    [JsonPropertyName("default")]
    public string Default { get; set; } = "front";

    [JsonPropertyName("foot_baseline_y")]
    public int FootBaselineY { get; set; }

    [JsonPropertyName("list")]
    public Dictionary<string, PoseDef> List { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("transition")]
    public TransitionDef Transition { get; set; } = new() { Type = "fade", Ms = 180 };

    [JsonPropertyName("triggers")]
    public PoseTriggersConfig Triggers { get; set; } = new();

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class PoseDef
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("full_expression")]
    public bool FullExpression { get; set; }

    /// <summary>"none" = freeze mouth on non-front poses.</summary>
    [JsonPropertyName("mouth")]
    public string? Mouth { get; set; }
}

public sealed class PoseTriggersConfig
{
    [JsonPropertyName("idle_random")]
    public IdleRandomPoseTrigger IdleRandom { get; set; } = new();

    [JsonPropertyName("manual")]
    public object? Manual { get; set; }

    [JsonPropertyName("emotion_hint")]
    public EmotionHintTrigger EmotionHint { get; set; } = new();
}

/// <summary>Optional: map LLM emotion words → whole-image pose ids.</summary>
public sealed class EmotionHintTrigger
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Emotion word (as in [emotion:x]) → pose id (tilt_left, …).</summary>
    [JsonPropertyName("map")]
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class IdleRandomPoseTrigger
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("interval_ms")]
    public int[] IntervalMs { get; set; } = [45000, 90000];

    /// <summary>Idle pool. Empty = tilt_left / tilt_right only (never side poses).</summary>
    [JsonPropertyName("poses")]
    public string[] Poses { get; set; } = ["tilt_left", "tilt_right"];

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

/// <summary>Head-tilt spring parameters (v0.2+, legacy layered path).</summary>
public sealed class TiltConfig
{
    /// <summary>Soft cap on tilt magnitude (degrees). Spring target is clamped to +-this.</summary>
    [JsonPropertyName("max_deg")]
    public float MaxDeg { get; set; } = 20f;

    /// <summary>Spring stiffness (omega^2). Higher = snappier. ~120 feels natural.</summary>
    [JsonPropertyName("stiffness")]
    public float Stiffness { get; set; } = 120f;

    /// <summary>Spring damping (2*zeta*omega). Higher = less overshoot. ~14 allows slight overshoot.</summary>
    [JsonPropertyName("damping")]
    public float Damping { get; set; } = 14f;
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
        if (TryLoad(assetsDirectory, out var pack))
            return pack;

        DebugLog.Write($"[Avatar] avatar.json missing/invalid under {assetsDirectory}; using empty placeholder pack");
        return CreatePlaceholderPack();
    }

    /// <summary>
    /// Parse avatar.json without falling back to the placeholder pack.
    /// Returns false when the file is missing, unreadable, or deserializes to null —
    /// so hot-reload can refuse to overwrite a live pack with a placeholder.
    /// </summary>
    public static bool TryLoad(string assetsDirectory, out AvatarPackConfig pack)
    {
        pack = null!;
        var path = Path.Combine(assetsDirectory, "avatar.json");
        if (!File.Exists(path))
            return false;

        try
        {
            // Utf8JsonReader rejects a leading UTF-8 BOM (0xEF); TrimStart covers ReadAllText
            // leaving U+FEFF when the on-disk file was saved with a BOM.
            var json = File.ReadAllText(path).TrimStart('\uFEFF');
            var parsed = JsonSerializer.Deserialize<AvatarPackConfig>(json, JsonOptions);
            if (parsed is null)
                return false;

            // Dictionaries deserialize without ordinal-ignore comparer; rebuild for safe lookups.
            parsed.States = new Dictionary<string, AvatarStateDef>(parsed.States, StringComparer.OrdinalIgnoreCase);
            parsed.Stickers.Items = new Dictionary<string, StickerItemDef>(parsed.Stickers.Items, StringComparer.OrdinalIgnoreCase);
            if (parsed.Poses is not null)
            {
                parsed.Poses.List = new Dictionary<string, PoseDef>(parsed.Poses.List, StringComparer.OrdinalIgnoreCase);
                parsed.Poses.Triggers.EmotionHint.Map = new Dictionary<string, string>(
                    parsed.Poses.Triggers.EmotionHint.Map, StringComparer.OrdinalIgnoreCase);
            }

            // Refuse the synthetic placeholder identity if somehow present — hot-reload must
            // never clobber a real pack with the empty fallback.
            if (string.Equals(parsed.Meta.Name, "dev_placeholder", StringComparison.OrdinalIgnoreCase)
                && parsed.States.Count <= 2)
                return false;

            pack = parsed;
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[Avatar] failed to parse avatar.json: {ex.Message}");
            return false;
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
