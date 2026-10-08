using System.Collections.Concurrent;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Vts;

namespace AIVTuber.Core.Bot;

/// <summary>
/// Coordinates one reply at a time: LLM (protocol v2) → approved segments → a pacer → TTS →
/// AudioPlayer. With Cortico present, Cortico decides when each piece starts and follows the
/// app's playback on the rig; otherwise each piece plays as soon as its audio arrives.
/// </summary>
public sealed class BotOrchestrator : IDisposable
{
    private readonly IAsrClient _asr;
    private readonly ILlmClient _llm;
    private readonly ITtsClient _tts;
    private readonly AudioPlayer _player;
    private IAvatarMotionSink? _motion;
    private Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> _play;
    private static long _avatarSequence;
    private long _activeAvatarGeneration;

    public void ConfigureContinuousControl(IAvatarMotionSink motion,
        Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task>? play = null)
    {
        _motion = motion;
        if (play is not null) _play = play;
    }

    private readonly TtsConfig _ttsConfig;
    private readonly VtsClient? _vts;
    private readonly VtsConfig _vtsConfig;
    private readonly IReadOnlyDictionary<string, string> _ttsEmotionMap;
    private readonly EventHandler<float>? _rmsUpdatedHandler;
    private readonly EventHandler? _playbackFinishedHandler;

    private readonly RequestCoordinator _coordinator;
    private readonly ConcurrentDictionary<long, Task> _commandTasks = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Action _stopPlayback;
    private readonly Func<string, CancellationToken, Task>? _triggerHotkeyAsync;
    private Func<string, CancellationToken, Task>? _assistantOutputCommand;
    private long _nextCommandId;
    private volatile bool _disposed;

