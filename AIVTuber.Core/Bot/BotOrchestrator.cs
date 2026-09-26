using System.Collections.Concurrent;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Avatar;
using System.Threading.Channels;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Vts;
using System.Text;

namespace AIVTuber.Core.Bot;

/// <summary>
/// Coordinates the full pipeline: VAD -> ASR -> LLM -> TTS -> AudioPlayer -> VTS lip-sync.
/// Uses a bounded channel (capacity 3) for sentence-level backpressure.
/// Integrates with VTS for lip-sync (RMS -> ParamMouthOpenY) and expressions (emotion -> hotkey).
/// </summary>
public sealed class BotOrchestrator : IDisposable
{
    private readonly IAsrClient _asr;
    private readonly ILlmClient _llm;
    private readonly ITtsClient _tts;
    private readonly AudioPlayer _player;
    private IAvatarMotionSink? _motion;
    private readonly ConcurrentDictionary<RequestGeneration, AvatarReplyPlan> _avatarPlans = new();
    private EventHandler<AvatarReplyPlan>? _avatarPlanHandler;
    private Func<IAsyncEnumerable<byte[]>, CancellationToken, Action, Task>? _playWithStart;
    private static long _avatarSequence;
    private long _activeAvatarGeneration;

    public void ConfigureContinuousControl(IAvatarMotionSink motion,
        Func<IAsyncEnumerable<byte[]>, CancellationToken, Action, Task>? playWithStart = null)
    {
        _motion = motion;
        _playWithStart = playWithStart ?? ((chunks, ct, start) => _player.PlayChunksAsync(chunks, ct, start));
        if (_llm is IAvatarReplySource source && _avatarPlanHandler is null)
        {
            _avatarPlanHandler = (_, plan) =>
            {
                var context = CurrentEventContext();
                if (context is null) return;
                _avatarPlans[context.Generation] = plan;
                if (plan.Diagnostic is not null)
                    AIVTuber.Core.Diagnostics.DebugLog.Write($"[Avatar/VTS] {plan.Diagnostic}");
            };
            source.OnAvatarPlanReady += _avatarPlanHandler;
        }
    }

    private readonly TtsConfig _ttsConfig;
    private readonly VtsClient? _vts;
    private readonly VtsConfig _vtsConfig;
    private readonly EventHandler<string> _sentenceReadyHandler;
    private readonly EventHandler<string> _emotionDetectedHandler;
    private readonly EventHandler<string> _actionDetectedHandler;
    private readonly EventHandler<string> _poseDetectedHandler;
    private readonly IReadOnlyDictionary<string, string> _ttsEmotionMap;
    private readonly EventHandler<float>? _rmsUpdatedHandler;
    private readonly EventHandler? _playbackFinishedHandler;

    private readonly RequestCoordinator _coordinator;
    private readonly AsyncLocal<RequestContext?> _eventContext = new();
    private readonly ConcurrentDictionary<long, Task> _commandTasks = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Func<IAsyncEnumerable<byte[]>, CancellationToken, Task> _playChunksAsync;
    private readonly Action _stopPlayback;
    private readonly Func<string, CancellationToken, Task>? _triggerHotkeyAsync;
    private Func<string, CancellationToken, Task>? _assistantOutputCommand;
    private Func<string, CancellationToken, Task>? _userOutputCommand;
    private long _nextCommandId;
    private volatile bool _disposed;
    // Last emotion detected in the current LLM stream; reset each new turn.
    private volatile string? _currentEmotion;
    private volatile bool _deferLlmEvents;
    private readonly List<string> _deferredEmotions = [];
    private readonly List<string> _deferredActions = [];
    private readonly List<string> _deferredPoses = [];

    public event EventHandler? OnAiStartSpeaking;
    public event EventHandler? OnAiStopSpeaking;
    public event EventHandler? OnFirstSentenceToTts;
    public event EventHandler<string>? OnEmotionDetected;
    public event EventHandler<string>? OnActionDetected;
    public event EventHandler<string>? OnPoseDetected;
    public event EventHandler<string>? OnSentenceReady;
    internal event EventHandler<ClassifiedReply>? OnReplyCommitted;
    public event EventHandler<string>? OnUserTranscript;
    /// <summary>Fired when Qwen-ASR returns a non-neutral emotion for the user's speech.</summary>
    public event EventHandler<string>? OnUserEmotionDetected;
    /// <summary>Fired with the transcribed text from loopback (PC) audio.</summary>
    public event EventHandler<string>? OnLoopbackTranscript;
    public event EventHandler<string>? OnError;

    /// <summary>
    /// Optional PK wake gate. Return false to publish transcripts but skip LLM/TTS.
    /// Probe text is ASR transcript or the full text turn (danmaku template, etc.).
    /// </summary>
    public Func<string, bool>? ShouldSpeak { get; set; }

    /// <summary>RT-00 chain tracer, set by BotRuntime. Null/disabled → all marks are no-ops;
    /// wiring these marks does not change pipeline behaviour.</summary>
    public AIVTuber.Core.Diagnostics.RealtimeTrace? Trace { get; set; }

    public ICorticoPerformance? Cortico { get; set; }

    public BotOrchestrator(
        IAsrClient asr, ILlmClient llm, ITtsClient tts,
        AudioPlayer player, TtsConfig ttsConfig,
        VtsClient? vts = null, VtsConfig? vtsConfig = null,
        IReadOnlyDictionary<string, string>? ttsEmotionMap = null)
        : this(asr, llm, tts, player, ttsConfig, vts, vtsConfig,
            player.PlayChunksAsync, player.Stop,
            vts is null ? null : vts.TriggerHotkeyAsync,
            ttsEmotionMap)
    {
    }

