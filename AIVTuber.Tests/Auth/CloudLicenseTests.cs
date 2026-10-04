using AIVTuber.Core.Auth;

namespace AIVTuber.Tests.Auth;

public sealed class CloudLicenseTests
{
    private static readonly DateTimeOffset ServerStart = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualClock _clock = new(ServerStart);
    private readonly FakeAuthApi _api = new();
    private readonly List<string> _revocations = [];

    private CloudLicense NewLicense()
    {
        var license = new CloudLicense(_api, "streamer-017", credentialRevision: 1, "0.36.0", _clock, startBackgroundLoop: false);
        license.Revoked += reason => _revocations.Add(reason);
        return license;
    }

    private AuthReply Ok(double leaseSeconds = 180, double accountSeconds = 86400, DateTimeOffset? serverNow = null)
    {
        var now = serverNow ?? ServerStart;
        return new AuthReply(AuthCode.Ok, "tok-1", "acc_1", now, now.AddSeconds(leaseSeconds), now.AddSeconds(accountSeconds), 60);
    }

    [Fact]
    public void BeforeLogin_CloudIsNotAllowed()
    {
        var license = NewLicense();

        Assert.False(license.IsAllowed);
        Assert.Equal(LicenseState.SignedOut, license.Snapshot.State);
    }

