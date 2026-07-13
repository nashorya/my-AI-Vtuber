using System.Collections.Concurrent;
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
    private readonly TtsConfig _ttsConfig;
    private readonly VtsClient? _vts;
    private readonly VtsConfig _vtsConfig;
    private readonly EventHandler<string> _sentenceReadyHandler;
    private readonly EventHandler<string> _emotionDetectedHandler;
    private readonly EventHandler<string> _actionDetectedHandler;
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

    public event EventHandler? OnAiStartSpeaking;
    public event EventHandler? OnAiStopSpeaking;
    public event EventHandler? OnFirstSentenceToTts;
    public event EventHandler<string>? OnEmotionDetected;
    public event EventHandler<string>? OnActionDetected;
    public event EventHandler<string>? OnSentenceReady;
    public event EventHandler<string>? OnUserTranscript;
    /// <summary>Fired when Qwen-ASR returns a non-neutral emotion for the user's speech.</summary>
    public event EventHandler<string>? OnUserEmotionDetected;
    /// <summary>Fired with the transcribed text from loopback (PC) audio.</summary>
    public event EventHandler<string>? OnLoopbackTranscript;
    public event EventHandler<string>? OnError;

    public BotOrchestrator(
        IAsrClient asr, ILlmClient llm, ITtsClient tts,
        AudioPlayer player, TtsConfig ttsConfig,
        VtsClient? vts = null, VtsConfig? vtsConfig = null)
        : this(asr, llm, tts, player, ttsConfig, vts, vtsConfig,
            player.PlayChunksAsync, player.Stop,
            vts is null ? null : vts.TriggerHotkeyAsync)
    {
    }

    internal BotOrchestrator(
        IAsrClient asr, ILlmClient llm, ITtsClient tts,
        AudioPlayer player, TtsConfig ttsConfig,
        VtsClient? vts, VtsConfig? vtsConfig,
        Func<IAsyncEnumerable<byte[]>, CancellationToken, Task> playChunksAsync,
        Action stopPlayback,
        Func<string, CancellationToken, Task>? triggerHotkeyAsync)
    {
        _asr = asr;
        _llm = llm;
        _tts = tts;
        _player = player;
        _ttsConfig = ttsConfig;
        _vts = vts;
        _vtsConfig = vtsConfig ?? new VtsConfig();
        _coordinator = new RequestCoordinator();
        _playChunksAsync = playChunksAsync;
        _stopPlayback = stopPlayback;
        _triggerHotkeyAsync = triggerHotkeyAsync;

        // Keep publisher subscriptions as named delegates so Dispose can detach them
        // symmetrically. The LLM and AudioPlayer may outlive this orchestrator during rewire.
        _sentenceReadyHandler = (_, sentence) =>
        {
            var context = CurrentEventContext();
            if (context is null) return;
            OnSentenceReady?.Invoke(this, sentence);
            if (_assistantOutputCommand is not null)
                QueueCommand(context, "[OBS] assistant subtitle",
                    ct => _assistantOutputCommand(sentence, ct));
        };
        _emotionDetectedHandler = (_, emotion) =>
        {
            var context = CurrentEventContext();
            if (context is null) return;
            _currentEmotion = emotion;
            OnEmotionDetected?.Invoke(this, emotion);
            QueueMappedHotkey(context, _vtsConfig.EmotionMap, emotion, "emotion");
        };
        _actionDetectedHandler = (_, action) =>
        {
            var context = CurrentEventContext();
            if (context is null) return;
            OnActionDetected?.Invoke(this, action);
            QueueMappedHotkey(context, _vtsConfig.ActionMap, action, "action");
        };
        _llm.OnSentenceReady += _sentenceReadyHandler;
        _llm.OnEmotionDetected += _emotionDetectedHandler;
        _llm.OnActionDetected += _actionDetectedHandler;

        // Wire up RMS -> VTS lip-sync
        if (_vts is not null)
        {
            _rmsUpdatedHandler = (_, rms) =>
            {
                if (!_disposed) HandleRmsAsync(rms);
            };
            _playbackFinishedHandler = (_, _) =>
            {
                if (!_disposed) TryCloseMouthAsync();
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

    private void QueueMappedHotkey(
        RequestContext context,
        IReadOnlyDictionary<string, string> map,
        string name,
        string kind)
    {
        if (_triggerHotkeyAsync is null) return;
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

    private bool _rmsErrorLogged;

    private async void HandleRmsAsync(float rms)
    {
        if (_vts is null) return;
        try
        {
            await _vts.SetMouthAsync(rms);
            _rmsErrorLogged = false;
        }
        catch (Exception ex)
        {
            // Log only the first failure per outage to avoid spamming the ~30ms RMS loop.
            if (!_rmsErrorLogged)
            {
                _rmsErrorLogged = true;
                var msg = $"[VTS] 口型注入失败: {ex.Message}";
                Console.Error.WriteLine(msg);
                OnError?.Invoke(this, msg);
            }
        }
    }

    private async void TryCloseMouthAsync()
    {
        if (_vts is null) return;
        try { await _vts.CloseMouthAsync(); }
        catch { /* ignore */ }
    }

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

    /// <summary>Process text directly (e.g., from danmaku). Interrupts ongoing processing.</summary>
    public Task ProcessTextAsync(string text, List<Message> history)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        return _coordinator.EnqueueAsync(InputSource.Danmaku, async (envelope, ct) =>
        {
            _currentEmotion = null;
            try { await RunStreamingPipelineAsync(history, text, envelope, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportCurrentError(envelope.Generation, $"[Pipeline] {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>Interrupt any ongoing processing and stop playback immediately.</summary>
    public void Interrupt()
    {
        _coordinator.CancelCurrentAsync().GetAwaiter().GetResult();
        _currentEmotion = null;
        _stopPlayback();
        if (_vts is not null)
        {
            try { _vts.CloseMouthAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                // Stop/disposal must remain safe when VTS is already disconnected.
                var message = $"[VTS] 关闭口型失败: {ex.Message}";
                AIVTuber.Core.Diagnostics.DebugLog.Write(message);
                if (!_disposed) OnError?.Invoke(this, message);
            }
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

    private async Task RunStreamingPipelineAsync(
        List<Message> history,
        string userInput,
        InputEnvelope envelope,
        CancellationToken ct)
    {
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[LLM输入] {userInput}");
        var sentenceChannel = Channel.CreateBounded<string>(3);
        var context = new RequestContext(envelope.Generation, ct);

        var producerTask = Task.Run(async () =>
        {
            var rawAll = new StringBuilder();
            int sentencesEmitted = 0;
            var previousContext = _eventContext.Value;
            _eventContext.Value = context;
            try
            {
                var buffer = new StringBuilder();
                await foreach (var token in _llm.StreamAsync(history, userInput, ct))
                {
                    if (!IsCurrent(envelope, ct)) break;
                    rawAll.Append(token);
                    buffer.Append(token);
                    var raw = buffer.ToString();
                    var cleaned = LlmClient.StripActionText(LlmClient.StripControlTags(raw));
                    if (cleaned.Length != raw.Length) { buffer.Clear(); buffer.Append(cleaned); }
                    if (LlmClient.ContainsSentenceBoundary(buffer.ToString(), out var sentence, out var remainder))
                    {
                        var trimmed = LlmClient.StripActionText(LlmClient.StripControlTags(LlmClient.StripPartialTags(sentence))).Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            sentencesEmitted++;
                            if (!IsCurrent(envelope, ct)) break;
                            await sentenceChannel.Writer.WriteAsync(trimmed, ct);
                        }
                        buffer.Clear();
                        buffer.Append(remainder);
                    }
                }
                var remaining = LlmClient.StripActionText(LlmClient.StripControlTags(LlmClient.StripPartialTags(buffer.ToString()))).Trim();
                if (IsCurrent(envelope, ct) && !string.IsNullOrWhiteSpace(remaining))
                {
                    sentencesEmitted++;
                    await sentenceChannel.Writer.WriteAsync(remaining, ct);
                }
                if (IsCurrent(envelope, ct) && sentencesEmitted == 0 && !string.IsNullOrWhiteSpace(rawAll.ToString()))
                {
                    var preview = rawAll.ToString().Trim();
                    if (preview.Length > 80) preview = preview[..80] + "…";
                    ReportCurrentError(envelope.Generation,
                        $"[LLM] 本轮回复被完全过滤掉了（可能整段都是动作/情绪标记），原始内容: {preview}");
                }
            }
            finally
            {
                _eventContext.Value = previousContext;
                sentenceChannel.Writer.TryComplete();
            }
        }, ct);

        // Stream TTS chunks from all sentences into one continuous IAsyncEnumerable.
        // WaveOut is created once in PlayChunksAsync — no re-init between sentences.
        bool ttsStarted = false;
        async IAsyncEnumerable<byte[]> TtsChunks([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken streamCt = default)
        {
            await foreach (var sentence in sentenceChannel.Reader.ReadAllAsync(streamCt))
            {
                if (!IsCurrent(envelope, streamCt)) yield break;
                if (string.IsNullOrWhiteSpace(sentence)) continue;
                if (!ttsStarted)
                {
                    if (!IsCurrent(envelope, streamCt)) yield break;
                    ttsStarted = true;
                    OnFirstSentenceToTts?.Invoke(this, EventArgs.Empty);
                    OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
                }
                await foreach (var chunk in _tts.StreamAsync(sentence, _ttsConfig.VoiceId, _currentEmotion, streamCt))
                {
                    if (!IsCurrent(envelope, streamCt)) yield break;
                    yield return chunk;
                }
            }
        }

        Exception? pipelineEx = null;
        try
        {
            var playbackTask = IsCurrent(envelope, ct)
                ? _playChunksAsync(TtsChunks(ct), ct)
                : Task.CompletedTask;
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
            if (IsCurrent(envelope, ct, allowCancellation: true))
                OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }

        if (pipelineEx is not null) throw pipelineEx;
    }
    private static string AnnotateWithUserEmotion(string text, string? emotion) => emotion switch
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

        _llm.OnSentenceReady -= _sentenceReadyHandler;
        _llm.OnEmotionDetected -= _emotionDetectedHandler;
        _llm.OnActionDetected -= _actionDetectedHandler;
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