    internal BotOrchestrator(
        IAsrClient asr, ILlmClient llm, ITtsClient tts,
        AudioPlayer player, TtsConfig ttsConfig,
        VtsClient? vts, VtsConfig? vtsConfig,
        Func<IAsyncEnumerable<byte[]>, CancellationToken, Task> playChunksAsync,
        Action stopPlayback,
        Func<string, CancellationToken, Task>? triggerHotkeyAsync,
        IReadOnlyDictionary<string, string>? ttsEmotionMap = null)
    {
        _asr = asr;
        _llm = llm;
        _tts = tts;
        _player = player;
        _ttsConfig = ttsConfig;
        _vts = vts;
        _vtsConfig = vtsConfig ?? new VtsConfig();
        _ttsEmotionMap = ttsEmotionMap
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _coordinator = new RequestCoordinator();
        _playChunksAsync = playChunksAsync;
        _stopPlayback = stopPlayback;
        _triggerHotkeyAsync = triggerHotkeyAsync;

        // Keep publisher subscriptions as named delegates so Dispose can detach them
        // symmetrically. The LLM and AudioPlayer may outlive this orchestrator during rewire.
        _sentenceReadyHandler = (_, sentence) =>
        {
            var context = CurrentEventContext();
            if (context is null || _deferLlmEvents) return;
            PublishSpokenSentence(context, sentence);
        };
        _emotionDetectedHandler = (_, emotion) =>
        {
            var context = CurrentEventContext();
            if (context is null) return;
            if (_deferLlmEvents)
            {
                lock (_deferredEmotions) _deferredEmotions.Add(emotion);
                return;
            }
            ApplyEmotion(context, emotion);
        };
        _actionDetectedHandler = (_, action) =>
        {
            var context = CurrentEventContext();
            if (context is null) return;
            if (_deferLlmEvents)
            {
                lock (_deferredActions) _deferredActions.Add(action);
                return;
            }
            ApplyAction(context, action);
        };
        _poseDetectedHandler = (_, pose) =>
        {
            var context = CurrentEventContext();
            if (context is null) return;
            if (_deferLlmEvents)
            {
                lock (_deferredPoses) _deferredPoses.Add(pose);
                return;
            }
            OnPoseDetected?.Invoke(this, pose);
        };
        _llm.OnSentenceReady += _sentenceReadyHandler;
        _llm.OnEmotionDetected += _emotionDetectedHandler;
        _llm.OnActionDetected += _actionDetectedHandler;
        _llm.OnPoseDetected += _poseDetectedHandler;

        // Wire up RMS -> VTS lip-sync
        if (_vts is not null)
        {
            _rmsUpdatedHandler = (_, rms) =>
            {
                if (!_disposed) HandleRms(rms);
            };
            _playbackFinishedHandler = (_, _) =>
            {
                if (!_disposed) TryCloseMouth();
            };
            _player.RmsUpdated += _rmsUpdatedHandler;
            _player.PlaybackFinished += _playbackFinishedHandler;
        }
    }

    private RequestContext? CurrentEventContext()
    {
        var context = _eventContext.Value;
        return context is not null && _coordinator.IsCurrent(context.Generation) ? context : null;
    }

    internal void ConfigureOutputCommands(
        Func<string, CancellationToken, Task>? assistantOutputCommand,
        Func<string, CancellationToken, Task>? userOutputCommand)
    {
        _assistantOutputCommand = assistantOutputCommand;
        _userOutputCommand = userOutputCommand;
    }

    public void SetHold(bool hold) => _coordinator.SetHold(hold);

    public Task<AsrResult> TranscribeAsync(byte[] pcm16k, CancellationToken cancellationToken = default) =>
        _asr.RecognizeAsync(pcm16k, cancellationToken);

    public Task<AsrResult> TranscribeStreamAsync(
        IAsyncEnumerable<byte[]> audioStream, CancellationToken cancellationToken = default) =>
        CollectStreamedAsync(audioStream, cancellationToken);

    private void PublishSpokenSentence(RequestContext context, string sentence)
    {
        OnSentenceReady?.Invoke(this, sentence);
        if (_assistantOutputCommand is not null)
            QueueCommand(context, "[OBS] assistant subtitle",
                ct => _assistantOutputCommand(sentence, ct));
    }

    private void ApplyEmotion(RequestContext context, string emotion)
    {
        _currentEmotion = emotion;
        OnEmotionDetected?.Invoke(this, emotion);
        QueueMappedHotkey(context, _vtsConfig.EmotionMap, emotion, "emotion");
    }

    private void ApplyAction(RequestContext context, string action)
    {
        OnActionDetected?.Invoke(this, action);
        QueueMappedHotkey(context, _vtsConfig.ActionMap, action, "action");
    }

