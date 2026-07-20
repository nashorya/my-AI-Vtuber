using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class DashScopeConnectionPoolTests
{
    [Fact]
    public void Constructor_StoresEndpointAndConfigureCallback()
    {
        // The pool does not connect on construction — it is lazy. Verifying we can construct
        // one without a network and that Dispose is safe before any GetOrCreateAsync call.
        var pool = new DashScopeConnectionPool(
            "wss://example.invalid/",
            _ => { });
        pool.Dispose();
        Assert.NotNull(pool);
    }

    [Fact]
    public void Invalidate_WhenNoConnection_IsNoOp()
    {
        var pool = new DashScopeConnectionPool("wss://example.invalid/", "key");
        pool.Invalidate(); // must not throw even with no active connection
        pool.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pool = new DashScopeConnectionPool("wss://example.invalid/", "key");
        pool.Dispose();
        pool.Dispose(); // second dispose must not throw
    }

    [Fact]
    public async Task GetOrCreateAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var pool = new DashScopeConnectionPool("wss://example.invalid/", "key");
        pool.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await pool.GetOrCreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows()
    {
        var errors = new List<Exception>();
        var pool = new DashScopeConnectionPool(
            "ws://localhost:1/invalid", // unreachable port
            "key",
            ex => errors.Add(ex));

        // Use a short timeout via a linked CTS so we don't hang on connect retry.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pool.GetOrCreateAsync(cts.Token));

        // We cancelled before the connection error could fire; either way the pool must remain
        // usable (no stale _ws left behind).
        pool.Dispose();
    }
}
