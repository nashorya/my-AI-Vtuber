using System.Threading.Channels;

namespace AIVTuber.Core.Bot.Pacing;

/// <summary>Streaming synthesis of one piece: chunks are buffered for playback (read once) and
/// forwarded as they arrive. Playback may start before synthesis ends.</summary>
internal sealed class PieceAudio
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts;

    private PieceAudio(string text, CancellationToken turn)
    {
        Text = text;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(turn);
    }

    public string Text { get; }

    public static PieceAudio Start(string text, Func<CancellationToken, IAsyncEnumerable<byte[]>> source,
        Func<byte[], Task> forward, Action firstChunk, Func<Exception?, Task> finished, CancellationToken turn)
    {
        var audio = new PieceAudio(text, turn);
        _ = Task.Run(() => audio.PumpAsync(source, forward, firstChunk, finished));
        return audio;
    }

    private async Task PumpAsync(Func<CancellationToken, IAsyncEnumerable<byte[]>> source,
        Func<byte[], Task> forward, Action firstChunk, Func<Exception?, Task> finished)
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
                await forward(chunk).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { _chunks.Writer.TryComplete(); }
        if (_cts.IsCancellationRequested) return; // cancelled pieces report nothing
        try { await finished(failure).ConfigureAwait(false); } catch { /* the watchdog owns host loss */ }
    }

    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken ct) => _chunks.Reader.ReadAllAsync(ct);

    public void Cancel() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
}
