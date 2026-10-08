using System.Collections.Concurrent;
using WebRtcVadSharp;

namespace AIVTuber.Core.Audio;

public enum AudioSource { Microphone, Loopback }

/// <summary>
/// Represents a detected speech segment with its audio data and timestamps.
/// </summary>
public sealed class SpeechSegment
{
    public byte[] AudioData { get; init; } = [];
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public AudioSource Source { get; init; } = AudioSource.Microphone;
}

/// <summary>
/// VAD detector using WebRtcVadSharp. Accepts continuous audio frames
/// and outputs complete SpeechSegments with pre-speech padding and post-speech silence.
/// </summary>
public sealed class VadDetector : IDisposable
{
    private readonly WebRtcVad? _vad;
    private readonly Func<byte[], bool> _hasSpeech;
    private readonly int _aggressiveness;
    private readonly int _preSpeechPaddingMs;
    private readonly int _postSpeechSilenceMs;
    private readonly int _sampleRate;
    private readonly int _frameDurationMs;
    private readonly int _softMaxSegmentMs;
    private readonly int _maxSegmentMs;
    /// <summary>Closing silence once a segment is past <see cref="_softMaxSegmentMs"/>.</summary>
    private readonly int _shortSilenceMs;

    // State tracking
    private bool _isSpeaking;
    private DateTime _speechStartTime;
    private readonly ConcurrentQueue<byte[]> _preSpeechBuffer = new();
    private readonly List<byte[]> _currentSpeechFrames = [];
    // Silence is measured in AUDIO time (consecutive non-speech frames × frameDurationMs),
    // not wall-clock. Loopback capture delivers frames in bursts, so a wall-clock timeout
    // never elapses mid-burst and segments grow unbounded (observed: 36s segments).
    private int _silenceFrames;
    private readonly object _lock = new();

    /// <summary>True while a speech segment is in progress. Used to gate other channels
    /// (e.g. suppress loopback while the local mic is actively speaking).</summary>
    public bool IsSpeaking { get { lock (_lock) return _isSpeaking; } }

    /// <summary>
    /// Fired when a complete speech segment is detected.
    /// </summary>
    public event EventHandler<SpeechSegment>? SpeechDetected;

    /// <summary>
    /// Fired for each audio frame while a speech segment is in progress (including the
    /// post-speech silence tail before <see cref="SpeechDetected"/>). Use this to stream
    /// frames to an ASR service in real time. Fires inside the VAD lock — handlers must
    /// not block; hand frames off via a Channel.
    /// </summary>
    public event EventHandler<byte[]>? SpeechFrame;

    public VadDetector(
        int aggressiveness = 2,
        int preSpeechPaddingMs = 200,
        int postSpeechSilenceMs = 500,
        int sampleRate = 16000,
        int frameDurationMs = 30,
        int softMaxSegmentMs = 0,
        int maxSegmentMs = 0)
        : this(aggressiveness, preSpeechPaddingMs, postSpeechSilenceMs, sampleRate, frameDurationMs,
            softMaxSegmentMs, maxSegmentMs, hasSpeech: null)
    {
    }

    /// <param name="hasSpeech">Speech classifier for tests; null uses WebRTC VAD.</param>
    internal VadDetector(
        int aggressiveness,
        int preSpeechPaddingMs,
        int postSpeechSilenceMs,
        int sampleRate,
        int frameDurationMs,
        int softMaxSegmentMs,
        int maxSegmentMs,
        Func<byte[], bool>? hasSpeech)
    {
        if (aggressiveness is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(aggressiveness), "Must be 0-3");

        _aggressiveness = aggressiveness;
        _preSpeechPaddingMs = preSpeechPaddingMs;
        _postSpeechSilenceMs = postSpeechSilenceMs;
        _sampleRate = sampleRate;
        _frameDurationMs = frameDurationMs;
        // A speaker who never pauses for the full closing silence (or a stream whose music keeps
        // the detector "speaking") would otherwise be heard only when the whole stretch ends:
        // live logs show 11 s segments at p90 and up to 46 s. Past the soft limit a short
        // breath closes the segment; the hard limit cuts regardless. 0 disables either.
        _softMaxSegmentMs = Math.Max(0, softMaxSegmentMs);
        _maxSegmentMs = Math.Max(0, maxSegmentMs);
        _shortSilenceMs = Math.Min(postSpeechSilenceMs, 250);

        // Map our 0-3 aggressiveness to WebRtcVadSharp.OperatingMode
        var mode = aggressiveness switch
        {
            0 => OperatingMode.HighQuality,
            1 => OperatingMode.LowBitrate,
            2 => OperatingMode.Aggressive,
            3 => OperatingMode.VeryAggressive,
            _ => OperatingMode.Aggressive,
        };

        if (hasSpeech is not null)
        {
            _hasSpeech = hasSpeech;
            return;
        }
        var vad = new WebRtcVad
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
        _vad = vad;
        _hasSpeech = frame => vad.HasSpeech(frame);
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
                isSpeech = _hasSpeech(frame);
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
                        SpeechFrame?.Invoke(this, preFrame);
                    }
                }

                _currentSpeechFrames.Add(frame);
                SpeechFrame?.Invoke(this, frame);
                _silenceFrames = 0;
                if (_maxSegmentMs > 0 && SegmentMs >= _maxSegmentMs)
                {
                    // Hard limit: the next speech frame starts a new segment.
                    _isSpeaking = false;
                    EmitSpeechSegment(now);
                }
            }
            else
            {
                if (_isSpeaking)
                {
                    // Still collect frames during post-speech silence period
                    _currentSpeechFrames.Add(frame);
                    SpeechFrame?.Invoke(this, frame);

                    _silenceFrames++;
                    var silenceMs = _silenceFrames * _frameDurationMs;
                    var closingSilenceMs = _softMaxSegmentMs > 0 && SegmentMs >= _softMaxSegmentMs
                        ? _shortSilenceMs
                        : _postSpeechSilenceMs;
                    if (silenceMs >= closingSilenceMs)
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

    /// <summary>Audio length of the segment in progress (frames × frame duration).</summary>
    private int SegmentMs => _currentSpeechFrames.Count * _frameDurationMs;

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

    /// <summary>
    /// Discards all buffered audio and resets VAD state.
    /// Call this when the source changes (e.g. loopback unmutes after TTS) to prevent
    /// stale frames from the previous speaking period being emitted as a speech segment.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _isSpeaking = false;
            _silenceFrames = 0;
            _currentSpeechFrames.Clear();
            while (_preSpeechBuffer.TryDequeue(out _)) { }
        }
    }

    public void Dispose()
    {
        Flush();
        _vad?.Dispose();
    }
}