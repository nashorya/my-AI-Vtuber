using System.Collections.ObjectModel;
using System.Text.Json;

namespace AIVTuber.Core.Avatar;

public sealed class ContinuousControlConfig
{
    public bool Enabled { get; set; }
    public bool UseBuiltInTracking { get; set; } = true;
    public Dictionary<string, AvatarModelProfile> Profiles { get; set; } = new();
    public ContinuousControlConfig Snapshot() => JsonSerializer.Deserialize<ContinuousControlConfig>(JsonSerializer.Serialize(this))!;
}

public sealed class AvatarModelProfile
{
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Revision { get; set; } = "";
    public List<AvatarChannelBinding> Channels { get; set; } = [];
}

public sealed class AvatarChannelBinding
{
    public string Channel { get; set; } = "";
    public string ParameterId { get; set; } = "";
    public float Minimum { get; set; } = -1;
    public float Neutral { get; set; }
    public float Maximum { get; set; } = 1;
    public bool Inverted { get; set; }
    public bool Verified { get; set; }
    public string InputId => AvatarChannels.All.FirstOrDefault(c => c.Name == Channel)?.InputId ?? "";
    public bool IsValid => AvatarChannels.All.Any(c => c.Name == Channel) &&
        !string.IsNullOrWhiteSpace(ParameterId) && float.IsFinite(Minimum) && float.IsFinite(Maximum) &&
        float.IsFinite(Neutral) && Minimum < Maximum && Neutral >= Minimum && Neutral <= Maximum;
    public float Map(float value)
    {
        var descriptor = AvatarChannels.All.First(c => c.Name == Channel);
        if (descriptor.Unipolar)
            return Minimum + (Maximum - Minimum) * Math.Clamp(Inverted ? 1 - value : value, 0, 1);
        value = Math.Clamp(Inverted ? -value : value, -1, 1);
        return value >= 0 ? Neutral + value * (Maximum - Neutral) : Neutral + value * (Neutral - Minimum);
    }
}

public sealed record AvatarChannel(string Name, string Label, string InputId, string SuggestedParameter,
    bool Unipolar = false, bool AiControlled = true);

public static class AvatarChannels
{
    public static readonly IReadOnlyList<AvatarChannel> All = Array.AsReadOnly(new[]
    {
        new AvatarChannel("headYaw", "头部左右", "AIVTuberHeadYaw", "ParamAngleX"),
        new AvatarChannel("headPitch", "头部上下", "AIVTuberHeadPitch", "ParamAngleY"),
        new AvatarChannel("headRoll", "歪头", "AIVTuberHeadRoll", "ParamAngleZ"),
        new AvatarChannel("gazeX", "目光左右", "AIVTuberGazeX", "ParamEyeBallX"),
        new AvatarChannel("gazeY", "目光上下", "AIVTuberGazeY", "ParamEyeBallY"),
        new AvatarChannel("eyeOpenL", "左眼开合", "AIVTuberEyeOpenL", "ParamEyeLOpen", true),
        new AvatarChannel("eyeOpenR", "右眼开合", "AIVTuberEyeOpenR", "ParamEyeROpen", true),
        new AvatarChannel("browHeightL", "左眉高低", "AIVTuberBrowHeightL", "ParamBrowLY"),
        new AvatarChannel("browHeightR", "右眉高低", "AIVTuberBrowHeightR", "ParamBrowRY"),
        new AvatarChannel("browFormL", "左眉形", "AIVTuberBrowFormL", "ParamBrowLForm"),
        new AvatarChannel("browFormR", "右眉形", "AIVTuberBrowFormR", "ParamBrowRForm"),
        new AvatarChannel("mouthSmile", "嘴形", "AIVTuberMouthSmile", "ParamMouthForm"),
        new AvatarChannel("bodyYaw", "身体左右", "AIVTuberBodyYaw", "ParamBodyAngleX"),
        new AvatarChannel("bodyRoll", "身体倾斜", "AIVTuberBodyRoll", "ParamBodyAngleZ"),
        new AvatarChannel("mouthOpen", "口型（音频）", "AIVTuberMouthOpen", "ParamMouthOpenY", true, false),
        new AvatarChannel("breath", "呼吸（自动）", "AIVTuberBreath", "ParamBreath", true, false),
    });
}

