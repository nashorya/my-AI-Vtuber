using Microsoft.Data.Sqlite;

namespace AIVTuber.Core.Memory;

/// <summary>
/// SQLite database manager for the memory system.
/// Automatically creates tables on first connection.
/// Tables: viewers, facts, sessions, conversations.
/// </summary>
public sealed class MemoryDb : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly object _lock = new();

    public MemoryDb(string databasePath = "memory.db")
    {
        _connectionString = $"Data Source={databasePath}";
    }

    /// <summary>Initializes the database and creates tables if they don't exist.</summary>
    public async Task InitializeAsync()
    {
        lock (_lock) { _connection = new SqliteConnection(_connectionString); }
        await _connection.OpenAsync().ConfigureAwait(false);

        await CreateTablesAsync().ConfigureAwait(false);
    }

    /// <summary>Gets an open connection. Must call InitializeAsync first.</summary>
    public SqliteConnection GetConnection()
    {
        if (_connection is null)
            throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");
        return _connection;
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

CREATE INDEX IF NOT EXISTS idx_facts_subject ON facts(subject_uid);
CREATE INDEX IF NOT EXISTS idx_facts_importance ON facts(importance);
CREATE INDEX IF NOT EXISTS idx_conversations_session ON conversations(session_id);
";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}