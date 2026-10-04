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

    private static AuthReply OkWithQuota(DateTimeOffset validUntil) => new(AuthCode.Ok, "tok", "acc_1", ServerStart,
        ServerStart.AddSeconds(180), validUntil, 60, new QuotaReply(3600, 3600, ServerStart.AddHours(14)));

    [Fact]
    public async Task Register_PasswordMismatch_DoesNotCallTheServer()
    {
        var (vm, _) = Create();
        vm.Username = "newbie";

        await vm.RegisterAsync("K7M4-9QXD", "password-1", "password-2");

        Assert.Equal("两次输入的密码不一致", vm.ErrorText);
        Assert.Null(_api.LastRegister);
    }

    [Theory]
    [InlineData("", "newbie", "password-1", "请填写邀请码")]
    [InlineData("K7M4-9QXD", "ab", "password-1", "账号名需要 3 到 20 位，只能用字母、数字、下划线和横线")]
    [InlineData("K7M4-9QXD", "new bie", "password-1", "账号名需要 3 到 20 位，只能用字母、数字、下划线和横线")]
    [InlineData("K7M4-9QXD", "newbie", "1234567", "密码至少需要 8 位")]
    public async Task Register_BadInput_FailsLocally(string invite, string username, string password, string expected)
    {
        var (vm, _) = Create();
        vm.Username = username;

        await vm.RegisterAsync(invite, password, password);

        Assert.Equal(expected, vm.ErrorText);
        Assert.Null(_api.LastRegister);
    }

    [Fact]
    public async Task Register_Success_SignsInAndLeavesRegisterMode()
    {
        var (vm, _) = Create();
        _api.Register = (_, _) => Task.FromResult(OkWithQuota(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        vm.Username = "newbie";
        vm.ToggleRegister();
        Assert.True(vm.IsRegistering);

        await vm.RegisterAsync(" K7M4-9QXD ", "password-1", "password-1");

        Assert.True(vm.IsSignedIn);
        Assert.False(vm.ShowLogin);
        Assert.False(vm.IsRegistering);
        Assert.Equal("K7M4-9QXD", _api.LastRegister!.InviteCode);
        Assert.Equal("长期有效", vm.ValidUntilText);
    }

    [Fact]
    public async Task Register_ServerDenial_ShowsMessageAndStaysInRegisterMode()
    {
        var (vm, _) = Create();
        _api.Register = (_, _) => Task.FromResult(new AuthReply(AuthCode.InvalidInvite, ServerTime: ServerStart));
        vm.Username = "newbie";
        vm.ToggleRegister();

        await vm.RegisterAsync("K7M4-9QXD", "password-1", "password-1");

        Assert.False(vm.IsSignedIn);
        Assert.True(vm.IsRegistering);
        Assert.Equal("邀请码不对或已经用过，请向发放者确认", vm.ErrorText);
    }

    [Fact]
    public void ToggleRegister_FlipsModeAndClearsTheError()
    {
        var (vm, _) = Create();

        vm.ToggleRegister();
        Assert.True(vm.IsRegistering);
        vm.ToggleRegister();
        Assert.False(vm.IsRegistering);
        Assert.Equal("", vm.ErrorText);
    }

    [Fact]
    public async Task DatedAccount_StillShowsTheEndDate()
    {
        var (vm, _) = Create();
        _api.Login = (_, _) => Task.FromResult(Ok());

        await vm.LoginAsync("pw");

        Assert.StartsWith("有效至 ", vm.ValidUntilText);
    }
}
