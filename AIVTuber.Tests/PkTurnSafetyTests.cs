using System.Runtime.CompilerServices;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Memory;

namespace AIVTuber.Tests;

public sealed class PkTurnSafetyTests
{
    [Fact]
    public async Task LaterForcedExtraction_DoesNotReceivePassOrDerivedReply()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pk-pass-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new MemoryDb(path);
            await db.InitializeAsync();
            var conversation = new ConversationManager(new LlmConfig { MaxHistoryTokens = 1000 });
            conversation.AddUserMessage("秘密目的地是杭州", persistEligible: false);
            conversation.AddUserMessage("我喜欢猫");
            conversation.AddAssistantMessage("去杭州时可以看猫");
            var llm = new RecordingLlm();
            var extractor = new MemoryExtractor(llm, new FactRepository(db), new MemoryConfig(), conversation);
            await extractor.ExtractFactsAsync();
            Assert.Contains("我喜欢猫", llm.Prompt);
            Assert.DoesNotContain("杭州", llm.Prompt);
        }
        finally { File.Delete(path); }
    }

    private sealed class RecordingLlm : ILlmClient
    {
        public string Prompt { get; private set; } = "";
        public event EventHandler<string>? OnSentenceReady { add { } remove { } }
        public event EventHandler<string>? OnEmotionDetected { add { } remove { } }
        public event EventHandler<string>? OnActionDetected { add { } remove { } }
        public event EventHandler<string>? OnPoseDetected { add { } remove { } }
        public async IAsyncEnumerable<string> StreamAsync(List<Message> history, string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Prompt = string.Join("\n", history.Select(m => m.Content));
            yield return "{\"facts\":[]}";
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void PassInput_RemainsInContextButNotExtraction_EvenAfterSummary()
    {
        var conversation = new ConversationManager(new LlmConfig { MaxHistoryTokens = 1000 });
        conversation.AddUserMessage("我想去杭州", persistEligible: false);
        conversation.AddUserMessage("你觉得那里怎么样");
        conversation.AddAssistantMessage("可以一起讨论行程");
        Assert.Contains(conversation.BuildMessages(), m => m.Content == "我想去杭州");
        Assert.DoesNotContain(conversation.GetPersistableHistory(), m => m.Content.Contains("杭州"));
        conversation.ReplaceWithSummary("使用者想去杭州");
        Assert.Contains(conversation.BuildMessages(), m => m.Content.Contains("杭州"));
        Assert.DoesNotContain(conversation.GetPersistableHistory(), m => m.Content.Contains("杭州"));
    }

    [Fact]
    public void OldMatchReply_CannotBeAddedToNewOpponentBuffer()
    {
        var buffer = new PkMatchBuffer();
        var oldId = buffer.Start(new PkOpponent { Uid = "a", Username = "甲", RoomId = 1 });
        var captured = buffer.CaptureIdentity();
        var newId = buffer.Start(new PkOpponent { Uid = "b", Username = "乙", RoomId = 2 });
        Assert.Equal("a", captured.Opponent!.Uid);
        Assert.Equal(oldId, captured.MatchId);
        Assert.False(buffer.TryAddPair(captured.MatchId, "旧语音", "旧回复"));
        Assert.Empty(buffer.Snapshot());
        Assert.True(buffer.TryAddPair(newId, "新语音", "新回复"));
        Assert.Equal("新语音", Assert.Single(buffer.Snapshot()).OpponentText);
    }
}
