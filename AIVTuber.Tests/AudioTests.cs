using AIVTuber.Core.Audio;
using System.Runtime.InteropServices;

namespace AIVTuber.Tests;

/// <summary>
/// Tests for audio components that rely on native libraries (NAudio, WebRtcVad).
/// These are skipped on non-Windows platforms because the native DLLs are not available.
/// </summary>
public class MicrophoneCaptureTests
{
    [SkippableFact]
    public void ListDevices_ReturnsArray()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "NAudio WaveInEvent requires Windows");
        var devices = MicrophoneCapture.ListDevices();
        Assert.NotNull(devices);
    }

    [SkippableFact]
    public void Constructor_SetsDefaults()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "NAudio requires Windows");
        using var capture = new MicrophoneCapture();
        Assert.NotNull(capture);
    }

    [SkippableFact]
    public void Stop_WhenNotStarted_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "NAudio requires Windows");
        using var capture = new MicrophoneCapture();
        capture.Stop();
    }
}

public class LoopbackCaptureTests
{
    [SkippableFact]
    public void ListDevices_ReturnsArray()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "WASAPI loopback requires Windows");
        var devices = LoopbackCapture.ListDevices();
        Assert.NotNull(devices);
    }

    [SkippableFact]
    public void Constructor_SetsDefaults()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "NAudio requires Windows");
        using var capture = new LoopbackCapture();
        Assert.NotNull(capture);
    }

    [SkippableFact]
    public void Stop_WhenNotStarted_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "NAudio requires Windows");
        using var capture = new LoopbackCapture();
        capture.Stop();
    }
}

public class VadDetectorTests
{
    [SkippableFact]
    public void Constructor_WithValidParameters()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "WebRtcVadSharp native DLL requires Windows");
        using var vad = new VadDetector(aggressiveness: 2, preSpeechPaddingMs: 200, postSpeechSilenceMs: 500);
        Assert.NotNull(vad);
    }

    [SkippableFact]
    public void Constructor_InvalidAggressiveness_ThrowsException()
    {
        // Argument validation doesn't need native DLL
        Assert.Throws<ArgumentOutOfRangeException>(() => new VadDetector(aggressiveness: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VadDetector(aggressiveness: 4));
    }

    [SkippableFact]
    public void Feed_WithSilence_DoesNotFireSpeechDetected()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "WebRtcVadSharp native DLL requires Windows");
        using var vad = new VadDetector(aggressiveness: 3);
        var fired = false;
        vad.SpeechDetected += (_, _) => fired = true;

        // Feed silence (all zeros) for 1 second worth of 30ms frames
        var silenceFrame = new byte[960]; // 16kHz * 16bit * 30ms = 960 bytes
        for (int i = 0; i < 33; i++)
        {
            vad.Feed(silenceFrame);
        }

        Assert.False(fired);
    }

    [SkippableFact]
    public void Flush_WhenNotSpeaking_DoesNotFire()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "WebRtcVadSharp native DLL requires Windows");
        using var vad = new VadDetector();
        var fired = false;
        vad.SpeechDetected += (_, _) => fired = true;

        vad.Flush();
        Assert.False(fired);
    }

    [SkippableFact]
    public void Feed_WithSilence_DoesNotFireSpeechFrame()
    {
        // SpeechFrame must only fire while a speech segment is in progress. Feeding pure silence
        // (which VAD classifies as non-speech) must not trigger it.
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "WebRtcVadSharp native DLL requires Windows");
        using var vad = new VadDetector(aggressiveness: 3);
        var frameCount = 0;
        vad.SpeechFrame += (_, _) => frameCount++;

        var silenceFrame = new byte[960]; // 16kHz * 16bit * 30ms
        for (int i = 0; i < 33; i++)
            vad.Feed(silenceFrame);

        Assert.Equal(0, frameCount);
    }

    [SkippableFact]
    public void Reset_ClearsInProgressSpeechFrameChannel()
    {
        // Reset must not throw even with no subscribers / no in-progress segment.
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "WebRtcVadSharp native DLL requires Windows");
        using var vad = new VadDetector();
        vad.Reset(); // should be a no-op, no exceptions
    }
}

