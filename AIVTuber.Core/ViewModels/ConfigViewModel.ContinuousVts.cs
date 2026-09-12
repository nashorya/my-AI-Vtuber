using System.Text.Json;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Vts;

namespace AIVTuber.Core.ViewModels;

public sealed partial class ConfigViewModel
{
    private readonly Func<VtsContinuousSession?> _getContinuousVts;
    private readonly Func<Task> _connectContinuousVts;
    private bool _continuousBusy;
    public string ExportContinuousProfiles() => JsonSerializer.Serialize(Working.Vts.ContinuousControl,
        AIVTuber.Core.Config.ConfigManager.JsonOptions);

    public void ImportContinuousProfiles(string json)
    {
        if (json.Length > 262144) throw new InvalidOperationException("通道配置超过 256 KiB");
        var imported = JsonSerializer.Deserialize<ContinuousControlConfig>(json, AIVTuber.Core.Config.ConfigManager.JsonOptions)
            ?? throw new InvalidOperationException("通道配置为空");
        if (imported.Profiles is null || imported.Profiles.Count > 64) throw new InvalidOperationException("模型列表无效");
        foreach (var (id, profile) in imported.Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(id) || id != profile.ModelId || profile.Channels is null ||
                profile.Channels.Count > AvatarChannels.All.Count || profile.Channels.Any(b => b is null || AvatarChannels.All.All(c => c.Name != b.Channel)) ||
                profile.Channels.Select(b => b.Channel).Distinct().Count() != profile.Channels.Count)
                throw new InvalidOperationException("模型或通道配置无效");
            profile.Revision = "";
            foreach (var binding in profile.Channels) binding.Verified = false;
        }
        // Imported IDs and calibration are candidates. Do not import another machine's
        // visual confirmation or silently enable the experiment.
        foreach (var (id, profile) in imported.Profiles) Working.Vts.ContinuousControl.Profiles[id] = profile;
        NotifyDraftChanged();
    }

    public object BuildContinuousState()
    {
        var session = _getContinuousVts();
        var model = session?.Model;
        return new
        {
            status = session?.Status ?? "请在 config.json 将 avatar.backend 设为 vts 或 both，再重启应用",
            busy = _continuousBusy,
            useBuiltInTracking = Working.Vts.ContinuousControl.UseBuiltInTracking,
            trackingChannels = model is null ? [] : VtsTrackingBackend.Describe(model.DefaultInputs),
            model,
            profile = model is null ? null : Working.Vts.ContinuousControl.Profiles.GetValueOrDefault(model.Id),
            descriptors = AvatarChannels.All
        };
    }

    public async Task ContinuousCommandAsync(string operation, JsonElement data)
    {
        if (operation == "stop") { if (_getContinuousVts() is { } active) await active.StopAsync(); return; }
        if (_continuousBusy) return;
        _continuousBusy = true;
        try
        {
            if (operation == "connect") await _connectContinuousVts();
            var session = _getContinuousVts() ?? throw new InvalidOperationException("未启用 VTS 后端，请先保存后端设置");
            if (operation is "connect" or "refresh")
            {
                await session.RefreshAsync();
                if (session.Model is not null && !Working.Vts.ContinuousControl.UseBuiltInTracking)
                {
                    var draft = session.CreateDraft(Working.Vts.ContinuousControl);
                    Working.Vts.ContinuousControl.Profiles[draft.ModelId] = draft;
                    NotifyDraftChanged();
                }
                return;
            }
            if (operation == "resume") { await session.ResumeAsync(); return; }
            if (Working.Vts.ContinuousControl.UseBuiltInTracking)
                throw new InvalidOperationException("内置面捕模式无需创建或校准映射；高级操作请切换模式并保存");
            if (session.Model is not { } model || !Working.Vts.ContinuousControl.Profiles.TryGetValue(model.Id, out var profile))
                throw new InvalidOperationException("请先刷新通道");
            if (profile.Revision != model.Revision) throw new InvalidOperationException("映射已失效，请刷新通道后重新试动");
            if (operation == "prepare") { await session.PrepareInputsAsync(profile); return; }
            var index = data.GetProperty("index").GetInt32();
            if (index < 0 || index >= profile.Channels.Count) throw new InvalidOperationException("通道索引无效");
            var binding = profile.Channels[index];
            if (operation == "edit")
            {
                var field = data.GetProperty("field").GetString();
                var value = data.GetProperty("value");
                switch (field)
                {
                    case "parameterId": binding.ParameterId = value.GetString() ?? ""; break;
                    case "minimum": binding.Minimum = value.GetSingle(); break;
                    case "maximum": binding.Maximum = value.GetSingle(); break;
                    case "neutral": binding.Neutral = value.GetSingle(); break;
                    case "inverted": binding.Inverted = value.GetBoolean(); break;
                    default: throw new InvalidOperationException("未知通道字段");
                }
                binding.Verified = false;
            }
            else if (operation == "test") await session.TestAsync(profile, binding, data.GetProperty("value").GetSingle());
            else if (operation == "verify") session.ConfirmTrial(profile, binding);
            NotifyDraftChanged();
        }
        finally { _continuousBusy = false; }
    }
}
