using Microsoft.Data.Sqlite;

namespace AIVTuber.Core.Memory;

/// <summary>One PK match row (lifecycle for a single opponent encounter).</summary>
public sealed class PkMatch
{
    public string MatchId { get; init; } = string.Empty;
    public string? OpponentUid { get; init; }
    public string? OpponentName { get; init; }
    public string StartedAt { get; init; } = string.Empty;
    public string? EndedAt { get; init; }
    public string Status { get; init; } = "active";
}

/// <summary>Original opponent utterance paired with the assistant reply at that time.</summary>
public sealed class PkTurn
{
    public string Id { get; init; } = string.Empty;
    public string MatchId { get; init; } = string.Empty;
    public string? OpponentUid { get; init; }
    public string? OpponentName { get; init; }
    public string OpponentText { get; init; } = string.Empty;
    public string AssistantText { get; init; } = string.Empty;
    public string Source { get; init; } = "loopback";
    public string Ts { get; init; } = string.Empty;
}

/// <summary>Persists PK dialogue pairs (original speech, not descriptive summaries).</summary>
public sealed class PkTurnRepository
{
    private readonly MemoryDb _db;
    private bool? _ftsAvailable;

    public PkTurnRepository(MemoryDb db) => _db = db;

    public async Task InsertMatchAsync(PkMatch match)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO pk_matches (match_id, opponent_uid, opponent_name, started_at, ended_at, status)
VALUES (@id, @uid, @name, @started, @ended, @status)
ON CONFLICT(match_id) DO UPDATE SET
    opponent_uid=excluded.opponent_uid,
    opponent_name=excluded.opponent_name,
    started_at=COALESCE(pk_matches.started_at, excluded.started_at)";
        cmd.Parameters.AddWithValue("@id", match.MatchId);
        cmd.Parameters.AddWithValue("@uid", (object?)match.OpponentUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", (object?)match.OpponentName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@started", match.StartedAt);
        cmd.Parameters.AddWithValue("@ended", (object?)match.EndedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", match.Status);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task EndMatchAsync(string matchId, string endedAt)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE pk_matches SET ended_at=@ended, status='ended' WHERE match_id=@id";
        cmd.Parameters.AddWithValue("@ended", endedAt);
        cmd.Parameters.AddWithValue("@id", matchId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task InsertTurnAsync(PkTurn turn)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO pk_turns (id, match_id, opponent_uid, opponent_name, opponent_text, assistant_text, source, ts)
VALUES (@id, @mid, @uid, @name, @opp, @asst, @src, @ts)";
        cmd.Parameters.AddWithValue("@id", turn.Id);
        cmd.Parameters.AddWithValue("@mid", turn.MatchId);
        cmd.Parameters.AddWithValue("@uid", (object?)turn.OpponentUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", (object?)turn.OpponentName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@opp", turn.OpponentText);
        cmd.Parameters.AddWithValue("@asst", turn.AssistantText);
        cmd.Parameters.AddWithValue("@src", turn.Source);
        cmd.Parameters.AddWithValue("@ts", turn.Ts);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        await TryInsertFtsAsync(turn).ConfigureAwait(false);
    }

    public async Task InsertTurnsAsync(IEnumerable<PkTurn> turns)
    {
        foreach (var turn in turns)
            await InsertTurnAsync(turn).ConfigureAwait(false);
    }

    public async Task<List<PkTurn>> ListAllAsync(int limit = 200)
    {
        var results = new List<PkTurn>();
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, match_id, opponent_uid, opponent_name, opponent_text, assistant_text, source, ts
FROM pk_turns ORDER BY ts DESC LIMIT @lim";
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            results.Add(ReadTurn(reader));
        return results;
    }

    public async Task DeleteTurnAsync(string turnId)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pk_turns WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", turnId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (_ftsAvailable == false) return;
        try
        {
            using var fts = conn.CreateCommand();
            fts.CommandText = "DELETE FROM pk_turns_fts WHERE turn_id=@id";
            fts.Parameters.AddWithValue("@id", turnId);
            await fts.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            _ftsAvailable = false;
        }
    }

    public async Task<List<PkTurn>> ListByOpponentAsync(string opponentUid, int limit = 50)
    {
        var results = new List<PkTurn>();
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, match_id, opponent_uid, opponent_name, opponent_text, assistant_text, source, ts
FROM pk_turns WHERE opponent_uid=@uid ORDER BY ts DESC LIMIT @lim";
        cmd.Parameters.AddWithValue("@uid", opponentUid);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            results.Add(ReadTurn(reader));
        return results;
    }

    private async Task TryInsertFtsAsync(PkTurn turn)
    {
        if (_ftsAvailable == false) return;
        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO pk_turns_fts (turn_id, opponent_text, assistant_text, opponent_name)
VALUES (@id, @opp, @asst, @name)";
            cmd.Parameters.AddWithValue("@id", turn.Id);
            cmd.Parameters.AddWithValue("@opp", turn.OpponentText);
            cmd.Parameters.AddWithValue("@asst", turn.AssistantText);
            cmd.Parameters.AddWithValue("@name", turn.OpponentName ?? "");
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            _ftsAvailable = true;
        }
        catch (SqliteException)
        {
            _ftsAvailable = false;
        }
    }

    private static PkTurn ReadTurn(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        MatchId = reader.GetString(1),
        OpponentUid = reader.IsDBNull(2) ? null : reader.GetString(2),
        OpponentName = reader.IsDBNull(3) ? null : reader.GetString(3),
        OpponentText = reader.GetString(4),
        AssistantText = reader.GetString(5),
        Source = reader.GetString(6),
        Ts = reader.GetString(7),
    };
}