    public event EventHandler? OnAiStartSpeaking;
    public event EventHandler? OnAiStopSpeaking;
    public event EventHandler? OnFirstSentenceToTts;
    public event EventHandler<string>? OnEmotionDetected;
    public event EventHandler<string>? OnActionDetected;
    public event EventHandler<string>? OnPoseDetected;
    public event EventHandler<string>? OnSentenceReady;
    internal event EventHandler<ClassifiedReply>? OnReplyCommitted;
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
        Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> play,
        Action stopPlayback,
        Func<string, CancellationToken, Task>? triggerHotkeyAsync,
        IReadOnlyDictionary<string, string>? ttsEmotionMap = null)
    {
        if (llm is not IReplyProtocolStream) throw new InvalidOperationException("LLM 客户端必须支持回复协议 v2");
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
        _play = play;
        _stopPlayback = stopPlayback;
        _triggerHotkeyAsync = triggerHotkeyAsync;

        // Keep publisher subscriptions as named delegates so Dispose can detach them
        // symmetrically. The AudioPlayer may outlive this orchestrator during rewire.
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

    internal void ConfigureOutputCommands(Func<string, CancellationToken, Task>? assistantOutputCommand) =>
        _assistantOutputCommand = assistantOutputCommand;

    public void SetHold(bool hold) => _coordinator.SetHold(hold);

    public Task<AsrResult> TranscribeAsync(byte[] pcm16k, CancellationToken cancellationToken = default) =>
        _asr.RecognizeAsync(pcm16k, cancellationToken);

    public Task<AsrResult> TranscribeStreamAsync(
        IAsyncEnumerable<byte[]> audioStream, CancellationToken cancellationToken = default) =>
        CollectStreamedAsync(audioStream, cancellationToken);

    /// <summary>
    /// Drains a streaming ASR result and returns the final transcript + emotion.
    /// DashScope streaming yields each finalized sentence separately; we concatenate them.
    /// Emotion comes from the last non-null result (Qwen-ASR fills it).
    /// </summary>
    private async Task<AsrResult> CollectStreamedAsync(
        IAsyncEnumerable<byte[]> audioStream, CancellationToken ct)
    {
        var transcript = new System.Text.StringBuilder();
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

    private void PublishSpokenSentence(RequestContext context, string sentence)
    {
        OnSentenceReady?.Invoke(this, sentence);
        if (_assistantOutputCommand is not null)
            QueueCommand(context, "[OBS] assistant subtitle",
                ct => _assistantOutputCommand(sentence, ct));
    }

    private void ApplyEmotion(RequestContext context, string emotion)
    {
        OnEmotionDetected?.Invoke(this, emotion);
        QueueMappedHotkey(context, _vtsConfig.EmotionMap, emotion, "emotion");
    }

    private void ApplyAction(RequestContext context, string action)
    {
        OnActionDetected?.Invoke(this, action);
        QueueMappedHotkey(context, _vtsConfig.ActionMap, action, "action");
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

    /// <summary>Process text directly (a talk turn, danmaku, a dual-silence turn).</summary>
    /// <param name="wakeProbe">Text used for wake matching; defaults to <paramref name="text"/>.</param>
    /// <param name="bypassWake">When true, the model decides PASS / thought / speak.</param>
    public Task ProcessTextAsync(string text, List<Message> history, string? wakeProbe = null,
        bool bypassWake = false, Func<bool>? canCommit = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        return _coordinator.EnqueueAsync(InputSource.Danmaku, async (envelope, ct) =>
        {
            var pipelineStarted = false;
            try
            {
                if (!bypassWake && !AllowSpeak(wakeProbe ?? text)) return;
                pipelineStarted = true;
                await RunReplyAsync(history, text, envelope, ct, canCommit).ConfigureAwait(false);
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
    public void Interrupt() => BeginInterrupt().GetAwaiter().GetResult();

    /// <summary>Silences local output now and returns the wait for the in-flight request to
    /// unwind. Playback never waits for a provider to acknowledge cancellation (AUTH-04).</summary>
    public Task BeginInterrupt()
    {
        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelRequested);
        _motion?.Cancel(Interlocked.Read(ref _activeAvatarGeneration));
        _motion?.OnRms(0);
        _coordinator.SetHold(false);
        var unwind = _coordinator.CancelCurrent();
        _stopPlayback();
        // The app player is already silent; stop the rig's performance too (fenced to earlier turns).
        var performanceStop = StopPerformance();
        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.PlaybackStopped);
        return CompleteInterruptAsync(unwind, performanceStop);
    }

    private Task StopPerformance()
    {
        var performance = Cortico;
        if (performance is null) return Task.CompletedTask;
        try
        {
            return performance.InterruptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3))
                .ContinueWith(t =>
                {
                    if (t.Exception is { } ex)
                        AIVTuber.Core.Diagnostics.DebugLog.Write($"[Cortico] 停止演出失败: {ex.GetBaseException().Message}");
                }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[Cortico] 停止演出失败: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    /// <summary>The part of an interrupt that may wait on providers: the in-flight request
    /// unwinding and, for bidirectional TTS, the vendor cancel barrier (RT-06) — its
    /// task_cancel acknowledgement or an epoch rebuild when the ack is lost — before a next
    /// turn's text can be submitted. Local output is already silent when this starts.</summary>
    private async Task CompleteInterruptAsync(Task unwind, Task performanceStop)
    {
        await unwind.ConfigureAwait(false);
        await performanceStop.ConfigureAwait(false);
        if (_tts is AIVTuber.Core.RealtimeTts.IBidiTtsController bidi)
        {
            var outcome = await bidi.CancelPendingAsync().ConfigureAwait(false);
            if (outcome == AIVTuber.Core.RealtimeTts.TtsCancelOutcome.ServerConfirmed)
                Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelAcked);
            else if (outcome == AIVTuber.Core.RealtimeTts.TtsCancelOutcome.EpochRebuilt)
                Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelEpochRebuild);
        }
        else
        {
            // Legacy/streaming paths have no vendor ack to observe.
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.CancelAcked);
        }
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

    /// <summary>Per-reply state. Every request owns its own instance.</summary>
    private sealed class ReplyTurn
    {
        public ReplyDecisionMode Decision;
        public string Thought = "";
        public string? ProtocolError;
        public bool Interrupted;
        public bool PlaybackStarted;
        public bool SpokeAny;
        public AvatarIntent? StagedMotion;
        public bool MotionFlushed;
        private readonly List<string> _emotions = [];
        private readonly List<string> _controls = [];
        private int _applied;
        private readonly Queue<(int Length, string Written)> _written = new();
        private string _unspokenWritten = "";
        private int _committed;

        public void NoteEmotion(string emotion) { lock (_emotions) _emotions.Add(emotion); }
        public void NoteControl(string tag) { lock (_controls) _controls.Add(tag); }
        public string? LatestEmotion() { lock (_emotions) return _emotions.LastOrDefault(); }
        public IReadOnlyList<string> DrainUnappliedEmotions()
        {
            lock (_emotions) { var copy = _emotions.Skip(_applied).ToArray(); _applied = _emotions.Count; return copy; }
        }
        public IReadOnlyList<string> DrainControls()
        {
            lock (_controls) { var copy = _controls.ToArray(); _controls.Clear(); return copy; }
        }

        /// <summary>Remembers a segment as the model wrote it. A segment with nothing to say
        /// (tags only) rides with the next spoken one, as the pacers handle it.</summary>
        public void NoteWritten(string clean, string written)
        {
            lock (_written)
            {
                if (!LlmClient.IsSpeakableText(clean)) { _unspokenWritten += written; return; }
                _written.Enqueue((Letters(clean), _unspokenWritten + written.Trim()));
                _unspokenWritten = "";
            }
        }

        /// <summary>The written form for a committed piece: the whole segment with its first piece,
        /// empty for later pieces of the same segment (Cortico may split a segment, never merge two).</summary>
        public string? TakeWritten(string committed)
        {
            lock (_written)
            {
                if (!_written.TryPeek(out var head)) return null;
                var first = _committed == 0;
                _committed += Letters(committed);
                if (_committed >= head.Length) { _written.Dequeue(); _committed = 0; }
                return first ? head.Written : "";
            }
        }

        private static int Letters(string text) => text.Count(c => !char.IsWhiteSpace(c));
    }

    /// <summary>
    /// The one reply pipeline (protocol v2). Approved segments go to a pacer: Cortico decides when
    /// each piece starts and the app plays it, or plain pacing plays as soon as audio arrives.
    /// Segments are released while the model is still writing; a segment is committed (captions,
    /// history) only when its audio is handed to the player, so an interrupted reply records only
    /// what was actually heard. PASS and thoughts are never spoken.
    /// </summary>
    private async Task RunReplyAsync(List<Message> history, string userInput, InputEnvelope envelope,
        CancellationToken ct, Func<bool>? canCommit)
    {
        var protocol = (IReplyProtocolStream)_llm;
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[LLM输入] {userInput}");
        _coordinator.SetHold(true);
        var context = new RequestContext(envelope.Generation, ct);
        var avatarGeneration = Interlocked.Increment(ref _avatarSequence);
        var previousAvatar = Interlocked.Exchange(ref _activeAvatarGeneration, avatarGeneration);
        _motion?.Cancel(previousAvatar);
        _motion?.BeginTurn(avatarGeneration);
        using var cancelAvatar = ct.Register(() => _motion?.Cancel(avatarGeneration));
        var turn = new ReplyTurn();
        // The prompt follows the configured layer: with Cortico configured the model writes Cortico
        // script even when the sidecar is down, so segments are always read as script; a voice-only
        // turn then speaks their clean text.
        var scriptMarkup = Cortico is not null;
        var cortico = Cortico is { IsAlive: true } live ? live : null;
        if (Cortico is not null && cortico is null)
            ReportCurrentError(envelope.Generation, "[Cortico] 皮套异常，本轮仅语音：皮套进程无响应");

        var ports = new Pacing.SpeechTurnPorts(
            Synthesize: (text, emotion, token) => _tts.StreamAsync(text, _ttsConfig.VoiceId, ResolveTtsEmotion(emotion), token),
            Play: _play,
            CanSpeak: () => IsCurrent(envelope, ct) && canCommit?.Invoke() != false,
            Commit: text => CommitSpokenSegment(context, turn, text),
            OnFirstPcm: () => FlushStagedMotion(envelope, ct, turn, avatarGeneration),
            Warn: message => ReportCurrentError(envelope.Generation, message),
            SampleRate: _player.SampleRate);

        var bidiTts = _tts as AIVTuber.Core.RealtimeTts.IBidiTtsController;
        if (bidiTts is not null && IsCurrent(envelope, ct))
            await bidiTts.BeginTurnAsync(ct).ConfigureAwait(false);
        Exception? pipelineEx = null;
        Pacing.ISpeechPacer pacer = cortico is not null
            ? new Pacing.CorticoPacer(cortico, ports, ct)
            : new Pacing.ImmediatePacer(ports, ct);
        try
        {
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmRequest);
            await foreach (var ev in protocol.StreamEventsAsync(history, userInput, ct).ConfigureAwait(false))
            {
                if (!IsCurrent(envelope, ct)) { turn.Interrupted = true; break; }
                if (!await HandleReplyEventAsync(ev, turn, pacer, scriptMarkup, cortico is not null, ports.CanSpeak, userInput, avatarGeneration, ct).ConfigureAwait(false))
                    break;
            }
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmDone);
            // Fail closed on protocol errors: segments already released still finish, nothing more starts.
            await pacer.CompleteAsync(ct).ConfigureAwait(false);
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
            await pacer.DisposeAsync().ConfigureAwait(false);
            bidiTts?.EndTurn();
            _coordinator.SetHold(false);
            if (turn.PlaybackStarted || ct.IsCancellationRequested || pipelineEx is not null)
            {
                _motion?.Cancel(avatarGeneration);
                _motion?.OnRms(0);
            }
            if (turn.PlaybackStarted && IsCurrent(envelope, ct, allowCancellation: true))
                OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }

        if (turn.ProtocolError is { } violation)
        {
            if (IsCurrent(envelope, ct, allowCancellation: true))
                ReportCurrentError(envelope.Generation, turn.SpokeAny
                    ? $"[LLM] 回复协议v2已终止（fail closed），已开始的部分照常说完：{violation}"
                    : $"[LLM] 回复协议v2已终止（fail closed）：{violation}");
            return;
        }
        if (turn.Interrupted || pipelineEx is not null || !IsCurrent(envelope, ct) || canCommit?.Invoke() == false) return;
        if (turn.Decision == ReplyDecisionMode.Pass)
        {
            OnReplyCommitted?.Invoke(this, new ClassifiedReply(ReplyKind.Pass, "", "", []));
            AIVTuber.Core.Diagnostics.DebugLog.Write("[PASS] 本轮不接话");
        }
        else if (turn.Decision == ReplyDecisionMode.Thought)
        {
            OnReplyCommitted?.Invoke(this, new ClassifiedReply(ReplyKind.InnerThought, "", turn.Thought, []));
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[心里话] （{turn.Thought}）");
        }
    }

    /// <summary>Returns false when the reply must stop releasing segments.</summary>
    private async Task<bool> HandleReplyEventAsync(ReplyStreamEvent ev, ReplyTurn turn, Pacing.ISpeechPacer pacer,
        bool cortico, bool corticoPacing, Func<bool> canSpeak, string userInput, long avatarGeneration, CancellationToken ct)
    {
        switch (ev.Kind)
        {
            case ReplyStreamEventKind.ProtocolError:
                turn.ProtocolError = ev.Error ?? "未知协议错误";
                return false;
            case ReplyStreamEventKind.Decision:
                turn.Decision = ev.Decision;
                turn.Thought = ev.Text;
                return true;
            case ReplyStreamEventKind.Control:
                if (cortico)
                {
                    // Cortico is the only rig writer; actions arrive as script markup instead.
                    AIVTuber.Core.Diagnostics.DebugLog.Write($"[Cortico] 忽略 v2 control 行（{ev.ControlKind}）：动作应写在台本标记里");
                    return true;
                }
                if (ev.ControlKind == "emotion")
                {
                    turn.NoteEmotion(ev.Text);
                    turn.NoteWritten("", $"[emotion:{ev.Text}]");
                }
                else if (ev.Motion is { } intent)
                {
                    bool started;
                    lock (turn) started = turn.PlaybackStarted;
                    if (started) _motion?.Submit(avatarGeneration, intent);
                    else turn.StagedMotion ??= intent;
                }
                return true;
            case ReplyStreamEventKind.Speech:
                string text;
                if (cortico)
                {
                    var segment = CorticoReplyAdapter.Sanitize(ev.Text);
                    if (segment is null or { Kind: CorticoReplyKind.Pass or CorticoReplyKind.Thought }) return true;
                    if (segment.Value.Kind == CorticoReplyKind.Invalid) { turn.ProtocolError = segment.Value.Text; return false; }
                    text = corticoPacing ? segment.Value.Text : CorticoScript.Clean(segment.Value.Text);
                    turn.NoteWritten(CorticoScript.Clean(segment.Value.Text), segment.Value.Text);
                }
                else
                {
                    var classified = ReplyClassifier.Classify(ev.Text);
                    if (classified.Kind == ReplyKind.Invalid)
                    {
                        turn.ProtocolError = "speech 段未通过内容隔离校验（括号/标记不完整）";
                        return false;
                    }
                    if (classified.Kind != ReplyKind.Speak) return true;
                    foreach (var tag in classified.StagedControls)
                    {
                        if (tag.StartsWith("[emotion:", StringComparison.OrdinalIgnoreCase)) turn.NoteEmotion(tag[9..^1].Trim());
                        else turn.NoteControl(tag);
                    }
                    text = classified.Spoken;
                    turn.NoteWritten(text, ev.Text);
                }
                // People resumed (or the turn ended) before this segment: nothing new may start.
                if (!canSpeak()) { turn.Interrupted = true; return false; }
                turn.SpokeAny = true;
                await pacer.SubmitAsync(new Pacing.SpeechItem(text, turn.LatestEmotion()), ct).ConfigureAwait(false);
                return true;
            case ReplyStreamEventKind.End:
                if (turn.Decision == ReplyDecisionMode.Speak && turn.StagedMotion is null &&
                    AvatarReplyProtocol.InferRequestedMotion(userInput) is { } inferred)
                    turn.StagedMotion = inferred;
                return true;
            default:
                return true;
        }
    }

    private void CommitSpokenSegment(RequestContext context, ReplyTurn turn, string text)
    {
        foreach (var emotion in turn.DrainUnappliedEmotions()) ApplyEmotion(context, emotion);
        bool first;
        lock (turn) { first = !turn.PlaybackStarted; turn.PlaybackStarted = true; }
        if (first)
        {
            FlushTurnControls(context, turn);
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmFirstSpeechSegment);
            OnFirstSentenceToTts?.Invoke(this, EventArgs.Empty);
            OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
        }
        OnReplyCommitted?.Invoke(this, new ClassifiedReply(ReplyKind.Speak, text, "", [], Written: turn.TakeWritten(text)));
        PublishSpokenSentence(context, text);
    }

    private void FlushStagedMotion(InputEnvelope envelope, CancellationToken ct, ReplyTurn turn, long avatarGeneration)
    {
        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.PlaybackFirst);
        if (!IsCurrent(envelope, ct)) return;
        lock (turn)
        {
            if (turn.StagedMotion is not { } intent || turn.MotionFlushed) return;
            turn.MotionFlushed = true;
            _motion?.Submit(avatarGeneration, intent);
        }
    }

    private void FlushTurnControls(RequestContext context, ReplyTurn turn)
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
