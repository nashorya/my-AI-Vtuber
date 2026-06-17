namespace AIVTuber.Core.Runtime;

/// <summary>High-level pipeline state shown on the monitor.</summary>
public enum PipelineState { Idle, Listening, Thinking, Speaking }

/// <summary>
/// Tracks the pipeline's high-level state and computes ASR / first-sentence latency.
/// Pure logic: callers pass monotonic millisecond timestamps (e.g. Environment.TickCount64),
/// so it is fully unit-testable. Thread-safe: the voice (audio thread) and danmaku
/// (thread-pool) flows can call concurrently, and the monitor reads State/latency from the
/// UI thread, so all access is guarded by a lock. The Changed event is raised outside the
/// lock to avoid holding it during subscriber callbacks.
/// </summary>
public sealed class PipelineStateTracker
{
    private readonly object _lock = new();
    private long? _inputStartMs;
    private long? _transcriptMs;
    private long? _llmFirstSentenceMs;
    private PipelineState _state = PipelineState.Idle;
    private long? _lastAsr;
    private long? _lastLlm;
    private long? _lastTts;

    public PipelineState State { get { lock (_lock) { return _state; } } }
    public long? LastAsrLatencyMs { get { lock (_lock) { return _lastAsr; } } }
    public long? LastLlmLatencyMs { get { lock (_lock) { return _lastLlm; } } }
    public long? LastTtsLatencyMs { get { lock (_lock) { return _lastTts; } } }

    public event EventHandler? Changed;

    public void Started() => Transition(() => _state = PipelineState.Listening);

    /// <summary>Voice input detected by VAD; start the ASR timer.</summary>
    public void InputStarted(long nowMs) => Transition(() =>
    {
        _inputStartMs = nowMs;
        _state = PipelineState.Thinking;
    });

    /// <summary>ASR transcript ready; record ASR latency, start the LLM timer.</summary>
    public void TranscriptReady(long nowMs) => Transition(() =>
    {
        if (_inputStartMs is { } start) _lastAsr = nowMs - start;
        _transcriptMs = nowMs;
        _state = PipelineState.Thinking;
    });

    /// <summary>Text (danmaku) input; no ASR step, start the LLM timer.</summary>
    public void TextInputStarted(long nowMs) => Transition(() =>
    {
        _inputStartMs = null;
        _lastAsr = null;
        _transcriptMs = nowMs;
        _state = PipelineState.Thinking;
    });

    /// <summary>First sentence sent to TTS; record LLM latency, start the TTS timer.</summary>
    public void LlmFirstSentenceReady(long nowMs) => Transition(() =>
    {
        _lastLlm = _transcriptMs is { } t ? nowMs - t : null;
        _llmFirstSentenceMs = nowMs;
    });

    /// <summary>AI audio playback started; record TTS latency.</summary>
    public void SpeakingStarted(long nowMs) => Transition(() =>
    {
        _lastTts = _llmFirstSentenceMs is { } t ? nowMs - t : null;
        _llmFirstSentenceMs = null;
        _transcriptMs = null;
        _state = PipelineState.Speaking;
    });

    public void SpeakingStopped() => Transition(() => _state = PipelineState.Listening);

    private void Transition(Action mutate)
    {
        lock (_lock) { mutate(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
