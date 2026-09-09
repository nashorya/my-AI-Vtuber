using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.LiveStream;

/// <summary>
/// The opposing streamer in a Bilibili PK match, as resolved by the danmaku bridge.
/// </summary>
public sealed class PkOpponent
{
    public string Uid { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public long FollowerCount { get; init; }
    public int RoomId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// True when the bridge body is a PK-end notification (<c>{"type":"end"}</c>).
    /// Start payloads must not be treated as end.
    /// </summary>
    public static bool IsEndPush(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("type", out var type)) return false;
            return string.Equals(type.GetString(), "end", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a push body from the bridge. Returns false for anything unusable so the
    /// caller can drop the event silently — a missed PK announcement is preferable to
    /// an exception on the HTTP accept path. End notifications (<see cref="IsEndPush"/>)
    /// also return false here.
    /// </summary>
    public static bool TryParse(string body, out PkOpponent? opponent)
    {
        opponent = null;
        if (string.IsNullOrWhiteSpace(body)) return false;
        if (IsEndPush(body)) return false;

        PkPush? push;
        try { push = JsonSerializer.Deserialize<PkPush>(body); }
        catch (JsonException) { return false; }

        if (push is null || string.IsNullOrEmpty(push.Uid)) return false;
        // Reject end-shaped objects that somehow carry a uid but are typed as end
        // (already handled above); also ignore explicit type != start/default.
        if (!string.IsNullOrEmpty(push.Type)
            && !string.Equals(push.Type, "start", StringComparison.OrdinalIgnoreCase))
            return false;

        opponent = new PkOpponent
        {
            Uid = push.Uid,
            Username = string.IsNullOrWhiteSpace(push.Username) ? "对面主播" : push.Username,
            FollowerCount = push.Follower,
            RoomId = push.RoomId,
            Timestamp = DateTime.UtcNow,
        };
        return true;
    }

    private sealed class PkPush
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("uid")] public string? Uid { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("follower")] public long Follower { get; set; }
        [JsonPropertyName("roomid")] public int RoomId { get; set; }
    }
}
