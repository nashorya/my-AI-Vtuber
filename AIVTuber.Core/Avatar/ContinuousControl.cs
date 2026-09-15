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
        new AvatarChannel("bodyPitch", "身体前后", "AIVTuberBodyPitch", "ParamBodyAngleY"),
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
        if (!TryExtractObject(json, out var payload))
            return new(json.Trim(), null, "动作已丢弃：回复不是 JSON 对象");
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return new(json.Trim(), null, "动作已丢弃：回复不是 JSON 对象"); }
        using (document)
        {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !TryReadSpoken(root, out var text, out var silent))
            return new(json.Trim(), null, "动作已丢弃：连续控制回复缺少 reply 字符串");
        if (silent)
            return new("【PASS】", null);
        var allowed = allowedChannels.ToHashSet(StringComparer.Ordinal);
        if (TryReadString(root, "motion", out var motionName) && motionName.Trim().Length > 0)
        {
            if (TryMapMotion(motionName.Trim(), allowed, out var mapped, out var error))
                return new(text, mapped);
            return new(text, null, "动作已丢弃：" + error);
        }
        if (!root.TryGetProperty("avatar", out var avatar) || avatar.ValueKind == JsonValueKind.Null)
            return new(text, null);
        try
        {
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
    }

    private static readonly (string Verb, string Channel, float Value)[] Motions =
    [
        ("摇头", "headYaw", .5f),
        ("点头", "headPitch", .5f),
        ("歪头", "headRoll", .35f),
    ];

    public static AvatarIntent? InferRequestedMotion(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return null;
        if (userInput.Contains("摇头", StringComparison.Ordinal) ||
            userInput.Contains("摇脑袋", StringComparison.Ordinal))
            return MotionIntent("headYaw", .5f);
        if (userInput.Contains("点头", StringComparison.Ordinal))
            return MotionIntent("headPitch", .5f);
        if (userInput.Contains("歪头", StringComparison.Ordinal))
            return MotionIntent("headRoll", .35f);
        if (userInput.Contains("转身体", StringComparison.Ordinal) ||
            userInput.Contains("扭身子", StringComparison.Ordinal))
            return MotionIntent("bodyYaw", .45f);
        if (userInput.Contains("弯腰", StringComparison.Ordinal) ||
            userInput.Contains("哈腰", StringComparison.Ordinal))
            return MotionIntent("bodyPitch", .4f);
        if (userInput.Contains("侧身", StringComparison.Ordinal))
            return MotionIntent("bodyRoll", .4f);
        return null;
    }

    internal static bool TryMapMotion(string motion, ISet<string> allowed, out AvatarIntent? intent, out string error)
    {
        intent = null;
        error = "";
        var verb = NormalizeMotion(motion);
        var match = Motions.FirstOrDefault(m => m.Verb == verb);
        if (string.IsNullOrEmpty(match.Verb))
        {
            error = "不是已知动作";
            return false;
        }
        if (!allowed.Contains(match.Channel))
        {
            error = "当前不能做这个动作";
            return false;
        }
        intent = MotionIntent(match.Channel, match.Value);
        return true;
    }

    private static string NormalizeMotion(string motion)
    {
        var value = motion.Trim().Trim('。', '！', '!', '～', '~');
        if (value.Contains("摇头", StringComparison.Ordinal) || value.Contains("摇脑袋", StringComparison.Ordinal))
            return "摇头";
        if (value.Contains("点头", StringComparison.Ordinal)) return "点头";
        if (value.Contains("歪头", StringComparison.Ordinal)) return "歪头";
        return value;
    }

    private static AvatarIntent MotionIntent(string channel, float value) =>
        new(new ReadOnlyDictionary<string, float>(new Dictionary<string, float> { [channel] = value }), 400, 1600);

    private static bool TryReadSpoken(JsonElement root, out string text, out bool silent)
    {
        text = "";
        silent = false;
        if (root.TryGetProperty("respond", out var respond))
        {
            if (respond.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            if (!respond.GetBoolean())
            {
                silent = true;
                return true;
            }
        }

        return TryReadString(root, "speech", out text) || TryReadString(root, "reply", out text);
    }

    private static bool TryReadString(JsonElement root, string name, out string text)
    {
        text = "";
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString() ?? "";
        return true;
    }

    private static bool TryExtractObject(string raw, out string payload)
    {
        payload = raw.Trim();
        if (payload.Length == 0) return false;
        var start = payload.IndexOf('{');
        var end = payload.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        payload = payload[start..(end + 1)];
        return true;
    }

    public static string Prompt(IEnumerable<string> allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        var channels = AvatarChannels.All.Where(c => c.AiControlled && names.Contains(c.Name)).ToArray();
        var hasHead = channels.Any(c => c.Name.StartsWith("head", StringComparison.Ordinal));
        var hasBody = channels.Any(c => c.Name.StartsWith("body", StringComparison.Ordinal));
        var split = hasHead && hasBody
            ? "头通道只转头和脸，身体通道转脖子和身子；要一起动就在 targets 里同时写这两套。"
            : hasHead ? "头通道转头和脸。"
            : hasBody ? "身体通道转脖子和身子。"
            : "";
        var sample = channels.FirstOrDefault(c => c.Name == "headYaw") ?? channels.FirstOrDefault();
        var example = JsonSerializer.Serialize(new
        {
            respond = true,
            speech = "准备朗读的口语正文",
            avatar = sample is null ? null : new { targets = new Dictionary<string, float> { [sample.Name] = .5f } }
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return "\n用同一条 JSON 控制自己出不出声、说什么、头、身体和眼睛怎么摆。必须包含 respond 和 speech。" +
            split +
            "每个通道 0 是中立（正对镜头、平视、不歪、身子直立）。0.4~0.6 是镜头里能看清的一记；1 或 -1 是该通道转到极限，不要当默认幅度。" +
            "要动时写 avatar.targets，值为连续数，不是动作名单。不要代码块。格式 " + example + "。" +
            "静默只用 {\"respond\":false,\"speech\":\"\"}，不要附 avatar。" +
            "speech 内遵守原人设、字数和情绪标签。" +
            (names.Contains("headYaw") ? "被要求摇头时写 headYaw 0.5，程序按这个幅度左右摆头" +
                (names.Contains("bodyYaw") ? "，身子会跟着一点，不必再写 bodyYaw，除非想额外拧腰。" : "。") : "") +
            (names.Contains("headPitch") ? "点头写 headPitch 0.5。" : "") +
            (names.Contains("bodyYaw") ? "只要转身子、不摇头时写 bodyYaw 0.45。" : "") +
            "不要写 VTS/Live2D 参数名。可用语义通道：" +
            (channels.Length == 0 ? "无，请省略 avatar" : string.Join("；", channels.Select(DescribeChannel))) + "。" +
            "每轮最多一组目标；幅度自己选，不必每次都动。";
    }

    private static string DescribeChannel(AvatarChannel channel) => channel.Name + "（" + channel.Label + "）：" + channel.Name switch
    {
        "headYaw" => "0 正对镜头，0.5 明显转向画面右（摇头用），1 头转到最右，-1 最左",
        "headPitch" => "0 平视，0.5 明显抬头（点头用），1 抬到最高，-1 低头最低",
        "headRoll" => "0 不歪，0.35 轻轻歪头，1 歪到最斜，负值向另一侧",
        "bodyYaw" => "0 身子正对镜头，0.45 明显转腰，1 身子转到最右，-1 最左",
        "bodyPitch" => "0 腰背直立，0.4 明显前倾哈腰，1 弯到最前，负值后仰",
        "bodyRoll" => "0 身子不侧，0.4 明显侧身，1 侧到最斜，负值向另一侧",
        "gazeX" => "0 看镜头，1 看向画面右，-1 左",
        "gazeY" => "0 平视，1 看上，-1 看下",
        "eyeOpenL" or "eyeOpenR" => "0 闭上，1 睁开",
        "browHeightL" or "browHeightR" => "0 眉在中位，1 抬到最高，-1 压到最低",
        "browFormL" or "browFormR" => "0 中性眉形，1 最舒展，-1 最皱",
        "mouthSmile" => "0 中性嘴，1 最笑，-1 最抿",
        _ => channel.Unipolar ? "0 到 1" : "-1~1，0 中立"
    };
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
