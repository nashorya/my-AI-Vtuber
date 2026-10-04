using AIVTuber.AuthServer;
using Microsoft.Data.Sqlite;

namespace AIVTuber.Tests.Auth;

public sealed class AdminCliTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"auth-cli-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new(Start);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private (int Code, string Out, string Err) Run(string stdin, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = AdminCli.Run(["--db", _db, .. args], new StringReader(stdin), output, error, _clock);
        return (code, output.ToString(), error.ToString());
    }

    private AuthResult Login(string password, int revision = 1)
    {
        using var store = AuthStore.Open(_db);
        var service = new AuthService(store, _clock, new AuthServerOptions());
        var result = service.Login(new LoginRequest("alice", password, "streamer-017", "0.36.0", revision), "t");
        SqliteConnection.ClearAllPools();
        return result;
    }

    [Fact]
    public void Create_WithPasswordFromStdin_AllowsLoginUntilExpiry()
    {
        var (code, output, _) = Run("secret-1\n", "create", "--username", "alice", "--profile", "streamer-017",
            "--days", "30", "--password-stdin");

        Assert.Equal(0, code);
        Assert.DoesNotContain("secret-1", output);
        var login = Login("secret-1");
        Assert.Equal(AuthStatus.Ok, login.Status);
        Assert.Equal(Start.AddDays(30), login.AccountValidUntil);
    }

    [Fact]
    public void Create_WithoutPassword_PrintsGeneratedPasswordOnce()
    {
        var (code, output, _) = Run("", "create", "--username", "alice", "--profile", "streamer-017",
            "--valid-until", "2026-12-31T00:00:00Z");

        Assert.Equal(0, code);
        var line = output.Split('\n').Single(l => l.StartsWith("password: ", StringComparison.Ordinal));
        Assert.Equal(AuthStatus.Ok, Login(line["password: ".Length..].Trim()).Status);
    }

    [Fact]
    public void Extend_AddsDaysFromCurrentExpiry()
    {
        Run("pw\n", "create", "--username", "alice", "--profile", "streamer-017", "--days", "10", "--password-stdin");

        var (code, _, _) = Run("", "extend", "--username", "alice", "--days", "5");

        Assert.Equal(0, code);
        Assert.Equal(Start.AddDays(15), Login("pw").AccountValidUntil);
    }

    [Fact]
    public void Disable_ThenEnable_ControlsLogin()
    {
        Run("pw\n", "create", "--username", "alice", "--profile", "streamer-017", "--days", "10", "--password-stdin");

        Run("", "disable", "--username", "alice");
        Assert.Equal(AuthStatus.Disabled, Login("pw").Status);

        Run("", "enable", "--username", "alice");
        Assert.Equal(AuthStatus.Ok, Login("pw").Status);
    }

    [Fact]
    public void SetMinRevision_RejectsOlderCredentialPackages()
    {
        Run("pw\n", "create", "--username", "alice", "--profile", "streamer-017", "--days", "10", "--password-stdin");

        Run("", "set-min-revision", "--username", "alice", "--revision", "2");

        Assert.Equal(AuthStatus.CredentialRevoked, Login("pw", revision: 1).Status);
        Assert.Equal(AuthStatus.Ok, Login("pw", revision: 2).Status);
    }

    [Fact]
    public void List_ShowsStateWithoutHashes()
    {
        Run("pw\n", "create", "--username", "alice", "--profile", "streamer-017", "--days", "10", "--password-stdin");

        var (code, output, _) = Run("", "list");

        Assert.Equal(0, code);
        Assert.Contains("alice", output);
        Assert.Contains("streamer-017", output);
        Assert.DoesNotContain("AQAAAA", output); // PasswordHasher v3 prefix
    }

    [Fact]
    public void UnknownAccount_ReturnsNonZero()
    {
        var (code, _, error) = Run("", "disable", "--username", "ghost");

        Assert.NotEqual(0, code);
        Assert.Contains("ghost", error);
    }

    private void CreateAlice() =>
        Run("secret-1\n", "create", "--username", "alice", "--profile", "streamer-017", "--days", "30", "--password-stdin");

    [Fact]
    public void SetDefaultDaily_ChangesEveryoneWithoutAnOverride()
    {
        CreateAlice();
        Assert.Equal(0, Run("", "set-default-daily", "--minutes", "30").Code);

        Assert.Equal(1800, Login("secret-1").QuotaSeconds);
    }

    [Fact]
    public void SetDaily_OverridesOneAccount_AndDefaultFlagRestores()
    {
        CreateAlice();
        Run("", "set-default-daily", "--minutes", "30");

        Assert.Equal(0, Run("", "set-daily", "--username", "alice", "--minutes", "120").Code);
        Assert.Equal(7200, Login("secret-1").QuotaSeconds);

        Assert.Equal(0, Run("", "set-daily", "--username", "alice", "--default").Code);
        Assert.Equal(1800, Login("secret-1").QuotaSeconds);
    }

    [Fact]
    public void AddToday_AddsToTodaysQuota()
    {
        CreateAlice();

        Assert.Equal(0, Run("", "add-today", "--username", "alice", "--minutes", "30").Code);

        Assert.Equal(3600 + 1800, Login("secret-1").QuotaSeconds);
    }

    [Fact]
    public void Usage_ListsDaysAndList_ShowsTodayColumns()
    {
        CreateAlice();
        var login = Login("secret-1");
        _clock.Advance(TimeSpan.FromSeconds(60));
        using (var store = AuthStore.Open(_db))
        {
            var service = new AuthService(store, _clock, new AuthServerOptions());
            service.Heartbeat(login.SessionToken!, "streamer-017", 60);
        }
        SqliteConnection.ClearAllPools();

        var usage = Run("", "usage", "--username", "alice", "--days", "7");
        var list = Run("", "list");

        Assert.Equal(0, usage.Code);
        Assert.Contains("2026-09-26", usage.Out);
        Assert.Contains("1.0", usage.Out); // minutes
        Assert.Contains("today_used_min=1.0", list.Out);
        Assert.Contains("today_quota_min=60.0", list.Out);
    }

    [Fact]
    public void QuotaCommands_RejectNonPositiveMinutes()
    {
        CreateAlice();
        Assert.Equal(1, Run("", "set-default-daily", "--minutes", "0").Code);
        Assert.Equal(1, Run("", "add-today", "--username", "alice", "--minutes", "-5").Code);
    }

    private static readonly System.Text.RegularExpressions.Regex CodeLine =
        new("^[A-HJ-KM-NP-Z2-9]{4}-[A-HJ-KM-NP-Z2-9]{4}$");

    [Fact]
    public void InviteCreate_PrintsFormattedCodes()
    {
        var (code, output, _) = Run("", "invite", "create", "--count", "3", "--profile", "shared-001", "--note", "内测");

        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Matches(CodeLine, l));
    }

    [Fact]
    public void InviteCreate_RejectsCountOutOfRange()
    {
        Assert.Equal(1, Run("", "invite", "create", "--count", "0", "--profile", "p").Code);
        Assert.Equal(1, Run("", "invite", "create", "--count", "501", "--profile", "p").Code);
    }

    [Fact]
    public void InviteList_ShowsUnusedUsedAndRevoked()
    {
        var codes = Run("", "invite", "create", "--count", "3", "--profile", "shared-001").Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using (var store = AuthStore.Open(_db))
        {
            var service = new AuthService(store, _clock, new AuthServerOptions());
            service.Register(new RegisterRequest(codes[0], "alice", "password-1", "shared-001", "0.37.0", 1), "t");
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(0, Run("", "invite", "revoke", "--code", codes[1]).Code);

        var list = Run("", "invite", "list").Out;

        Assert.Contains("已用 alice", list);
        Assert.Contains("已作废", list);
        Assert.Contains("未用", list);
        Assert.Contains("profile=shared-001", list);
    }

    [Fact]
    public void InviteRevoke_UnknownOrUsedCodeFails()
    {
        Assert.Equal(1, Run("", "invite", "revoke", "--code", "NOPE-NOPE").Code);
    }

    [Fact]
    public void Create_NoExpiry_AccountNeverExpires()
    {
        var (code, _, _) = Run("secret-1\n", "create", "--username", "alice", "--profile", "streamer-017",
            "--no-expiry", "--password-stdin");

        Assert.Equal(0, code);
        Assert.Equal(AuthService.NoExpiry, Login("secret-1").AccountValidUntil);
    }

    [Fact]
    public void Create_NoExpiry_ConflictsWithDays()
    {
        var (code, _, err) = Run("secret-1\n", "create", "--username", "alice", "--profile", "streamer-017",
            "--no-expiry", "--days", "30", "--password-stdin");

        Assert.Equal(1, code);
        Assert.Contains("--no-expiry", err);
    }
}
