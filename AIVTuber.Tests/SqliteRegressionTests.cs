using AIVTuber.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests;

public sealed class SqliteRegressionTests
{
    [Fact]
    public async Task InitializeAsync_OpensLegacyDatabaseWithoutLosingViewerOrFactData()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await CreateLegacyDatabaseAsync(dbPath);

            using var db = new MemoryDb(dbPath);
            await db.InitializeAsync();

            var viewer = await new ViewerRepository(db).GetAsync("legacy-user", "bilibili");
            var facts = await new FactRepository(db).SearchAsync("喜欢猫", "legacy-user", 5);

            Assert.NotNull(viewer);
            Assert.Equal("旧观众", viewer.Nickname);
            Assert.Equal(7, viewer.InteractionCount);
            var fact = Assert.Single(facts).fact;
            Assert.Equal("legacy-fact", fact.Id);
            Assert.Equal("legacy-user 喜欢猫", fact.Content);
            Assert.Equal(4, fact.Importance);
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task Dispose_ReleasesDatabaseFilesForImmediateDeletion()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            var db = new MemoryDb(dbPath);
            await db.InitializeAsync();

            await new ViewerRepository(db).RecordInteractionAsync(
                "disposal-user", "bilibili", "待删除观众");

            db.Dispose();

            File.Delete(dbPath);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    private static string CreateDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"aivtuber_sqlite_regression_{Guid.NewGuid():N}.db");

    private static async Task CreateLegacyDatabaseAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE viewers (
                uid TEXT NOT NULL,
                platform TEXT NOT NULL,
                nickname TEXT,
                first_seen TEXT,
                last_seen TEXT,
                interaction_count INTEGER DEFAULT 0,
                notes TEXT,
                PRIMARY KEY (uid, platform)
            );
            CREATE TABLE facts (
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
            INSERT INTO viewers
                (uid, platform, nickname, first_seen, last_seen, interaction_count, notes)
            VALUES
                ('legacy-user', 'bilibili', '旧观众', '2025-01-01', '2025-01-02', 7, '保留');
            INSERT INTO facts
                (id, subject_uid, content, importance, expires, created_at, last_accessed, access_count)
            VALUES
                ('legacy-fact', 'legacy-user', 'legacy-user 喜欢猫', 4, 'stable',
                 '2025-01-01', '2025-01-02', 2);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static void DeleteDatabaseFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, $"{dbPath}-shm", $"{dbPath}-wal" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