    [Fact]
    public async Task Login_GrantsAccessAndNewEpoch()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());

        var outcome = await license.LoginAsync("alice", "pw");

        Assert.True(outcome.Success);
        Assert.True(license.IsAllowed);
        Assert.Equal(1, license.Epoch);
        Assert.Equal(("alice", "streamer-017", 1), (_api.LastLogin!.Username, _api.LastLogin.ProfileId, _api.LastLogin.CredentialRevision));
    }

    [Theory]
    [InlineData(AuthCode.InvalidCredentials, "账号或密码错误")]
    [InlineData(AuthCode.Expired, "使用期限已结束")]
    [InlineData(AuthCode.Disabled, "已被停用")]
    [InlineData(AuthCode.ProfileMismatch, "需要使用对应的安装包")]
    [InlineData(AuthCode.CredentialRevoked, "已停止使用")]
    public async Task Login_Denied_KeepsCloudClosedWithVisibleReason(AuthCode code, string expectedText)
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(new AuthReply(code, ServerTime: ServerStart));

        var outcome = await license.LoginAsync("alice", "pw");

        Assert.False(outcome.Success);
        Assert.Contains(expectedText, outcome.Message);
        Assert.False(license.IsAllowed);
    }

    [Fact]
    public async Task Login_NetworkFailure_IsReportedAsNetworkNotAccountProblem()
    {
        var license = NewLicense();
        _api.Login = (_, _) => throw new AuthTransportException("connection refused");

        var outcome = await license.LoginAsync("alice", "pw");

        Assert.False(outcome.Success);
        Assert.Equal(CloudLicense.TransportFailureMessage, outcome.Message);
        Assert.False(license.IsAllowed);
    }

    [Fact]
    public async Task Heartbeat_ExtendsLeaseWithoutNewEpoch()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");

        _clock.Advance(TimeSpan.FromSeconds(170));
        _api.Heartbeat = (_, _) => Task.FromResult(Ok(serverNow: ServerStart.AddSeconds(170)));
        await license.HeartbeatOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(120));

        Assert.True(license.IsAllowed);
        Assert.Equal(1, license.Epoch);
        Assert.Equal("tok-1", _api.LastHeartbeatToken);
    }

    [Fact]
    public async Task Heartbeat_ExplicitDenial_RevokesImmediatelyOnce()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");

        _api.Heartbeat = (_, _) => Task.FromResult(new AuthReply(AuthCode.Disabled, ServerTime: ServerStart));
        await license.HeartbeatOnceAsync();
        await license.HeartbeatOnceAsync();

        Assert.False(license.IsAllowed);
        Assert.Equal(2, license.Epoch);
        Assert.Contains("已被停用", Assert.Single(_revocations));
        Assert.Equal(LicenseState.Denied, license.Snapshot.State);
    }

    [Fact]
    public async Task NetworkLoss_KeepsOldLeaseOnlyUntilItExpires()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");
        _api.Heartbeat = (_, _) => throw new AuthTransportException("timeout");

        _clock.Advance(TimeSpan.FromSeconds(100));
        await license.HeartbeatOnceAsync();
        license.EnforceExpiry();
        Assert.True(license.IsAllowed);
        Assert.Empty(_revocations);

        _clock.Advance(TimeSpan.FromSeconds(81));
        await license.HeartbeatOnceAsync();
        Assert.False(license.IsAllowed);
        license.EnforceExpiry();
        Assert.Equal(CloudLicense.VerificationLostMessage, Assert.Single(_revocations));
        Assert.Equal(LicenseStopReason.VerificationLost, license.Snapshot.Reason);
    }

    [Fact]
    public async Task ExpiringAccount_IsNotNetworkFailure()
    {
        // Account ends in 30 s, so the service caps the lease at 30 s; the next heartbeat is
        // due at 60 s. The network is fine throughout — the account simply ended.
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok(leaseSeconds: 30, accountSeconds: 30));
        await license.LoginAsync("alice", "pw");

        _clock.Advance(TimeSpan.FromSeconds(31));
        license.EnforceExpiry();

        Assert.False(license.IsAllowed);
        Assert.Equal(CloudLicense.AccountEndedMessage, Assert.Single(_revocations));
        Assert.DoesNotContain("网络", license.Snapshot.Message);
        Assert.Equal(LicenseStopReason.AccountExpired, license.Snapshot.Reason);
    }

    [Fact]
    public async Task ExpiringAccount_DetectionUsesMonotonicClockNotWallClock()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok(leaseSeconds: 180, accountSeconds: 86400));
        await license.LoginAsync("alice", "pw");
        _api.Heartbeat = (_, _) => throw new AuthTransportException("timeout");

        // Moving the PC's date past the account end must not turn a network outage into
        // an "account ended" message, and vice versa.
        _clock.SetWall(ServerStart.AddDays(5));
        _clock.AdvanceMonotonicOnly(TimeSpan.FromSeconds(181));
        license.EnforceExpiry();

        Assert.Equal(LicenseStopReason.VerificationLost, license.Snapshot.Reason);
    }

    [Fact]
    public async Task ServerSaysExpiredOnHeartbeat_IsReportedAsAccountEnded()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");
        _api.Heartbeat = (_, _) => Task.FromResult(new AuthReply(AuthCode.Expired, ServerTime: ServerStart));

        await license.HeartbeatOnceAsync();

        Assert.Equal(LicenseStopReason.AccountExpired, license.Snapshot.Reason);
        Assert.Equal(CloudLicense.AccountEndedMessage, license.Snapshot.Message);
    }

    [Fact]
    public async Task WallClockRollback_DoesNotExtendLease()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");

        _clock.SetWall(ServerStart.AddDays(-3));
        _clock.AdvanceMonotonicOnly(TimeSpan.FromSeconds(181));

        Assert.False(license.IsAllowed);
    }

    [Fact]
    public async Task LeaseFollowsServerDurationNotLocalWallClock()
    {
        // Local clock is a day ahead; the lease is measured as server duration on the monotonic clock.
        _clock.SetWall(ServerStart.AddDays(1));
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok(leaseSeconds: 30, accountSeconds: 30));
        await license.LoginAsync("alice", "pw");

        _clock.AdvanceMonotonicOnly(TimeSpan.FromSeconds(29));
        Assert.True(license.IsAllowed);
        _clock.AdvanceMonotonicOnly(TimeSpan.FromSeconds(2));
        Assert.False(license.IsAllowed);
    }

    [Fact]
    public async Task HeartbeatSucceedingAfterLogout_DoesNotRestoreAccess()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");
        var pending = new TaskCompletionSource<AuthReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Heartbeat = (_, _) => pending.Task;

        var heartbeat = license.HeartbeatOnceAsync();
        await license.LogoutAsync();
        pending.SetResult(Ok());
        await heartbeat;

        Assert.False(license.IsAllowed);
        Assert.Equal(LicenseState.SignedOut, license.Snapshot.State);
        Assert.Equal("tok-1", _api.LastLogoutToken);
    }

    [Fact]
    public async Task LoginResponseArrivingAfterLogout_IsDiscarded()
    {
        var license = NewLicense();
        var pending = new TaskCompletionSource<AuthReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Login = (_, _) => pending.Task;

        var login = license.LoginAsync("alice", "pw");
        await license.LogoutAsync();
        pending.SetResult(Ok());
        var outcome = await login;

        Assert.False(outcome.Success);
        Assert.False(license.IsAllowed);
    }

    [Fact]
    public async Task Logout_ClosesCloudBeforeServerCallCompletes()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");
        var serverLogout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.Logout = _ => serverLogout.Task;

        var logout = license.LogoutAsync();

        Assert.False(license.IsAllowed);
        Assert.Single(_revocations);
        serverLogout.SetResult();
        await logout;
    }

    [Fact]
    public async Task Relogin_AfterRevocation_StartsNewEpoch()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");
        await license.LogoutAsync();

        await license.LoginAsync("alice", "pw");

        Assert.True(license.IsAllowed);
        Assert.Equal(3, license.Epoch);
    }

    private AuthReply OkWithQuota(int quota = 3600, int remaining = 3600, DateTimeOffset? serverNow = null)
    {
        var now = serverNow ?? ServerStart;
        return new AuthReply(AuthCode.Ok, "tok-1", "acc_1", now, now.AddSeconds(180), now.AddDays(1), 60,
            new QuotaReply(quota, remaining, now.AddHours(14)));
    }

    private async Task<CloudLicense> LoginWithQuotaAsync(int remaining = 3600)
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(OkWithQuota(remaining: remaining));
        await license.LoginAsync("alice", "pw");
        return license;
    }

    [Fact]
    public async Task ActiveSeconds_CountOnlyWhileTheCompanionIsActive()
    {
        var license = await LoginWithQuotaAsync();

        _clock.Advance(TimeSpan.FromSeconds(90));
        license.SetCompanionActive(false);
        _clock.Advance(TimeSpan.FromSeconds(600));
        license.SetCompanionActive(true);
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(100, license.ActiveSeconds);
    }

    [Fact]
    public async Task ActiveSeconds_IgnoreSystemClockChanges()
    {
        var license = await LoginWithQuotaAsync();

        _clock.SetWall(ServerStart.AddDays(-3));
        _clock.AdvanceMonotonicOnly(TimeSpan.FromSeconds(30));

        Assert.Equal(30, license.ActiveSeconds);
    }

    [Fact]
    public async Task Heartbeat_ReportsTheCumulativeActiveSeconds()
    {
        var license = await LoginWithQuotaAsync();
        _api.Heartbeat = (_, _) => Task.FromResult(OkWithQuota(remaining: 3540, serverNow: ServerStart.AddSeconds(60)));
        _clock.Advance(TimeSpan.FromSeconds(60));

        await license.HeartbeatOnceAsync();

        Assert.Equal(60, _api.LastHeartbeatActiveSeconds);
    }

    [Fact]
    public async Task LocalRemaining_CountsDownOnlyWhileActive()
    {
        var license = await LoginWithQuotaAsync(remaining: 100);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(70, license.QuotaRemainingSeconds);

        license.SetCompanionActive(false);
        _clock.Advance(TimeSpan.FromSeconds(500));
        Assert.Equal(70, license.QuotaRemainingSeconds);
    }

    [Fact]
    public async Task Exhausted_FiresOnce_ThenRestoredWhenTheServerRefills()
    {
        var license = await LoginWithQuotaAsync(remaining: 50);
        var exhausted = 0;
        var restored = 0;
        license.QuotaExhausted += () => exhausted++;
        license.QuotaRestored += () => restored++;

        _clock.Advance(TimeSpan.FromSeconds(50));
        license.CheckQuota();
        license.CheckQuota();
        Assert.Equal(1, exhausted);

        license.SetCompanionActive(false);
        _api.Heartbeat = (_, _) => Task.FromResult(OkWithQuota(remaining: 3600, serverNow: ServerStart.AddHours(14)));
        await license.HeartbeatOnceAsync();

        Assert.Equal(1, restored);
        Assert.Equal(3600, license.QuotaRemainingSeconds);
    }

    [Fact]
    public async Task ExhaustedQuota_DoesNotRevokeTheLicense()
    {
        var license = await LoginWithQuotaAsync(remaining: 0);
        license.CheckQuota();

        Assert.True(license.IsAllowed);
        Assert.Empty(_revocations);
    }

    [Fact]
    public async Task ReplyWithoutQuota_MeansNoQuotaIsEnforced()
    {
        var license = NewLicense();
        _api.Login = (_, _) => Task.FromResult(Ok());
        await license.LoginAsync("alice", "pw");

        Assert.False(license.QuotaManaged);
        Assert.Equal(int.MaxValue, license.QuotaRemainingSeconds);
    }

    [Fact]
    public async Task Register_Success_BehavesLikeLogin()
    {
        var license = NewLicense();
        _api.Register = (_, _) => Task.FromResult(OkWithQuota());

        var outcome = await license.RegisterAsync("K7M4-9QXD", "alice", "password-1");

        Assert.True(outcome.Success);
        Assert.True(license.IsAllowed);
        Assert.Equal(1, license.Epoch);
        Assert.True(license.QuotaManaged);
        Assert.Equal(("K7M4-9QXD", "alice", "streamer-017", 1),
            (_api.LastRegister!.InviteCode, _api.LastRegister.Username, _api.LastRegister.ProfileId, _api.LastRegister.CredentialRevision));
    }

    [Theory]
    [InlineData(AuthCode.InvalidInvite, "邀请码不对或已经用过，请向发放者确认")]
    [InlineData(AuthCode.UsernameTaken, "这个账号名已被占用，换一个试试")]
    [InlineData(AuthCode.InvalidUsername, "账号名需要 3 到 20 位，只能用字母、数字、下划线和横线")]
    [InlineData(AuthCode.WeakPassword, "密码至少需要 8 位")]
    [InlineData(AuthCode.RateLimited, "注册请求过多，请稍后再试")]
    [InlineData(AuthCode.BadRequest, "注册信息不完整")]
    public async Task Register_Denied_ShowsPlainChinese(AuthCode code, string expected)
    {
        var license = NewLicense();
        _api.Register = (_, _) => Task.FromResult(new AuthReply(code, ServerTime: ServerStart));

        var outcome = await license.RegisterAsync("AAAA-BBBB", "alice", "password-1");

        Assert.False(outcome.Success);
        Assert.Equal(expected, outcome.Message);
        Assert.False(license.IsAllowed);
    }

    [Fact]
    public async Task Register_TransportFailure_IsNotAnAccountVerdict()
    {
        var license = NewLicense(); // FakeAuthApi.Register throws AuthTransportException by default

        var outcome = await license.RegisterAsync("AAAA-BBBB", "alice", "password-1");

        Assert.False(outcome.Success);
        Assert.Contains("请先尝试用刚才的账号和密码登录", outcome.Message);
    }

    [Fact]
    public void IsNoExpiry_Boundary()
    {
        Assert.True(CloudLicense.IsNoExpiry(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(CloudLicense.IsNoExpiry(new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero)));
    }
}

