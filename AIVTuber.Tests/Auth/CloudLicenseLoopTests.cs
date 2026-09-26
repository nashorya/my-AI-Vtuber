using AIVTuber.Core.Auth;

namespace AIVTuber.Tests.Auth;

/// <summary>The background loop on the real clock: renewals keep access, and when renewals
/// stop reaching the service the lease still ends on time and raises Revoked (AUTH-05).</summary>
public sealed class CloudLicenseLoopTests
{
    [Fact]
    public async Task BackgroundLoop_RenewsThenRevokesWhenServiceBecomesUnreachable()
    {
        var api = new FakeAuthApi();
        var heartbeats = 0;
        var reachable = true;
        AuthReply Lease() => new(AuthCode.Ok, "tok", "acc", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(3), DateTimeOffset.UtcNow.AddDays(1), HeartbeatSeconds: 1);
        api.Login = (_, _) => Task.FromResult(Lease());
        api.Heartbeat = (_, _) =>
        {
            Interlocked.Increment(ref heartbeats);
            return Volatile.Read(ref reachable) ? Task.FromResult(Lease()) : throw new AuthTransportException("offline");
        };
        await using var license = new CloudLicense(api, "streamer-017", 1, "0.36.0");
        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        license.Revoked += reason => revoked.TrySetResult(reason);

        Assert.True((await license.LoginAsync("alice", "pw")).Success);
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(license.IsAllowed);
        Assert.True(Volatile.Read(ref heartbeats) >= 2);

        Volatile.Write(ref reachable, false);
        // The first failed renewal happens while the lease is still valid and the retry is 10s
        // away, so only the loop's own expiry check can end access within this window.
        var reason = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Contains("无法联系鉴权服务", reason);
        Assert.False(license.IsAllowed);
    }
}
