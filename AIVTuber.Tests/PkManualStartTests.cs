using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests;

public class PkManualStartTests
{
    private static BotRuntime Runtime() => new(new AppConfig(), Path.GetTempPath());

    private static PkOpponent Opponent(string name = "对面主播") => new()
    {
        Uid = "8739477", Username = name, FollowerCount = 2821200, RoomId = 545068,
    };

    [Fact]
    public void CurrentPkOpponent_StartsNull()
    {
        Assert.Null(Runtime().CurrentPkOpponent);
    }

    [Fact]
    public void AnnouncePkOpponent_RecordsOpponent()
    {
        var rt = Runtime();

        rt.AnnouncePkOpponent(Opponent("笑笑"));

        Assert.NotNull(rt.CurrentPkOpponent);
        Assert.Equal("笑笑", rt.CurrentPkOpponent!.Username);
    }

    [Fact]
    public void StartNewPk_ClearsPreviousOpponent()
    {
        // The whole point of the manual button: the AI must not keep referring to
        // whoever it was PK-ing last round.
        var rt = Runtime();
        rt.AnnouncePkOpponent(Opponent());

        rt.StartNewPk();

        Assert.Null(rt.CurrentPkOpponent);
    }

    [Fact]
    public void StartNewPk_OnUninitializedRuntime_DoesNotThrow()
    {
        // The button is reachable before the pipeline is started; it must no-op quietly
        // rather than take down the UI thread.
        var rt = Runtime();

        var ex = Record.Exception(rt.StartNewPk);

        Assert.Null(ex);
    }

    [Fact]
    public void AnnouncePkOpponent_OnUninitializedRuntime_DoesNotThrow()
    {
        var rt = Runtime();

        var ex = Record.Exception(() => rt.AnnouncePkOpponent(Opponent()));

        Assert.Null(ex);
    }

    [Fact]
    public void StartNewPk_IsIdempotent()
    {
        var rt = Runtime();

        rt.StartNewPk();
        var ex = Record.Exception(rt.StartNewPk);

        Assert.Null(ex);
        Assert.Null(rt.CurrentPkOpponent);
    }

    [Fact]
    public void MonitorViewModel_StartNewPk_ClearsRuntimeOpponent()
    {
        var rt = Runtime();
        var vm = new MonitorViewModel(rt, run => run());
        rt.AnnouncePkOpponent(Opponent());

        vm.StartNewPk();

        Assert.Null(rt.CurrentPkOpponent);
    }

    [Fact]
    public void PkManualTemplate_HasUsableDefault()
    {
        var template = new AppConfig().Input.PkManualTemplate;

        Assert.False(string.IsNullOrWhiteSpace(template));
        // No opponent is known in this path, so the template must not carry placeholders
        // that would render as literal braces.
        Assert.DoesNotContain("{uname}", template);
        Assert.DoesNotContain("{follower}", template);
    }
}
