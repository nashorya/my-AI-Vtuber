namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Streaming TTS interface. Converts text to audio in chunks.
/// </summary>
public interface ITtsClient
{
    /// <summary>
    /// Streams audio data for the given text. Yields PCM 16kHz mono audio chunks.
    /// </summary>
    IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        CancellationToken cancellationToken = default);
}