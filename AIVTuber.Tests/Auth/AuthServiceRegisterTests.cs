using System.Net;
using System.Text.RegularExpressions;
using AIVTuber.AuthServer;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests.Auth;

public sealed class AuthServiceRegisterTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"auth-reg-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new(Start);
    private readonly AuthStore _store;
    private readonly AuthService _service;

    public AuthServiceRegisterTests()
    {
        _store = AuthStore.Open(_db);
        _service = new AuthService(_store, _clock, new AuthServerOptions());
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private string NewCode(string profile = "shared-001") => _service.CreateInvites(1, profile, "t")[0];

    private AuthResult Reg(string code, string username = "alice", string password = "password-1",
        string profile = "shared-001", string source = "1.2.3.4")
        => _service.Register(new RegisterRequest(code, username, password, profile, "0.37.0", 1), source);

    [Fact]
    public void Register_ParallelBadCodes_CannotPassTheFailureBudget()
    {
        // Hold the real store lock until every request is blocked. In the old implementation
        // all requests pass the limit and hash before reaching this lock; no timing lottery.
        var sync = typeof(AuthStore).GetField("_sync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_store)!;
        var results = new AuthStatus[16];
        var errors = new Exception?[16];
        var threads = Enumerable.Range(0, 16).Select(i => new Thread(() =>
        {
            try { results[i] = Reg("AAAA-BBBB", $"user{i}").Status; }
            catch (Exception ex) { errors[i] = ex; }
        }) { IsBackground = true }).ToArray();
        bool allWaiting;
        lock (sync)
        {
            foreach (var thread in threads) thread.Start();
            allWaiting = SpinWait.SpinUntil(() => threads.All(t =>
                (t.ThreadState & ThreadState.WaitSleepJoin) != 0), TimeSpan.FromSeconds(15));
        }
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.True(allWaiting, "Requests did not reach the held store / admission lock.");
        Assert.All(errors, Assert.Null);
        Assert.Equal(5, results.Count(s => s == AuthStatus.InvalidInvite));
        Assert.Equal(11, results.Count(s => s == AuthStatus.RateLimited));
        Assert.Empty(_store.ListAccounts());
    }

    [Fact]
    public async Task Register_ParallelSameCode_OnlyOneAccountAndUsableSession()
    {
        var code = NewCode();
        using var start = new Barrier(2);
        var attempts = Enumerable.Range(0, 2).Select(i => Task.Factory.StartNew(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            return Reg(code, $"user{i}", source: $"source{i}");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var results = await Task.WhenAll(attempts);
        var winner = Assert.Single(results.Where(r => r.Status == AuthStatus.Ok));
        Assert.Single(results.Where(r => r.Status == AuthStatus.InvalidInvite));
        Assert.Single(_store.ListAccounts());
        Assert.Equal(AuthStatus.Ok, _service.Heartbeat(winner.SessionToken!, "shared-001").Status);
    }

    [Fact]
    public void Register_SourceCapacity_IsBoundedAndExpiredSourcesAreReclaimed()
    {
        for (var i = 0; i < 4096; i++)
            Assert.Equal(AuthStatus.InvalidInvite, Reg("AAAA-BBBB", source: $"source{i}").Status);
        var code = NewCode();
        Assert.Equal(AuthStatus.RateLimited, Reg(code, source: "new-source").Status);
        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(AuthStatus.Ok, Reg(code, source: "new-source").Status);
    }

    [Fact]
    public void Register_LoginFailureKeys_CannotLockOutRegistration()
    {
        for (var i = 0; i < 5; i++)
            _service.Login(new LoginRequest("reg:1.2.3.4", "wrong", "shared-001", "0", 1), "other");
        Assert.Equal(AuthStatus.Ok, Reg(NewCode()).Status);
    }

    [Fact]
    public void Register_FailureBudget_ExpiresWithoutBurningTheInvite()
    {
        for (var i = 0; i < 5; i++) Reg("AAAA-BBBB");
        var code = NewCode();
        Assert.Equal(AuthStatus.RateLimited, Reg(code).Status);
        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(AuthStatus.Ok, Reg(code).Status);
    }

    [Fact]
    public void NormalizeInvite_IgnoresCaseDashAndSpaces()
    {
        Assert.Equal("K7M49QXD", AuthService.NormalizeInvite(" k7m4-9qxd "));
        Assert.Equal("K7M4-9QXD", AuthService.FormatInvite("K7M49QXD"));
    }

    [Fact]
    public void CreateInvites_ReturnsDistinctFormattedCodes()
    {
        var codes = _service.CreateInvites(50, "shared-001", "batch");

        Assert.Equal(50, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches(new Regex("^[A-HJ-KM-NP-Z2-9]{4}-[A-HJ-KM-NP-Z2-9]{4}$"), c));
    }

    [Fact]
    public void Register_Success_CreatesAccountMarksInviteAndLogsIn()
    {
        var code = NewCode();

        var result = Reg(code);

        Assert.Equal(AuthStatus.Ok, result.Status);
        Assert.False(string.IsNullOrEmpty(result.SessionToken));
        Assert.Equal(3600, result.QuotaSeconds);
        var account = _store.FindAccountByUsername("alice")!;
        Assert.Equal("shared-001", account.ProfileId);
        var invite = _service.ListInvites().Single();
        Assert.Equal("alice", invite.UsedByUsername);
        Assert.NotNull(invite.UsedAt);
    }

    [Fact]
    public void Register_NoExpiryAccount_GetsNormalLeaseAndQuota()
    {
        var result = Reg(NewCode());

        Assert.Equal(AuthService.NoExpiry, result.AccountValidUntil);
        Assert.Equal(result.ServerTime!.Value.AddSeconds(180), result.LeaseValidUntil);

        _clock.Advance(TimeSpan.FromSeconds(60));
        var beat = _service.Heartbeat(result.SessionToken!, "shared-001", 55);
        Assert.Equal(AuthStatus.Ok, beat.Status);
        Assert.Equal(3545, beat.QuotaRemainingSeconds);
    }

    [Fact]
    public void Register_SameCodeTwice_OnlyOneAccount()
    {
        var code = NewCode();

        Assert.Equal(AuthStatus.Ok, Reg(code, "alice").Status);
        Assert.Equal(AuthStatus.InvalidInvite, Reg(code, "bob").Status);

        Assert.Single(_store.ListAccounts());
    }

    [Fact]
    public void Register_UsernameTaken_KeepsTheInviteUsable()
    {
        _service.CreateAccount("alice", "pw-alice-1", "shared-001", AuthService.NoExpiry);
        var code = NewCode();

        Assert.Equal(AuthStatus.UsernameTaken, Reg(code, "alice").Status);
        Assert.Equal(AuthStatus.Ok, Reg(code, "bob").Status);
    }

    [Fact]
    public void Register_BadInvites_AllLookTheSame()
    {
        var revoked = NewCode();
        _service.RevokeInvite(revoked);
        var otherProfile = NewCode("another-package");

        Assert.Equal(AuthStatus.InvalidInvite, Reg("ZZZZ-ZZZZ", "a1x").Status);
        Assert.Equal(AuthStatus.InvalidInvite, Reg(revoked, "b2x").Status);
        Assert.Equal(AuthStatus.InvalidInvite, Reg(otherProfile, "c3x").Status);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("名字名字名")]
    public void Register_BadUsername_IsRejectedWithoutConsumingTheInvite(string username)
    {
        var code = NewCode();

        Assert.Equal(AuthStatus.InvalidUsername, Reg(code, username).Status);
        Assert.Equal(AuthStatus.Ok, Reg(code, "alice").Status);
    }

    [Fact]
    public void Register_ShortPassword_IsRejectedWithoutConsumingTheInvite()
    {
        var code = NewCode();

        Assert.Equal(AuthStatus.WeakPassword, Reg(code, "alice", "1234567").Status);
        Assert.Equal(AuthStatus.Ok, Reg(code, "alice").Status);
    }

    [Fact]
    public void Register_InvalidInviteIsRateLimited_PerSource_AndFormatErrorsDoNotCount()
    {
        for (var i = 0; i < 5; i++) Assert.Equal(AuthStatus.InvalidInvite, Reg("AAAA-BBBB", $"user{i}x", source: "1.2.3.4").Status);

        var code = NewCode();
        Assert.Equal(AuthStatus.RateLimited, Reg(code, "alice", source: "1.2.3.4").Status);
        Assert.Equal(AuthStatus.Ok, Reg(code, "alice", source: "5.6.7.8").Status);

        for (var i = 0; i < 10; i++) Assert.Equal(AuthStatus.InvalidUsername, Reg(NewCode(), "x", source: "9.9.9.9").Status);
        Assert.Equal(AuthStatus.Ok, Reg(NewCode(), "carol", source: "9.9.9.9").Status);
    }

    [Fact]
    public void Register_CodeTypedWithLowercaseAndDash_Works()
    {
        var code = NewCode().ToLowerInvariant().Replace("-", " - ");

        Assert.Equal(AuthStatus.Ok, Reg(code).Status);
    }

    [Fact]
    public void RevokeInvite_BlocksUse_AndUsedInviteCannotBeRevoked()
    {
        var unused = NewCode();
        var used = NewCode();
        Reg(used, "alice");

        Assert.True(_service.RevokeInvite(unused));
        Assert.Equal(AuthStatus.InvalidInvite, Reg(unused, "bob").Status);
        Assert.False(_service.RevokeInvite(used));
        Assert.False(_service.RevokeInvite("NOPE-NOPE"));
    }

    [Fact]
    public void SourceKey_UsesRealIpBehindLoopback()
    {
        Assert.Equal("9.9.9.9", AuthServerApp.SourceKey(IPAddress.Loopback, "9.9.9.9"));
        Assert.Equal("8.8.8.8", AuthServerApp.SourceKey(IPAddress.Parse("8.8.8.8"), "9.9.9.9"));
        Assert.Equal("127.0.0.1", AuthServerApp.SourceKey(IPAddress.Loopback, null));
        Assert.Equal("", AuthServerApp.SourceKey(null, null));
    }
}
