using AIVTuber.Core.Memory;
using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Obs;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests;

public class MemoryDbTests : IAsyncLifetime
{
    private MemoryDb _db = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_memory_{Guid.NewGuid():N}.db");
        _db = new MemoryDb(_dbPath);
        return _db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_CreatesTables()
    {
        // Verify tables exist by inserting and reading
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('viewers','facts','sessions','conversations')";
        var count = await cmd.ExecuteScalarAsync();
        Assert.Equal(4L, count);
    }

    [Fact]
    public async Task GetConnection_ReturnsOpenConnection()
    {
        var conn = _db.GetConnection();
        Assert.NotNull(conn);
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
    }
}

public class ViewerRepositoryTests : IAsyncLifetime
{
    private MemoryDb _db = null!;
    private ViewerRepository _repo = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_viewer_{Guid.NewGuid():N}.db");
        _db = new MemoryDb(_dbPath);
        _repo = new ViewerRepository(_db);
        return _db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpsertAsync_InsertsNewViewer()
    {
        var viewer = new Viewer { Uid = "12345", Platform = "bilibili", Nickname = "TestUser", InteractionCount = 1 };
        await _repo.UpsertAsync(viewer);
        var result = await _repo.GetAsync("12345", "bilibili");
        Assert.NotNull(result);
        Assert.Equal("TestUser", result.Nickname);
        Assert.Equal(1, result.InteractionCount);
    }

    [Fact]
    public async Task RecordInteractionAsync_CreatesNewViewer()
    {
        await _repo.RecordInteractionAsync("67890", "bilibili", "NewUser");
        var result = await _repo.GetAsync("67890", "bilibili");
        Assert.NotNull(result);
        Assert.Equal("NewUser", result.Nickname);
        Assert.Equal(1, result.InteractionCount);
    }

    [Fact]
    public async Task IsRegularAsync_ReturnsFalseForNewViewer()
    {
        Assert.False(await _repo.IsRegularAsync("99999", "bilibili"));
    }

    [Fact]
    public async Task IsRegularAsync_ReturnsTrueAfterThreshold()
    {
        for (int i = 0; i < 6; i++)
            await _repo.RecordInteractionAsync("11111", "bilibili");
        Assert.True(await _repo.IsRegularAsync("11111", "bilibili"));
    }
}

public class FactRepositoryTests : IAsyncLifetime
{
    private MemoryDb _db = null!;
    private FactRepository _repo = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_fact_{Guid.NewGuid():N}.db");
        _db = new MemoryDb(_dbPath);
        _repo = new FactRepository(_db);
        return _db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InsertAsync_InsertsNewFact()
    {
        var fact = new Fact { Id = "f1", SubjectUid = "u1", Content = "User likes cats", Importance = 3 };
        await _repo.InsertAsync(fact);
        var results = await _repo.SearchAsync("cats", "u1", 5);
        Assert.Single(results);
        Assert.Equal("User likes cats", results[0].fact.Content);
    }

    [Fact]
    public async Task InsertOrMergeAsync_NewFact_Inserts()
    {
        var fact = new Fact { SubjectUid = "u2", Content = "User plays guitar", Importance = 3 };
        await _repo.InsertOrMergeAsync(fact);
        var results = await _repo.SearchAsync("guitar", "u2", 5);
        Assert.Single(results);
    }

    [Fact]
    public async Task UpdateWeightAsync_IncrementsAccessCount()
    {
        var fact = new Fact { Id = "f2", Content = "Test fact", Importance = 3 };
        await _repo.InsertAsync(fact);
        await _repo.UpdateWeightAsync("f2", 2);
        var results = await _repo.SearchAsync("Test fact", null, 1);
        Assert.Equal(2, results[0].fact.AccessCount);
    }

    [Fact]
    public async Task UpdateContentAsync_ChangesContent()
    {
        var fact = new Fact { Id = "f3", Content = "Old content", Importance = 2 };
        await _repo.InsertAsync(fact);
        await _repo.UpdateContentAsync("f3", "New content", 4);
        var results = await _repo.SearchAsync("New content", null, 1);
        Assert.Equal("New content", results[0].fact.Content);
        Assert.Equal(4, results[0].fact.Importance);
    }

    [Fact]
    public void Score_CalculatesCorrectly()
    {
        var fact = new Fact { LastAccessed = DateTime.UtcNow.ToString("o"), AccessCount = 5 };
        var score = FactRepository.Score(0.8f, fact);
        Assert.True(score > 0);
        Assert.True(score > 0.3f); // Score can exceed 1.0 due to log frequency term
    }

