using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.LiveStream;

/// <summary>
/// The opposing streamer in a Bilibili PK match, as resolved by the Python bridge.
/// </summary>
public sealed class PkOpponent
{
    public string Uid { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public long FollowerCount { get; init; }
    public int RoomId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Parses a push body from the bridge. Returns false for anything unusable so the
    /// caller can drop the event silently — a missed PK announcement is preferable to
    /// an exception on the HTTP accept path.
    /// </summary>
    public static bool TryParse(string body, out PkOpponent? opponent)
    {
        opponent = null;
        if (string.IsNullOrWhiteSpace(body)) return false;

        PkPush? push;
        try { push = JsonSerializer.Deserialize<PkPush>(body); }
        catch (JsonException) { return false; }

        if (push is null || string.IsNullOrEmpty(push.Uid)) return false;

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
        [JsonPropertyName("uid")] public string? Uid { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("follower")] public long Follower { get; set; }
        [JsonPropertyName("roomid")] public int RoomId { get; set; }
    }
}
