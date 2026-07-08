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
    public void VoiceFlow_ComputesAsrLlmAndTtsLatency()
    {
        var t = new PipelineStateTracker();
        t.InputStarted(1000);
        Assert.Equal(PipelineState.Thinking, t.State);
        t.TranscriptReady(1300);           // ASR = 300 ms
        Assert.Equal(300, t.LastAsrLatencyMs);
        t.LlmFirstSentenceReady(1900);     // LLM = 600 ms
        Assert.Equal(600, t.LastLlmLatencyMs);
        t.SpeakingStarted(2100);           // TTS = 200 ms
        Assert.Equal(PipelineState.Speaking, t.State);
        Assert.Equal(200, t.LastTtsLatencyMs);
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
        t.LlmFirstSentenceReady(1000);     // LLM = 500 ms
        Assert.Equal(500, t.LastLlmLatencyMs);
        t.SpeakingStarted(1200);           // TTS = 200 ms
        Assert.Equal(200, t.LastTtsLatencyMs);
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
    public void SpeakingStarted_WithoutLlmReady_TtsLatencyIsNull()
    {
        var t = new PipelineStateTracker();
        // Normal turn — establishes LLM latency
        t.InputStarted(1000);
        t.TranscriptReady(1200);
        t.LlmFirstSentenceReady(1800);
        t.SpeakingStarted(2000);
        Assert.Equal(200, t.LastTtsLatencyMs);
        // Interrupted turn: speaking fires without a preceding LlmFirstSentenceReady
        t.SpeakingStarted(3000);
        Assert.Null(t.LastTtsLatencyMs);
    }

    [Fact]
    public void BackToBackVoiceTurns_RecomputeLatencyPerTurn()
    {
        var t = new PipelineStateTracker();
        t.InputStarted(1000);
        t.TranscriptReady(1300); // ASR 300
        Assert.Equal(300, t.LastAsrLatencyMs);
        t.LlmFirstSentenceReady(1500);
        t.SpeakingStarted(1600);
        t.SpeakingStopped();

        t.InputStarted(5000);
        t.TranscriptReady(5100); // ASR 100, from the new turn
        Assert.Equal(100, t.LastAsrLatencyMs);
    }
}
