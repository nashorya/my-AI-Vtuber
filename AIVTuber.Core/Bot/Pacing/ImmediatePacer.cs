using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot.Pacing;

/// <summary>
/// Plain pacing (the tested pre-Cortico behaviour): one player call for the whole reply, each
/// segment synthesized and played as soon as its audio arrives, gated and committed at its first
/// chunk. Also the voice-only fallback of <see cref="CorticoPacer"/>; <paramref name="after"/> lets
/// that fallback wait for a piece still playing (the player stops whatever plays when it starts).
/// </summary>
internal sealed class ImmediatePacer(SpeechTurnPorts ports, CancellationToken turn, Task? after = null) : ISpeechPacer
{
    private readonly Channel<SpeechItem> _items = Channel.CreateUnbounded<SpeechItem>();
    private readonly object _sync = new();
    private Task? _playback;

    public Task SubmitAsync(SpeechItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Enqueue(item);
        return Task.CompletedTask;
    }

    /// <summary>Synchronous submit, for handing over a queue from another pacer.</summary>
    public void Enqueue(SpeechItem item)
    {
        lock (_sync) _playback ??= Task.Run(RunAsync);
        if (!_items.Writer.TryWrite(item)) item.Audio?.Cancel();
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        _items.Writer.TryComplete();
        Task? playback;
        lock (_sync) playback = _playback;
        if (playback is not null) await playback.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        if (after is not null)
        {
            // A cancelled turn must not stay parked behind the piece it was waiting for.
            try { await after.WaitAsync(turn).ConfigureAwait(false); } catch { /* its own owner reported it */ }
        }
        if (turn.IsCancellationRequested) { Drain(); return; }
        await ports.Play(Chunks(turn), turn, ports.OnFirstPcm).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<byte[]> Chunks([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _items.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (!LlmClient.IsSpeakableText(item.Text)) { item.Audio?.Cancel(); continue; }
            var first = true;
            var audio = item.Audio?.ReadAllAsync(ct) ?? ports.Synthesize(item.Text, item.Emotion, ct);
            await foreach (var chunk in audio.WithCancellation(ct).ConfigureAwait(false))
            {
                if (first)
                {
                    first = false;
                    // Recheck after synthesis: people may have resumed while TTS was on the network.
                    if (!ports.CanSpeak()) { item.Audio?.Cancel(); Drain(); yield break; }
                    ports.Commit(item.Text);
                }
                yield return chunk;
            }
        }
    }

    private void Drain()
    {
        _items.Writer.TryComplete();
        while (_items.Reader.TryRead(out var rest)) rest.Audio?.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        Drain();
        return ValueTask.CompletedTask;
    }
}
