namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Streaming TTS interface. Converts text to audio in chunks.
/// </summary>
public interface ITtsClient
{
    /// <summary>
    /// Streams audio data for the given text. Yields raw PCM audio chunks.
    /// <paramref name="emotion"/> is the LLM-detected emotion name (e.g. "happy", "sad") or null for neutral.
    /// Each provider applies it differently: MiniMax via voice_setting.emotion, Fish Audio via inline tags,
    /// DashScope via an instructions string.
    /// </summary>
    IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        CancellationToken cancellationToken = default);
}