namespace AIVTuber.Core.Cortico;

/// <summary>
/// The Cortico performance layer. While this backend is selected it is the only audio player and
/// the only VTS parameter writer for replies; the app's own player and VTS controller are not
/// initialized. Replies of every protocol are adapted into Cortico scripts and performed here.
/// </summary>
public interface ICorticoPerformance
{
    /// <summary>Cortico script grammar and vocabulary only. The reply envelope (legacy script or
    /// protocol v2 events) and the PASS/thought rules are composed by the app, so the prompt always
    /// matches the parser in use (see <see cref="CorticoPrompt"/>).</summary>
    string ScriptGrammar { get; }

    /// <summary>Clean spoken text of a script, as Cortico's own parser extracts it.</summary>
    Task<string> PrepareAsync(string script, CancellationToken ct);

    /// <summary>Starts one reply's performance. Segments are fed as they are approved, so the first
    /// one is performed while the LLM is still generating. <paramref name="authorize"/> is asked
    /// with the clean text of every piece right before it becomes audible; returning false stops
    /// the rest of the turn. <paramref name="started"/> fires when audio actually starts.</summary>
    Task<ICorticoStage> BeginAsync(Func<string, bool> authorize, Action started, CancellationToken ct);

    /// <summary>Stops the real performance now: queued segments, audio, synthesis, held states and
    /// gestures under way. Safe to call when nothing is performing.</summary>
    Task InterruptAsync(CancellationToken ct);
}

/// <summary>One reply being performed. Dispose always; disposing an unfinished stage interrupts it.</summary>
public interface ICorticoStage : IAsyncDisposable
{
    /// <summary>Performs one approved segment (appended after the earlier ones).</summary>
    Task FeedAsync(string script, CancellationToken ct);

    /// <summary>No more segments: waits until everything fed has been performed. Throws when the
    /// performance was cancelled, denied or failed.</summary>
    Task CompleteAsync(CancellationToken ct);
}

public static class CorticoPerformanceExtensions
{
    /// <summary>Whole-script convenience: one segment, then complete.</summary>
    public static async Task PerformAsync(this ICorticoPerformance performance, string script,
        Func<string, bool> authorize, Action started, CancellationToken ct)
    {
        await using var stage = await performance.BeginAsync(authorize, started, ct).ConfigureAwait(false);
        await stage.FeedAsync(script, ct).ConfigureAwait(false);
        await stage.CompleteAsync(ct).ConfigureAwait(false);
    }
}