internal sealed class FakeAuthApi : IAuthApi
{
    public Func<AuthLoginRequest, CancellationToken, Task<AuthReply>> Login { get; set; } =
        (_, _) => throw new AuthTransportException("no login stub");
    public Func<string, CancellationToken, Task<AuthReply>> Heartbeat { get; set; } =
        (_, _) => throw new AuthTransportException("no heartbeat stub");
    public Func<string, Task> Logout { get; set; } = _ => Task.CompletedTask;
    public AuthLoginRequest? LastLogin { get; private set; }
    public string? LastHeartbeatToken { get; private set; }
    public string? LastLogoutToken { get; private set; }

    Task<AuthReply> IAuthApi.LoginAsync(AuthLoginRequest request, CancellationToken ct)
    {
        LastLogin = request;
        return Login(request, ct);
    }

    public long LastHeartbeatActiveSeconds { get; private set; }
    public Func<AuthRegisterRequest, CancellationToken, Task<AuthReply>> Register { get; set; } =
        (_, _) => throw new AuthTransportException("no register stub");
    public AuthRegisterRequest? LastRegister { get; private set; }

    Task<AuthReply> IAuthApi.RegisterAsync(AuthRegisterRequest request, CancellationToken ct)
    {
        LastRegister = request;
        return Register(request, ct);
    }

    Task<AuthReply> IAuthApi.HeartbeatAsync(string token, string profileId, long activeSeconds, CancellationToken ct)
    {
        LastHeartbeatToken = token;
        LastHeartbeatActiveSeconds = activeSeconds;
        return Heartbeat(token, ct);
    }

    Task IAuthApi.LogoutAsync(string token, CancellationToken ct)
    {
        LastLogoutToken = token;
        return Logout(token);
    }
}
