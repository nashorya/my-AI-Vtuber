using AIVTuber.Core.Bot;
using AIVTuber.Core.LiveStream;

namespace AIVTuber.Tests;

public class IdentityPromptTests
{
    [Fact]
    public void FormatTurn_LabelsThreeIdentities()
    {
        var text = IdentityPrompt.FormatTurn([
            new TalkLine(TalkIdentity.Self, "小明", "在吗", "u-self"),
            new TalkLine(TalkIdentity.Opponent, "笑笑", "在的", "u-opp"),
            new TalkLine(TalkIdentity.Danmaku, "路人", "加油", "u-d"),
        ]);
        Assert.Contains("使用者（小明）：在吗", text);
        Assert.Contains("对方主播（笑笑）：在的", text);
        Assert.Contains("直播间弹幕（路人）：加油", text);
    }

    [Fact]
    public void ResolveOpponent_PrefersLiveWhenConfigEmpty()
    {
        Assert.Equal("笑笑", IdentityPrompt.ResolveOpponentName("", new PkOpponent { Username = "笑笑" }));
        Assert.Equal("手动名", IdentityPrompt.ResolveOpponentName("手动名", new PkOpponent { Username = "笑笑" }));
    }

    [Fact]
    public void ProtocolAppendix_NamesIdentities_WithoutOutputMarks()
    {
        var p = IdentityPrompt.ProtocolAppendix("小明", "笑笑", "直播间弹幕");
        Assert.Contains("小明", p);
        Assert.Contains("笑笑", p);
        Assert.DoesNotContain("【PASS】", p);
        Assert.DoesNotContain("每轮只选择一种输出", p);
        Assert.DoesNotContain("必须使用全角括号", p);
    }
}
