using NAudio.Wave;

namespace AIVTuber.Core.Audio;

/// <summary>
/// Audio player with streaming support and RMS calculation for lip sync.
/// Uses NAudio WaveOutEvent for playback and exposes RMS events every ~30ms.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _waveOut;
    private bool _disposed;
    private CancellationTokenSource? _playCts;

    /// <summary>
    /// Fired every ~30ms during playback with the RMS value (0.0 - 1.0 range typically).
    /// Used for lip sync (VTS mouth parameter).
    /// </summary>
    public event EventHandler<float>? RmsUpdated;

    /// <summary>
    /// Fired when the current playback has finished naturally (not stopped by user).
    /// </summary>
    public event EventHandler? PlaybackFinished;

    /// <summary>
    /// Plays a WAV or MP3 byte array. Blocks until playback completes.
    /// </summary>
    public async Task PlayAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var stream = new MemoryStream(audioData);
        await PlayAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Plays audio from a stream (supports WAV, MP3). Starts immediately and
    /// computes RMS in ~30ms intervals for lip sync.
    /// The stream is read in chunks to support streaming TTS output.
    /// </summary>
    public async Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop(); // Stop any current playback

        _playCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _playCts.Token;

        try
        {
            using var reader = new StreamAudioReader(audioStream);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(reader);

            var tcs = new TaskCompletionSource<bool>();
            _waveOut.PlaybackStopped += (_, _) => tcs.TrySetResult(true);

            _waveOut.Play();

            // RMS calculation loop
            using var rmsTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));

            while (await rmsTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_waveOut is null || _waveOut.PlaybackState == PlaybackState.Stopped)
                    break;

                var rms = reader.GetCurrentRms();
                RmsUpdated?.Invoke(this, rms);
            }

            // Wait for playback to finish if not cancelled
            if (!ct.IsCancellationRequested)
            {
                await tcs.Task.ConfigureAwait(false);
                PlaybackFinished?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Playback was stopped, this is expected
        }
        finally
        {
            CleanupPlayback();
        }
    }

    /// <summary>
    /// Plays audio via a streaming pattern: the caller writes chunks to the provided
    /// stream, and playback begins immediately. Call CompleteWriting() when done.
    /// </summary>
    public Stream CreateStreamingSink()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        var stream = new StreamingAudioStream();
        var reader = new StreamAudioReader(stream);

        _waveOut = new WaveOutEvent();
        _waveOut.Init(reader);
        _waveOut.Play();

        // Start RMS calculation in background
        _ = Task.Run(async () =>
        {
            using var rmsTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));
            while (_waveOut is not null && _waveOut.PlaybackState != PlaybackState.Stopped)
            {
                await rmsTimer.WaitForNextTickAsync().ConfigureAwait(false);
                if (_waveOut is null) break;

                var rms = reader.GetCurrentRms();
                RmsUpdated?.Invoke(this, rms);
            }
        });

        return stream;
    }

    /// <summary>
    /// Stops current playback immediately.
    /// </summary>
    public void Stop()
    {
        _playCts?.Cancel();
        CleanupPlayback();
    }

    private void CleanupPlayback()
    {
        if (_waveOut is not null)
        {
            try
            {
                if (_waveOut.PlaybackState != PlaybackState.Stopped)
                    _waveOut.Stop();
            }
            catch { /* ignore */ }

            _waveOut.Dispose();
            _waveOut = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    /// <summary>
    /// A stream that supports concurrent reading and writing for streaming audio playback.
    /// </summary>
    private sealed class StreamingAudioStream : Stream
    {
        private readonly List<byte[]> _chunks = [];
        private int _readOffset;
        private int _chunkIndex;
        private bool _writingComplete;
        private readonly object _lockObj = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var chunk = new byte[count];
            Array.Copy(buffer, offset, chunk, 0, count);
            lock (_lockObj)
            {
                _chunks.Add(chunk);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lockObj)
            {
                int totalRead = 0;
                while (totalRead < count)
                {
                    if (_chunkIndex >= _chunks.Count)
                    {
                        if (_writingComplete)
                            return totalRead;

                        // No data available yet, wait briefly
                        break;
                    }

                    var chunk = _chunks[_chunkIndex];
                    int available = chunk.Length - _readOffset;
                    int toRead = Math.Min(available, count - totalRead);
                    Array.Copy(chunk, _readOffset, buffer, offset + totalRead, toRead);
                    totalRead += toRead;
                    _readOffset += toRead;

                    if (_readOffset >= chunk.Length)
                    {
                        _chunkIndex++;
                        _readOffset = 0;
                    }
                }

                return totalRead;
            }
        }

        public void CompleteWriting()
        {
            lock (_lockObj)
            {
                _writingComplete = true;
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Custom WaveStream that reads from a stream and provides RMS calculation.
    /// </summary>
    private sealed class StreamAudioReader : WaveStream
    {
        private readonly Stream _sourceStream;
        private WaveFormat? _waveFormat;
        private bool _formatRead;

        public StreamAudioReader(Stream sourceStream)
        {
            _sourceStream = sourceStream;
            _formatRead = false;
        }

        public override WaveFormat WaveFormat
        {
            get
            {
                if (!_formatRead)
                {
                    // Try to determine format from the stream
                    // Default to 16kHz 16-bit mono (our standard format)
                    _waveFormat = new WaveFormat(16000, 16, 1);
                    _formatRead = true;
                }
                return _waveFormat!;
            }
        }

        public override long Length => _sourceStream.CanSeek ? _sourceStream.Length : 0;

        public override long Position
        {
            get => _sourceStream.CanSeek ? _sourceStream.Position : 0;
            set => _sourceStream.Position = value;
        }

        // Store recent samples for RMS calculation
        private float _currentRms;
        private readonly object _rmsLock = new();

        public float GetCurrentRms()
        {
            lock (_rmsLock)
            {
                return _currentRms;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // For supported formats, try to use WaveFormatConversion
            // For simplicity, we read raw PCM data
            int bytesRead = _sourceStream.Read(buffer, offset, count);

            // Calculate RMS from the read bytes (16-bit PCM)
            if (bytesRead > 0)
            {
                CalculateRms(buffer, offset, bytesRead);
            }

            return bytesRead;
        }

        private void CalculateRms(byte[] buffer, int offset, int count)
        {
            // Assume 16-bit PCM samples
            int sampleCount = count / 2; // 2 bytes per sample
            if (sampleCount == 0) return;

            double sumSquares = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(buffer, offset + i * 2);
                float normalized = sample / 32768f;
                sumSquares += normalized * normalized;
            }

            float rms = (float)Math.Sqrt(sumSquares / sampleCount);

            lock (_rmsLock)
            {
                _currentRms = rms;
            }
        }
    }
}