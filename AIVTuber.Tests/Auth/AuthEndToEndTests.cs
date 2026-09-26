using AIVTuber.AuthServer;
using AIVTuber.Core.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AIVTuber.Tests.Auth;

/// <summary>Real client (<see cref="AuthApiClient"/> + <see cref="CloudLicense"/>) against the
/// real service over loopback HTTP; only the clock of the client is left real.</summary>
public sealed class AuthEndToEndTests : IAsyncLifetime
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"auth-e2e-{Guid.NewGuid():N}.db");
    private WebApplication _app = null!;
    private Uri _baseUri = null!;
    private AuthService Service => _app.Services.GetRequiredService<AuthService>();

    public async Task InitializeAsync()
    {
        _app = AuthServerApp.Build(["--urls", "http://127.0.0.1:0"],
            new AuthServerOptions { DatabasePath = _db, LeaseSeconds = 180, HeartbeatSeconds = 60 });
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _baseUri = new Uri(address);
        Service.CreateAccount("alice", "pw-alice-1", "streamer-017", DateTimeOffset.UtcNow.AddDays(30));
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private CloudLicense NewLicense(AuthApiClient api, string profile = "streamer-017") =>
        new(api, profile, credentialRevision: 1, "0.36.0", startBackgroundLoop: false);

    [Fact]
    public async Task Login_Heartbeat_Disable_OverHttp()
    {
        using var api = new AuthApiClient(_baseUri);
        await using var license = NewLicense(api);
        var revoked = new List<string>();
        license.Revoked += revoked.Add;

        var outcome = await license.LoginAsync("alice", "pw-alice-1");
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(license.IsAllowed);
        Assert.NotNull(license.Snapshot.AccountValidUntil);

        await license.HeartbeatOnceAsync();
        Assert.True(license.IsAllowed);

        Service.SetEnabled("alice", false);
        await license.HeartbeatOnceAsync();

        Assert.False(license.IsAllowed);
        Assert.Contains("已被停用", Assert.Single(revoked));
    }

    [Fact]
    public async Task WrongPassword_And_WrongPackage_AreDistinguishable()
    {
        using var api = new AuthApiClient(_baseUri);
        await using var license = NewLicense(api);
        await using var otherPackage = NewLicense(api, profile: "streamer-018");

        Assert.Contains("账号或密码错误", (await license.LoginAsync("alice", "nope")).Message);
        Assert.Contains("需要使用对应的安装包", (await otherPackage.LoginAsync("alice", "pw-alice-1")).Message);
    }

    [Fact]
    public async Task Logout_RevokesServerSession()
    {
        using var api = new AuthApiClient(_baseUri);
        await using var license = NewLicense(api);
        await license.LoginAsync("alice", "pw-alice-1");
        var token = typeof(CloudLicense).GetField("_token", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(license) as string;

        await license.LogoutAsync();

        Assert.Equal(AuthCode.SessionRevoked, (await api.HeartbeatAsync(token!, "streamer-017")).Status);
    }

    [Fact]
    public async Task UnreachableService_IsTransportFailureNotDenial()
    {
        using var api = new AuthApiClient(new Uri("http://127.0.0.1:9/"), TimeSpan.FromSeconds(2));
        await using var license = NewLicense(api);

        var outcome = await license.LoginAsync("alice", "pw-alice-1");

        Assert.False(outcome.Success);
        Assert.Equal(CloudLicense.TransportFailureMessage, outcome.Message);
    }

    [Fact]
    public void Client_DoesNotCarryADefaultAuthorizationHeader()
    {
        using var api = new AuthApiClient(_baseUri);

        Assert.Null(api.HttpClientForTests.DefaultRequestHeaders.Authorization);
    }
}
