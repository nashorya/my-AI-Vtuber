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

    Task<AuthReply> IAuthApi.HeartbeatAsync(string token, string profileId, CancellationToken ct)
    {
        LastHeartbeatToken = token;
        return Heartbeat(token, ct);
    }

    Task IAuthApi.LogoutAsync(string token, CancellationToken ct)
    {
        LastLogoutToken = token;
        return Logout(token);
    }
}
