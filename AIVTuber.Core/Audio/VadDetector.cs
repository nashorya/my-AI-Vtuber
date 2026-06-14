using System.Collections.Concurrent;
using WebRtcVadSharp;

namespace AIVTuber.Core.Audio;

/// <summary>
/// Represents a detected speech segment with its audio data and timestamps.
/// </summary>
public sealed class SpeechSegment
{
    public byte[] AudioData { get; init; } = [];
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
}

/// <summary>
/// VAD detector using WebRtcVadSharp. Accepts continuous audio frames
/// and outputs complete SpeechSegments with pre-speech padding and post-speech silence.
/// </summary>
public sealed class VadDetector : IDisposable
{
    private readonly WebRtcVad _vad;
    private readonly int _aggressiveness;
    private readonly int _preSpeechPaddingMs;
    private readonly int _postSpeechSilenceMs;
    private readonly int _sampleRate;
    private readonly int _frameDurationMs;

    // State tracking
    private bool _isSpeaking;
    private DateTime _speechStartTime;
    private readonly ConcurrentQueue<byte[]> _preSpeechBuffer = new();
    private readonly List<byte[]> _currentSpeechFrames = [];
    private DateTime _lastSpeechTime;
    private readonly object _lock = new();

    /// <summary>
    /// Fired when a complete speech segment is detected.
    /// </summary>
    public event EventHandler<SpeechSegment>? SpeechDetected;

    public VadDetector(
        int aggressiveness = 2,
        int preSpeechPaddingMs = 200,
        int postSpeechSilenceMs = 500,
        int sampleRate = 16000,
        int frameDurationMs = 30)
    {
        if (aggressiveness is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(aggressiveness), "Must be 0-3");

        _aggressiveness = aggressiveness;
        _preSpeechPaddingMs = preSpeechPaddingMs;
        _postSpeechSilenceMs = postSpeechSilenceMs;
        _sampleRate = sampleRate;
        _frameDurationMs = frameDurationMs;

        // Map our 0-3 aggressiveness to WebRtcVadSharp.OperatingMode
        var mode = aggressiveness switch
        {
            0 => OperatingMode.HighQuality,
            1 => OperatingMode.LowBitrate,
            2 => OperatingMode.Aggressive,
            3 => OperatingMode.VeryAggressive,
            _ => OperatingMode.Aggressive,
        };

        _vad = new WebRtcVad
        {
            SampleRate = SampleRate.Is16kHz,
            FrameLength = frameDurationMs switch
            {
                10 => FrameLength.Is10ms,
                20 => FrameLength.Is20ms,
                30 => FrameLength.Is30ms,
                _ => FrameLength.Is30ms,
            },
            OperatingMode = mode,
        };
    }

    /// <summary>
    /// Feed an audio frame (16-bit mono PCM at configured sample rate) into the VAD detector.
    /// </summary>
    /// <param name="frame">PCM audio bytes, should correspond to frameDurationMs duration.</param>
    public void Feed(byte[] frame)
    {
        lock (_lock)
        {
            bool isSpeech;
            try
            {
                // WebRtcVadSharp expects 16-bit PCM samples
                isSpeech = _vad.HasSpeech(frame);
            }
            catch
            {
                // VAD may reject frames that are too short; skip them
                return;
            }

            var now = DateTime.UtcNow;

            if (isSpeech)
            {
                if (!_isSpeaking)
                {
                    // Speech started
                    _isSpeaking = true;
                    _speechStartTime = now;
                    _currentSpeechFrames.Clear();

                    // Drain the pre-speech buffer into the current segment
                    while (_preSpeechBuffer.TryDequeue(out var preFrame))
                    {
                        _currentSpeechFrames.Add(preFrame);
                    }
                }

                _currentSpeechFrames.Add(frame);
                _lastSpeechTime = now;
            }
            else
            {
                if (_isSpeaking)
                {
                    // Still collect frames during post-speech silence period
                    _currentSpeechFrames.Add(frame);

                    var silenceDuration = (now - _lastSpeechTime).TotalMilliseconds;
                    if (silenceDuration >= _postSpeechSilenceMs)
                    {
                        // Speech segment ended
                        _isSpeaking = false;
                        EmitSpeechSegment(now);
                    }
                }
                else
                {
                    // Not speaking — buffer for pre-speech padding
                    _preSpeechBuffer.Enqueue(frame);

                    // Keep only the last preSpeechPaddingMs worth of frames
                    int maxPreFrames = _preSpeechPaddingMs / _frameDurationMs + 1;
                    while (_preSpeechBuffer.Count > maxPreFrames)
                    {
                        _preSpeechBuffer.TryDequeue(out _);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Force-flush any ongoing speech segment (e.g., on Stop).
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_isSpeaking && _currentSpeechFrames.Count > 0)
            {
                _isSpeaking = false;
                EmitSpeechSegment(DateTime.UtcNow);
            }
        }
    }

    private void EmitSpeechSegment(DateTime endTime)
    {
        var totalBytes = _currentSpeechFrames.Sum(f => f.Length);
        var audioData = new byte[totalBytes];
        int offset = 0;
        foreach (var frame in _currentSpeechFrames)
        {
            Array.Copy(frame, 0, audioData, offset, frame.Length);
            offset += frame.Length;
        }
        _currentSpeechFrames.Clear();

        var segment = new SpeechSegment
        {
            AudioData = audioData,
            StartTime = _speechStartTime,
            EndTime = endTime,
        };

        SpeechDetected?.Invoke(this, segment);
    }

    public void Dispose()
    {
        Flush();
        _vad.Dispose();
    }
}