    [Fact]
    public void EmbeddingEngine_EncodeDecodeBytes()
    {
        var original = new float[] { 0.1f, 0.5f, -0.3f, 1.0f };
        var bytes = EmbeddingEngine.EncodeToBytes(original);
        var decoded = EmbeddingEngine.DecodeFromBytes(bytes);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void EmbeddingEngine_CosineSimilarity_SameVector()
    {
        var v = new float[] { 0.5f, 0.5f, 0.5f };
        var sim = EmbeddingEngine.CosineSimilarity(v, v);
        Assert.True(Math.Abs(sim - 1.0f) < 0.001f);
    }

    [Fact]
    public void EmbeddingEngine_CosineSimilarity_Orthogonal()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        var sim = EmbeddingEngine.CosineSimilarity(a, b);
        Assert.True(Math.Abs(sim) < 0.001f);
    }
}

public class DanmakuSelectorTests
{
    [Fact]
    public void Enqueue_IncreasesCount()
    {
        var selector = new DanmakuSelector(8);
        selector.Enqueue(new Danmaku { Uid = "1", Content = "hello" });
        Assert.Equal(1, selector.QueueCount);
    }

    [Fact]
    public void Clear_EmptiesQueue()
    {
        var selector = new DanmakuSelector(8);
        selector.Enqueue(new Danmaku { Uid = "1", Content = "hello" });
        selector.Clear();
        Assert.Equal(0, selector.QueueCount);
    }

    [Fact]
    public void SetSpeaking_True_PausesSelection()
    {
        var selector = new DanmakuSelector(0); // 0 interval for testing
        selector.Enqueue(new Danmaku { Uid = "1", Content = "test" });
        selector.SetSpeaking(true);
        // Should not select when speaking
        // (interval check may prevent selection anyway)
    }

    [Fact]
    public async Task TrySelectNextAsync_SelectsQueuedDanmaku()
    {
        var selector = new DanmakuSelector(0);
        Danmaku? selected = null;
        selector.OnDanmakuSelected += (_, d) => selected = d;

        selector.Enqueue(new Danmaku { Uid = "1", Content = "你好" });
        await selector.TrySelectNextAsync();

        Assert.NotNull(selected);
        Assert.Equal("你好", selected!.Content);
        Assert.Equal(0, selector.QueueCount);
    }

    [Fact]
    public async Task TrySelectNextAsync_WhileSpeaking_DoesNotSelect()
    {
        var selector = new DanmakuSelector(0);
        bool fired = false;
        selector.OnDanmakuSelected += (_, _) => fired = true;

        selector.SetSpeaking(true);
        selector.Enqueue(new Danmaku { Uid = "1", Content = "你好" });
        await selector.TrySelectNextAsync();

        Assert.False(fired);
    }

    [Fact]
    public async Task TrySelectNextAsync_EmptyQueue_DoesNotSelect()
    {
        var selector = new DanmakuSelector(0);
        bool fired = false;
        selector.OnDanmakuSelected += (_, _) => fired = true;

        await selector.TrySelectNextAsync();

        Assert.False(fired);
    }

    [Fact]
    public async Task TrySelectNextAsync_PrefersQuestion()
    {
        // No ViewerRepo: question (+5) always outscores plain (0), even with the +0..2 random bonus.
        var selector = new DanmakuSelector(0);
        Danmaku? selected = null;
        selector.OnDanmakuSelected += (_, d) => selected = d;

        selector.Enqueue(new Danmaku { Uid = "a", Content = "随便说说" });
        selector.Enqueue(new Danmaku { Uid = "b", Content = "这是什么？" });
        await selector.TrySelectNextAsync();

        Assert.NotNull(selected);
        Assert.Equal("这是什么？", selected!.Content);
    }
}

public class ObsConfigTests
{
    [Fact]
    public void ObsConfig_DefaultValues()
    {
        var config = new ObsConfig();
        Assert.False(config.Enable);
        Assert.Equal("localhost", config.Host);
        Assert.Equal(4455, config.Port);
        Assert.Equal("AssistantText", config.AssistantTextComponent);
        Assert.Equal(50, config.TypewriterIntervalMs);
    }
}

public class BilibiliConfigTests
{
    [Fact]
    public void BilibiliConfig_DefaultValues()
    {
        var config = new BilibiliConfig();
        Assert.False(config.Enable);
        Assert.Equal(0, config.RoomId);
        Assert.Equal(19876, config.PushPort);
        Assert.Equal(8, config.SelectionIntervalSec);
        Assert.Equal("python", config.PythonPath);
    }
}

public class MemoryConfigTests
{
    [Fact]
    public void MemoryConfig_DefaultValues()
    {
        var config = new MemoryConfig();
        Assert.Equal("memory.db", config.DatabasePath);
        Assert.Equal(5, config.ExtractEveryNTurns);
        Assert.Equal("models/bge-small-zh", config.EmbeddingModelPath);
    }
}