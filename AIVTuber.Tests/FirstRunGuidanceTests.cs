using AIVTuber.Core.Config;
using AIVTuber.Core.Ui;

namespace AIVTuber.Tests;

public class FirstRunGuidanceTests
{
    [Fact]
    public void NeedsGuidance_WhenBothProviderKeysAreMissing()
    {
        var config = new AppConfig();

        Assert.True(FirstRunGuidance.NeedsGuidance(config));
    }

    [Fact]
    public void NeedsGuidance_IsDismissedWhenEitherProviderKeyExists()
    {
        var withLlm = new AppConfig();
        withLlm.Llm.ApiKey = "llm-key";
        var withTts = new AppConfig();
        withTts.Tts.ApiKey = "tts-key";

        Assert.False(FirstRunGuidance.NeedsGuidance(withLlm));
        Assert.False(FirstRunGuidance.NeedsGuidance(withTts));
    }
}
