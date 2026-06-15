using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class PipelineStateTrackerTests
{
    [Fact]
    public void Started_GoesListening()
    {
        var t = new PipelineStateTracker();
        t.Started();
        Assert.Equal(PipelineState.Listening, t.State);
    }

    [Fact]
    public void VoiceFlow_ComputesAsrAndFirstSentenceLatency()
    {
        var t = new PipelineStateTracker();
        t.InputStarted(1000);
        Assert.Equal(PipelineState.Thinking, t.State);
        t.TranscriptReady(1300);
        Assert.Equal(300, t.LastAsrLatencyMs);
        t.SpeakingStarted(2100);
        Assert.Equal(PipelineState.Speaking, t.State);
        Assert.Equal(800, t.LastFirstSentenceMs);
        t.SpeakingStopped();
        Assert.Equal(PipelineState.Listening, t.State);
    }

    [Fact]
    public void TextFlow_HasNoAsrLatency()
    {
        var t = new PipelineStateTracker();
        t.TextInputStarted(500);
        Assert.Null(t.LastAsrLatencyMs);
        Assert.Equal(PipelineState.Thinking, t.State);
        t.SpeakingStarted(1200);
        Assert.Equal(700, t.LastFirstSentenceMs);
    }

    [Fact]
    public void Changed_FiresOnEachTransition()
    {
        var t = new PipelineStateTracker();
        int n = 0; t.Changed += (_, _) => n++;
        t.Started();
        t.InputStarted(100);
        t.SpeakingStopped();
        Assert.Equal(3, n);
    }

    [Fact]
    public void SpeakingStarted_WithoutInput_ClearsStaleFirstSentenceLatency()
    {
        var t = new PipelineStateTracker();
        // A normal turn establishes a latency...
        t.InputStarted(1000);
        t.TranscriptReady(1200);
        t.SpeakingStarted(2000);
        Assert.Equal(800, t.LastFirstSentenceMs);
        // ...then an interrupted turn speaks with no tracked input; latency must not be stale.
        t.SpeakingStarted(3000);
        Assert.Null(t.LastFirstSentenceMs);
    }

    [Fact]
    public void BackToBackVoiceTurns_RecomputeLatencyPerTurn()
    {
        var t = new PipelineStateTracker();
        t.InputStarted(1000);
        t.TranscriptReady(1300); // ASR 300
        Assert.Equal(300, t.LastAsrLatencyMs);
        t.SpeakingStarted(1500);
        t.SpeakingStopped();

        t.InputStarted(5000);
        t.TranscriptReady(5100); // ASR 100, from the new turn (not 4100 from the old start)
        Assert.Equal(100, t.LastAsrLatencyMs);
    }
}
