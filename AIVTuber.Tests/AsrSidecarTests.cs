using System.Net;
using System.Text;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests;

public sealed class AsrSidecarTests
{
    [Theory]
    [InlineData("loading", LocalAsrHealthStatus.Loading, false)]
    [InlineData("ready", LocalAsrHealthStatus.Ready, true)]
    [InlineData("failed", LocalAsrHealthStatus.Failed, false)]
    [InlineData("unexpected", LocalAsrHealthStatus.Unknown, false)]
    public async Task Health_contract_requires_explicit_ready(
        string status,
        LocalAsrHealthStatus expected,
        bool expectedReady)
    {
        using var http = new HttpClient(new StubHttpHandler(
            HttpStatusCode.OK,
            $"{{\"status\":\"{status}\",\"detail\":\"model detail\"}}"));
        var client = new LocalAsrClient("http://localhost:8765", http);

        var health = await client.GetHealthAsync();

        Assert.Equal(expected, health.Status);
        Assert.Equal("model detail", health.Detail);
        Assert.Equal(expectedReady, await client.PingAsync());
    }

    [Fact]
    public async Task Health_contract_preserves_http_and_body_diagnostics()
    {
        using var http = new HttpClient(new StubHttpHandler(
            HttpStatusCode.ServiceUnavailable,
            "{\"status\":\"failed\",\"detail\":\"model missing\"}"));
        var client = new LocalAsrClient("http://localhost:8765", http);

        var health = await client.GetHealthAsync();

        Assert.Equal(LocalAsrHealthStatus.Failed, health.Status);
        Assert.Contains("HTTP 503", health.Detail);
        Assert.Contains("model missing", health.Detail);
    }

    [Fact]
    public async Task Readiness_waits_through_loading_until_ready()
    {
        var states = new Queue<LocalAsrHealth>(
        [
            new(LocalAsrHealthStatus.Loading, "loading model"),
            new(LocalAsrHealthStatus.Ready, "ready"),
        ]);

        var health = await AsrSidecarReadiness.WaitUntilReadyAsync(
            _ => Task.FromResult(states.Dequeue()),
            () => false,
            () => "",
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(LocalAsrHealthStatus.Ready, health.Status);
    }

    [Fact]
    public async Task Readiness_fails_immediately_with_server_diagnostic()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AsrSidecarReadiness.WaitUntilReadyAsync(
                _ => Task.FromResult(new LocalAsrHealth(LocalAsrHealthStatus.Failed, "CUDA unavailable")),
                () => false,
                () => "stderr fallback",
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                CancellationToken.None));

        Assert.Contains("CUDA unavailable", error.Message);
    }

    [Fact]
    public async Task Readiness_reports_early_process_exit()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AsrSidecarReadiness.WaitUntilReadyAsync(
                _ => Task.FromResult(new LocalAsrHealth(LocalAsrHealthStatus.Loading, "loading")),
                () => true,
                () => "exit code 17: import failed",
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                CancellationToken.None));

        Assert.Contains("exit code 17", error.Message);
    }

    [Fact]
    public async Task Readiness_timeout_includes_last_health_and_process_diagnostics()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            AsrSidecarReadiness.WaitUntilReadyAsync(
                _ => Task.FromResult(new LocalAsrHealth(LocalAsrHealthStatus.Loading, "loading weights")),
                () => false,
                () => "stderr: slow disk",
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None));

        Assert.Contains("loading weights", error.Message);
        Assert.Contains("slow disk", error.Message);
    }

    [Fact]
    public async Task Readiness_honors_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AsrSidecarReadiness.WaitUntilReadyAsync(
                _ => Task.FromResult(new LocalAsrHealth(LocalAsrHealthStatus.Loading)),
                () => false,
                () => "",
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(1),
                cts.Token));
    }

    [Fact]
    public void Local_asr_defaults_to_packaged_runtime()
    {
        Assert.Equal("sidecar/python/python.exe", new AIVTuber.Core.Config.AsrConfig().PythonPath);
    }

    private sealed class StubHttpHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
