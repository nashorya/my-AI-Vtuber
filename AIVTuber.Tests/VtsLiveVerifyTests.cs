using System.Text.Json;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Config;
using AIVTuber.Core.Vts;

namespace AIVTuber.Tests;

public sealed class VtsLiveVerifyTests
{
    [SkippableFact]
    public async Task ProbeCurrentModelAndBodyAxis()
    {
        Skip.If(Environment.GetEnvironmentVariable("AIVTUBER_LIVE_VTS") != "1", "live VTS probe is opt-in");
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "vts-live-verify"));
        Directory.CreateDirectory(dir);
        var report = Path.Combine(dir, "probe.json");
        using var client = new VtsClient(new VtsConfig { Host = "localhost", Port = 8001 });
        await client.ConnectAsync();
        await using var session = new VtsContinuousSession(client, new ContinuousControlConfig { Enabled = true });
        await session.ConnectAsync();
        var model = session.Model ?? throw new InvalidOperationException("no model");
        var bodyParams = model.Parameters.Where(p => VtsContinuousSession.IsBodyLive2D(p.Id)).Select(p =>
            new { p.Id, p.Min, p.Max, p.Default, p.Value }).ToArray();
        var snapshot = new
        {
            status = session.Status,
            allowed = session.AllowedChannels,
            model = new { model.Id, model.Name, paramCount = model.Parameters.Count, inputs = model.Inputs },
            bodyParams,
            mappings = session.BodyMappings,
            inspectFile = VtsModelFile.TryInspectLoadedModel(model.Id, out var inspections) ? inspections : []
        };
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(await File.ReadAllTextAsync(report));
        Assert.False(string.IsNullOrWhiteSpace(session.Status));
        File.WriteAllText(Path.Combine(dir, "ready.flag"), session.Status);
        await session.StopAsync(false);
        await client.CreateParameterAsync("AIVTuberBodyYaw", -1, 1, 0);
        await client.InjectAsync(new Dictionary<string, float> { ["AIVTuberBodyYaw"] = .7f, ["FaceAngleX"] = 0 }, default);
        File.WriteAllText(Path.Combine(dir, "body.flag"), "body");
        await Task.Delay(4000);
        var afterBody = await client.QueryAsync("Live2DParameterListRequest");
        await File.WriteAllTextAsync(Path.Combine(dir, "after-body-yaw.json"), afterBody.GetRawText());
        await client.InjectAsync(new Dictionary<string, float> { ["AIVTuberBodyYaw"] = 0, ["FaceAngleX"] = 20 }, default);
        File.WriteAllText(Path.Combine(dir, "head.flag"), "head");
        await Task.Delay(4000);
        var afterHead = await client.QueryAsync("Live2DParameterListRequest");
        await File.WriteAllTextAsync(Path.Combine(dir, "after-head-yaw.json"), afterHead.GetRawText());
        await client.InjectAsync(new Dictionary<string, float> { ["AIVTuberBodyYaw"] = 0, ["FaceAngleX"] = 0 }, default);
    }
}
