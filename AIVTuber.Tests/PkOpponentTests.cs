using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public class PkOpponentTests
{
    [Fact]
    public void TryParse_ValidPush_MapsAllFields()
    {
        const string json = """
            {"uid":"8739477","username":"老实憨厚的笑笑","follower":2821200,"roomid":545068}
            """;

        Assert.True(PkOpponent.TryParse(json, out var pk));
        Assert.Equal("8739477", pk!.Uid);
        Assert.Equal("老实憨厚的笑笑", pk.Username);
        Assert.Equal(2821200L, pk.FollowerCount);
        Assert.Equal(545068, pk.RoomId);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        Assert.False(PkOpponent.TryParse("{not json", out var pk));
        Assert.Null(pk);
    }

    [Fact]
    public void TryParse_EmptyBody_ReturnsFalse()
    {
        Assert.False(PkOpponent.TryParse("", out _));
    }

    [Fact]
    public void TryParse_AcceptsUsernameWithoutUid()
    {
        Assert.True(PkOpponent.TryParse(
            """{"username":"笑笑","follower":12,"roomid":545068}""", out var pk));
        Assert.Equal("笑笑", pk!.Username);
        Assert.Equal("", pk.Uid);
        Assert.Equal(545068, pk.RoomId);
    }

    [Fact]
    public void TryParse_AcceptsRoomIdOnly()
    {
        Assert.True(PkOpponent.TryParse("""{"roomid":1907447144}""", out var pk));
        Assert.Equal(1907447144, pk!.RoomId);
        Assert.Equal("对面主播", pk.Username);
        Assert.Equal("", pk.Uid);
    }

    [Fact]
    public void TryParse_EmptyIdentity_ReturnsFalse()
    {
        Assert.False(PkOpponent.TryParse("""{"follower":12}""", out _));
    }

    [Fact]
    public void TryParse_MissingFollower_DefaultsToZero()
    {
        const string json = """{"uid":"1","username":"x","roomid":2}""";

        Assert.True(PkOpponent.TryParse(json, out var pk));
        Assert.Equal(0L, pk!.FollowerCount);
    }

    [Fact]
    public void TryParse_MissingUsername_FallsBackToPlaceholder()
    {
        const string json = """{"uid":"1","follower":5,"roomid":2}""";

        Assert.True(PkOpponent.TryParse(json, out var pk));
        Assert.Equal("对面主播", pk!.Username);
    }

    [Fact]
    public void IsEndPush_RecognizesEndType()
    {
        Assert.True(PkOpponent.IsEndPush("""{"type":"end"}"""));
        Assert.True(PkOpponent.IsEndPush("""{"type":"END"}"""));
    }

    [Fact]
    public void IsEndPush_RejectsStartPayload()
    {
        Assert.False(PkOpponent.IsEndPush("""{"uid":"1","username":"x","follower":1,"roomid":2}"""));
        Assert.False(PkOpponent.TryParse("""{"type":"end"}""", out _));
    }
}

public class PkConfigDiffTests
{
    [Fact]
    public void PkNoticeChange_RestartsDanmakuBridge()
    {
        // PkNotice is read by the Python bridge at startup, so toggling it must respawn it.
        var candidate = new AppConfig();
        candidate.Bilibili.PkNotice = true;

        var change = ConfigDiff.Compute(new AppConfig(), candidate);

        Assert.Equal(RuntimeChange.RestartDanmaku, change);
        Assert.True(ConfigDiff.IsHeavy(change));
    }

    [Fact]
    public void PkTemplateChange_IsNotHeavy()
    {
        // The template is read per-event on the C# side; no module needs restarting.
        var candidate = new AppConfig();
        candidate.Input.PkTemplate = "（对手：{uname}）";

        var change = ConfigDiff.Compute(new AppConfig(), candidate);

        Assert.False(ConfigDiff.IsHeavy(change));
    }

    [Fact]
    public void PkNoticeDefaultsOff()
    {
        Assert.False(new AppConfig().Bilibili.PkNotice);
    }

    [Fact]
    public void PkTemplateHasUsernameAndFollowerPlaceholders()
    {
        var template = new AppConfig().Input.PkTemplate;
        Assert.Contains("{uname}", template);
        Assert.Contains("{follower}", template);
    }
}
