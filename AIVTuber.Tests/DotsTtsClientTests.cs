using System.Net;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

/// <summary>
/// Drives DotsTtsClient against a local HttpListener so the contract with the self-hosted
/// dots.tts service is pinned without a GPU box in the loop.
/// </summary>
public sealed class DotsTtsClientTests
{
    /// <summary>Captures what the client sent and replies with a canned response.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string Url { get; }
        public string? LastPath { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }
        public int RequestCount;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public byte[] Body { get; set; } = [];
        /// <summary>Writes the body in pieces of this size, flushing between them, to mimic
        /// the arbitrary framing a real network connection delivers.</summary>
        public int WritePieceSize { get; set; }
        public string? SampleRateHeader { get; set; } = AudioPlayer.DefaultSampleRate.ToString();
        /// <summary>Delays the response so cancellation has something to interrupt.</summary>
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public StubServer()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _ = AcceptAsync();
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                Interlocked.Increment(ref RequestCount);
                LastPath = ctx.Request.Url?.AbsolutePath;
                LastMethod = ctx.Request.HttpMethod;
                LastAuthorization = ctx.Request.Headers["Authorization"];
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    LastBody = await reader.ReadToEndAsync();

                if (Delay > TimeSpan.Zero) await Task.Delay(Delay);

                try
                {
                    ctx.Response.StatusCode = (int)Status;
                    if (SampleRateHeader is not null)
                        ctx.Response.Headers["X-Sample-Rate"] = SampleRateHeader;
                    if (WritePieceSize > 0)
                    {
                        for (var i = 0; i < Body.Length; i += WritePieceSize)
                        {
                            var n = Math.Min(WritePieceSize, Body.Length - i);
                            await ctx.Response.OutputStream.WriteAsync(Body.AsMemory(i, n));
                            await ctx.Response.OutputStream.FlushAsync();
                            await Task.Delay(5);
                        }
                    }
                    else
                    {
                        await ctx.Response.OutputStream.WriteAsync(Body);
                    }
                    ctx.Response.Close();
                }
                catch { /* client hung up (cancellation tests) */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }

    private static TtsConfig Config(string baseUrl, string apiKey = "") => new()
    {
        Provider = "dots", BaseUrl = baseUrl, ApiKey = apiKey,
    };

    private static async Task<byte[]> CollectAsync(ITtsClient client, string text = "你好")
    {
        var buffer = new List<byte>();
        await foreach (var chunk in client.StreamAsync(text, voiceId: "", emotion: null))
            buffer.AddRange(chunk);
        return [.. buffer];
    }

    [Fact]
    public async Task StreamAsync_ReturnsResponseBodyVerbatim()
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        using var server = new StubServer { Body = payload };
        using var client = new DotsTtsClient(Config(server.Url));

        var got = await CollectAsync(client);

        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task StreamAsync_PostsToV1Tts()
    {
        using var server = new StubServer { Body = [1, 2] };
        using var client = new DotsTtsClient(Config(server.Url));

        await CollectAsync(client);

        Assert.Equal("POST", server.LastMethod);
        Assert.Equal("/v1/tts", server.LastPath);
    }

    [Fact]
    public async Task StreamAsync_SendsConfiguredSynthesisParameters()
    {
        using var server = new StubServer { Body = [1] };
        var config = Config(server.Url);
        config.Language = "EN";
        config.Seed = 7;
        config.NumSteps = 16;
        config.GuidanceScale = 2.5;
        using var client = new DotsTtsClient(config);

        await CollectAsync(client, "hello");

        var body = JsonDocument.Parse(server.LastBody!).RootElement;
        Assert.Equal("hello", body.GetProperty("text").GetString());
        Assert.Equal("EN", body.GetProperty("language").GetString());
        Assert.Equal(7, body.GetProperty("seed").GetInt32());
        Assert.Equal(16, body.GetProperty("num_steps").GetInt32());
        Assert.Equal(2.5, body.GetProperty("guidance_scale").GetDouble());
    }

    [Fact]
    public async Task StreamAsync_AsksForThePlayersSampleRate()
    {
        // The player decodes raw PCM at a fixed rate; a server returning anything else
        // plays at the wrong speed and pitch. State the expectation in the request.
        using var server = new StubServer { Body = [1] };
        using var client = new DotsTtsClient(Config(server.Url));

        await CollectAsync(client);

        var body = JsonDocument.Parse(server.LastBody!).RootElement;
        Assert.Equal(AudioPlayer.DefaultSampleRate, body.GetProperty("sample_rate").GetInt32());
    }

