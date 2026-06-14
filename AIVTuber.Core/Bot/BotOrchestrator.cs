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

    public event EventHandler? OnAiStartSpeaking;
    public event EventHandler? OnAiStopSpeaking;
    public event EventHandler<string>? OnEmotionDetected;
    public event EventHandler<string>? OnSentenceReady;
    public event EventHandler<string>? OnUserTranscript;

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
            OnEmotionDetected?.Invoke(this, emotion);
            HandleEmotionAsync(emotion);
        };

        // Wire up RMS -> VTS lip-sync
        if (_vts is not null)
        {
            _player.RmsUpdated += (_, rms) => HandleRmsAsync(rms);
            _player.PlaybackFinished += (_, _) => TryCloseMouthAsync();
        }
    }

    private async void HandleEmotionAsync(string emotion)
    {
        if (_vts is null) return;
        if (!_vtsConfig.EmotionMap.TryGetValue(emotion, out var hotkeyId)) return;
        try { await _vts.TriggerHotkeyAsync(hotkeyId); }
        catch (Exception ex) { Console.Error.WriteLine($"[VTS] Emotion hotkey error: {ex.Message}"); }
    }

    private async void HandleRmsAsync(float rms)
    {
        if (_vts is null) return;
        try { await _vts.SetMouthAsync(rms); }
        catch { /* ignore VTS errors in RMS loop */ }
    }

    private async void TryCloseMouthAsync()
    {
        if (_vts is null) return;
        try { await _vts.CloseMouthAsync(); }
        catch { /* ignore */ }
    }

    /// <summary>Process a speech segment from VAD. Interrupts any ongoing processing.</summary>
    public async Task ProcessSpeechAsync(SpeechSegment speech, List<Message> history)
    {
        Interrupt();
        lock (_lock) { _isProcessing = true; }
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        try
        {
            var transcript = await _asr.RecognizeAsync(speech.AudioData, ct);
            OnUserTranscript?.Invoke(this, transcript);
            if (string.IsNullOrWhiteSpace(transcript) || ct.IsCancellationRequested) return;
            await RunStreamingPipelineAsync(history, transcript, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[Orchestrator] Error: {ex.Message}"); }
        finally { lock (_lock) { _isProcessing = false; } }
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
        catch (Exception ex) { Console.Error.WriteLine($"[Orchestrator] Error: {ex.Message}"); }
        finally { lock (_lock) { _isProcessing = false; } }
    }

    /// <summary>Interrupt any ongoing processing and stop playback immediately.</summary>
    public void Interrupt()
    {
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
        var sentenceChannel = Channel.CreateBounded<string>(3);

        var producerTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new StringBuilder();
                await foreach (var token in _llm.StreamAsync(history, userInput, ct))
                {
                    buffer.Append(token);
                    if (LlmClient.ContainsSentenceBoundary(buffer.ToString(), out var sentence, out var remainder))
                    {
                        var trimmed = sentence.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                            await sentenceChannel.Writer.WriteAsync(trimmed, ct);
                        buffer.Clear();
                        buffer.Append(remainder);
                    }
                }
                var remaining = buffer.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(remaining))
                    await sentenceChannel.Writer.WriteAsync(remaining, ct);
            }
            finally { sentenceChannel.Writer.Complete(); }
        }, ct);

        var consumerTask = Task.Run(async () =>
        {
            await foreach (var sentence in sentenceChannel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(sentence)) continue;
                try { await PlayTtsSentenceAsync(sentence, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.Error.WriteLine($"[Orchestrator] TTS error: {ex.Message}"); }
            }
        }, ct);

        await Task.WhenAll(producerTask, consumerTask);
        OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);

        if (producerTask.IsFaulted && producerTask.Exception is not null)
            throw producerTask.Exception.InnerException ?? producerTask.Exception;
        if (consumerTask.IsFaulted && consumerTask.Exception is not null)
            throw consumerTask.Exception.InnerException ?? consumerTask.Exception;
    }
    /// <summary>Collect TTS audio for a sentence and play it.</summary>
    private async Task PlayTtsSentenceAsync(string sentence, CancellationToken ct)
    {
        var chunks = new List<byte[]>();
        await foreach (var chunk in _tts.StreamAsync(sentence, _ttsConfig.VoiceId, ct))
        {
            chunks.Add(chunk);
            OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
        }
        if (chunks.Count == 0) return;
        var audioData = chunks.SelectMany(c => c).ToArray();
        await _player.PlayAsync(audioData, ct);
    }
    public void Dispose()
    {
        Interrupt();
        _currentCts?.Dispose();
    }
}