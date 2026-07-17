using AIVTuber.Core.Config;

namespace AIVTuber.Core.Ui;

/// <summary>Determines whether the first-run setup guide should be shown.</summary>
public static class FirstRunGuidance
{
    public static bool NeedsGuidance(AppConfig config)
        => string.IsNullOrEmpty(config.Llm.ApiKey) && string.IsNullOrEmpty(config.Tts.ApiKey);
}
