using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIVTuber.Core.Vts;

/// <summary>Rewrites VTS model mappings so FaceAngle does not also drive body or footsteps.</summary>
public static class VtsModelFile
{
    public static bool IsBodyFollowOutput(string output)
        => output.Contains("Body", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("Step", StringComparison.OrdinalIgnoreCase);

    public static bool IsHeadCoupledInput(string input)
        => input.StartsWith("FaceAngle", StringComparison.OrdinalIgnoreCase) ||
           input.StartsWith("FacePosition", StringComparison.OrdinalIgnoreCase);

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
            var input = item["Input"]?.GetValue<string>() ?? "";
            if (!IsBodyFollowOutput(output) || !IsHeadCoupledInput(input)) continue;
            item["Input"] = "";
            changed = true;
        }
        if (!changed) return false;
        updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return true;
    }

    public static bool TryPinLoadedModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        foreach (var file in CandidateFiles())
        {
            try
            {
                var json = File.ReadAllText(file);
                if (!json.Contains(modelId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PinBodyFollow(json, out var updated)) return false;
                File.WriteAllText(file, updated);
                return true;
            }
            catch { /* locked or unreadable model files stay unchanged */ }
        }
        return false;
    }

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
