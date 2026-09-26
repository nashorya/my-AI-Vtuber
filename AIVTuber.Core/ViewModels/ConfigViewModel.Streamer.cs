using System.ComponentModel;
using System.Text.Json;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.ViewModels;

/// <summary>Availability of one catalog voice as last checked against the vendor.</summary>
public enum VoiceAvailability { Unknown, Available, Unavailable }

/// <summary>Outcome of a streamer-settings save, echoed back to the page with its request id.</summary>
public sealed record StreamerSaveResult(
    bool Ok,
    bool Applied,
    string StateText,
    string Message,
    long EffectiveRevision,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Streamer (distribution) settings (U01/U02/U08). The page receives a small projection of the
/// settings a streamer may change, and a patch is applied field by field from an explicit
/// allow-list. Provider, endpoint, model, key and local-inference fields are not part of the
/// projection and are rejected by the patch; the runtime pins them from the private profile
/// anyway. Saving goes through the same <see cref="SaveAsync"/> chain as the developer console.
/// </summary>
public sealed partial class ConfigViewModel
{
    /// <summary>Private-package profile; enables the managed voice catalog.</summary>
    public DistributionProfile? Profile { get; init; }

    /// <summary>Reads the configuration the runtime actually applied (not the submitted draft).</summary>
    public Func<AppConfig>? ReadEffectiveConfig { get; init; }

    /// <summary>Reads the runtime's active configuration revision.</summary>
    public Func<long>? ReadEffectiveRevision { get; init; }

    /// <summary>Reads a notice produced by the last apply (e.g. voice fell back to the default).</summary>
    public Func<string?>? ReadApplyNotice { get; init; }

    private readonly Dictionary<string, VoiceAvailability> _voiceAvailability = new(StringComparer.Ordinal);
    private string? _voiceNotice;

    /// <summary>Set once at startup from <see cref="ConfigManager.LastLoadNotice"/>.</summary>
    public string? VoiceNotice
    {
        get => _voiceNotice;
        set { _voiceNotice = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VoiceNotice))); }
    }

    /// <summary>Allow-listed paths of the streamer patch. Anything else is rejected.</summary>
    public static readonly IReadOnlySet<string> StreamerPatchPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        "persona.systemPrompt",
        "voice.choiceId", "voice.speed",
        "audio.inputDeviceIndex", "audio.enableLoopbackListen", "audio.loopbackProcessName",
        "audio.enableVirtualMic", "audio.virtualMicDeviceName",
        "live.bilibiliEnable", "live.roomId", "live.pkNotice", "live.isPkMode",
        "live.selfName", "live.opponentName", "live.wakeKeywords",
        "obs.enable", "obs.host", "obs.port", "obs.password",
        "avatar.vtsHost", "avatar.vtsPort",
    };

    public void SetVoiceAvailability(IReadOnlyDictionary<string, VoiceAvailability> byChoiceId)
    {
        _voiceAvailability.Clear();
        foreach (var (id, availability) in byChoiceId) _voiceAvailability[id] = availability;
    }

    /// <summary>The currently selected catalog entry of the working draft (null if unmanaged).</summary>
    public DistributionProfile.VoiceChoice? SelectedVoice => Profile?.FindVoiceByProviderId(Working.Tts.VoiceId);

    /// <summary>The catalog entry the running configuration uses.</summary>
    public DistributionProfile.VoiceChoice? EffectiveVoice =>
        Profile?.FindVoiceByProviderId((ReadEffectiveConfig?.Invoke() ?? _original).Tts.VoiceId);

    public object BuildStreamerDraft()
    {
        var voices = Profile?.AvailableVoices ?? [];
        return new
        {
            persona = new { systemPrompt = Working.Llm.SystemPrompt },
            voice = new
            {
                selectedChoiceId = SelectedVoice?.Id ?? "",
                effectiveChoiceId = EffectiveVoice?.Id ?? "",
                speed = Working.Tts.Speed,
                notice = VoiceNotice ?? "",
                choices = voices.Select(v => new
                {
                    choiceId = v.Id,
                    displayName = v.Name,
                    description = v.Description,
                    availability = (_voiceAvailability.TryGetValue(v.Id, out var a) ? a : VoiceAvailability.Unknown)
                        .ToString().ToLowerInvariant(),
                }).ToList(),
            },
            audio = new
            {
                inputDeviceIndex = Working.Audio.InputDeviceIndex,
                inputDevices = InputDevices.Select((name, i) => new { index = i, name }).ToList(),
                enableLoopbackListen = Working.Audio.EnableLoopbackListen,
                loopbackProcessName = Working.Audio.LoopbackProcessName ?? "",
                loopbackSources = LoopbackSources.Select(s => new { displayName = s.DisplayName, processName = s.ProcessName }).ToList(),
                enableVirtualMic = Working.Audio.EnableVirtualMic,
                virtualMicDeviceName = Working.Audio.VirtualMicDeviceName ?? "",
                outputDevices = OutputDevices.ToList(),
            },
            live = new
            {
                bilibiliEnable = Working.Bilibili.Enable,
                roomId = Working.Bilibili.RoomId,
                bilibiliLoggedIn = !string.IsNullOrEmpty(Working.Bilibili.Sessdata),
                pkNotice = Working.Bilibili.PkNotice,
                isPkMode = Working.Interaction.IsPkMode,
                selfName = Working.Identity.SelfName ?? "",
                opponentName = Working.Identity.OpponentName ?? "",
                wakeKeywords = string.Join(", ", Working.Interaction.WakeKeywords),
            },
            obs = new
            {
                enable = Working.Obs.Enable,
                host = Working.Obs.Host,
                port = Working.Obs.Port,
                passwordSet = !string.IsNullOrEmpty(Working.Obs.Password),
            },
            avatar = new
            {
                usesVts = Working.Avatar.UsesVts,
                vtsHost = Working.Vts.Host,
                vtsPort = Working.Vts.Port,
            },
            saveStateText = SaveStateText,
            isDirty = IsDirty,
            isSaving = IsSaving,
            status = Status,
            validationMessage = ValidationMessage,
            effectiveRevision = ReadEffectiveRevision?.Invoke() ?? 0,
        };
    }

    /// <summary>Applies an allow-listed patch to the draft. Returns the rejected paths; when any
    /// path is rejected nothing from this patch is applied.</summary>
    public IReadOnlyList<string> ApplyStreamerPatch(JsonElement data)
    {
        var rejected = new List<string>();
        if (data.ValueKind != JsonValueKind.Object) return ["(root)"];
        var fields = new List<(string Path, JsonElement Value)>();
        foreach (var group in data.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object) { rejected.Add(group.Name); continue; }
            foreach (var field in group.Value.EnumerateObject())
            {
                var path = $"{group.Name}.{field.Name}";
                if (StreamerPatchPaths.Contains(path)) fields.Add((path, field.Value));
                else rejected.Add(path);
            }
        }

        DistributionProfile.VoiceChoice? voice = null;
        var voiceField = fields.FirstOrDefault(f => f.Path == "voice.choiceId");
        if (voiceField.Path is not null)
        {
            voice = Profile?.FindVoiceByChoiceId(voiceField.Value.GetString());
            if (voice is null) rejected.Add("voice.choiceId");
        }
        if (rejected.Count > 0)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[设置] 拒绝主播设置修改: {string.Join(",", rejected)}");
            return rejected;
        }

        foreach (var (path, v) in fields)
        {
            switch (path)
            {
                case "persona.systemPrompt": Working.Llm.SystemPrompt = Str(v); break;
                case "voice.choiceId": Working.Tts.VoiceId = voice!.VoiceId; break;
                case "voice.speed": if (Dbl(v) is { } speed) Working.Tts.Speed = speed; break;
                case "audio.inputDeviceIndex": if (Int(v) is { } idx) Working.Audio.InputDeviceIndex = idx; break;
                case "audio.enableLoopbackListen": Working.Audio.EnableLoopbackListen = Bool(v); break;
                case "audio.loopbackProcessName": Working.Audio.LoopbackProcessName = Str(v); RestoreSelection(); break;
                case "audio.enableVirtualMic": Working.Audio.EnableVirtualMic = Bool(v); break;
                case "audio.virtualMicDeviceName": SelectedOutputDevice = Str(v); break;
                case "live.bilibiliEnable": Working.Bilibili.Enable = Bool(v); break;
                case "live.roomId": if (Int(v) is { } room) Working.Bilibili.RoomId = room; break;
                case "live.pkNotice": Working.Bilibili.PkNotice = Bool(v); break;
                case "live.isPkMode": InteractionIsPkMode = Bool(v); break;
                case "live.selfName": Working.Identity.SelfName = Str(v).Trim(); break;
                case "live.opponentName": Working.Identity.OpponentName = Str(v).Trim(); break;
                case "live.wakeKeywords": WakeKeywordsText = Str(v); break;
                case "obs.enable": Working.Obs.Enable = Bool(v); break;
                case "obs.host": Working.Obs.Host = Str(v).Trim(); break;
                case "obs.port": if (Int(v) is { } op) Working.Obs.Port = op; break;
                case "obs.password": if (Str(v).Length > 0) Working.Obs.Password = Str(v); break;
                case "avatar.vtsHost": Working.Vts.Host = Str(v).Trim(); break;
                case "avatar.vtsPort": if (Int(v) is { } vp) Working.Vts.Port = vp; break;
            }
        }
        NotifyDraftChanged();
        return [];
    }

    /// <summary>Patch + save + apply. Rejected fields fail the whole request without saving.</summary>
    public async Task<StreamerSaveResult> SaveStreamerAsync(JsonElement patch)
    {
        var rejected = patch.ValueKind == JsonValueKind.Undefined ? [] : ApplyStreamerPatch(patch);
        if (rejected.Count > 0)
        {
            SaveState = ConfigSaveState.ValidationFailed;
            var message = rejected.Contains("voice.choiceId") && rejected.Count == 1
                ? "所选音色当前不可用，请重新选择。"
                : "有设置不能在这里修改，已拒绝本次保存。";
            Status = message;
            return new StreamerSaveResult(false, false, SaveStateText, message, ReadEffectiveRevision?.Invoke() ?? 0, rejected);
        }
        await SaveAsync().ConfigureAwait(true);
        var applied = SaveState == ConfigSaveState.Applied;
        return new StreamerSaveResult(
            applied, applied, SaveStateText, Status, ReadEffectiveRevision?.Invoke() ?? 0, []);
    }

    /// <summary>After a successful apply the draft is reset to what the runtime really runs, so
    /// the page shows the effective values (U02). Voice notices from the apply are surfaced.</summary>
    private void AdoptEffectiveConfig()
    {
        if (ReadEffectiveConfig is null) return;
        var effective = ReadEffectiveConfig();
        Working = ConfigManager.Clone(effective);
        _original = ConfigManager.Clone(effective);
        ReplaceRows(EmotionRows, Working.Vts.EmotionMap.Select(kv => new EmotionMapRow { Emotion = kv.Key, HotkeyId = kv.Value }));
        ReplaceRows(ActionRows, Working.Vts.ActionMap.Select(kv => new ActionMapRow { Action = kv.Key, HotkeyId = kv.Value }));
        RestoreSelection();
        if (ReadApplyNotice?.Invoke() is { Length: > 0 } notice) VoiceNotice = notice;
        else if (SelectedVoice is not null) VoiceNotice = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Working)));
    }

    private static string StreamerApplyFailureText(Exception ex)
    {
        var error = AIVTuber.Core.Diagnostics.UserErrorMapper.FromException(ex, AIVTuber.Core.Diagnostics.ErrorArea.Settings);
        return error is null
            ? "已保存，但应用被取消，当前仍使用之前的设置。"
            : $"已保存，但暂未生效，当前仍使用之前的设置。{error.UserMessage}（诊断编号 {error.DiagnosticId}）";
    }

    private static string Str(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => v.ToString(),
    };

    private static bool Bool(JsonElement v) =>
        v.ValueKind == JsonValueKind.True ||
        v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b;

    private static int? Int(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i) ? i : null;
    }

    private static double? Dbl(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return v.ValueKind == JsonValueKind.String &&
               double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out d) ? d : null;
    }
}
