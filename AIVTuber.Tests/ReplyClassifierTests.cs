using AIVTuber.Core.Bot;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class ReplyClassifierTests
{
    [Fact]
    public void Pass_IsExactFullwidthMark()
    {
        var r = ReplyClassifier.Classify("【PASS】");
        Assert.Equal(ReplyKind.Pass, r.Kind);
        Assert.Equal("", r.Spoken);
        Assert.Equal("", r.Thought);
    }

    [Fact]
    public void Pass_IgnoresSurroundingWhitespace()
    {
        var r = ReplyClassifier.Classify("  【PASS】\n");
        Assert.Equal(ReplyKind.Pass, r.Kind);
    }

    [Fact]
    public void InnerThought_FullwidthParensOnly()
    {
        var r = ReplyClassifier.Classify("（他们在聊游戏，没点我）");
        Assert.Equal(ReplyKind.InnerThought, r.Kind);
        Assert.Equal("他们在聊游戏，没点我", r.Thought);
        Assert.Equal("", r.Spoken);
    }

    [Fact]
    public void Speak_NormalLine_StripsControlTagsAfterProtocol()
    {
        var r = ReplyClassifier.Classify("你好啊[emotion:neutral]");
        Assert.Equal(ReplyKind.Speak, r.Kind);
        Assert.Equal("你好啊", r.Spoken);
        Assert.Contains(r.StagedControls, c => c.Contains("emotion:neutral"));
    }

    [Fact]
    public void Classify_DoesNotRunActionStripBeforeProtocol()
    {
        // Existing ActionTextRegex would wipe both （心里话） and 【PASS】.
        Assert.Equal("", LlmClient.StripActionText("【PASS】").Trim());
        Assert.Equal("", LlmClient.StripActionText("（他们在聊游戏）").Trim());

        Assert.Equal(ReplyKind.Pass, ReplyClassifier.Classify("【PASS】").Kind);
        Assert.Equal(ReplyKind.InnerThought, ReplyClassifier.Classify("（他们在聊游戏）").Kind);
    }

    [Fact]
    public void HalfwidthParens_AreNotInnerThought()
    {
        var r = ReplyClassifier.Classify("你好 (whisper)");
        Assert.Equal(ReplyKind.Speak, r.Kind);
        Assert.Equal("你好", r.Spoken);
    }

    [Fact]
    public void Pass_MixedWithBody_IsInvalid()
    {
        Assert.Equal(ReplyKind.Invalid, ReplyClassifier.Classify("好的【PASS】").Kind);
    }

    [Fact]
    public void SpeakMixedWithThought_IsInvalid()
    {
        Assert.Equal(ReplyKind.Invalid, ReplyClassifier.Classify("等一下哈（这人好急）").Kind);
    }

    [Fact]
    public void EmptyThought_IsInvalid()
    {
        Assert.Equal(ReplyKind.Invalid, ReplyClassifier.Classify("（）").Kind);
    }

    [Fact]
    public void UnclosedThought_IsInvalid()
    {
        Assert.Equal(ReplyKind.Invalid, ReplyClassifier.Classify("（还没说完").Kind);
    }

    [Fact]
    public void AsciiPassAlias_IsNotPass()
    {
        var r = ReplyClassifier.Classify("[PASS]");
        Assert.NotEqual(ReplyKind.Pass, r.Kind);
    }
}
