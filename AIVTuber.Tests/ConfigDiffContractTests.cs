using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class ConfigDiffContractTests
{
    private static readonly string[] RegisteredRuntimeFields =
    [
        "Asr.Provider", "Asr.ApiKey", "Asr.AppId", "Asr.Model", "Asr.LocalAsrUrl", "Asr.PythonPath",
        "Asr.PersistConnection", "Asr.Streaming",
        "Tts.Provider", "Tts.ApiKey", "Tts.VoiceId", "Tts.Model", "Tts.GroupId", "Tts.Speed",
    ];

    public static TheoryData<string, Action<AppConfig>, RuntimeChange> AsrAndTtsRuntimeFields => new()
    {
        { "Asr.Provider", c => c.Asr.Provider = "local", RuntimeChange.RebuildAsr },
        { "Asr.ApiKey", c => c.Asr.ApiKey = "new-asr-key", RuntimeChange.RebuildAsr },
        { "Asr.AppId", c => c.Asr.AppId = "new-app-id", RuntimeChange.RebuildAsr },
        { "Asr.Model", c => c.Asr.Model = "qwen3-asr-flash", RuntimeChange.RebuildAsr },
        { "Asr.LocalAsrUrl", c => c.Asr.LocalAsrUrl = "http://localhost:9876", RuntimeChange.RebuildAsr },
        { "Asr.PythonPath", c => c.Asr.PythonPath = "python3", RuntimeChange.RebuildAsr },
        { "Asr.PersistConnection", c => c.Asr.PersistConnection = false, RuntimeChange.RebuildAsr },
        { "Asr.Streaming", c => c.Asr.Streaming = false, RuntimeChange.RebuildAsr },
        { "Tts.Provider", c => c.Tts.Provider = "minimax", RuntimeChange.RebuildTts },
        { "Tts.ApiKey", c => c.Tts.ApiKey = "new-tts-key", RuntimeChange.RebuildTts },
        { "Tts.VoiceId", c => c.Tts.VoiceId = "new-voice", RuntimeChange.RebuildTts },
        { "Tts.Model", c => c.Tts.Model = "speech-2.8-hd", RuntimeChange.RebuildTts },
        { "Tts.GroupId", c => c.Tts.GroupId = "new-group", RuntimeChange.RebuildTts },
        { "Tts.Speed", c => c.Tts.Speed = 1.25, RuntimeChange.RebuildTts },
    };

    [Theory]
    [MemberData(nameof(AsrAndTtsRuntimeFields))]
    public void AsrAndTtsRuntimeField_MapsToExactChange(
        string property,
        Action<AppConfig> mutate,
        RuntimeChange expected)
    {
        var declaredRuntimeFields = typeof(AsrConfig).GetProperties()
            .Select(p => $"Asr.{p.Name}")
            .Concat(typeof(TtsConfig).GetProperties().Select(p => $"Tts.{p.Name}"))
            .Order()
            .ToArray();
        Assert.Equal(declaredRuntimeFields, RegisteredRuntimeFields.Order().ToArray());
        Assert.Contains(property, RegisteredRuntimeFields);

        var active = new AppConfig();
        var candidate = new AppConfig();

        mutate(candidate);

        var actual = ConfigDiff.Compute(active, candidate);

        Assert.True(actual == expected, $"{property} mapped to {actual}, expected exactly {expected}.");
        Assert.False(ConfigDiff.IsHeavy(actual));
    }
}