    private void ApplyStagedControls(RequestContext context, IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            var body = tag.Trim('[', ']');
            var colon = body.IndexOf(':');
            if (colon <= 0 || colon >= body.Length - 1) continue;
            var kind = body[..colon];
            var value = body[(colon + 1)..].Trim();
            if (kind.Equals("emotion", StringComparison.OrdinalIgnoreCase))
                ApplyEmotion(context, value);
            else if (kind.Equals("action", StringComparison.OrdinalIgnoreCase))
                ApplyAction(context, value);
            else if (kind.Equals("pose", StringComparison.OrdinalIgnoreCase))
                OnPoseDetected?.Invoke(this, value);
        }
    }

    private void FlushDeferredControls(RequestContext context)
    {
        string[] emotions, actions, poses;
        lock (_deferredEmotions)
        {
            emotions = [.. _deferredEmotions];
            _deferredEmotions.Clear();
        }
        lock (_deferredActions)
        {
            actions = [.. _deferredActions];
            _deferredActions.Clear();
        }
        lock (_deferredPoses)
        {
            poses = [.. _deferredPoses];
            _deferredPoses.Clear();
        }
        foreach (var e in emotions) ApplyEmotion(context, e);
        foreach (var a in actions) ApplyAction(context, a);
        foreach (var p in poses) OnPoseDetected?.Invoke(this, p);
    }

    private void ClearDeferredControls()
    {
        lock (_deferredEmotions) _deferredEmotions.Clear();
        lock (_deferredActions) _deferredActions.Clear();
        lock (_deferredPoses) _deferredPoses.Clear();
    }

    private void QueueMappedHotkey(
        RequestContext context,
        IReadOnlyDictionary<string, string> map,
        string name,
        string kind)
    {
        if (_motion is not null || _triggerHotkeyAsync is null) return;
        if (!TryGetHotkeyId(map, name, out var hotkeyId))
        {
            ReportCurrentError(context.Generation, $"[VTS] unknown {kind}: {name}");
            return;
        }
        QueueCommand(context, $"[VTS] {kind} hotkey", ct => _triggerHotkeyAsync(hotkeyId, ct));
    }

    private void QueueCommand(
        RequestContext context,
        string kind,
        Func<CancellationToken, Task> command)
    {
        var id = Interlocked.Increment(ref _nextCommandId);
        var task = RunCommandAsync(context, kind, command);
        _commandTasks[id] = task;
        _ = task.ContinueWith(
            completedTask => _commandTasks.TryRemove(id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunCommandAsync(
        RequestContext context,
        string kind,
        Func<CancellationToken, Task> command)
    {
        try
        {
            await _commandGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                if (_coordinator.IsCurrent(context.Generation))
                    await command(context.CancellationToken).ConfigureAwait(false);
            }
            finally { _commandGate.Release(); }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_coordinator.IsCurrent(context.Generation))
            {
                var message = $"{kind} error: {ex.Message}";
                Console.Error.WriteLine(message);
                OnError?.Invoke(this, message);
            }
        }
    }

    private async Task AwaitCommandsAsync(RequestGeneration generation)
    {
        while (_coordinator.IsCurrent(generation))
        {
            var tasks = _commandTasks.Values.ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (_commandTasks.IsEmpty) return;
        }
    }

    private sealed record RequestContext(RequestGeneration Generation, CancellationToken CancellationToken);

    private static bool TryGetHotkeyId(
        IReadOnlyDictionary<string, string> map, string name, out string hotkeyId)
    {
        if (map.TryGetValue(name, out hotkeyId!) && !string.IsNullOrWhiteSpace(hotkeyId))
            return true;

        foreach (var pair in map)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair.Value))
            {
                hotkeyId = pair.Value;
                return true;
            }
        }

        hotkeyId = string.Empty;
        return false;
    }

    private void HandleRms(float rms) => _motion?.OnRms(rms * _vtsConfig.MouthScale);

    private void TryCloseMouth() => _motion?.OnRms(0);

    /// <summary>Process a speech segment from VAD. Interrupts any ongoing processing.</summary>
    public Task ProcessSpeechAsync(SpeechSegment speech, List<Message> history, string micTemplate) =>
        _coordinator.EnqueueAsync(InputSource.Microphone, async (envelope, ct) =>
        {
            _currentEmotion = null;
            var pipelineStarted = false;
            try
            {
                var result = await _asr.RecognizeAsync(speech.AudioData, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.Text) || !IsCurrent(envelope, ct)) return;
                OnUserTranscript?.Invoke(this, result.Text);
                if (_userOutputCommand is not null)
                    QueueCommand(new RequestContext(envelope.Generation, ct), "[OBS] user subtitle",
                        commandCt => _userOutputCommand(result.Text, commandCt));
                if (result.Emotion is not null && IsCurrent(envelope, ct))
                    OnUserEmotionDetected?.Invoke(this, result.Emotion);
                if (!AllowSpeak(result.Text)) return;
                pipelineStarted = true;
                var annotated = AnnotateWithUserEmotion(result.Text, result.Emotion);
                await RunStreamingPipelineAsync(
                    history, micTemplate.Replace("{text}", annotated), envelope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportCurrentError(envelope.Generation, $"[ASR/Pipeline] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!pipelineStarted && IsCurrent(envelope, ct, allowCancellation: true))
                    OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
            }
        });

    /// <summary>
    /// Process loopback (PC audio) speech. Lower priority than microphone:
    /// skipped if the bot is already processing, and mic speech will cancel it via Interrupt().
    /// Injects "对面说：xxx" context into the LLM without a full interrupt.
    /// </summary>
    public Task ProcessLoopbackSpeechAsync(SpeechSegment speech, List<Message> history, string loopbackTemplate) =>
        _coordinator.EnqueueAsync(InputSource.Loopback, async (envelope, ct) =>
        {
            _currentEmotion = null;
            var pipelineStarted = false;
            try
            {
                var result = await _asr.RecognizeAsync(speech.AudioData, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.Text) || !IsCurrent(envelope, ct)) return;
                OnLoopbackTranscript?.Invoke(this, result.Text);
                if (!AllowSpeak(result.Text)) return;
                pipelineStarted = true;
                await RunStreamingPipelineAsync(
                    history, loopbackTemplate.Replace("{text}", result.Text), envelope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportCurrentError(envelope.Generation, $"[Loopback/ASR] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!pipelineStarted && IsCurrent(envelope, ct, allowCancellation: true))
                    OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
            }
        });

    /// <summary>
    /// Process a microphone speech segment with real-time streaming ASR: pushes audio chunks
    /// to the ASR service as they arrive (via <see cref="IAsrClient.StreamRecognizeAsync"/>)
    /// instead of waiting for the full VAD segment. <paramref name="meta"/> carries only
    /// timestamps/source; audio comes from <paramref name="audioStream"/>.
    /// </summary>
    public Task ProcessSpeechStreamingAsync(
        IAsyncEnumerable<byte[]> audioStream, SpeechSegment meta, List<Message> history, string micTemplate) =>
        _coordinator.EnqueueAsync(InputSource.Microphone, async (envelope, ct) =>
        {
            _currentEmotion = null;
            var pipelineStarted = false;
            try
            {
                var result = await CollectStreamedAsync(audioStream, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.Text) || !IsCurrent(envelope, ct)) return;
                OnUserTranscript?.Invoke(this, result.Text);
                if (_userOutputCommand is not null)
                    QueueCommand(new RequestContext(envelope.Generation, ct), "[OBS] user subtitle",
                        commandCt => _userOutputCommand(result.Text, commandCt));
                if (result.Emotion is not null && IsCurrent(envelope, ct))
                    OnUserEmotionDetected?.Invoke(this, result.Emotion);
                if (!AllowSpeak(result.Text)) return;
                pipelineStarted = true;
                var annotated = AnnotateWithUserEmotion(result.Text, result.Emotion);
                await RunStreamingPipelineAsync(
                    history, micTemplate.Replace("{text}", annotated), envelope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportCurrentError(envelope.Generation, $"[ASR/Pipeline] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!pipelineStarted && IsCurrent(envelope, ct, allowCancellation: true))
                    OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
            }
        });

    /// <summary>
    /// Streaming variant of <see cref="ProcessLoopbackSpeechAsync"/>. Same priority semantics.
    /// </summary>
    public Task ProcessLoopbackSpeechStreamingAsync(
        IAsyncEnumerable<byte[]> audioStream, SpeechSegment meta, List<Message> history, string loopbackTemplate) =>
        _coordinator.EnqueueAsync(InputSource.Loopback, async (envelope, ct) =>
        {
            _currentEmotion = null;
            var pipelineStarted = false;
            try
            {
                var result = await CollectStreamedAsync(audioStream, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.Text) || !IsCurrent(envelope, ct)) return;
                OnLoopbackTranscript?.Invoke(this, result.Text);
                if (!AllowSpeak(result.Text)) return;
                pipelineStarted = true;
                await RunStreamingPipelineAsync(
                    history, loopbackTemplate.Replace("{text}", result.Text), envelope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportCurrentError(envelope.Generation, $"[Loopback/ASR] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!pipelineStarted && IsCurrent(envelope, ct, allowCancellation: true))
                    OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
            }
        });

    /// <summary>
    /// Drains a streaming ASR result and returns the final transcript + emotion.
    /// DashScope streaming yields each finalized sentence separately; we concatenate them.
    /// Emotion comes from the last non-null result (Qwen-ASR fills it).
    /// </summary>
    private async Task<AsrResult> CollectStreamedAsync(
        IAsyncEnumerable<byte[]> audioStream, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        string? emotion = null;
        await foreach (var r in _asr.StreamRecognizeAsync(audioStream, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(r.Text))
                transcript.Append(r.Text);
            if (r.Emotion is not null)
                emotion = r.Emotion;
        }
        return new AsrResult(transcript.ToString(), emotion);
    }

    /// <summary>Process text directly (e.g., from danmaku or a dual-silence turn).</summary>
    /// <param name="wakeProbe">Text used for wake matching; defaults to <paramref name="text"/>.</param>
    /// <param name="bypassWake">When true, the model decides PASS / thought / speak.</param>
    public Task ProcessTextAsync(
        string text,
        List<Message> history,
        string? wakeProbe = null,
        bool bypassWake = false,
        Func<bool>? canCommit = null,
        bool requireStructuredReply = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        return _coordinator.EnqueueAsync(InputSource.Danmaku, async (envelope, ct) =>
        {
            _currentEmotion = null;
            var pipelineStarted = false;
            try
            {
                if (!bypassWake && !AllowSpeak(wakeProbe ?? text)) return;
                pipelineStarted = true;
                await RunStreamingPipelineAsync(history, text, envelope, ct, canCommit, requireStructuredReply).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportCurrentError(envelope.Generation, $"[Pipeline] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!pipelineStarted && IsCurrent(envelope, ct, allowCancellation: true))
                    OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private bool AllowSpeak(string probeText)
    {
        if (ShouldSpeak is null || ShouldSpeak(probeText)) return true;
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[唤起] PK 静默，跳过：「{probeText}」");
        return false;
    }

    /// <summary>Interrupt any ongoing processing and stop playback immediately.</summary>
    public void Interrupt()
    {
        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelRequested);
        _motion?.Cancel(Interlocked.Read(ref _activeAvatarGeneration));
        _motion?.OnRms(0);
        _coordinator.SetHold(false);
        _coordinator.CancelCurrentAsync().GetAwaiter().GetResult();
        // RT-06: bidirectional TTS has a real vendor-side cancel barrier — wait for the
        // task_cancel acknowledgement (or an epoch rebuild when the ack is lost) before any
        // next turn's text can be submitted. Legacy/streaming paths keep the local-generation
        // mark because there is no vendor ack to observe.
        if (_tts is AIVTuber.Core.RealtimeTts.IBidiTtsController bidi)
        {
            var outcome = bidi.CancelPendingAsync().GetAwaiter().GetResult();
            if (outcome == AIVTuber.Core.RealtimeTts.TtsCancelOutcome.ServerConfirmed)
                Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelAcked);
            else if (outcome == AIVTuber.Core.RealtimeTts.TtsCancelOutcome.EpochRebuilt)
                Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelEpochRebuild);
        }
        else
        {
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelAcked);
        }
        _currentEmotion = null;
        _deferLlmEvents = false;
        ClearDeferredControls();
        _stopPlayback();
        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.PlaybackStopped);
    }

    public bool IsProcessing => _coordinator.IsBusy;

    private bool IsCurrent(InputEnvelope envelope, CancellationToken cancellationToken, bool allowCancellation = false) =>
        _coordinator.IsCurrent(envelope.Generation) && (allowCancellation || !cancellationToken.IsCancellationRequested);

    private void ReportCurrentError(RequestGeneration generation, string message)
    {
        if (!_coordinator.IsCurrent(generation)) return;
        Console.Error.WriteLine(message);
        OnError?.Invoke(this, message);
    }

    private async Task RunStreamingPipelineAsync(
        List<Message> history,
        string userInput,
        InputEnvelope envelope,
        CancellationToken ct,
        Func<bool>? canCommit = null,
        bool requireStructuredReply = false)
    {
        if (Cortico is not null)
        {
            await RunCorticoAsync(history, userInput, envelope, ct, canCommit).ConfigureAwait(false);
            return;
        }
        if (_llm is IReplyProtocolStream { ReplyProtocol: "v2" } protocolStream)
        {
            await RunStreamingPipelineV2Async(
                protocolStream, history, userInput, envelope, ct, canCommit).ConfigureAwait(false);
            return;
        }
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[LLM输入] {userInput}");
        _coordinator.SetHold(true);
        var sentenceChannel = Channel.CreateBounded<string>(3);
        var context = new RequestContext(envelope.Generation, ct);
        var avatarGeneration = Interlocked.Increment(ref _avatarSequence);
        var previousAvatar = Interlocked.Exchange(ref _activeAvatarGeneration, avatarGeneration);
        _motion?.Cancel(previousAvatar);
        _motion?.BeginTurn(avatarGeneration);
        using var cancelAvatar = ct.Register(() => _motion?.Cancel(avatarGeneration));
        ClassifiedReply? pendingReply = null;
        string pendingRaw = "";

        var producerTask = Task.Run(async () =>
        {
            var rawAll = new StringBuilder();
            var previousContext = _eventContext.Value;
            _eventContext.Value = context;
            _deferLlmEvents = true;
            ClearDeferredControls();
            bool llmFirstContentMarked = false;
            try
            {
                Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmRequest);
                await foreach (var token in _llm.StreamAsync(history, userInput, ct))
                {
                    if (!IsCurrent(envelope, ct)) break;
                    if (!llmFirstContentMarked && token.Length > 0)
                    {
                        llmFirstContentMarked = true;
                        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmFirstContent);
                    }
                    rawAll.Append(token);
                }
                Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmDone);

                if (!IsCurrent(envelope, ct)) return;
                _avatarPlans.TryRemove(context.Generation, out var avatarPlan);
                var classified = ReplyClassifier.ClassifyTurn(rawAll.ToString(), avatarPlan, requireStructuredReply);
                if (classified.Kind == ReplyKind.Speak && classified.AvatarIntent is null &&
                    AvatarReplyProtocol.InferRequestedMotion(userInput) is { } inferred)
                    classified = classified with { AvatarIntent = inferred };
                _deferLlmEvents = false;
                if (canCommit is not null && !canCommit()) return;
                if (classified.Kind == ReplyKind.Speak)
                {
                    pendingReply = classified;
                    pendingRaw = rawAll.ToString();
                    await sentenceChannel.Writer.WriteAsync(classified.Spoken, ct);
                }
                else
                {
                    CommitClassifiedReply(context, classified, rawAll.ToString());
                    if (classified.Kind == ReplyKind.InnerThought && classified.AvatarIntent is { } intent && IsCurrent(envelope, ct))
                        _motion?.Submit(avatarGeneration, intent);
                }
            }
            finally
            {
                _deferLlmEvents = false;
                _eventContext.Value = previousContext;
                sentenceChannel.Writer.TryComplete();
            }
        }, ct);

        // Stream TTS for the (usually single) utterance. WaveOut stays open for the turn.
        bool ttsStarted = false;
        bool ttsFirstChunkMarked = false;
        bool ttsFirstPcmMarked = false;
        async IAsyncEnumerable<byte[]> TtsChunks([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken streamCt = default)
        {
            await foreach (var sentence in sentenceChannel.Reader.ReadAllAsync(streamCt))
            {
                if (!IsCurrent(envelope, streamCt)) yield break;
                if (!LlmClient.IsSpeakableText(sentence)) continue;
                Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.TtsRequest);
                await foreach (var chunk in _tts.StreamAsync(
                                   sentence,
                                   _ttsConfig.VoiceId,
                                   ResolveTtsEmotion(PeekPendingEmotion(pendingReply)),
                                   streamCt))
                {
                    if (!IsCurrent(envelope, streamCt)) yield break;
                    if (!ttsFirstChunkMarked && chunk.Length > 0)
                    {
                        ttsFirstChunkMarked = true;
                        // First vendor audio for this turn (encoded transport representation).
                        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.TtsFirstEncodedAudio);
                    }
                    if (!ttsStarted)
                    {
                        // Recheck after synthesis too: people may have resumed while
                        // the LLM or TTS was waiting on the network. No public effects yet.
                        if (canCommit is not null && !canCommit()) yield break;
                        ttsStarted = true;
                        CommitClassifiedReply(context, pendingReply!.Value, pendingRaw);
                        // First complete speakable segment handed to TTS. Note: the existing
                        // OnFirstSentenceToTts actually fires on first synthesized audio, not
                        // at request time (plan RT-00 caveat) — hence TtsRequest above.
                        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmFirstSpeechSegment);
                        OnFirstSentenceToTts?.Invoke(this, EventArgs.Empty);
                        OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
                    }
                    if (!ttsFirstPcmMarked && chunk.Length > 0)
                    {
                        ttsFirstPcmMarked = true;
                        // Software playback estimate: the chunk is being handed to the player's
                        // write path; actual soundcard output is not separately measured yet.
                        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.TtsFirstPcm);
                        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.PlaybackFirst);
                    }
                    yield return chunk;
                }
            }
        }

        Exception? pipelineEx = null;
        // RT-06: mark the turn on the bidi TTS session so audio attribution and the cancel
        // barrier are turn-scoped. Non-bidi clients skip this entirely.
        var bidiTts = _tts as AIVTuber.Core.RealtimeTts.IBidiTtsController;
        if (bidiTts is not null && IsCurrent(envelope, ct))
            await bidiTts.BeginTurnAsync(ct).ConfigureAwait(false);
        try
        {
            void FirstPcmRead()
            {
                if (IsCurrent(envelope, ct) && pendingReply?.AvatarIntent is { } intent)
                    _motion?.Submit(avatarGeneration, intent);
            }
            var playbackTask = !IsCurrent(envelope, ct) ? Task.CompletedTask
                : _playWithStart is not null ? _playWithStart(TtsChunks(ct), ct, FirstPcmRead)
                : _playChunksAsync(TtsChunks(ct), ct);
            await Task.WhenAll(playbackTask, producerTask).ConfigureAwait(false);
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.PlaybackEnd);
            await AwaitCommandsAsync(envelope.Generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            pipelineEx = ex;
            ReportCurrentError(envelope.Generation, $"[LLM/TTS] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _coordinator.SetHold(false);
            _avatarPlans.TryRemove(context.Generation, out _);
            if (ttsStarted || ct.IsCancellationRequested || pipelineEx is not null)
            {
                _motion?.Cancel(avatarGeneration);
                _motion?.OnRms(0);
            }
            if (ttsStarted && IsCurrent(envelope, ct, allowCancellation: true))
                OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }

        if (pipelineEx is not null) throw pipelineEx;
    }

    private enum ReplySegmentState { Generated, SubmittedToTts, Played }

    /// <summary>Per-turn state for one approved speech segment (state ledger, plan 4.3).</summary>
    private sealed class ReplySegment(int seq, string text)
    {
        public int Seq { get; } = seq;
        public string Text { get; } = text;
        public ReplySegmentState State { get; set; } = ReplySegmentState.Generated;
        public int EmotionCursor { get; set; } // emotions noted before this segment was generated
    }

    /// <summary>
    /// Per-turn staging and ledger for protocol v2. Deliberately NOT the shared
    /// _deferLlmEvents/_deferred* fields: every async request owns its own instance, so
    /// one turn's deferred controls can never leak into another request.
    /// </summary>
    private sealed class ReplyTurnV2
    {
        public ReplyDecisionMode Decision;
        public string Thought = "";
        public string? ProtocolError;
        public bool Interrupted;
        public bool PlaybackStarted;
        public readonly List<ReplySegment> Segments = [];
        private readonly List<string> _emotions = []; // ordered: control events + segment [emotion:x] tags
        private readonly List<string> _otherControls = [];
        private int _appliedEmotions;
        public AvatarIntent? StagedMotion;
        public bool MotionFlushed;

        public ReplySegment AddSegment(string text)
        {
            var segment = new ReplySegment(Segments.Count, text) { EmotionCursor = _emotions.Count };
            Segments.Add(segment);
            return segment;
        }

        public void NoteEmotion(string emotion) { lock (_emotions) _emotions.Add(emotion); }
        public void NoteControl(string tag) { lock (_otherControls) _otherControls.Add(tag); }
        public string? PeekEmotion(ReplySegment segment)
        {
            lock (_emotions)
                return _emotions.Take(segment.EmotionCursor).LastOrDefault();
        }
        /// <summary>Emotions not yet applied to VTS; consumed when a segment starts playing,
        /// so expression changes ride with the voice they belong to.</summary>
        public IReadOnlyList<string> DrainUnappliedEmotions()
        {
            lock (_emotions)
            {
                var copy = _emotions.Skip(_appliedEmotions).ToArray();
                _appliedEmotions = _emotions.Count;
                return copy;
            }
        }
        public IReadOnlyList<string> DrainControls()
        {
            lock (_otherControls)
            {
                var copy = _otherControls.ToArray();
                _otherControls.Clear();
                return copy;
            }
        }
    }

    /// <summary>
    /// Protocol v2 pipeline (RT-05): consumes validated NDJSON events and releases each
    /// approved speech segment to TTS as soon as it arrives — the first segment is spoken
    /// while the LLM may still be generating the rest. No rawAll whole-reply buffering;
    /// classification happens per segment, before any public side effect.
    /// </summary>
    private async Task RunStreamingPipelineV2Async(
        IReplyProtocolStream protocolStream,
        List<Message> history,
        string userInput,
        InputEnvelope envelope,
        CancellationToken ct,
        Func<bool>? canCommit)
    {
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[LLM输入] {userInput}");
        _coordinator.SetHold(true);
        var sentenceChannel = Channel.CreateBounded<ReplySegment>(3);
        var context = new RequestContext(envelope.Generation, ct);
        var avatarGeneration = Interlocked.Increment(ref _avatarSequence);
        var previousAvatar = Interlocked.Exchange(ref _activeAvatarGeneration, avatarGeneration);
        _motion?.Cancel(previousAvatar);
        _motion?.BeginTurn(avatarGeneration);
        using var cancelAvatar = ct.Register(() => _motion?.Cancel(avatarGeneration));
        var turn = new ReplyTurnV2();

        var producerTask = Task.Run(async () =>
        {
            var previousContext = _eventContext.Value;
            _eventContext.Value = context;
            try
            {
                await foreach (var ev in protocolStream.StreamEventsAsync(history, userInput, ct))
                {
                    if (!IsCurrent(envelope, ct)) { turn.Interrupted = true; return; }
                    switch (ev.Kind)
                    {
                        case ReplyStreamEventKind.ProtocolError:
                            // Fail closed: already-submitted segments keep playing (played audio
                            // cannot be recalled) but nothing further is released.
                            turn.ProtocolError = ev.Error ?? "未知协议错误";
                            return;
                        case ReplyStreamEventKind.Decision:
                            turn.Decision = ev.Decision;
                            turn.Thought = ev.Text;
                            break;
                        case ReplyStreamEventKind.Control:
                            if (ev.ControlKind == "emotion")
                            {
                                turn.NoteEmotion(ev.Text);
                            }
                            else if (ev.Motion is { } intent)
                            {
                                bool started;
                                lock (turn) started = turn.PlaybackStarted;
                                // Exactly-once: staged before playback, direct submit after.
                                if (started) _motion?.Submit(avatarGeneration, intent);
                                else turn.StagedMotion ??= intent;
                            }
                            break;
                        case ReplyStreamEventKind.Speech:
                            var classified = ReplyClassifier.Classify(ev.Text);
                            if (classified.Kind == ReplyKind.Invalid)
                            {
                                turn.ProtocolError = "speech 段未通过内容隔离校验（括号/标记不完整）";
                                return;
                            }
                            if (classified.Kind != ReplyKind.Speak) break; // thought/pass-only segment: no TTS, no caption
                            if (canCommit is not null && !canCommit()) { turn.Interrupted = true; return; }
                            foreach (var tag in classified.StagedControls)
                            {
                                if (tag.StartsWith("[emotion:", StringComparison.OrdinalIgnoreCase))
                                    turn.NoteEmotion(tag[9..^1].Trim());
                                else turn.NoteControl(tag);
                            }
                            var segment = turn.AddSegment(classified.Spoken);
                            await sentenceChannel.Writer.WriteAsync(segment, ct);
                            break;
                        case ReplyStreamEventKind.End:
                            if (turn.Decision == ReplyDecisionMode.Speak && turn.StagedMotion is null &&
                                AvatarReplyProtocol.InferRequestedMotion(userInput) is { } inferred)
                                turn.StagedMotion = inferred;
                            break;
                    }
                }
            }
            finally
            {
                _eventContext.Value = previousContext;
                sentenceChannel.Writer.TryComplete();
            }
        }, ct);

        // Stream TTS per approved segment; each segment is committed to history/captions
        // only when its own audio actually starts, so an interrupted turn never records
        // segments that were generated but never played.
        bool ttsStarted = false;
        async IAsyncEnumerable<byte[]> TtsChunks([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken streamCt = default)
        {
            await foreach (var segment in sentenceChannel.Reader.ReadAllAsync(streamCt))
            {
                if (!IsCurrent(envelope, streamCt)) { turn.Interrupted = true; yield break; }
                if (!LlmClient.IsSpeakableText(segment.Text)) continue;
                segment.State = ReplySegmentState.SubmittedToTts;
                await foreach (var chunk in _tts.StreamAsync(
                                   segment.Text,
                                   _ttsConfig.VoiceId,
                                   ResolveTtsEmotion(turn.PeekEmotion(segment)),
                                   streamCt))
                {
                    if (!IsCurrent(envelope, streamCt)) { turn.Interrupted = true; yield break; }
                        if (segment.State != ReplySegmentState.Played)
                        {
                            // Recheck after synthesis: people may have resumed while the LLM or
                            // TTS was waiting on the network. No public effects yet for this segment.
                            if (canCommit is not null && !canCommit()) { turn.Interrupted = true; yield break; }
                            segment.State = ReplySegmentState.Played;
                            foreach (var emotion in turn.DrainUnappliedEmotions())
                                ApplyEmotion(context, emotion);
                            if (!ttsStarted)
                        {
                            ttsStarted = true;
                            lock (turn) turn.PlaybackStarted = true;
                            FlushTurnControls(context, turn);
                            OnFirstSentenceToTts?.Invoke(this, EventArgs.Empty);
                            OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
                        }
                        OnReplyCommitted?.Invoke(this, new ClassifiedReply(ReplyKind.Speak, segment.Text, "", []));
                        PublishSpokenSentence(context, segment.Text);
                    }
                    yield return chunk;
                }
            }
        }

        Exception? pipelineEx = null;
        // RT-06: mark the turn on the bidi TTS session so audio attribution and the cancel
        // barrier are turn-scoped. Non-bidi clients skip this entirely.
        var bidiTts = _tts as AIVTuber.Core.RealtimeTts.IBidiTtsController;
        if (bidiTts is not null && IsCurrent(envelope, ct))
            await bidiTts.BeginTurnAsync(ct).ConfigureAwait(false);
        try
        {
            void FirstPcmRead()
            {
                // Motion follows voice, not network: the staged intent fires with the first
                // real audio and is cancelled with the same avatar generation as the voice.
                // Exactly-once via MotionFlushed (this callback runs per audio start).
                if (IsCurrent(envelope, ct))
                    lock (turn)
                    {
                        if (turn.StagedMotion is { } intent && !turn.MotionFlushed)
                        {
                            turn.MotionFlushed = true;
                            _motion?.Submit(avatarGeneration, intent);
                        }
                    }
            }
            var playbackTask = !IsCurrent(envelope, ct) ? Task.CompletedTask
                : _playWithStart is not null ? _playWithStart(TtsChunks(ct), ct, FirstPcmRead)
                : _playChunksAsync(TtsChunks(ct), ct);
            await Task.WhenAll(playbackTask, producerTask).ConfigureAwait(false);
            await AwaitCommandsAsync(envelope.Generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            pipelineEx = ex;
            ReportCurrentError(envelope.Generation, $"[LLM/TTS] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            bidiTts?.EndTurn();
            _coordinator.SetHold(false);
            if (turn.Segments.Any(s => s.State == ReplySegmentState.Generated) &&
                turn.Segments.Any(s => s.State == ReplySegmentState.Played))
                AIVTuber.Core.Diagnostics.DebugLog.Write("[v2] 后续段落未播放即被中断（interrupted），不写入已说历史");
            if (ttsStarted || ct.IsCancellationRequested || pipelineEx is not null)
            {
                _motion?.Cancel(avatarGeneration);
                _motion?.OnRms(0);
            }
            if (ttsStarted && IsCurrent(envelope, ct, allowCancellation: true))
                OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }

        if (turn.ProtocolError is { } violation)
        {
            if (IsCurrent(envelope, ct, allowCancellation: true))
                ReportCurrentError(envelope.Generation, $"[LLM] 回复协议v2已终止（fail closed）：{violation}");
            return;
        }
        if (turn.Interrupted || pipelineEx is not null || !IsCurrent(envelope, ct) ||
            (canCommit is not null && !canCommit())) return;
        if (turn.Decision == ReplyDecisionMode.Pass)
            CommitClassifiedReply(context, new ClassifiedReply(ReplyKind.Pass, "", "", []), "");
        else if (turn.Decision == ReplyDecisionMode.Thought)
            CommitClassifiedReply(context, new ClassifiedReply(ReplyKind.InnerThought, "", turn.Thought, []), "");
    }

    private void FlushTurnControls(RequestContext context, ReplyTurnV2 turn)
    {
        foreach (var tag in turn.DrainControls())
        {
            var body = tag.Trim('[', ']');
            var colon = body.IndexOf(':');
            if (colon <= 0 || colon >= body.Length - 1) continue;
            var kind = body[..colon];
            var value = body[(colon + 1)..].Trim();
            if (kind.Equals("action", StringComparison.OrdinalIgnoreCase)) ApplyAction(context, value);
            else if (kind.Equals("pose", StringComparison.OrdinalIgnoreCase)) OnPoseDetected?.Invoke(this, value);
        }
    }

    private async Task RunCorticoAsync(List<Message> history, string userInput,
        InputEnvelope envelope, CancellationToken ct, Func<bool>? canCommit)
    {
        var performance = Cortico!;
        var context = new RequestContext(envelope.Generation, ct);
        var oldContext = _eventContext.Value;
        var committed = false;
        var started = false;
        _coordinator.SetHold(true);
        _eventContext.Value = context;
        _deferLlmEvents = true;
        ClearDeferredControls();
        try
        {
            var raw = new StringBuilder();
            await foreach (var token in _llm.StreamAsync(history, userInput, ct))
            {
                if (!IsCurrent(envelope, ct)) return;
                raw.Append(token);
            }
            if (!IsCurrent(envelope, ct) || canCommit?.Invoke() == false) return;
            var script = raw.ToString();
            var reply = ReplyClassifier.Classify(script);
            if (reply.Kind != ReplyKind.Speak)
            {
                CommitClassifiedReply(context, reply, script);
                return;
            }
            var spoken = await performance.PrepareAsync(script, ct).ConfigureAwait(false);
            if (!LlmClient.IsSpeakableText(spoken)) return;
            reply = reply with { Spoken = spoken, StagedControls = [] };
            if (!IsCurrent(envelope, ct) || canCommit?.Invoke() == false) return;
            await performance.PerformAsync(script, () =>
            {
                if (!IsCurrent(envelope, ct) || canCommit?.Invoke() == false) return false;
                if (!committed)
                {
                    committed = true;
                    // Upstream owns all VTS controls. Publish only clean speech to history/OBS.
                    OnReplyCommitted?.Invoke(this, reply);
                    PublishSpokenSentence(context, spoken);
                    OnFirstSentenceToTts?.Invoke(this, EventArgs.Empty);
                }
                return true;
            }, () =>
            {
                if (started || !IsCurrent(envelope, ct)) return;
                started = true;
                OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _eventContext.Value = oldContext;
            _deferLlmEvents = false;
            ClearDeferredControls();
            _coordinator.SetHold(false);
            if (started && IsCurrent(envelope, ct, allowCancellation: true))
                OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CommitClassifiedReply(RequestContext context, ClassifiedReply classified, string raw)
    {
        OnReplyCommitted?.Invoke(this, classified);
        switch (classified.Kind)
        {
            case ReplyKind.Speak:
                FlushDeferredControls(context);
                ApplyStagedControls(context, classified.StagedControls);
                PublishSpokenSentence(context, classified.Spoken);
                return;
            case ReplyKind.InnerThought:
                ClearDeferredControls();
                AIVTuber.Core.Diagnostics.DebugLog.Write($"[心里话] （{classified.Thought}）");
                return;
            case ReplyKind.Pass:
                ClearDeferredControls();
                AIVTuber.Core.Diagnostics.DebugLog.Write("[PASS] 本轮不接话");
                return;
            default:
                ClearDeferredControls();
                var preview = raw.Trim();
                if (preview.Length > 80) preview = preview[..80] + "…";
                ReportCurrentError(context.Generation,
                    $"[LLM] 回复不符合协议，已丢弃: {preview}");
                return;
        }
    }
    private string? PeekPendingEmotion(ClassifiedReply? reply)
    {
        // Synthesis needs the emotion before public commit. Reading the staged value
        // must not fire the pixel avatar, hotkeys or subtitles while TTS is pending.
        var tag = reply?.StagedControls.LastOrDefault(t => t.StartsWith("[emotion:", StringComparison.OrdinalIgnoreCase));
        if (tag is not null) return tag[9..^1].Trim();
        lock (_deferredEmotions) return _deferredEmotions.LastOrDefault() ?? _currentEmotion;
    }

    private string? ResolveTtsEmotion(string? emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion)) return null;
        // Map LLM words (incl. Chinese) through avatar EmotionMap → English state for Fish/MiniMax.
        if (_ttsEmotionMap.TryGetValue(emotion, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped;
        return emotion;
    }

    internal static string AnnotateWithUserEmotion(string text, string? emotion) => emotion switch
    {
        null or "neutral" => text,
        "happy" => $"[用户当前情绪：愉快] {text}",
        "sad" => $"[用户当前情绪：悲伤] {text}",
        "angry" => $"[用户当前情绪：愤怒] {text}",
        "fearful" => $"[用户当前情绪：恐惧] {text}",
        "disgusted" => $"[用户当前情绪：厌恶] {text}",
        "surprised" => $"[用户当前情绪：惊讶] {text}",
        _ => $"[用户当前情绪：{emotion}] {text}",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_llm is IAvatarReplySource avatarSource && _avatarPlanHandler is not null)
            avatarSource.OnAvatarPlanReady -= _avatarPlanHandler;
        _llm.OnSentenceReady -= _sentenceReadyHandler;
        _llm.OnEmotionDetected -= _emotionDetectedHandler;
        _llm.OnActionDetected -= _actionDetectedHandler;
        _llm.OnPoseDetected -= _poseDetectedHandler;
        if (_rmsUpdatedHandler is not null)
            _player.RmsUpdated -= _rmsUpdatedHandler;
        if (_playbackFinishedHandler is not null)
            _player.PlaybackFinished -= _playbackFinishedHandler;

        Interrupt();
        _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Task.WhenAll(_commandTasks.Values).GetAwaiter().GetResult();
        _commandGate.Dispose();
    }
}
