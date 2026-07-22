using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class ActionStreamingTests
{
    [Fact]
    public void Parser_HandlesActionSplitAcrossEveryChunkBoundary()
    {
        const string input = "你好[action:wave]。";

        for (var split = 1; split < input.Length; split++)
        {
            var actions = new List<string>();
            var parser = new StreamingControlTagParser(actions.Add, _ => { });

            var clean = parser.Consume(input[..split]) +
                        parser.Consume(input[split..]) +
                        parser.Complete();

            Assert.Equal("你好。", clean);
            Assert.Equal(["wave"], actions);
        }
    }

    [Fact]
    public void Parser_EmitsMultipleDistinctActionsAndDeduplicatesRepeats()
    {
        var actions = new List<string>();
        var parser = new StreamingControlTagParser(actions.Add, _ => { });

        var clean = parser.Consume("[action:wave]你好[action:nod][action:WAVE]。") + parser.Complete();

        Assert.Equal("你好。", clean);
        Assert.Equal(["wave", "nod"], actions);
    }

    [Theory]
    [InlineData("你好[action:", "你好")]
    [InlineData("你好[action:wave", "你好")]
    [InlineData("你好[action:]。", "你好。")]
    [InlineData("你好[action=wave]。", "你好。")]
    [InlineData("你好[action wave]。", "你好。")]
    [InlineData("你好[action:wave\n继续。", "你好\n继续。")]
    public void Parser_DropsMalformedOrTruncatedActionTags(string input, string expected)
    {
        var actions = new List<string>();
        var parser = new StreamingControlTagParser(actions.Add, _ => { });

        var clean = parser.Consume(input) + parser.Complete();

        Assert.Equal(expected, clean);
        Assert.Empty(actions);
        Assert.DoesNotContain("[action", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_DropsOversizedActionWithoutLeakingItsPayload()
    {
        var parser = new StreamingControlTagParser(_ => { }, _ => { });
        var input = $"before[action:{new string('x', 300)}]after";

        var clean = parser.Consume(input) + parser.Complete();

        Assert.Equal("beforeafter", clean);
    }

    [Fact]
    public void Parser_PreservesOrdinaryBracketedText()
    {
        var parser = new StreamingControlTagParser(_ => { }, _ => { });

        var clean = parser.Consume("数组[0]与[a:b]。") + parser.Complete();

        Assert.Equal("数组[0]与[a:b]。", clean);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("[act")]
    public void Parser_PreservesIncompleteTextThatIsNotAControlTag(string input)
    {
        var parser = new StreamingControlTagParser(_ => { }, _ => { });

        Assert.Equal(input, parser.Consume(input) + parser.Complete());
    }

    [Fact]
    public void Parser_RemovesEmotionAndActionFromCleanOutput()
    {
        var actions = new List<string>();
        var emotions = new List<string>();
        var parser = new StreamingControlTagParser(actions.Add, emotions.Add);

        var clean = parser.Consume("[emo") + parser.Consume("tion:happy]你好[action:wave]。") + parser.Complete();

        Assert.Equal("你好。", clean);
        Assert.Equal(["wave"], actions);
        Assert.Equal(["happy"], emotions);
    }

    [Fact]
    public void Parser_EmitsPoseTagsAndStripsFromSpeechText()
    {
        var poses = new List<string>();
        var emotions = new List<string>();
        var parser = new StreamingControlTagParser(_ => { }, emotions.Add, poses.Add);

        var clean = parser.Consume("嗯[pose:tilt_left][emotion:shy]。") + parser.Complete();

        Assert.Equal("嗯。", clean);
        Assert.Equal(["tilt_left"], poses);
        Assert.Equal(["shy"], emotions);
    }
}
