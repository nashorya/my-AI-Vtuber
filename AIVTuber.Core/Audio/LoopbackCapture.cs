using NAudio.Wave;

namespace AIVTuber.Core.Audio;

/// <summary>
/// Captures system audio (loopback) via WASAPI and resamples to 16kHz mono.
/// Uses BufferedWaveProvider as intermediate buffer for reliable resampling.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveInEvent? _silenceWaveIn;
    private BufferedWaveProvider? _bufferedProvider;
    private MediaFoundationResampler? _resampler;
    private readonly int _targetSampleRate;
    private readonly string? _deviceName;
    private bool _disposed;
    private volatile bool _formatInitialized;

    /// <summary>
    /// Fired when a resampled audio frame is available (16-bit mono PCM bytes, ~30ms chunks).
    /// </summary>
    public event EventHandler<byte[]>? AudioFrameAvailable;

    /// <summary>
    /// Fired when a capture error occurs.
    /// </summary>
    public event EventHandler<Exception>? ErrorOccurred;

    public LoopbackCapture(int targetSampleRate = 16000, string? deviceName = null)
    {
        _targetSampleRate = targetSampleRate;
        _deviceName = deviceName;
    }

    /// <summary>
    /// Lists available output devices that can be captured in loopback mode.
    /// </summary>
    public static string[] ListDevices()
    {
        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(
            NAudio.CoreAudioApi.DataFlow.Render,
            NAudio.CoreAudioApi.DeviceState.Active);

        var names = new List<string>();
        foreach (var device in devices)
        {
            names.Add(device.FriendlyName);
            device.Dispose();
        }
        enumerator.Dispose();
        return names.ToArray();
    }

    /// <summary>
    /// Starts capturing system audio (loopback).
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_capture is not null)
            return;

        var targetFormat = new WaveFormat(_targetSampleRate, 16, 1);
        // Bytes per frame at 30ms intervals: sampleRate * channels * (bits/8) * duration_s
        int frameBytes = _targetSampleRate * 1 * 2 * 30 / 1000; // ~960 bytes for 16kHz mono 16-bit 30ms

        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        NAudio.CoreAudioApi.MMDevice? device;

        if (_deviceName is not null)
        {
            device = enumerator.EnumerateAudioEndPoints(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(_deviceName));
            if (device is null)
                device = enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia);
        }
        else
        {
            device = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.Role.Multimedia);
        }

        _capture = new WasapiLoopbackCapture(device);
        _formatInitialized = false;

        _capture.DataAvailable += (s, e) =>
        {
            if (!_formatInitialized)
            {
                _bufferedProvider = new BufferedWaveProvider(_capture.WaveFormat)
                {
                    BufferLength = _capture.WaveFormat.AverageBytesPerSecond * 10, // 10 seconds buffer
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                };

                _resampler = new MediaFoundationResampler(_bufferedProvider, targetFormat);
                _formatInitialized = true;
            }

            // Feed captured data into the buffer
            _bufferedProvider!.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // Read resampled data in ~30ms chunks
            while (_resampler != null && _bufferedProvider != null &&
                   _bufferedProvider.BufferedBytes > _capture.WaveFormat.AverageBytesPerSecond / 33)
            {
                var readBuffer = new byte[frameBytes];
                int bytesRead = _resampler.Read(readBuffer, 0, readBuffer.Length);
                if (bytesRead == 0) break;

                var frame = new byte[bytesRead];
                Array.Copy(readBuffer, frame, bytesRead);
                AudioFrameAvailable?.Invoke(this, frame);
            }
        };

        _capture.RecordingStopped += (s, e) =>
        {
            if (e.Exception is not null)
                ErrorOccurred?.Invoke(this, e.Exception);
        };

        // Start a silence wave-in to keep the loopback stream active
        // (WASAPI loopback requires an active render stream)
        _silenceWaveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(44100, 16, 2),
            BufferMilliseconds = 100,
        };
        _silenceWaveIn.DataAvailable += (_, _) => { }; // discard
        _silenceWaveIn.StartRecording();

        _capture.StartRecording();
        enumerator.Dispose();
    }

    /// <summary>
    /// Stops capturing system audio.
    /// </summary>
    public void Stop()
    {
        _capture?.StopRecording();
        _silenceWaveIn?.StopRecording();
        _silenceWaveIn?.Dispose();
        _silenceWaveIn = null;
        _resampler?.Dispose();
        _resampler = null;
        _bufferedProvider = null;
        _formatInitialized = false;
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}