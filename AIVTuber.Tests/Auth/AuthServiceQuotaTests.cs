using AIVTuber.AuthServer;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests.Auth;

public sealed class AuthServiceQuotaTests : IDisposable
{
    // 2026-09-26 16:00 Beijing → quota day 2026-09-26, resets 2026-09-27 06:00 Beijing (= 22:00Z).
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"auth-quota-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new(Start);
    private readonly AuthStore _store;
    private readonly AuthService _service;

    public AuthServiceQuotaTests()
    {
        _store = AuthStore.Open(_db);
        _service = new AuthService(_store, _clock, new AuthServerOptions());
        _service.CreateAccount("alice", "pw-alice-1", "streamer-017", Start.AddDays(60));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private string LoginToken()
    {
        var login = _service.Login(new LoginRequest("alice", "pw-alice-1", "streamer-017", "0.36.0", 1), "t");
        Assert.Equal(AuthStatus.Ok, login.Status);
        return login.SessionToken!;
    }

    private AuthResult Beat(string token, long active) => _service.Heartbeat(token, "streamer-017", active);

    [Fact]
    public void Login_ReturnsDefaultQuotaAndResetTime()
    {
        var login = _service.Login(new LoginRequest("alice", "pw-alice-1", "streamer-017", "0.36.0", 1), "t");

        Assert.Equal(3600, login.QuotaSeconds);
        Assert.Equal(3600, login.QuotaRemainingSeconds);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero), login.QuotaResetsAt);
    }

    [Fact]
    public void Heartbeat_DeductsTheReportedDelta()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 55);

        Assert.Equal(AuthStatus.Ok, reply.Status);
        Assert.Equal(3545, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void Heartbeat_RepeatedCumulativeValue_DoesNotDeductTwice()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 55);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 55); // retry of the same report

        Assert.Equal(3545, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void Heartbeat_DecreasingCumulativeValue_CreditsNothing()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 55);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 40);

        Assert.Equal(3545, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void Heartbeat_CreditIsCappedByElapsedTime()
    {
        var token = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 500); // 60 s elapsed + 30 s slack = at most 90 credited

        Assert.Equal(3510, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void TwoSessionsOfOneAccount_ShareOneLedger()
    {
        var first = LoginToken();
        var second = LoginToken();
        _clock.Advance(TimeSpan.FromSeconds(60));

        Beat(first, 60);
        var reply = Beat(second, 60);

        Assert.Equal(3480, reply.QuotaRemainingSeconds);
    }

    [Fact]
    public void AccountQuotaAndTodayBonus_ChangeTheTotal_BonusExpiresNextDay()
    {
        var token = LoginToken();
        _service.SetDailyQuota("alice", 7200);
        _service.AddTodayBonus("alice", 600);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(7800, Beat(token, 0).QuotaSeconds);

        _clock.Advance(TimeSpan.FromHours(20)); // past 06:00 Beijing the next morning
        Assert.Equal(7200, Beat(token, 0).QuotaSeconds);

        _service.SetDailyQuota("alice", null); // back to the global default
        Assert.Equal(3600, Beat(token, 0).QuotaSeconds);
    }

    [Fact]
    public void GlobalDefault_CanBeChanged()
    {
        _service.SetDefaultDailyQuota(1800);

        var login = _service.Login(new LoginRequest("alice", "pw-alice-1", "streamer-017", "0.36.0", 1), "t");

        Assert.Equal(1800, login.QuotaSeconds);
    }

    [Fact]
    public void Exhausted_IsStillOk_AndTheSessionStaysValid()
    {
        var token = LoginToken();
        _service.SetDailyQuota("alice", 100);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 60);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var reply = Beat(token, 120);
        var next = Beat(token, 120);

        Assert.Equal(AuthStatus.Ok, reply.Status);
        Assert.Equal(0, reply.QuotaRemainingSeconds);
        Assert.Equal(AuthStatus.Ok, next.Status);
    }

    [Fact]
    public void QuotaResetsAtSixBeijing()
    {
        var token = LoginToken();
        _service.SetDailyQuota("alice", 100);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Beat(token, 60);
        Assert.Equal(40, Beat(token, 60).QuotaRemainingSeconds);

        _clock.Advance(TimeSpan.FromHours(14)); // 08:00Z + 60 s + 14 h = 22:01Z = 06:01 Beijing, the next quota day
        var reply = Beat(token, 60);

        Assert.Equal(100, reply.QuotaRemainingSeconds);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 22, 0, 0, TimeSpan.Zero), reply.QuotaResetsAt);
    }
}
