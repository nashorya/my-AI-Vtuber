using AIVTuber.Core.Memory;

namespace AIVTuber.Tests;

public sealed class SqliteUpgradeFixtureTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"aivtuber_legacy_memory_{Guid.NewGuid():N}.db");
    private MemoryDb _database = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Memory",
            "legacy-memory-v9.db");
        File.Copy(fixturePath, _databasePath);

        _database = new MemoryDb(_databasePath);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LegacySchema_RemainsReadableAfterUpgrade()
    {
        using var command = _database.GetConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('viewers', 'facts', 'sessions', 'conversations')";

        Assert.Equal(4L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LegacyViewer_CanBeReadAndWrittenBack()
    {
        var repository = new ViewerRepository(_database);

        var viewer = await repository.GetAsync("legacy-viewer", "bilibili");
        Assert.NotNull(viewer);
        Assert.Equal("Old Friend", viewer.Nickname);
        Assert.Equal(7, viewer.InteractionCount);

        await repository.RecordInteractionAsync("legacy-viewer", "bilibili", "Returning Friend");
        viewer = await repository.GetAsync("legacy-viewer", "bilibili");

        Assert.NotNull(viewer);
        Assert.Equal("Returning Friend", viewer.Nickname);
        Assert.Equal(8, viewer.InteractionCount);
    }

    [Fact]
    public async Task LegacyFact_CanBeReadAndNewFactWritten()
    {
        var repository = new FactRepository(_database);

        var legacyFacts = await repository.SearchAsync("喜欢猫", "legacy-viewer", 5);
        Assert.Contains(legacyFacts, item => item.fact.Id == "legacy-fact" && item.fact.Content == "观众喜欢猫");

        await repository.InsertAsync(new Fact
        {
            Id = "new-fact",
            SubjectUid = "legacy-viewer",
            Content = "观众也喜欢狗",
            Importance = 4
        });

        var facts = await repository.GetAllAsync();
        Assert.Contains(facts, fact => fact.Id == "new-fact" && fact.Importance == 4);
    }
}
