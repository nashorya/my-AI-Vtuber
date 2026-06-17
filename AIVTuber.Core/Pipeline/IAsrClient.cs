namespace AIVTuber.Core.Pipeline;

/// <summary>Transcription result with optional detected user emotion (e.g. from Qwen-ASR).</summary>
public record AsrResult(string Text, string? Emotion = null);

public interface IAsrClient
{
    Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AsrResult> StreamRecognizeAsync(IAsyncEnumerable<byte[]> audioStream, CancellationToken cancellationToken = default);
}
