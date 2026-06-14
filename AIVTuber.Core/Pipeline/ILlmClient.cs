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
/// OnSentenceReady when a complete sentence is detected.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Fires when a complete sentence is ready (terminated by sentence-ending punctuation).
    /// </summary>
    event EventHandler<string>? OnSentenceReady;

    /// <summary>
    /// Fires when an emotion tag is detected in the output (e.g., [emotion:happy]).
    /// </summary>
    event EventHandler<string>? OnEmotionDetected;

    /// <summary>
    /// Streams LLM response tokens. Each yielded string is a token chunk.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        List<Message> history,
        string userInput,
        CancellationToken cancellationToken = default);
}