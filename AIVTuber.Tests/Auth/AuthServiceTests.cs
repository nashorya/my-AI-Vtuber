using AIVTuber.AuthServer;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests.Auth;

public sealed class AuthServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"auth-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new(Start);
    private readonly AuthStore _store;
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _store = AuthStore.Open(_dbPath);
        _service = new AuthService(_store, _clock, new AuthServerOptions { LeaseSeconds = 180, HeartbeatSeconds = 60 });
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private LoginRequest Req(string password = "pw-alice-1", string profile = "streamer-017", int revision = 1)
        => new("alice", password, profile, "0.36.0", revision);

    [Fact]
    public void Login_ReturnsLeaseCappedByAccountExpiry()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddSeconds(100));

        var result = _service.Login(Req(), "client-a");

        Assert.Equal(AuthStatus.Ok, result.Status);
        Assert.False(string.IsNullOrEmpty(result.SessionToken));
        Assert.Equal(Start, result.ServerTime);
        Assert.Equal(Start.AddSeconds(100), result.LeaseValidUntil);
        Assert.Equal(Start.AddSeconds(100), result.AccountValidUntil);
    }

    [Fact]
    public void Login_LeaseIsShortWhenAccountValidForLong()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(30));

        var result = _service.Login(Req(), "client-a");

        Assert.Equal(Start.AddSeconds(180), result.LeaseValidUntil);
        Assert.Equal(60, result.HeartbeatSeconds);
    }

    [Theory]
    [InlineData("wrong-password", "alice")]
    [InlineData("pw-alice-1", "nobody")]
    public void Login_BadCredentials_AreIndistinguishable(string password, string username)
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));

        var result = _service.Login(new LoginRequest(username, password, "streamer-017", "0.36.0", 1), "client-a");

        Assert.Equal(AuthStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionToken);
    }

    [Fact]
    public void Login_ExpiredAccount_IsRejected()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddSeconds(-1));

        Assert.Equal(AuthStatus.Expired, _service.Login(Req(), "client-a").Status);
    }

    [Fact]
    public void Login_DisabledAccount_IsRejected()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        _service.SetEnabled("alice", false);

        Assert.Equal(AuthStatus.Disabled, _service.Login(Req(), "client-a").Status);
    }

    [Fact]
    public void Login_WithAnotherStreamersPackage_ReportsProfileMismatch()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));

        Assert.Equal(AuthStatus.ProfileMismatch, _service.Login(Req(profile: "streamer-018"), "client-a").Status);
    }

    [Fact]
    public void Login_WithRevokedCredentialRevision_IsRejected()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        _service.SetMinCredentialRevision("alice", 3);

        Assert.Equal(AuthStatus.CredentialRevoked, _service.Login(Req(revision: 2), "client-a").Status);
        Assert.Equal(AuthStatus.Ok, _service.Login(Req(revision: 3), "client-a").Status);
    }

    [Fact]
    public void Database_DoesNotStorePasswordOrSessionTokenInPlainText()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        var token = _service.Login(Req(), "client-a").SessionToken!;
        _store.Dispose();
        SqliteConnection.ClearAllPools();

        var bytes = File.ReadAllText(_dbPath, System.Text.Encoding.Latin1);
        Assert.DoesNotContain("pw-alice-1", bytes);
        Assert.DoesNotContain(token, bytes);
    }

    [Fact]
    public void Heartbeat_RenewsLeaseFromServerTime()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        var token = _service.Login(Req(), "client-a").SessionToken!;
        _clock.Advance(TimeSpan.FromSeconds(60));

        var result = _service.Heartbeat(token, "streamer-017");

        Assert.Equal(AuthStatus.Ok, result.Status);
        Assert.Equal(Start.AddSeconds(60 + 180), result.LeaseValidUntil);
    }

    [Fact]
    public void Heartbeat_AfterDisable_IsDeniedImmediately()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        var token = _service.Login(Req(), "client-a").SessionToken!;

        _service.SetEnabled("alice", false);

        Assert.Equal(AuthStatus.Disabled, _service.Heartbeat(token, "streamer-017").Status);
    }

    [Fact]
    public void Heartbeat_AfterAccountExpires_IsDenied()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddSeconds(90));
        var token = _service.Login(Req(), "client-a").SessionToken!;
        _clock.Advance(TimeSpan.FromSeconds(91));

        Assert.Equal(AuthStatus.Expired, _service.Heartbeat(token, "streamer-017").Status);
    }

    [Fact]
    public void Extending_Validity_TakesEffectOnNextHeartbeat_WithoutNewLogin()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddSeconds(90));
        var token = _service.Login(Req(), "client-a").SessionToken!;

        _service.SetValidUntil("alice", Start.AddDays(10));
        _clock.Advance(TimeSpan.FromSeconds(120));

        var result = _service.Heartbeat(token, "streamer-017");
        Assert.Equal(AuthStatus.Ok, result.Status);
        Assert.Equal(Start.AddDays(10), result.AccountValidUntil);
    }

    [Fact]
    public void Logout_RevokesSession()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        var token = _service.Login(Req(), "client-a").SessionToken!;

        _service.Logout(token);

        Assert.Equal(AuthStatus.SessionRevoked, _service.Heartbeat(token, "streamer-017").Status);
    }

    [Fact]
    public void Heartbeat_WithUnknownToken_IsInvalidSession()
    {
        Assert.Equal(AuthStatus.InvalidSession, _service.Heartbeat("not-a-token", "streamer-017").Status);
    }

    [Fact]
    public void ResetPassword_RevokesExistingSessions()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        var token = _service.Login(Req(), "client-a").SessionToken!;

        _service.SetPassword("alice", "pw-alice-2");

        Assert.Equal(AuthStatus.SessionRevoked, _service.Heartbeat(token, "streamer-017").Status);
        Assert.Equal(AuthStatus.InvalidCredentials, _service.Login(Req(), "client-a").Status);
        Assert.Equal(AuthStatus.Ok, _service.Login(Req(password: "pw-alice-2"), "client-a").Status);
    }

    [Fact]
    public void RepeatedFailures_AreRateLimited_UntilWindowPasses()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));
        for (var i = 0; i < 5; i++)
            Assert.Equal(AuthStatus.InvalidCredentials, _service.Login(Req(password: "bad"), "client-a").Status);

        Assert.Equal(AuthStatus.RateLimited, _service.Login(Req(), "client-a").Status);

        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(AuthStatus.Ok, _service.Login(Req(), "client-a").Status);
    }

    [Fact]
    public void CreateAccount_RejectsDuplicateUsername()
    {
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(1));

        Assert.Throws<InvalidOperationException>(() =>
            _service.CreateAccount("alice", "other", "streamer-099", Start.AddDays(1)));
    }
}
