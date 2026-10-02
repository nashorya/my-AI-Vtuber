namespace AIVTuber.Core.Bot.Pacing;

/// <summary>One approved segment. Immediate: clean speech text. Cortico: sanitized script.
/// <paramref name="Audio"/> is audio already being synthesized for exactly this text.</summary>
internal readonly record struct SpeechItem(string Text, string? Emotion = null, PieceAudio? Audio = null);

/// <summary>What a pacer needs from the turn.</summary>
/// <param name="Synthesize">TTS for one text (voice and emotion already resolved by the caller).</param>
/// <param name="Play">The app player; the callback fires when the first PCM reaches the device.</param>
/// <param name="CanSpeak">The last gate before a piece becomes public (people resumed, stop, sign-out).</param>
/// <param name="Commit">Captions and history for a piece handed to the player.</param>
/// <param name="OnFirstPcm">First audible audio of the turn (staged avatar motion).</param>
/// <param name="Warn">A user-visible, already-localized warning for this turn.</param>
internal sealed record SpeechTurnPorts(
    Func<string, string?, CancellationToken, IAsyncEnumerable<byte[]>> Synthesize,
    Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> Play,
    Func<bool> CanSpeak,
    Action<string> Commit,
    Action OnFirstPcm,
    Action<string> Warn,
    int SampleRate);

/// <summary>Decides when approved segments become audible. One instance per reply.</summary>
internal interface ISpeechPacer : IAsyncDisposable
{
    Task SubmitAsync(SpeechItem item, CancellationToken ct);
    /// <summary>No more segments; returns when everything submitted has played or the turn stopped.</summary>
    Task CompleteAsync(CancellationToken ct);
}
