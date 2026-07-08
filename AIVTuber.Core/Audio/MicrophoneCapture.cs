using NAudio.Wave;

namespace AIVTuber.Core.Audio;

/// <summary>
/// Captures audio from a microphone using NAudio WaveInEvent.
/// Output: 16kHz, 16-bit, mono PCM at ~30ms per frame.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private WaveInEvent? _waveIn;
    private readonly int _deviceIndex;
    private readonly int _sampleRate;
    private readonly int _frameDurationMs;
    private bool _disposed;

    public event EventHandler<byte[]>? AudioFrameAvailable;
    /// <summary>Fired per frame with RMS in [0, 1]. Use for a mic level indicator.</summary>
    public event EventHandler<float>? LevelUpdated;
    public event EventHandler<Exception>? ErrorOccurred;

    public MicrophoneCapture(int deviceIndex = 0, int sampleRate = 16000, int frameDurationMs = 30)
    {
        _deviceIndex = deviceIndex;
        _sampleRate = sampleRate;
        _frameDurationMs = frameDurationMs;
    }

    /// <summary>
    /// Lists all available microphone devices.
    /// </summary>
    public static string[] ListDevices()
    {
        var count = WaveInEvent.DeviceCount;
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            names[i] = $"[{i}] {caps.ProductName}";
        }
        return names;
    }

    /// <summary>
    /// Starts capturing audio from the configured microphone device.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_waveIn is not null)
            return; // Already started

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _deviceIndex,
            WaveFormat = new WaveFormat(_sampleRate, 16, 1),
            BufferMilliseconds = _frameDurationMs,
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveIn.StartRecording();
    }

    /// <summary>
    /// Stops capturing audio.
    /// </summary>
    public void Stop()
    {
        if (_waveIn is null)
            return;

        _waveIn.StopRecording();
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var buffer = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, buffer, e.BytesRecorded);
        AudioFrameAvailable?.Invoke(this, buffer);

        if (LevelUpdated is not null)
        {
            int samples = e.BytesRecorded / 2;
            double sum = 0;
            for (int i = 0; i < samples; i++)
            {
                float s = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                sum += s * s;
            }
            LevelUpdated.Invoke(this, (float)Math.Sqrt(sum / Math.Max(samples, 1)));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            ErrorOccurred?.Invoke(this, e.Exception);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}