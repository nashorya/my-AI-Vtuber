using System.Threading.Channels;

namespace AIVTuber.Core.Bot.Pacing;

/// <summary>Streaming synthesis of one piece: chunks are buffered for playback (read once) and
/// forwarded as they arrive. Playback may start before synthesis ends.</summary>
internal sealed class PieceAudio
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenSource _forwardCts;
    private readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>();

    private PieceAudio(string text, CancellationToken turn)
    {
        Text = text;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(turn);
        _forwardCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
    }

    public string Text { get; }

    public static PieceAudio Start(string text, Func<CancellationToken, IAsyncEnumerable<byte[]>> source,
        Func<byte[], Task> forward, Action firstChunk, Func<Exception?, Task> finished, CancellationToken turn)
    {
        var audio = new PieceAudio(text, turn);
        _ = Task.Run(() => audio.ForwardAsync(forward, finished));
        _ = Task.Run(() => audio.PumpAsync(source, firstChunk));
        return audio;
    }

    private async Task PumpAsync(Func<CancellationToken, IAsyncEnumerable<byte[]>> source,
        Action firstChunk)
    {
        Exception? failure = null;
        var first = true;
        try
        {
            await foreach (var chunk in source(_cts.Token).WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                if (chunk.Length == 0) continue;
                _chunks.Writer.TryWrite(chunk);
                if (first) { first = false; firstChunk(); }
                _reports.Writer.TryWrite(chunk);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _chunks.Writer.TryComplete();
            _reports.Writer.TryComplete(failure);
        }
    }

    private async Task ForwardAsync(Func<byte[], Task> forward, Func<Exception?, Task> finished)
    {
        Exception? failure = null;
        try
        {
            await foreach (var chunk in _reports.Reader.ReadAllAsync(_forwardCts.Token).ConfigureAwait(false))
                await forward(chunk).WaitAsync(_forwardCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _reports.Writer.TryComplete();
            while (_reports.Reader.TryRead(out _)) { }
        }
        if (_forwardCts.IsCancellationRequested) return;
        try { await finished(failure).WaitAsync(_forwardCts.Token).ConfigureAwait(false); }
        catch { /* the watchdog owns host loss */ }
    }

    // Stop visual reports without cancelling the local TTS stream or its playback buffer.
    public void StopForwarding()
    {
        _reports.Writer.TryComplete();
        _forwardCts.Cancel();
    }

    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken ct) => _chunks.Reader.ReadAllAsync(ct);

    public void Cancel() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
}
