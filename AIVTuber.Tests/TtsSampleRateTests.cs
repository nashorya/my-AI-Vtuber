using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

/// <summary>
/// The playback rate has to follow the provider. 24000 keeps the cloud providers happy
/// (CosyVoice rejects 44100), while a self-hosted dots.tts generates 48000 natively and
/// sounds better if nothing resamples it.
/// </summary>
public class TtsSampleRateTests
{
    [Fact]
    public void DefaultsToTheCloudProviderRate()
    {
        Assert.Equal(AudioPlayer.DefaultSampleRate, new AppConfig().Tts.SampleRate);
    }

    [Fact]
    public void SampleRateChange_RebuildsTtsAndRestartsAudio()
    {
        // The player's WaveFormat and the virtual-mic buffer are both built from this
        // value, so changing it has to tear down more than the TTS client.
        var candidate = new AppConfig();
        candidate.Tts.SampleRate = 48000;

        var change = ConfigDiff.Compute(new AppConfig(), candidate);

        Assert.Equal(RuntimeChange.RebuildTts | RuntimeChange.RestartAudio, change);
        Assert.True(ConfigDiff.IsHeavy(change));
    }

    [Theory]
    [InlineData(24000)]
    [InlineData(48000)]
    public void AudioPlayer_HonoursTheConfiguredRate(int rate)
    {
        using var player = new AudioPlayer(sampleRate: rate, deviceIndex: -1);

        Assert.Equal(rate, player.SampleRate);
    }

    [Fact]
    public void VirtualMicMixer_HonoursTheConfiguredRate()
    {
        // A mismatch here would leave the stream sent to OBS pitch-shifted while local
        // playback sounded correct — the kind of split that is painful to diagnose live.
        using var mixer = new VirtualMicMixer(deviceName: null, ttsSampleRate: 48000);

        Assert.Equal(48000, mixer.TtsSampleRate);
    }
}
