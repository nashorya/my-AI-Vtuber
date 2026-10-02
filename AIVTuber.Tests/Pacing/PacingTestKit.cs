using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Bot.Pacing;

namespace AIVTuber.Tests.Pacing;

/// <summary>Ports whose "audio" is the UTF-8 of the spoken text, so what was played is readable.</summary>
internal sealed class PacingTestKit
{
    public readonly List<string> Synthesized = [], Committed = [], Warnings = [];
    public readonly List<string> Played = [];
    public int FirstPcm;
    public bool Speakable = true;
    public Func<string, Task>? BeforeSynthChunk;
    public TaskCompletionSource? HoldPlayback;

    public SpeechTurnPorts Ports => new(Synth, Play, () => Speakable,
        t => { lock (Committed) Committed.Add(t); }, () => Interlocked.Increment(ref FirstPcm),
        w => { lock (Warnings) Warnings.Add(w); }, 24000);

    private async IAsyncEnumerable<byte[]> Synth(string text, string? emotion, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (Synthesized) Synthesized.Add(text);
        if (BeforeSynthChunk is not null) await BeforeSynthChunk(text);
        ct.ThrowIfCancellationRequested();
        yield return Encoding.UTF8.GetBytes(text);
    }

    private async Task Play(IAsyncEnumerable<byte[]> chunks, CancellationToken ct, Action? first)
    {
        var bytes = new List<byte>();
        var started = false;
        try
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (!started) { started = true; first?.Invoke(); }
                bytes.AddRange(chunk);
            }
            if (HoldPlayback is not null) await HoldPlayback.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) { }
        finally { if (bytes.Count > 0) lock (Played) Played.Add(Encoding.UTF8.GetString(bytes.ToArray())); }
    }
}
