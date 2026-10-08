using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
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
    public void InvitationPolicy_AllowsAvatarOnSameEnvelope()
    {
        Assert.Contains("avatar", IdentityPrompt.InvitationPolicy);
        Assert.Contains("headYaw", IdentityPrompt.InvitationPolicy);
        Assert.Contains("bodyYaw", IdentityPrompt.InvitationPolicy);
        Assert.Contains("中立正对镜头", IdentityPrompt.InvitationPolicy);
        Assert.DoesNotContain("motion", IdentityPrompt.InvitationPolicy);
        Assert.DoesNotContain("恰好包含", IdentityPrompt.InvitationPolicy);
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

    [Fact]
    public void InvitationPolicy_FollowsModeAndCompanionLevel()
    {
        var pk = IdentityPrompt.InvitationPolicyFor(mode: InteractionModes.Pk);
        var solo = IdentityPrompt.InvitationPolicyFor(mode: InteractionModes.Solo);
        Assert.Contains("PK 模式", pk);
        Assert.Contains("AI 读播模式", solo);
        Assert.Contains("伴播模式", IdentityPrompt.InvitationPolicyFor(mode: InteractionModes.Companion, companionLevel: 2));
        Assert.Contains("大多数都可以自然接一句", IdentityPrompt.InvitationPolicyFor(mode: InteractionModes.Companion, companionLevel: 3));
        // The quietest companion level is the established listen-first policy.
        Assert.Equal(IdentityPrompt.InvitationPolicyV2, IdentityPrompt.InvitationPolicyFor(mode: InteractionModes.Companion, companionLevel: 1));
        // Out-of-range levels clamp; the old "normal" mode and unknown values read as companion.
        Assert.Equal(IdentityPrompt.InvitationPolicyFor(companionLevel: 3), IdentityPrompt.InvitationPolicyFor(companionLevel: 9));
        Assert.Equal(IdentityPrompt.InvitationPolicyFor(mode: InteractionModes.Companion), IdentityPrompt.InvitationPolicyFor(mode: "normal"));
        foreach (var policy in new[] { pk, solo })
            Assert.Contains("{\"v\":2,\"type\":\"decision\",\"mode\":\"pass\"}", policy);
        // Danmaku are answered in every mode and at every companion level.
        foreach (var policy in new[] { pk, solo, IdentityPrompt.InvitationPolicyV2,
            IdentityPrompt.InvitationPolicyFor(companionLevel: 2), IdentityPrompt.InvitationPolicyFor(companionLevel: 3) })
            Assert.Contains("每条弹幕都要回应", policy);
        // Cortico swaps only the output format, in every mode.
        var corticoPk = IdentityPrompt.InvitationPolicyFor(cortico: true, mode: InteractionModes.Pk);
        Assert.Contains("PK 模式", corticoPk);
        Assert.Contains("不要输出 control 行", corticoPk);
        Assert.DoesNotContain("控制走 control 行", corticoPk);
    }

    [Fact]
    public void InteractionModes_CycleThroughAllThree()
    {
        Assert.Equal(InteractionModes.Pk, InteractionModes.Next(InteractionModes.Companion));
        Assert.Equal(InteractionModes.Solo, InteractionModes.Next(InteractionModes.Pk));
        Assert.Equal(InteractionModes.Companion, InteractionModes.Next(InteractionModes.Solo));
        Assert.Equal(InteractionModes.Pk, InteractionModes.Next("normal"));
        var config = new InteractionConfig { Mode = "normal" };
        Assert.Equal(InteractionModes.Companion, config.CurrentMode);
        Assert.False(config.IsPkMode);
    }
}
