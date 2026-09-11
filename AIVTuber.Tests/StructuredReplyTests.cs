using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public class StructuredReplyTests
{
    [Theory]
    [InlineData("pass")]
    [InlineData("PASS")]
    [InlineData("[PASS]")]
    [InlineData("【pass】")]
    [InlineData(" [ pass ]。 ")]
    [InlineData("[emotion:happy]PASS!")]
    [InlineData("（继续听）")]
    [InlineData("{\"respond\":false,\"speech\":\"不应被读出的文字\"}")]
    [InlineData("{\"respond\":true,\"speech\":\"PASS\"}")]
    public void SilentReplies_NeverHaveSpokenText(string raw)
    {
        var reply = ReplyClassifier.ClassifyStructured(raw);
        Assert.Equal(ReplyKind.Pass, reply.Kind);
        Assert.Empty(reply.Spoken);
        Assert.Empty(reply.StagedControls);
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("{\"respond\":true}")]
    [InlineData("{\"respond\":\"true\",\"speech\":\"你好\"}")]
    [InlineData("{\"respond\":true,\"speech\":\"你好\"")]
    [InlineData("{\"respond\":true,\"speech\":\"你好\",\"reason\":\"被叫到\"}")]
    [InlineData("{\"respond\":false,\"respond\":true,\"speech\":\"你好\"}")]
    [InlineData("{\"respond\":true,\"speech\":null}")]
    [InlineData("{\"respond\":true,\"speech\":\"好的[PASS]\"}")]
    public void MalformedOrUnstructuredReplies_FailClosed(string raw)
    {
        Assert.Equal(ReplyKind.Invalid, ReplyClassifier.ClassifyStructured(raw).Kind);
    }

    [Theory]
    [InlineData("{\"respond\":true,\"speech\":\"我觉得可以。[emotion:happy]\"}")]
    [InlineData("```json\n{\"respond\":true,\"speech\":\"我觉得可以。[emotion:happy]\"}\n```")]
    public void PositiveReply_ExtractsOnlyDialogueAndControls(string raw)
    {
        var reply = ReplyClassifier.ClassifyStructured(raw);
        Assert.Equal(ReplyKind.Speak, reply.Kind);
        Assert.Equal("我觉得可以。", reply.Spoken);
        Assert.Equal("[emotion:happy]", Assert.Single(reply.StagedControls));
    }

    [Fact]
    public void OrdinaryDiscussionOfPass_IsNotSuppressed()
    {
        var reply = ReplyClassifier.ClassifyStructured("{\"respond\":true,\"speech\":\"这里的 pass 是跳过的意思。\"}");
        Assert.Equal(ReplyKind.Speak, reply.Kind);
    }

    [Theory]
    [InlineData("先别回答", true)]
    [InlineData("大肥鱼，等等！", true)]
    [InlineData("她昨天说先别回答", false)]
    [InlineData("等等是什么意思？", false)]
    public void StopRequest_RequiresAStandaloneCommand(string text, bool expected)
    {
        Assert.Equal(expected, IdentityPrompt.IsStopRequest(text, ["大肥鱼"]));
    }
}
