using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIVTuber.Core.Vts;

/// <summary>
/// Official VTS models wire FaceAngleX to head, body, and footsteps together
/// (webcam "turn" looks like the whole person turns). Head shake must keep the
/// feet planted and drive body the opposite way so ParamAngleX's torso deformers
/// do not read as a hop.
/// </summary>
public static class VtsModelFile
{
    public static bool IsBodyFollowOutput(string output)
        => output.Contains("Body", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Step", StringComparison.OrdinalIgnoreCase);

    public static bool IsHeadCoupledInput(string input)
        => input.StartsWith("FaceAngle", StringComparison.OrdinalIgnoreCase) ||
           input.StartsWith("FacePosition", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<VtsMappingInspection> InspectBodyMappings(string json)
    {
        var list = new List<VtsMappingInspection>();
        var root = JsonNode.Parse(json)?.AsObject();
        if (root?["ParameterSettings"] is not JsonArray settings) return list;
        foreach (var node in settings)
        {
            if (node is not JsonObject item) continue;
            var output = item["OutputLive2D"]?.GetValue<string>() ?? "";
            var name = item["Name"]?.GetValue<string>() ?? "";
            if ((!IsBodyFollowOutput(output) && !IsBodyFollowOutput(name)) || IsStep(output, name))
                continue;
            var input = item["Input"]?.GetValue<string>() ?? "";
            var inputLower = ReadFloat(item, "InputRangeLower", -1);
            var inputUpper = ReadFloat(item, "InputRangeUpper", 1);
            var outputLower = ReadFloat(item, "OutputRangeLower", 0);
            var outputUpper = ReadFloat(item, "OutputRangeUpper", 0);
            var expected = BodyInputFor(output, name);
            var issue = "";
            if (IsHeadCoupledInput(input) || input.Length == 0)
                issue = "head-coupled";
            else if (!input.Equals(expected, StringComparison.OrdinalIgnoreCase))
                issue = "missing-body-input";
            else if (LooksLikeFaceAngleInputRange(inputLower, inputUpper))
                issue = "range-mismatch";
            list.Add(new(output.Length > 0 ? output : name, input, inputLower, inputUpper, outputLower, outputUpper, issue));
        }
        return list;
    }

    public static bool PinBodyFollow(string json, out string updated)
    {
        updated = json;
        var root = JsonNode.Parse(json)?.AsObject();
        if (root?["ParameterSettings"] is not JsonArray settings) return false;
        var changed = false;
        foreach (var node in settings)
        {
            if (node is not JsonObject item) continue;
            var output = item["OutputLive2D"]?.GetValue<string>() ?? "";
            var name = item["Name"]?.GetValue<string>() ?? "";
            if (!IsBodyFollowOutput(output) && !IsBodyFollowOutput(name) && !IsMouthOpenOutput(output, name))
                continue;
            if (PinOne(item, output, name)) changed = true;
        }
        if (!changed) return false;
        updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return true;
    }

    private static bool PinOne(JsonObject item, string output, string name)
    {
        var input = item["Input"]?.GetValue<string>() ?? "";
        if (IsMouthOpenOutput(output, name))
        {
            if (input.Equals("MouthOpen", StringComparison.OrdinalIgnoreCase)) return false;
            item["Input"] = "MouthOpen";
            return true;
        }
        if (IsStep(output, name))
        {
            if (input.Length == 0) return false;
            item["Input"] = "";
            return true;
        }

        var body = BodyInputFor(output, name);
        var inputLower = ReadFloat(item, "InputRangeLower", -1);
        var inputUpper = ReadFloat(item, "InputRangeUpper", 1);
        var changed = false;
        if (!input.Equals(body, StringComparison.OrdinalIgnoreCase))
        {
            item["Input"] = body;
            item["InputRangeLower"] = -1;
            item["InputRangeUpper"] = 1;
            changed = true;
        }
        else if (LooksLikeFaceAngleInputRange(inputLower, inputUpper))
        {
            item["InputRangeLower"] = -1;
            item["InputRangeUpper"] = 1;
            changed = true;
        }
        return changed;
    }

    internal static bool LooksLikeFaceAngleInputRange(float lower, float upper)
        => Math.Max(Math.Abs(lower), Math.Abs(upper)) > 2;

    internal static string BodyInputFor(string output, string name)
    {
        var face = FaceInputForBody(output, name);
        return face switch
        {
            "FaceAngleY" => "AIVTuberBodyPitch",
            "FaceAngleZ" => "AIVTuberBodyRoll",
            _ => "AIVTuberBodyYaw"
        };
    }

    internal static string FaceInputForBody(string output, string name)
    {
        var key = output + " " + name;
        if (ContainsAxis(key, "Y") || key.Contains("Pitch", StringComparison.OrdinalIgnoreCase))
            return "FaceAngleY";
        if (ContainsAxis(key, "Z") || key.Contains("Roll", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Lean", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Tilt", StringComparison.OrdinalIgnoreCase))
            return "FaceAngleZ";
        return "FaceAngleX";
    }

    internal static bool IsMouthOpenOutput(string output, string name)
        => output.Contains("MouthOpen", StringComparison.OrdinalIgnoreCase) ||
           (name.Equals("Mouth Open", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Smile", StringComparison.OrdinalIgnoreCase));

    private static bool IsStep(string output, string name)
        => output.Contains("Step", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Step", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAxis(string key, string axis)
        => key.Contains("Angle" + axis, StringComparison.OrdinalIgnoreCase) ||
           key.Contains("Rotation " + axis, StringComparison.OrdinalIgnoreCase) ||
           key.Contains("Rotation" + axis, StringComparison.OrdinalIgnoreCase);

    private static float ReadFloat(JsonObject item, string key, float fallback)
        => item[key] is JsonValue value && value.TryGetValue<float>(out var number) ? number : fallback;

    internal static readonly AsyncLocal<Func<string, bool>?> TryPinOverride = new();
    internal static readonly AsyncLocal<Func<string, IReadOnlyList<VtsMappingInspection>?>?> TryInspectOverride = new();

    public static bool TryPinLoadedModel(string modelId, bool allowWrite = false)
    {
        if (TryPinOverride.Value is { } hook) return hook(modelId);
        if (!allowWrite || string.IsNullOrWhiteSpace(modelId)) return false;
        foreach (var file in CandidateFiles())
        {
            try
            {
                var json = File.ReadAllText(file);
                if (!json.Contains(modelId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PinBodyFollow(json, out var updated)) return false;
                Backup(file);
                File.WriteAllText(file, updated);
                return true;
            }
            catch { /* locked or unreadable model files stay unchanged */ }
        }
        return false;
    }

    public static bool TryInspectLoadedModel(string modelId, out IReadOnlyList<VtsMappingInspection> inspections)
    {
        inspections = [];
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        if (TryInspectOverride.Value is { } hook)
        {
            var hooked = hook(modelId);
            if (hooked is null) return false;
            inspections = hooked;
            return true;
        }
        foreach (var file in CandidateFiles())
        {
            try
            {
                var json = File.ReadAllText(file);
                if (!json.Contains(modelId, StringComparison.OrdinalIgnoreCase)) continue;
                inspections = InspectBodyMappings(json);
                return true;
            }
            catch { /* unreadable model files stay unknown */ }
        }
        return false;
    }

    public static bool TryRestorePinnedModel(string modelId, out string restored)
    {
        restored = "";
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        foreach (var file in CandidateFiles())
        {
            var backup = BackupPath(file);
            try
            {
                if (!File.Exists(backup)) continue;
                var json = File.ReadAllText(backup);
                if (!json.Contains(modelId, StringComparison.OrdinalIgnoreCase)) continue;
                File.WriteAllText(file, json);
                restored = file;
                return true;
            }
            catch { /* locked backup stays unused */ }
        }
        return false;
    }

    private static void Backup(string file)
    {
        var backup = BackupPath(file);
        if (!File.Exists(backup)) File.Copy(file, backup);
    }

    internal static string BackupPath(string file) => file + ".aivtuber.bak";

    internal static IEnumerable<string> CandidateFiles()
    {
        foreach (var root in ModelRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.vtube.json", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static IEnumerable<string> ModelRoots()
    {
        foreach (var name in new[] { "VTube Studio", "VTubeStudio" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                string? exe = null;
                try { exe = process.MainModule?.FileName; } catch { /* 32/64-bit access */ }
                if (string.IsNullOrEmpty(exe)) continue;
                var dir = Path.Combine(Path.GetDirectoryName(exe)!, "VTube Studio_Data", "StreamingAssets", "Live2DModels");
                if (Directory.Exists(dir)) yield return dir;
            }
        foreach (var guess in new[]
                 {
                     @"D:\SteamLibrary\steamapps\common\VTube Studio\VTube Studio_Data\StreamingAssets\Live2DModels",
                     @"C:\Program Files (x86)\Steam\steamapps\common\VTube Studio\VTube Studio_Data\StreamingAssets\Live2DModels",
                     @"C:\Program Files\Steam\steamapps\common\VTube Studio\VTube Studio_Data\StreamingAssets\Live2DModels"
                 })
            yield return guess;
    }
}
