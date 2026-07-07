using Microsoft.Data.Sqlite;

namespace AIVTuber.Core.Memory;

/// <summary>
/// Represents a viewer (audience member) in the memory system.
/// </summary>
public sealed class Viewer
{
    public string Uid { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public string? FirstSeen { get; set; }
    public string? LastSeen { get; set; }
    public int InteractionCount { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// CRUD operations for the viewers table.
/// Primary key: (uid, platform). Used by DanmakuSelector to check
/// if a viewer is a returning audience member.
/// </summary>
public sealed class ViewerRepository
{
    private readonly MemoryDb _db;

    public ViewerRepository(MemoryDb db) => _db = db;

    /// <summary>Get a viewer by UID and platform.</summary>
    public async Task<Viewer?> GetAsync(string uid, string platform)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT uid, platform, nickname, first_seen, last_seen, interaction_count, notes FROM viewers WHERE uid = @uid AND platform = @platform";
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.Parameters.AddWithValue("@platform", platform);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return new Viewer
            {
                Uid = reader.GetString(0),
                Platform = reader.GetString(1),
                Nickname = reader.IsDBNull(2) ? null : reader.GetString(2),
                FirstSeen = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastSeen = reader.IsDBNull(4) ? null : reader.GetString(4),
                InteractionCount = reader.GetInt32(5),
                Notes = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
        }
        return null;
    }

    /// <summary>Insert a new viewer or update an existing one.</summary>
    public async Task UpsertAsync(Viewer viewer)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO viewers (uid, platform, nickname, first_seen, last_seen, interaction_count, notes)
VALUES (@uid, @platform, @nickname, @first_seen, @last_seen, @interaction_count, @notes)
ON CONFLICT(uid, platform) DO UPDATE SET
    nickname = @nickname,
    last_seen = @last_seen,
    interaction_count = @interaction_count,
    notes = @notes";
        cmd.Parameters.AddWithValue("@uid", viewer.Uid);
        cmd.Parameters.AddWithValue("@platform", viewer.Platform);
        cmd.Parameters.AddWithValue("@nickname", (object?)viewer.Nickname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@first_seen", (object?)viewer.FirstSeen ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_seen", (object?)viewer.LastSeen ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@interaction_count", viewer.InteractionCount);
        cmd.Parameters.AddWithValue("@notes", (object?)viewer.Notes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Record a viewer interaction: increment count and update last_seen.</summary>
    public async Task RecordInteractionAsync(string uid, string platform, string? nickname = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        var existing = await GetAsync(uid, platform).ConfigureAwait(false);

        if (existing is null)
        {
            await UpsertAsync(new Viewer
            {
                Uid = uid, Platform = platform, Nickname = nickname,
                FirstSeen = now, LastSeen = now, InteractionCount = 1
            }).ConfigureAwait(false);
        }
        else
        {
            existing.InteractionCount++;
            existing.LastSeen = now;
            if (nickname is not null) existing.Nickname = nickname;
            await UpsertAsync(existing).ConfigureAwait(false);
        }
    }

    public async Task<List<Viewer>> GetAllAsync()
    {
        var results = new List<Viewer>();
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT uid, platform, nickname, first_seen, last_seen, interaction_count, notes FROM viewers ORDER BY last_seen DESC";
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(new Viewer
            {
                Uid = reader.GetString(0),
                Platform = reader.GetString(1),
                Nickname = reader.IsDBNull(2) ? null : reader.GetString(2),
                FirstSeen = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastSeen = reader.IsDBNull(4) ? null : reader.GetString(4),
                InteractionCount = reader.GetInt32(5),
                Notes = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return results;
    }

    /// <summary>Check if a viewer is a "regular" (interaction count > threshold).</summary>
    public async Task<bool> IsRegularAsync(string uid, string platform, int threshold = 5)
    {
        var viewer = await GetAsync(uid, platform).ConfigureAwait(false);
        return viewer is not null && viewer.InteractionCount > threshold;
    }
}