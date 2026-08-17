using Microsoft.Data.Sqlite;

namespace AIVTuber.Core.Memory;

/// <summary>
/// SQLite database manager for the memory system.
/// Automatically creates tables on first connection.
/// Tables: viewers, facts, sessions, conversations, pk_matches, pk_turns.
/// </summary>
public sealed class MemoryDb : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly object _lock = new();

    public MemoryDb(string databasePath = "memory.db")
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    /// <summary>Initializes the database and creates tables if they don't exist.</summary>
    public async Task InitializeAsync()
    {
        SqliteConnection connection;
        lock (_lock)
        {
            if (_connection is not null)
                throw new InvalidOperationException("Database is already initialized.");

            connection = new SqliteConnection(_connectionString);
            _connection = connection;
        }

        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await CreateTablesAsync().ConfigureAwait(false);
        }
        catch
        {
            lock (_lock) { _connection = null; }
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Gets an open connection. Must call InitializeAsync first.</summary>
    public SqliteConnection GetConnection()
    {
        lock (_lock)
        {
            if (_connection is null)
                throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");
            return _connection;
        }
    }

    private async Task CreateTablesAsync()
    {
        var sql = @"
CREATE TABLE IF NOT EXISTS viewers (
    uid TEXT NOT NULL,
    platform TEXT NOT NULL,
    nickname TEXT,
    first_seen TEXT,
    last_seen TEXT,
    interaction_count INTEGER DEFAULT 0,
    notes TEXT,
    PRIMARY KEY (uid, platform)
);

CREATE TABLE IF NOT EXISTS facts (
    id TEXT PRIMARY KEY,
    subject_uid TEXT,
    content TEXT NOT NULL,
    importance INTEGER DEFAULT 3,
    expires TEXT DEFAULT 'stable',
    embedding BLOB,
    created_at TEXT,
    last_accessed TEXT,
    access_count INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS sessions (
    id TEXT PRIMARY KEY,
    started_at TEXT,
    ended_at TEXT,
    summary TEXT
);

CREATE TABLE IF NOT EXISTS conversations (
    id TEXT PRIMARY KEY,
    session_id TEXT,
    role TEXT,
    content TEXT,
    speaker_uid TEXT,
    timestamp TEXT
);

CREATE TABLE IF NOT EXISTS pk_matches (
    match_id TEXT PRIMARY KEY,
    opponent_uid TEXT,
    opponent_name TEXT,
    started_at TEXT NOT NULL,
    ended_at TEXT,
    status TEXT NOT NULL DEFAULT 'active'
);

CREATE TABLE IF NOT EXISTS pk_turns (
    id TEXT PRIMARY KEY,
    match_id TEXT NOT NULL,
    opponent_uid TEXT,
    opponent_name TEXT,
    opponent_text TEXT NOT NULL,
    assistant_text TEXT NOT NULL,
    source TEXT NOT NULL,
    ts TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_facts_subject ON facts(subject_uid);
CREATE INDEX IF NOT EXISTS idx_facts_importance ON facts(importance);
CREATE INDEX IF NOT EXISTS idx_conversations_session ON conversations(session_id);
CREATE INDEX IF NOT EXISTS idx_pk_turns_opponent ON pk_turns(opponent_uid);
CREATE INDEX IF NOT EXISTS idx_pk_turns_match ON pk_turns(match_id);
";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Standalone FTS for later BM25-style recall (kept in sync by PkTurnRepository).
        try
        {
            using var fts = _connection.CreateCommand();
            fts.CommandText = @"
CREATE VIRTUAL TABLE IF NOT EXISTS pk_turns_fts USING fts5(
    turn_id UNINDEXED, opponent_text, assistant_text, opponent_name
);";
            await fts.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[Memory] pk_turns_fts unavailable: {ex.Message}");
        }
    }

    public void Dispose()
    {
        SqliteConnection? connection;
        lock (_lock)
        {
            connection = _connection;
            _connection = null;
        }

        connection?.Close();
        connection?.Dispose();
    }
}
