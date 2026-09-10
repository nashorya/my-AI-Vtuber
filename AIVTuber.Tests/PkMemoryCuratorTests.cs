using System.Runtime.CompilerServices;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Memory;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class PkMatchBufferTests
{
    private static PkOpponent Opponent() => new()
    {
        Uid = "u1",
        Username = "对面A",
        FollowerCount = 10,
        RoomId = 1,
    };

    [Fact]
    public void PairsOpponentSpeechWithAssistantReply()
    {
        var buf = new PkMatchBuffer();
        buf.Start(Opponent());
        buf.NoteOpponentSpeech("你粉丝才这点啊");
        buf.NoteAssistantChunk("嘴硬倒是挺练的");
        buf.NoteAssistantChunk("。");
        buf.EndAssistantReply();

        var snap = buf.Snapshot();
        Assert.Single(snap);
        Assert.Equal("你粉丝才这点啊", snap[0].OpponentText);
        Assert.Equal("嘴硬倒是挺练的。", snap[0].AssistantText);
        Assert.Equal(0, snap[0].Index);
    }

    [Fact]
    public void IgnoresOpponentOnlyWithoutAssistant()
    {
        var buf = new PkMatchBuffer();
        buf.Start(Opponent());
        buf.NoteOpponentSpeech("啊哈哈");
        buf.NoteOpponentSpeech("第二句");
        buf.NoteAssistantChunk("回第二句");
        buf.EndAssistantReply();

        var snap = buf.Snapshot();
        Assert.Single(snap);
        Assert.Equal("第二句", snap[0].OpponentText);
    }
}

public class PkMemoryCuratorTests : IAsyncLifetime
{
    private MemoryDb _db = null!;
    private string _dbPath = null!;
    private PkTurnRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pk_curator_{Guid.NewGuid():N}.db");
        _db = new MemoryDb(_dbPath);
        await _db.InitializeAsync();
        _repo = new PkTurnRepository(_db);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public void ParseKeepIndices_ReadsJsonArray()
    {
        var keep = PkMemoryCurator.ParseKeepIndices("""{"keep":[0,2],"reason":"交锋"}""", 3);
        Assert.NotNull(keep);
        Assert.Equal(new HashSet<int> { 0, 2 }, keep);
    }

    [Fact]
    public void ParseKeepIndices_BadJson_ReturnsNull()
    {
        Assert.Null(PkMemoryCurator.ParseKeepIndices("not json", 2));
    }

    [Fact]
    public void ParseKeepIndices_ReadsFencedJson()
    {
        var keep = PkMemoryCurator.ParseKeepIndices("好的\n```json\n{\"keep\":[1],\"reason\":\"x\"}\n```", 3);
        Assert.Equal(new HashSet<int> { 1 }, keep);
    }

    [Fact]
    public void FallbackKeep_FiltersShortPairs()
    {
        var turns = new List<BufferedPkTurn>
        {
            new(0, "啊", "嗯", "loopback", "t0"),
            new(1, "你粉丝才这点啊", "嘴硬倒是挺练的。", "loopback", "t1"),
        };
        var keep = PkMemoryCurator.FallbackKeep(turns);
        Assert.Equal(new HashSet<int> { 1 }, keep);
    }

    [Fact]
    public void FallbackKeep_SkipsPassAndThought()
    {
        var turns = new List<BufferedPkTurn>
        {
            new(0, "哈喽", "【PASS】", "loopback", "t0"),
            new(1, "在吗", "（先听着）", "loopback", "t1"),
            new(2, "你怎么看", "我觉得可以。", "loopback", "t2"),
        };
        var keep = PkMemoryCurator.FallbackKeep(turns);
        Assert.Equal(new HashSet<int> { 2 }, keep);
    }

    [Fact]
    public async Task PersistMatch_WithFakeLlm_StoresOnlyKeptPairs()
    {
        var llm = new ScriptedLlm("""{"keep":[1],"reason":"挑衅"}""");
        var curator = new PkMemoryCurator(llm, _repo);
        const string matchId = "m1";
        await _repo.InsertMatchAsync(new PkMatch
        {
            MatchId = matchId,
            OpponentUid = "u1",
            OpponentName = "对面A",
            StartedAt = DateTime.UtcNow.ToString("o"),
        });

        var turns = new List<BufferedPkTurn>
        {
            new(0, "哈喽哈喽", "你好你好", "loopback", DateTime.UtcNow.ToString("o")),
            new(1, "你粉丝才这点啊", "嘴硬倒是挺练的。", "loopback", DateTime.UtcNow.ToString("o")),
        };

        await curator.PersistMatchAsync(matchId, "u1", "对面A", turns);

        var stored = await _repo.ListByOpponentAsync("u1");
        Assert.Single(stored);
        Assert.Equal("你粉丝才这点啊", stored[0].OpponentText);
        Assert.Equal("嘴硬倒是挺练的。", stored[0].AssistantText);
    }

    [Fact]
    public async Task PersistMatch_BadLlm_FallsBackToLengthFilter()
    {
        var llm = new ScriptedLlm("???");
        var curator = new PkMemoryCurator(llm, _repo);
        const string matchId = "m2";
        await _repo.InsertMatchAsync(new PkMatch
        {
            MatchId = matchId,
            OpponentUid = "u2",
            OpponentName = "B",
            StartedAt = DateTime.UtcNow.ToString("o"),
        });

        var turns = new List<BufferedPkTurn>
        {
            new(0, "啊", "嗯", "loopback", DateTime.UtcNow.ToString("o")),
            new(1, "来真的吗兄弟", "来就来。", "loopback", DateTime.UtcNow.ToString("o")),
        };

        await curator.PersistMatchAsync(matchId, "u2", "B", turns);
        var stored = await _repo.ListByOpponentAsync("u2");
        Assert.Single(stored);
        Assert.Equal("来真的吗兄弟", stored[0].OpponentText);
    }

    [Fact]
    public async Task PersistMatch_WithoutPriorInsertMatch_StillWritesTurns()
    {
        var llm = new ScriptedLlm("""{"keep":[0],"reason":"ok"}""");
        var curator = new PkMemoryCurator(llm, _repo);
        var turns = new List<BufferedPkTurn>
        {
            new(0, "你过来啊", "来就来。", "loopback", DateTime.UtcNow.ToString("o")),
        };

        await curator.PersistMatchAsync("late-end", "u3", "C", turns);
        var stored = await _repo.ListByOpponentAsync("u3");
        Assert.Single(stored);
        Assert.Equal("你过来啊", stored[0].OpponentText);
    }

    private sealed class ScriptedLlm(string response) : ILlmClient
    {
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return response;
            OnSentenceReady?.Invoke(this, response);
        }
    }
}
