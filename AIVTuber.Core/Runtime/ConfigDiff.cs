using AIVTuber.Core.Config;

namespace AIVTuber.Core.Runtime;

/// <summary>
/// Module-rebuild actions triggered by a config change. Light actions recreate
/// in-memory objects (instant); heavy actions reopen devices/sockets/files (~1-2s).
/// </summary>
[Flags]
public enum RuntimeChange
{
    None = 0,
    RebuildLlm = 1 << 0,
    RebuildAsr = 1 << 1,
    RebuildTts = 1 << 2,
    UpdateVtsParams = 1 << 3,
    UpdateObsParams = 1 << 4,
    RebuildDanmakuSelector = 1 << 5,
    UpdateMemoryParams = 1 << 6,
    RestartAudio = 1 << 7,
    ReconnectVts = 1 << 8,
    ReconnectObs = 1 << 9,
    RestartDanmaku = 1 << 10,
    ReopenMemory = 1 << 11,
}

/// <summary>Computes which modules must rebuild when config changes.</summary>
public static class ConfigDiff
{
    /// <summary>Mask of heavy (device/socket/file) actions.</summary>
    public const RuntimeChange HeavyMask =
        RuntimeChange.RestartAudio | RuntimeChange.ReconnectVts | RuntimeChange.ReconnectObs |
        RuntimeChange.RestartDanmaku | RuntimeChange.ReopenMemory;

    public static bool IsHeavy(RuntimeChange change) => (change & HeavyMask) != 0;

    public static RuntimeChange Compute(AppConfig a, AppConfig b)
    {
        var c = RuntimeChange.None;

        if (a.Llm.BaseUrl != b.Llm.BaseUrl || a.Llm.ApiKey != b.Llm.ApiKey ||
            a.Llm.Model != b.Llm.Model || a.Llm.SystemPrompt != b.Llm.SystemPrompt ||
            a.Llm.MaxHistoryTokens != b.Llm.MaxHistoryTokens)
            c |= RuntimeChange.RebuildLlm;

        if (a.Asr.Provider != b.Asr.Provider || a.Asr.ApiKey != b.Asr.ApiKey || a.Asr.AppId != b.Asr.AppId ||
            a.Asr.Model != b.Asr.Model || a.Asr.LocalAsrUrl != b.Asr.LocalAsrUrl ||
            a.Asr.PythonPath != b.Asr.PythonPath)
            c |= RuntimeChange.RebuildAsr;

        if (a.Tts.Provider != b.Tts.Provider || a.Tts.ApiKey != b.Tts.ApiKey || a.Tts.VoiceId != b.Tts.VoiceId ||
            a.Tts.Model != b.Tts.Model || a.Tts.GroupId != b.Tts.GroupId || a.Tts.Speed != b.Tts.Speed)
            c |= RuntimeChange.RebuildTts;

        if (a.Vts.MouthScale != b.Vts.MouthScale ||
            !DictEqual(a.Vts.EmotionMap, b.Vts.EmotionMap) ||
            !DictEqual(a.Vts.ActionMap, b.Vts.ActionMap))
            c |= RuntimeChange.UpdateVtsParams;
        if (a.Vts.Host != b.Vts.Host || a.Vts.Port != b.Vts.Port)
            c |= RuntimeChange.ReconnectVts;

        if (a.Obs.AssistantTextComponent != b.Obs.AssistantTextComponent ||
            a.Obs.UserTextComponent != b.Obs.UserTextComponent ||
            a.Obs.TypewriterIntervalMs != b.Obs.TypewriterIntervalMs)
            c |= RuntimeChange.UpdateObsParams;
        if (a.Obs.Enable != b.Obs.Enable || a.Obs.Host != b.Obs.Host ||
            a.Obs.Port != b.Obs.Port || a.Obs.Password != b.Obs.Password)
            c |= RuntimeChange.ReconnectObs;

        if (a.Bilibili.SelectionIntervalSec != b.Bilibili.SelectionIntervalSec)
            c |= RuntimeChange.RebuildDanmakuSelector;
        if (a.Bilibili.Enable != b.Bilibili.Enable || a.Bilibili.RoomId != b.Bilibili.RoomId ||
            a.Bilibili.Sessdata != b.Bilibili.Sessdata || a.Bilibili.BiliJct != b.Bilibili.BiliJct ||
            a.Bilibili.Buvid3 != b.Bilibili.Buvid3 || a.Bilibili.PushPort != b.Bilibili.PushPort ||
            a.Bilibili.PythonPath != b.Bilibili.PythonPath)
            c |= RuntimeChange.RestartDanmaku;

        if (a.Memory.ExtractEveryNTurns != b.Memory.ExtractEveryNTurns)
            c |= RuntimeChange.UpdateMemoryParams;
        if (a.Memory.DatabasePath != b.Memory.DatabasePath ||
            a.Memory.EmbeddingModelPath != b.Memory.EmbeddingModelPath)
            c |= RuntimeChange.ReopenMemory;

        if (a.Audio.InputDeviceIndex != b.Audio.InputDeviceIndex ||
            a.Audio.OutputDeviceIndex != b.Audio.OutputDeviceIndex ||
            a.Audio.UseLoopback != b.Audio.UseLoopback ||
            a.Audio.LoopbackDeviceName != b.Audio.LoopbackDeviceName ||
            a.Audio.EnableLoopbackListen != b.Audio.EnableLoopbackListen ||
            a.Audio.LoopbackProcessName != b.Audio.LoopbackProcessName ||
            a.Audio.VadAggressiveness != b.Audio.VadAggressiveness ||
            a.Audio.PreSpeechPaddingMs != b.Audio.PreSpeechPaddingMs ||
            a.Audio.PostSpeechSilenceMs != b.Audio.PostSpeechSilenceMs ||
            a.Audio.EnableVirtualMic != b.Audio.EnableVirtualMic ||
            a.Audio.VirtualMicDeviceName != b.Audio.VirtualMicDeviceName)
            c |= RuntimeChange.RestartAudio;

        return c;
    }

    private static bool DictEqual(Dictionary<string, string>? x, Dictionary<string, string>? y)
    {
        x ??= []; y ??= [];
        if (x.Count != y.Count) return false;
        foreach (var kv in x)
            if (!y.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }
}
