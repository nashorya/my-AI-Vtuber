using AIVTuber.Core;
using AIVTuber.Core.Auth;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests.Auth;

public sealed class AccountViewModelTests
{
    private static readonly DateTimeOffset ServerStart = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeAuthApi _api = new();
    private readonly ManualClock _clock = new(ServerStart);

    private (AccountViewModel Vm, CloudLicense License) Create()
    {
        var license = new CloudLicense(_api, "streamer-017", 2, "0.36.0", _clock, startBackgroundLoop: false);
        return (new AccountViewModel(license, "streamer-017", "alice", run => run()), license);
    }

    private static AuthReply Ok() => new(AuthCode.Ok, "tok", "acc_1", ServerStart,
        ServerStart.AddSeconds(180), new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), 60);

    [Fact]
    public void StartsSignedOut_WithPrefilledAccount()
    {
        var (vm, _) = Create();

        Assert.False(vm.IsSignedIn);
        Assert.True(vm.ShowLogin);
        Assert.Equal("alice", vm.Username);
        Assert.Equal("streamer-017", vm.ProfileId);
    }

    [Fact]
    public async Task SuccessfulLogin_HidesLoginAndShowsExpiry()
    {
        var (vm, _) = Create();
        _api.Login = (_, _) => Task.FromResult(Ok());

        await vm.LoginAsync("pw");

        Assert.True(vm.IsSignedIn);
        Assert.False(vm.ShowLogin);
        Assert.Contains("2026-10-26", vm.ValidUntilText);
        Assert.Equal("", vm.ErrorText);
    }

    [Fact]
    public async Task FailedLogin_ShowsReason()
    {
        var (vm, _) = Create();
        _api.Login = (_, _) => Task.FromResult(new AuthReply(AuthCode.Expired, ServerTime: ServerStart));

        await vm.LoginAsync("pw");

        Assert.False(vm.IsSignedIn);
        Assert.Contains("使用期限已结束", vm.ErrorText);
    }

    [Fact]
    public async Task Revocation_ReturnsToLoginWithReason()
    {
        var (vm, license) = Create();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await vm.LoginAsync("pw");

        _api.Heartbeat = (_, _) => Task.FromResult(new AuthReply(AuthCode.Disabled, ServerTime: ServerStart));
        await license.HeartbeatOnceAsync();

        Assert.False(vm.IsSignedIn);
        Assert.True(vm.ShowLogin);
        Assert.Contains("已被停用", vm.ErrorText);
    }

    [Fact]
    public async Task Logout_ReturnsToLogin()
    {
        var (vm, _) = Create();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await vm.LoginAsync("pw");

        await vm.LogoutAsync();

        Assert.False(vm.IsSignedIn);
        Assert.True(vm.ShowLogin);
    }

    [Fact]
    public void Dismiss_KeepsSettingsReachableWhileSignedOut()
    {
        var (vm, _) = Create();

        vm.DismissLogin();

        Assert.False(vm.ShowLogin);
        Assert.False(vm.IsSignedIn);
    }

    [Fact]
    public void AppVersion_ComesFromAssembly()
    {
        Assert.Equal("0.36.0-rc.2", AppVersion.Current);
    }
}
