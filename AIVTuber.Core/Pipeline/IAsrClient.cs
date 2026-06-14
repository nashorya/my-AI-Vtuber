namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Streaming ASR interface. Takes PCM audio data and returns transcribed text.
/// </summary>
public interface IAsrClient
{
    /// <summary>
    /// Recognizes speech from 16kHz 16-bit mono PCM audio data.
    /// </summary>
    Task<string> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recognizes speech from a stream of audio frames.
    /// </summary>
    IAsyncEnumerable<string> StreamRecognizeAsync(IAsyncEnumerable<byte[]> audioStream, CancellationToken cancellationToken = default);
}