using System.Reflection;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class ListeningContextTests
{
    [Fact]
    public async Task BothSpeakers_AreRememberedBeforeAnyReply_AndReachLaterInvitationOnce()
    {
        var config = new AppConfig();
        await using var runtime = new BotRuntime(config, Path.GetTempPath());
        var conversation = new ConversationManager(config.Llm);
        var now = DateTime.UtcNow;
        using var gate = new ConversationTurnGate(now: () => now);
        SetField(runtime, "_conversation", conversation);
        SetField(runtime, "_turnGate", gate);
        var turns = new List<IReadOnlyList<TalkLine>>();
        gate.TurnReady += turns.Add;

        runtime.AcceptTalkLine(new(TalkIdentity.Self, "纳什", "昨天火锅太辣了", null));
        runtime.AcceptTalkLine(new(TalkIdentity.Opponent, "朋友", "我喜欢清汤", null));
        Assert.Equal(2, conversation.GetHistory().Count); // before any model invocation
        now += TimeSpan.FromMilliseconds(400);
        gate.Tick();
        var first = turns[0];
        var history = runtime.BuildTurnHistory(first, IdentityPrompt.FormatTurn(first));
        Assert.DoesNotContain(history, m => m.Role == MessageRole.User); // current input isn't duplicated

        // Models may PASS, fail or return invalid JSON. No commit is needed to retain context.
        var revision = gate.ActiveTurnRevision;
        runtime.AcceptTalkLine(new(TalkIdentity.Self, "纳什", "大肥鱼，你觉得哪种好？", null));
        Assert.Equal(3, conversation.GetHistory().Count); // accepted while AI is busy
        Assert.Empty(conversation.GetPersistableHistory());
        now += TimeSpan.FromMilliseconds(400);
        gate.CompleteTurn(revision);
        var next = turns[1];
        var nextHistory = runtime.BuildTurnHistory(next, IdentityPrompt.FormatTurn(next));
        var userHistory = nextHistory.Where(m => m.Role == MessageRole.User).ToList();
        Assert.Equal(2, userHistory.Count);
        Assert.Contains("使用者（纳什）：昨天火锅太辣了", userHistory[0].Content);
        Assert.Contains("对方主播（朋友）：我喜欢清汤", userHistory[1].Content);
        Assert.Equal(IdentityPrompt.InvitationPolicy, nextHistory[^1].Content);
        Assert.Equal("大肥鱼，你觉得哪种好？", Assert.Single(next).Text);
    }

    [Fact]
    public async Task Stop_RetainsObservationsButClearsTheirQueuedReplies()
    {
        var config = new AppConfig();
        await using var runtime = new BotRuntime(config, Path.GetTempPath());
        var conversation = new ConversationManager(config.Llm);
        var now = DateTime.UtcNow;
        using var gate = new ConversationTurnGate(now: () => now);
        SetField(runtime, "_conversation", conversation);
        SetField(runtime, "_turnGate", gate);
        var turns = new List<IReadOnlyList<TalkLine>>();
        gate.TurnReady += turns.Add;
        runtime.AcceptTalkLine(new(TalkIdentity.Self, "搭档", "刚才说火锅", null));
        runtime.AcceptTalkLine(new(TalkIdentity.Self, "搭档", "先别回答", null));
        now += TimeSpan.FromSeconds(2);
        gate.Tick();
        Assert.Empty(turns);
        runtime.AcceptTalkLine(new(TalkIdentity.Self, "搭档", "现在说吧", null));
        now += TimeSpan.FromSeconds(1);
        gate.Tick();
        var history = runtime.BuildTurnHistory(turns[0], IdentityPrompt.FormatTurn(turns[0]));
        Assert.Contains(history, m => m.Content.Contains("刚才说火锅"));
        Assert.Contains(history, m => m.Content.Contains("先别回答"));
        Assert.Equal("现在说吧", Assert.Single(turns[0]).Text);
    }

    private static void SetField(BotRuntime runtime, string name, object value) =>
        typeof(BotRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);
}
