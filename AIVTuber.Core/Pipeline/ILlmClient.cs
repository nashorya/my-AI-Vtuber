namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Message role for LLM conversation history.
/// </summary>
public enum MessageRole
{
    System,
    User,
    Assistant
}

/// <summary>
/// A single message in LLM conversation history.
/// </summary>
public sealed class Message
{
    public MessageRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// Streaming LLM interface. Returns tokens as they arrive and fires
/// OnSentenceReady once per turn with the full speakable reply.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Fires when the turn's speakable text is ready (tags stripped).
    /// </summary>
    event EventHandler<string>? OnSentenceReady;

    /// <summary>
    /// Fires when an emotion tag is detected in the output (e.g., [emotion:happy]).
    /// </summary>
    event EventHandler<string>? OnEmotionDetected;

    /// <summary>
    /// Fires when a structured avatar action tag is detected (e.g., [action:head_shake]).
    /// </summary>
    event EventHandler<string>? OnActionDetected;

    /// <summary>
    /// Fires when a pose tag is detected (e.g., [pose:tilt_left]).
    /// </summary>
    event EventHandler<string>? OnPoseDetected;

    /// <summary>
    /// Streams LLM response tokens. Each yielded string is a token chunk.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        List<Message> history,
        string userInput,
        CancellationToken cancellationToken = default);
}
