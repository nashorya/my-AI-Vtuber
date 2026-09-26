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
}
