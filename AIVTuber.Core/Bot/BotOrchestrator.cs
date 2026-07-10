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

    private CancellationTokenSource? _currentCts;
    private bool _isProcessing;
    private readonly object _lock = new();
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
    {
        _asr = asr;
        _llm = llm;
        _tts = tts;
        _player = player;
        _ttsConfig = ttsConfig;
        _vts = vts;
        _vtsConfig = vtsConfig ?? new VtsConfig();

        // Forward LLM events
        _llm.OnSentenceReady += (_, s) => OnSentenceReady?.Invoke(this, s);
        _llm.OnEmotionDetected += (_, emotion) =>
        {
            _currentEmotion = emotion;
            OnEmotionDetected?.Invoke(this, emotion);
            _ = TriggerMappedHotkeyAsync(_vtsConfig.EmotionMap, emotion, "emotion");
        };
        _llm.OnActionDetected += (_, action) =>
        {
            OnActionDetected?.Invoke(this, action);
            _ = TriggerMappedHotkeyAsync(_vtsConfig.ActionMap, action, "action");
        };

        // Wire up RMS -> VTS lip-sync
        if (_vts is not null)
        {
            _player.RmsUpdated += (_, rms) => HandleRmsAsync(rms);
            _player.PlaybackFinished += (_, _) => TryCloseMouthAsync();
        }
    }

    private async Task TriggerMappedHotkeyAsync(
        IReadOnlyDictionary<string, string> map, string name, string kind)
    {
        if (_vts is null) return;
        if (!TryGetHotkeyId(map, name, out var hotkeyId)) return;
        try { await _vts.TriggerHotkeyAsync(hotkeyId); }
        catch (Exception ex)
        {
            var message = $"[VTS] {kind} hotkey error: {ex.Message}";
            Console.Error.WriteLine(message);
            OnError?.Invoke(this, message);
        }
    }

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
    public async Task ProcessSpeechAsync(SpeechSegment speech, List<Message> history, string micTemplate)
    {
        Interrupt();
        lock (_lock) { _isProcessing = true; }
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        bool pipelineStarted = false;
        try
        {
            var result = await _asr.RecognizeAsync(speech.AudioData, ct);
            if (string.IsNullOrWhiteSpace(result.Text) || ct.IsCancellationRequested) return;
            OnUserTranscript?.Invoke(this, result.Text);
            if (result.Emotion is not null)
                OnUserEmotionDetected?.Invoke(this, result.Emotion);
            pipelineStarted = true;
            var annotated = AnnotateWithUserEmotion(result.Text, result.Emotion);
            await RunStreamingPipelineAsync(history, micTemplate.Replace("{text}", annotated), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var msg = $"[ASR/Pipeline] {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(msg);
            OnError?.Invoke(this, msg);
        }
        finally
        {
            lock (_lock) { _isProcessing = false; }
            // If pipeline never started (ASR error / empty transcript), still leave Thinking state.
            if (!pipelineStarted) OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Process loopback (PC audio) speech. Lower priority than microphone:
    /// skipped if the bot is already processing, and mic speech will cancel it via Interrupt().
    /// Injects "对面说：xxx" context into the LLM without a full interrupt.
    /// </summary>
    public async Task ProcessLoopbackSpeechAsync(SpeechSegment speech, List<Message> history, string loopbackTemplate)
    {
        // Skip if mic or a previous loopback response is already running
        lock (_lock)
        {
            if (_isProcessing) return;
            _isProcessing = true;
        }
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        bool pipelineStarted = false;
        try
        {
            var result = await _asr.RecognizeAsync(speech.AudioData, ct);
            if (string.IsNullOrWhiteSpace(result.Text) || ct.IsCancellationRequested) return;
            OnLoopbackTranscript?.Invoke(this, result.Text);
            pipelineStarted = true;
            await RunStreamingPipelineAsync(history, loopbackTemplate.Replace("{text}", result.Text), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var msg = $"[Loopback/ASR] {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(msg);
            OnError?.Invoke(this, msg);
        }
        finally
        {
            lock (_lock) { _isProcessing = false; }
            if (!pipelineStarted) OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Process text directly (e.g., from danmaku). Interrupts ongoing processing.</summary>
    public async Task ProcessTextAsync(string text, List<Message> history)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Interrupt();
        lock (_lock) { _isProcessing = true; }
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        try { await RunStreamingPipelineAsync(history, text, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var msg = $"[Pipeline] {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(msg);
            OnError?.Invoke(this, msg);
        }
        finally { lock (_lock) { _isProcessing = false; } }
    }

    /// <summary>Interrupt any ongoing processing and stop playback immediately.</summary>
    public void Interrupt()
    {
        _currentEmotion = null; // reset per-turn emotion
        var cts = _currentCts;
        _currentCts = null;
        cts?.Cancel();
        _player.Stop();
        if (_vts is not null) _ = _vts.CloseMouthAsync();
        lock (_lock) { _isProcessing = false; }
    }

    public bool IsProcessing { get { lock (_lock) { return _isProcessing; } } }

    private async Task RunStreamingPipelineAsync(List<Message> history, string userInput, CancellationToken ct)
    {
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[LLM输入] {userInput}");
        var sentenceChannel = Channel.CreateBounded<string>(3);

        var producerTask = Task.Run(async () =>
        {
            var rawAll = new StringBuilder();
            int sentencesEmitted = 0;
            try
            {
                var buffer = new StringBuilder();
                await foreach (var token in _llm.StreamAsync(history, userInput, ct))
                {
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
                            await sentenceChannel.Writer.WriteAsync(trimmed, ct);
                        }
                        buffer.Clear();
                        buffer.Append(remainder);
                    }
                }
                var remaining = LlmClient.StripActionText(LlmClient.StripControlTags(LlmClient.StripPartialTags(buffer.ToString()))).Trim();
                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    sentencesEmitted++;
                    await sentenceChannel.Writer.WriteAsync(remaining, ct);
                }
                if (sentencesEmitted == 0 && !string.IsNullOrWhiteSpace(rawAll.ToString()))
                {
                    var preview = rawAll.ToString().Trim();
                    if (preview.Length > 80) preview = preview[..80] + "…";
                    OnError?.Invoke(this, $"[LLM] 本轮回复被完全过滤掉了（可能整段都是动作/情绪标记），原始内容: {preview}");
                }
            }
            finally { sentenceChannel.Writer.Complete(); }
        }, ct);

        // Stream TTS chunks from all sentences into one continuous IAsyncEnumerable.
        // WaveOut is created once in PlayChunksAsync — no re-init between sentences.
        bool ttsStarted = false;
        async IAsyncEnumerable<byte[]> TtsChunks([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken streamCt = default)
        {
            await foreach (var sentence in sentenceChannel.Reader.ReadAllAsync(streamCt))
            {
                if (string.IsNullOrWhiteSpace(sentence)) continue;
                if (!ttsStarted)
                {
                    ttsStarted = true;
                    OnFirstSentenceToTts?.Invoke(this, EventArgs.Empty);
                    OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
                }
                await foreach (var chunk in _tts.StreamAsync(sentence, _ttsConfig.VoiceId, _currentEmotion, streamCt))
                    yield return chunk;
            }
        }

        Exception? pipelineEx = null;
        try
        {
            await _player.PlayChunksAsync(TtsChunks(ct), ct);
            await producerTask;
        }
        catch (OperationCanceledException) { /* normal interrupt */ }
        catch (Exception ex)
        {
            pipelineEx = ex;
            var msg = $"[LLM/TTS] {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(msg);
            OnError?.Invoke(this, msg);
        }
        finally
        {
            OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }

        if (pipelineEx is not null) throw pipelineEx;
    }
    private static string AnnotateWithUserEmotion(string text, string? emotion) => emotion switch
    {
        null or "neutral" => text,
        "happy"     => $"[用户当前情绪：愉快] {text}",
        "sad"       => $"[用户当前情绪：悲伤] {text}",
        "angry"     => $"[用户当前情绪：愤怒] {text}",
        "fearful"   => $"[用户当前情绪：恐惧] {text}",
        "disgusted" => $"[用户当前情绪：厌恶] {text}",
        "surprised" => $"[用户当前情绪：惊讶] {text}",
        _           => $"[用户当前情绪：{emotion}] {text}",
    };

    public void Dispose()
    {
        Interrupt();
        _currentCts?.Dispose();
    }
}
