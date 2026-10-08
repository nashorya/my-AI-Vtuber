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
        Assert.Equal(IdentityPrompt.InvitationPolicyV2, nextHistory[^1].Content);
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

    [Fact]
    public async Task OwnPastReplies_ReachTheModelInTheReplyProtocol_NotAsPlainText()
    {
        // Plain-text assistant turns taught the model to answer in plain text, which the v2
        // parser then dropped (fail closed) — the AI looked like it ignored people.
        var config = new AppConfig();
        await using var runtime = new BotRuntime(config, Path.GetTempPath());
        var conversation = new ConversationManager(config.Llm);
        SetField(runtime, "_conversation", conversation);

        Dispatch(runtime, Accept(runtime, conversation, new TalkLine(TalkIdentity.Self, "纳什", "大肥鱼摇摇脑袋", null)));
        Commit(runtime, new ClassifiedReply(ReplyKind.Speak, "好呀，老板叫我摇我就摇。", "", []));
        Commit(runtime, new ClassifiedReply(ReplyKind.Speak, "像不像一条真的鱼？", "", []));

        var next = new TalkLine(TalkIdentity.Opponent, "花花", "你为什么要用女生的？", null);
        var history = runtime.BuildTurnHistory([next], IdentityPrompt.FormatTurn([next]));

        var reply = Assert.Single(history, m => m.Role == MessageRole.Assistant);
        Assert.Equal(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"好呀，老板叫我摇我就摇。\"}\n" +
            "{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"像不像一条真的鱼？\"}\n" +
            "{\"v\":2,\"type\":\"end\"}",
            reply.Content);
        // Memory extraction still reads what was actually said.
        Assert.Contains(conversation.GetPersistableHistory(), m => m.Content == "好呀，老板叫我摇我就摇。");
    }

    [Fact]
    public async Task OwnPastReplies_AreShownWithTheTagsTheModelWrote()
    {
        // Replayed without their [emotion:] tags, past turns taught the model to stop writing them.
        var config = new AppConfig();
        await using var runtime = new BotRuntime(config, Path.GetTempPath());
        var conversation = new ConversationManager(config.Llm);
        SetField(runtime, "_conversation", conversation);

        Dispatch(runtime, Accept(runtime, conversation, new TalkLine(TalkIdentity.Self, "纳什", "大肥鱼笑一个", null)));
        Commit(runtime, new ClassifiedReply(ReplyKind.Speak, "好呀。", "", [], Written: "好呀[emotion:happy]。"));
        // A later piece of the same segment (Cortico split it) adds nothing to the replay.
        Commit(runtime, new ClassifiedReply(ReplyKind.Speak, "嘿嘿。", "", [], Written: ""));

        var next = new TalkLine(TalkIdentity.Self, "纳什", "再来一个", null);
        var history = runtime.BuildTurnHistory([next], IdentityPrompt.FormatTurn([next]));

        var reply = Assert.Single(history, m => m.Role == MessageRole.Assistant);
        Assert.Equal(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"好呀[emotion:happy]。\"}\n" +
            "{\"v\":2,\"type\":\"end\"}",
            reply.Content);
        // Memory extraction still reads only what was said.
        Assert.Contains(conversation.GetPersistableHistory(), m => m.Content == "好呀。");
    }

    [Fact]
    public async Task PassAndThought_AreRecordedRightAfterTheirTurn_AndNeverPersisted()
    {
        var config = new AppConfig();
        await using var runtime = new BotRuntime(config, Path.GetTempPath());
        var conversation = new ConversationManager(config.Llm);
        SetField(runtime, "_conversation", conversation);

        Dispatch(runtime, Accept(runtime, conversation, new TalkLine(TalkIdentity.Self, "纳什", "怎么就跟我一模一样了？", null)));
        // A line heard while the model was still deciding belongs after the decision.
        var heardMeanwhile = Accept(runtime, conversation, new TalkLine(TalkIdentity.Opponent, "花花", "真的很像啊", null));
        Commit(runtime, new ClassifiedReply(ReplyKind.Pass, "", "", []));

        Dispatch(runtime, heardMeanwhile, Accept(runtime, conversation, new TalkLine(TalkIdentity.Opponent, "花花", "地摊上买的", null)));
        Commit(runtime, new ClassifiedReply(ReplyKind.InnerThought, "", "记录：人类好奇怪", []));

        var next = new TalkLine(TalkIdentity.Self, "纳什", "大肥鱼你说呢？", null);
        var history = runtime.BuildTurnHistory([next], IdentityPrompt.FormatTurn([next]))
            .Where(m => m.Role != MessageRole.System).Select(m => m.Content).ToList();

        Assert.Equal(
        [
            "使用者（纳什）：怎么就跟我一模一样了？",
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"pass\"}\n{\"v\":2,\"type\":\"end\"}",
            "对方主播（花花）：真的很像啊",
            "对方主播（花花）：地摊上买的",
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"thought\",\"text\":\"记录：人类好奇怪\"}\n{\"v\":2,\"type\":\"end\"}",
        ], history);
        Assert.DoesNotContain(conversation.GetPersistableHistory(), m => m.Role == MessageRole.Assistant);
    }

    /// <summary>Accepts a line; returns it bound to its history message, as the gate hands it on.</summary>
    private static TalkLine Accept(BotRuntime runtime, ConversationManager conversation, TalkLine line)
    {
        runtime.AcceptTalkLine(line);
        return line with { HistoryMessage = conversation.GetHistory()[^1] };
    }

    /// <summary>Dispatches lines as the current turn, as HandleTurnReadyAsync does.</summary>
    private static void Dispatch(BotRuntime runtime, params TalkLine[] lines)
    {
        runtime.BuildTurnHistory(lines, IdentityPrompt.FormatTurn(lines));
        SetField(runtime, "_pendingTurnLines", (IReadOnlyList<TalkLine>)lines);
    }

    private static void Commit(BotRuntime runtime, ClassifiedReply reply) =>
        typeof(BotRuntime).GetMethod("CommitReply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [reply]);

    private static void SetField(BotRuntime runtime, string name, object value) =>
        typeof(BotRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);
}