public sealed record AvatarIntent(IReadOnlyDictionary<string, float> Targets, int TransitionMs = 400, int HoldMs = 1500);
public sealed record AvatarReplyPlan(string Reply, AvatarIntent? Intent, string? Diagnostic = null);

public static class AvatarReplyProtocol
{
    public static AvatarReplyPlan Parse(string json, IEnumerable<string> allowedChannels)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String)
            throw new JsonException("连续控制回复缺少 reply 字符串");
        var text = reply.GetString()!;
        if (!root.TryGetProperty("avatar", out var avatar) || avatar.ValueKind == JsonValueKind.Null)
            return new(text, null);
        try
        {
            var allowed = allowedChannels.ToHashSet(StringComparer.Ordinal);
            var targets = new Dictionary<string, float>();
            foreach (var property in avatar.GetProperty("targets").EnumerateObject())
            {
                var channel = AvatarChannels.All.FirstOrDefault(c => c.Name == property.Name && c.AiControlled);
                if (channel is null || !allowed.Contains(channel.Name)) throw new FormatException("通道不可用或不允许 AI 控制");
                var value = property.Value.GetSingle();
                if (!float.IsFinite(value) || value < (channel.Unipolar ? 0 : -1) || value > 1)
                    throw new FormatException("目标值超出通道范围");
                if (!targets.TryAdd(property.Name, value)) throw new FormatException("目标通道重复");
            }
            var transition = avatar.TryGetProperty("transitionMs", out var tr) ? tr.GetInt32() : 400;
            var hold = avatar.TryGetProperty("holdMs", out var h) ? h.GetInt32() : 1500;
            return new(text, new(new ReadOnlyDictionary<string, float>(targets), Math.Clamp(transition, 100, 2000), Math.Clamp(hold, 0, 5000)));
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return new(text, null, "动作已丢弃：" + e.Message); }
    }

    public static string Prompt(IEnumerable<string> allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        var channels = AvatarChannels.All.Where(c => c.AiControlled && names.Contains(c.Name)).ToArray();
        var descriptions = channels.Select(c => c.Name + "（" + c.Label + "）：" + (c.Unipolar ? "0 闭眼到 1 睁眼" :
            "-1~1，0 中立，正值" + (c.Name switch
            {
                "headYaw" or "gazeX" or "bodyYaw" => "向画面右",
                "headPitch" or "gazeY" or "browHeightL" or "browHeightR" => "向上",
                "headRoll" or "bodyRoll" => "顺时针",
                _ => "更开心"
            })));
        var example = JsonSerializer.Serialize(new
        {
            reply = "正文、（心里话）或【PASS】",
            avatar = new { targets = channels.Take(1).ToDictionary(c => c.Name, _ => .2), transitionMs = 400, holdMs = 1500 }
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return "\n连续控制实验协议优先于正文输出格式：整次回复只输出一个 JSON 对象，不要代码块。格式 " + example + "。" +
            "reply 内遵守原人设、字数、情绪标签和静默规则。avatar 可省略；PASS 不附动作。" +
            "每轮最多一个目标姿态，动作克制；不必每次变化。仅使用以下可用语义通道（实际表现取决于当前模型映射）：" +
            (channels.Length == 0 ? "无，请省略 avatar" : string.Join("；", descriptions)) + "。" +
            "transitionMs 为 100~2000，holdMs 为 0~5000。缺省 400/1500。不要输出参数 ID 或文件名。";
    }
}

public interface IAvatarReplySource
{
    event EventHandler<AvatarReplyPlan>? OnAvatarPlanReady;
}
public interface IAvatarMotionSink
{
    void BeginTurn(long generation) { }
    void Submit(long generation, AvatarIntent intent);
    void Cancel(long generation);
    void OnRms(float rms);
}
