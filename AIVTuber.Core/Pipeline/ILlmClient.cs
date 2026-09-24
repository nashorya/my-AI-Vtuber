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

/// <summary>
/// Optional capability implemented by LLM clients that support the versioned
/// reply protocol (RT-05): typed NDJSON events streamed line-by-line so the
/// first approved speech segment can reach TTS before the model finishes.
/// "legacy" clients do not implement this and keep the whole-reply contract.
/// </summary>
public interface IReplyProtocolStream
{
    /// <summary>Configured protocol version: "legacy" or "v2".</summary>
    string ReplyProtocol { get; }

    /// <summary>
    /// Streams validated reply-protocol events. Speech events are only produced after a
    /// decision=speak event; every violation is reported as a single fail-closed
    /// <see cref="ReplyStreamEventKind.ProtocolError"/> terminal event.
    /// </summary>
    IAsyncEnumerable<ReplyStreamEvent> StreamEventsAsync(
        List<Message> history,
        string userInput,
        CancellationToken cancellationToken = default);
}