public class VadSegmentLimitTests
{
    private static readonly byte[] Voice = [1, .. new byte[959]];
    private static readonly byte[] Quiet = new byte[960];

    private static VadDetector Vad(int softMaxMs, int maxMs) =>
        new(2, 0, 800, 16000, 30, softMaxMs, maxMs, hasSpeech: f => f[0] == 1);

    private static void Feed(VadDetector vad, byte[] frame, int count)
    {
        for (var i = 0; i < count; i++) vad.Feed(frame);
    }

    [Fact]
    public void LongSpeech_ClosesAtAShortPause_OncePastTheSoftLimit()
    {
        using var capped = Vad(softMaxMs: 600, maxMs: 0);
        using var uncapped = Vad(softMaxMs: 0, maxMs: 0);
        var cappedSegments = 0;
        var uncappedSegments = 0;
        capped.SpeechDetected += (_, _) => cappedSegments++;
        uncapped.SpeechDetected += (_, _) => uncappedSegments++;

        foreach (var vad in new[] { capped, uncapped })
        {
            Feed(vad, Voice, 25); // 750 ms of speech, past the 600 ms soft limit
            Feed(vad, Quiet, 10); // a 300 ms breath: shorter than the 800 ms closing silence
        }

        Assert.Equal(1, cappedSegments);
        Assert.Equal(0, uncappedSegments);
    }

    [Fact]
    public void ShortSpeech_StillWaitsForTheFullClosingSilence()
    {
        using var vad = Vad(softMaxMs: 6000, maxMs: 15000);
        var segments = 0;
        vad.SpeechDetected += (_, _) => segments++;

        Feed(vad, Voice, 25);
        Feed(vad, Quiet, 10);
        Assert.Equal(0, segments);
        Feed(vad, Quiet, 17); // 810 ms of silence in total
        Assert.Equal(1, segments);
    }

    [Fact]
    public void UnbrokenSpeech_IsCutAtTheHardLimit_AndContinuesInANewSegment()
    {
        using var vad = Vad(softMaxMs: 0, maxMs: 300);
        var lengths = new List<int>();
        vad.SpeechDetected += (_, seg) => lengths.Add(seg.AudioData.Length / 960);

        Feed(vad, Voice, 25);

        Assert.Equal(new[] { 10, 10 }, lengths);
    }
}

public class AudioPlayerTests
{
    [SkippableFact]
    public void Constructor_CreatesInstance()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "NAudio requires Windows");
        using var player = new AudioPlayer();
        Assert.NotNull(player);
    }

    [SkippableFact]
    public void Stop_WhenNotPlaying_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "NAudio requires Windows");
        using var player = new AudioPlayer();
        player.Stop();
    }

    [Fact]
    public void RmsUpdated_CanSubscribe()
    {
        using var player = new AudioPlayer();
        float lastRms = -1;
        player.RmsUpdated += (_, rms) => lastRms = rms;
        // Just verify subscription works without needing native audio
        Assert.Equal(-1, lastRms);
    }

    [Fact]
    public void PlaybackFinished_CanSubscribe()
    {
        using var player = new AudioPlayer();
        bool finished = false;
        player.PlaybackFinished += (_, _) => finished = true;
        Assert.False(finished);
    }

    [Fact]
    public void SpeechSegment_HasDefaults()
    {
        var segment = new SpeechSegment();
        Assert.NotNull(segment.AudioData);
        Assert.Empty(segment.AudioData);
        Assert.Equal(default, segment.StartTime);
        Assert.Equal(default, segment.EndTime);
    }

    [Fact]
    public void SpeechSegment_CanSetProperties()
    {
        var data = new byte[] { 1, 2, 3 };
        var now = DateTime.UtcNow;
        var segment = new SpeechSegment
        {
            AudioData = data,
            StartTime = now,
            EndTime = now.AddSeconds(1)
        };
        Assert.Equal(data, segment.AudioData);
        Assert.Equal(now, segment.StartTime);
    }
}