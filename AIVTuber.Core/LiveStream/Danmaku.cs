namespace AIVTuber.Core.LiveStream;

/// <summary>
/// Represents a single danmaku (bullet comment) from a live stream.
/// </summary>
public sealed class Danmaku
{
    public string Uid { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Platform { get; init; } = "bilibili";
}