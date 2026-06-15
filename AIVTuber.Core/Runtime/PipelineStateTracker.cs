namespace AIVTuber.Core.Runtime;

/// <summary>High-level pipeline state shown on the monitor.</summary>
public enum PipelineState { Idle, Listening, Thinking, Speaking }

/// <summary>
/// Tracks the pipeline's high-level state and computes ASR / first-sentence latency.
/// Pure logic: callers pass monotonic millisecond timestamps (e.g. Environment.TickCount64),
/// so it is fully unit-testable.
/// </summary>
public sealed class PipelineStateTracker
{
    private long _inputStartMs;
    private long _transcriptMs;

    public PipelineState State { get; private set; } = PipelineState.Idle;
    public long? LastAsrLatencyMs { get; private set; }
    public long? LastFirstSentenceMs { get; private set; }

    public event EventHandler? Changed;

    public void Started() => Set(PipelineState.Listening);

    public void InputStarted(long nowMs)
    {
        _inputStartMs = nowMs;
        Set(PipelineState.Thinking);
    }

    public void TranscriptReady(long nowMs)
    {
        if (_inputStartMs != 0) LastAsrLatencyMs = nowMs - _inputStartMs;
        _transcriptMs = nowMs;
        Set(PipelineState.Thinking);
    }

    public void TextInputStarted(long nowMs)
    {
        _inputStartMs = 0;
        LastAsrLatencyMs = null;
        _transcriptMs = nowMs;
        Set(PipelineState.Thinking);
    }

    public void SpeakingStarted(long nowMs)
    {
        if (_transcriptMs != 0) { LastFirstSentenceMs = nowMs - _transcriptMs; _transcriptMs = 0; }
        Set(PipelineState.Speaking);
    }

    public void SpeakingStopped() => Set(PipelineState.Listening);

    private void Set(PipelineState s)
    {
        State = s;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
