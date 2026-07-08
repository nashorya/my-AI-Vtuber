using Microsoft.Data.Sqlite;

namespace AIVTuber.Core.Memory;

/// <summary>A fact stored in the memory system.</summary>
public sealed class Fact
{
    public string Id { get; set; } = string.Empty;
    public string? SubjectUid { get; set; }
    public string Content { get; set; } = string.Empty;
    public int Importance { get; set; } = 3;
    public string Expires { get; set; } = "stable";
    public float[]? Embedding { get; set; }
    public string? CreatedAt { get; set; }
    public string? LastAccessed { get; set; }
    public int AccessCount { get; set; }
    /// <summary>LLM-suggested relation to an existing fact, e.g., "duplicate:xxx" or "conflict:xxx".</summary>
    public string? RelationToOld { get; set; }
}

/// <summary>
/// Fact repository with InsertOrMerge (duplicate/conflict detection),
/// scoring-based retrieval (similarity + recency + frequency).
/// </summary>
public sealed class FactRepository
{
    private readonly MemoryDb _db;
    private readonly EmbeddingEngine? _embeddingEngine;
    private const float DuplicateThreshold = 0.92f;
    private const float ConflictThreshold = 0.6f;

    public FactRepository(MemoryDb db, EmbeddingEngine? embeddingEngine = null)
    {
        _db = db;
        _embeddingEngine = embeddingEngine;
    }

    /// <summary>
    /// Insert with duplicate/conflict detection.
    /// similarity > 0.92: duplicate (boost old fact weight).
    /// LLM-marked conflict with similarity > 0.6: replace old content.
    /// Otherwise: insert as new.
    /// </summary>
    public async Task InsertOrMergeAsync(Fact fact)
    {
        if (_embeddingEngine is not null && fact.Embedding is null && !string.IsNullOrEmpty(fact.Content))
            fact.Embedding = _embeddingEngine.Encode(fact.Content);

        var candidates = await SearchAsync(fact.Content, fact.SubjectUid, 5).ConfigureAwait(false);

        Fact? duplicate = null;
        Fact? conflict = null;

        foreach (var (candidate, similarity) in candidates)
        {
            if (similarity > DuplicateThreshold) { duplicate = candidate; break; }

            if (fact.RelationToOld is not null && fact.RelationToOld.StartsWith("conflict:"))
            {
                var conflictId = fact.RelationToOld["conflict:".Length..];
                if (candidate.Id == conflictId && similarity > ConflictThreshold)
                    conflict = candidate;
            }
        }

        if (duplicate is not null)
        {
            await UpdateWeightAsync(duplicate.Id, 1).ConfigureAwait(false);
            return;
        }
        if (conflict is not null)
        {
            await UpdateContentAsync(conflict.Id, fact.Content, fact.Importance).ConfigureAwait(false);
            return;
        }
        await InsertAsync(fact).ConfigureAwait(false);
    }

    public async Task InsertAsync(Fact fact)
    {
        if (string.IsNullOrEmpty(fact.Id)) fact.Id = Guid.NewGuid().ToString("N");
        fact.CreatedAt ??= DateTime.UtcNow.ToString("o");
        fact.LastAccessed ??= DateTime.UtcNow.ToString("o");

        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO facts (id, subject_uid, content, importance, expires, embedding, created_at, last_accessed, access_count)
VALUES (@id, @subject_uid, @content, @importance, @expires, @embedding, @created_at, @last_accessed, @access_count)";
        cmd.Parameters.AddWithValue("@id", fact.Id);
        cmd.Parameters.AddWithValue("@subject_uid", (object?)fact.SubjectUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@content", fact.Content);
        cmd.Parameters.AddWithValue("@importance", fact.Importance);
        cmd.Parameters.AddWithValue("@expires", fact.Expires);
        cmd.Parameters.AddWithValue("@embedding", fact.Embedding is not null ? (object)EmbeddingEngine.EncodeToBytes(fact.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", (object?)fact.CreatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_accessed", (object?)fact.LastAccessed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@access_count", fact.AccessCount);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Search facts by content similarity, optionally filtered by subject.</summary>
    public async Task<List<(Fact fact, float similarity)>> SearchAsync(string query, string? subjectUid, int topK)
    {
        var queryEmb = _embeddingEngine?.Encode(query);
        var results = new List<(Fact fact, float similarity)>();
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = subjectUid is not null
            ? "SELECT id,subject_uid,content,importance,expires,embedding,created_at,last_accessed,access_count FROM facts WHERE subject_uid=@suid"
            : "SELECT id,subject_uid,content,importance,expires,embedding,created_at,last_accessed,access_count FROM facts";
        if (subjectUid is not null) cmd.Parameters.AddWithValue("@suid", subjectUid);

        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var fact = ReadFact(reader);
            var sim = queryEmb is not null && fact.Embedding is not null
                ? EmbeddingEngine.CosineSimilarity(queryEmb, fact.Embedding) : StringSimilarity(query, fact.Content);
            results.Add((fact, sim));
        }
        return results.OrderByDescending(r => Score(r.similarity, r.fact)).Take(topK).ToList();
    }

    public async Task UpdateContentAsync(string factId, string newContent, int newImportance)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE facts SET content=@c,importance=@i,last_accessed=@la WHERE id=@id";
        cmd.Parameters.AddWithValue("@c", newContent);
        cmd.Parameters.AddWithValue("@i", newImportance);
        cmd.Parameters.AddWithValue("@la", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", factId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<List<Fact>> GetAllAsync()
    {
        var results = new List<Fact>();
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,subject_uid,content,importance,expires,embedding,created_at,last_accessed,access_count FROM facts ORDER BY last_accessed DESC";
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            results.Add(ReadFact(reader));
        return results;
    }

    public async Task DeleteAsync(string factId)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM facts WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", factId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task UpdateWeightAsync(string factId, int delta)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE facts SET access_count=access_count+@d,last_accessed=@la WHERE id=@id";
        cmd.Parameters.AddWithValue("@d", delta);
        cmd.Parameters.AddWithValue("@la", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", factId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Combined scoring: similarity*0.6 + recency_decay*0.2 + log(freq)*0.2.</summary>
    internal static float Score(float similarity, Fact fact)
    {
        var daysSince = fact.LastAccessed is not null
            ? (DateTime.UtcNow - DateTime.Parse(fact.LastAccessed)).TotalDays : 30.0;
        var decay = MathF.Exp(-0.01f * (float)daysSince);
        var freq = MathF.Log(fact.AccessCount + 1);
        return similarity * 0.6f + decay * 0.2f + freq * 0.2f;
    }

    private static Fact ReadFact(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        SubjectUid = reader.IsDBNull(1) ? null : reader.GetString(1),
        Content = reader.GetString(2),
        Importance = reader.GetInt32(3),
        Expires = reader.IsDBNull(4) ? "stable" : reader.GetString(4),
        Embedding = reader.IsDBNull(5) ? null : EmbeddingEngine.DecodeFromBytes(reader.GetFieldValue<byte[]>(5)),
        CreatedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
        LastAccessed = reader.IsDBNull(7) ? null : reader.GetString(7),
        AccessCount = reader.GetInt32(8)
    };

    private static float StringSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var common = a.Intersect(b).Count();
        return (float)(2.0 * common) / (a.Length + b.Length);
    }
}