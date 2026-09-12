using AIVTuber.Core.Avatar;

namespace AIVTuber.Core.Vts;

/// <summary>Emulates standard tracking inputs; does not create or certify model mappings.</summary>
public sealed class VtsTrackingBackend : IAvatarParameterBackend
{
    private readonly IAvatarParameterBackend _backend;
    private readonly Dictionary<string, VtsParameter> _inputs;
    private static readonly IReadOnlyDictionary<string, string[]> Routes = new Dictionary<string, string[]>
    {
        ["headYaw"] = ["FaceAngleX"], ["headPitch"] = ["FaceAngleY"], ["headRoll"] = ["FaceAngleZ"],
        ["gazeX"] = ["EyeLeftX", "EyeRightX"], ["gazeY"] = ["EyeLeftY", "EyeRightY"],
        ["eyeOpenL"] = ["EyeOpenLeft"], ["eyeOpenR"] = ["EyeOpenRight"],
        ["browHeightL"] = ["BrowLeftY", "Brows"], ["browHeightR"] = ["BrowRightY", "Brows"],
        ["mouthSmile"] = ["MouthSmile"],
        ["mouthOpen"] = ["MouthOpen", "VoiceVolume", "VoiceVolumePlusMouthOpen"]
    };

    public VtsTrackingBackend(IAvatarParameterBackend backend, IEnumerable<VtsParameter> inputs)
    {
        _backend = backend;
        _inputs = ReadInputs(inputs);
    }

    private static Dictionary<string, VtsParameter> ReadInputs(IEnumerable<VtsParameter> inputs)
        => inputs.Where(p => float.IsFinite(p.Min) && float.IsFinite(p.Max) &&
            float.IsFinite(p.Default) && p.Min < p.Max && p.Default >= p.Min && p.Default <= p.Max)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());

    public string[] Channels => Routes.Where(r => r.Value.Any(_inputs.ContainsKey)).Select(r => r.Key).ToArray();
    public static object[] Describe(IEnumerable<VtsParameter> inputs)
    {
        var available = ReadInputs(inputs);
        return Routes.Select(r => (object)new
        {
            channel = r.Key,
            label = AvatarChannels.All.Single(c => c.Name == r.Key).Label,
            inputs = r.Value.Where(available.ContainsKey).ToArray(),
            available = r.Value.Any(available.ContainsKey)
        }).ToArray();
    }

    public AvatarChannelBinding[] CreateBindings() => Channels.Select(name => new AvatarChannelBinding
    {
        Channel = name, ParameterId = "tracking:" + name,
        Minimum = AvatarChannels.All.Single(c => c.Name == name).Unipolar ? 0 : -1,
        Maximum = 1, Neutral = name.StartsWith("eyeOpen", StringComparison.Ordinal) ? 1 : 0,
        // Internal normalized transport bindings, never persisted as visually verified profiles.
        Verified = true
    }).ToArray();

    public Task InjectAsync(IReadOnlyDictionary<string, float> values, CancellationToken ct)
    {
        var output = new Dictionary<string, float>();
        var brows = new List<float>();
        foreach (var (channel, routes) in Routes)
        {
            var descriptor = AvatarChannels.All.Single(c => c.Name == channel);
            if (!values.TryGetValue(descriptor.InputId, out var value) || !float.IsFinite(value)) continue;
            value = Math.Clamp(value, descriptor.Unipolar ? 0 : -1, 1);
            foreach (var id in routes)
            {
                if (!_inputs.TryGetValue(id, out var parameter)) continue;
                if (id == "Brows") { brows.Add(value); continue; }
                output[id] = Map(channel, value, parameter);
            }
        }
        if (brows.Count > 0) output["Brows"] = Map("browHeightL", brows.Average(), _inputs["Brows"]);
        return output.Count == 0 ? Task.CompletedTask : _backend.InjectAsync(output, ct);
    }

    private static float Map(string channel, float value, VtsParameter parameter)
    {
        double min = parameter.Min, max = parameter.Max;
        double mapped;
        if (channel.StartsWith("eyeOpen", StringComparison.Ordinal) || channel == "mouthOpen")
            mapped = min + value * (max - min);
        else
        {
            // Face/gaze inputs are signed. Brows/smile usually have a unipolar range:
            // center them to retain both directions without assuming a model's output ranges.
            var neutral = min < 0 && max > 0 ? 0 : min + (max - min) / 2;
            var extent = value >= 0 ? max - neutral : neutral - min;
            var amplitude = channel.StartsWith("head", StringComparison.Ordinal) ? Math.Min(12, extent) : extent * .5;
            mapped = neutral + value * amplitude;
        }
        return (float)Math.Clamp(mapped, min, max);
    }
}