    [Fact]
    public async Task StreamAsync_ThrowsWhenServerReturnsWrongSampleRate()
    {
        // Fail loudly instead of emitting chipmunk audio.
        using var server = new StubServer { Body = [1, 2, 3], SampleRateHeader = "44100" };
        using var client = new DotsTtsClient(Config(server.Url));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(client));

        Assert.Contains("44100", ex.Message);
        Assert.Contains(AudioPlayer.DefaultSampleRate.ToString(), ex.Message);
    }

    [Fact]
    public async Task StreamAsync_AcceptsResponseWithoutSampleRateHeader()
    {
        using var server = new StubServer { Body = [1, 2, 3, 4], SampleRateHeader = null };
        using var client = new DotsTtsClient(Config(server.Url));

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await CollectAsync(client));
    }

    [Fact]
    public async Task StreamAsync_SendsBearerTokenWhenConfigured()
    {
        using var server = new StubServer { Body = [1] };
        using var client = new DotsTtsClient(Config(server.Url, apiKey: "secret-token"));

        await CollectAsync(client);

        Assert.Equal("Bearer secret-token", server.LastAuthorization);
    }

    [Fact]
    public async Task StreamAsync_OmitsAuthorizationWhenNoApiKey()
    {
        using var server = new StubServer { Body = [1] };
        using var client = new DotsTtsClient(Config(server.Url));

        await CollectAsync(client);

        Assert.True(string.IsNullOrEmpty(server.LastAuthorization));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamAsync_EmptyText_MakesNoRequest(string text)
    {
        using var server = new StubServer { Body = [1] };
        using var client = new DotsTtsClient(Config(server.Url));

        Assert.Empty(await CollectAsync(client, text));
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task StreamAsync_ErrorStatus_ThrowsWithStatusAndBody()
    {
        using var server = new StubServer
        {
            Status = HttpStatusCode.Unauthorized,
            Body = Encoding.UTF8.GetBytes("{\"detail\":\"Invalid API key\"}"),
        };
        using var client = new DotsTtsClient(Config(server.Url));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(client));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public async Task StreamAsync_Cancellation_Throws()
    {
        using var server = new StubServer { Body = [1], Delay = TimeSpan.FromSeconds(10) };
        using var client = new DotsTtsClient(Config(server.Url));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamAsync("你好", "", null, cts.Token)) { }
        });
    }

    [Fact]
    public async Task StreamAsync_TrailingSlashInBaseUrl_StillHitsV1Tts()
    {
        using var server = new StubServer { Body = [1] };
        using var client = new DotsTtsClient(Config(server.Url + "/"));

        await CollectAsync(client);

        Assert.Equal("/v1/tts", server.LastPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyBaseUrl(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => new DotsTtsClient(Config(baseUrl)));
    }

    [Theory]
    [InlineData(333)]
    [InlineData(1001)]
    [InlineData(7)]
    public async Task StreamAsync_NeverYieldsAnOddLengthChunk(int piece)
    {
        // Every chunk is a whole number of 16-bit samples or the consumer's sample
        // alignment shifts by a byte, turning the rest of the utterance into noise.
        // Network framing does not respect sample boundaries, so the client must.
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        using var server = new StubServer { Body = payload, WritePieceSize = piece };
        using var client = new DotsTtsClient(Config(server.Url));

        var sizes = new List<int>();
        var buffer = new List<byte>();
        await foreach (var chunk in client.StreamAsync("你好", "", null))
        {
            sizes.Add(chunk.Length);
            buffer.AddRange(chunk);
        }

        Assert.All(sizes, n => Assert.Equal(0, n % 2));
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task StreamAsync_OddTotalLength_DropsTheDanglingByte()
    {
        // A trailing half-sample cannot be played; emitting it would misalign nothing
        // downstream only because there is nothing after it, but it is still not audio.
        var payload = new byte[1025];
        Random.Shared.NextBytes(payload);
        using var server = new StubServer { Body = payload, WritePieceSize = 101 };
        using var client = new DotsTtsClient(Config(server.Url));

        var total = 0;
        await foreach (var chunk in client.StreamAsync("你好", "", null))
        {
            Assert.Equal(0, chunk.Length % 2);
            total += chunk.Length;
        }

        Assert.Equal(1024, total);
    }
}
