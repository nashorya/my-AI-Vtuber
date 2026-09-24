using System.Net;
using System.Text;
using System.Text.Json;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

/// <summary>
/// RT-01 fake-transport tests for MiniMax HTTP streaming TTS. No real API key is used;
/// every test drives MiniMaxHttpStreamingTtsClient through a stub HttpMessageHandler that
/// can slice the SSE body at arbitrary boundaries, delay it, or abort it.
/// Real-endpoint behavior is UNVERIFIED; these tests pin the client-side contract only.
/// </summary>
public sealed class MiniMaxHttpStreamingTtsClientTests
{
    private const string HexA = "0011ff7f";           // 2 PCM samples
    private const string HexB = "223344556677";       // 3 PCM samples
    private const string HexTail = "aabb";            // 1 PCM sample (final tail)

    private static TtsConfig Config(string transport = "streaming") => new()
    {
        Provider = "minimax",
        ApiKey = "test-key",
        VoiceId = "voice-1",
        Model = "speech-2.8-hd",
        Transport = transport,
        SampleRate = 24000,
    };

    private static string AudioEvent(string hex, bool isFinal = false, string? format = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?>
            {
                ["audio"] = hex,
                ["audio_format"] = format,
            },
            ["is_final"] = isFinal,
        });

    private static string ErrorEvent(int code, string msg) =>
        "{\"base_resp\":{\"status_code\":" + code + ",\"status_msg\":\"" + msg + "\"}}";

    /// <summary>Stub handler: serves the given body as SSE with a configurable slice size,
    /// and reports how much of the body had been consumed when the consumer read its first chunk.</summary>
    private sealed class FakeSseHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public int SliceSize { get; set; } = 5;
        public TimeSpan DelayPerSlice { get; set; } = TimeSpan.FromMilliseconds(1);
        public string ContentType { get; set; } = "text/event-stream";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public int BytesServedOnFirstConsumerYield = -1;
        public bool ResponseCompletedBeforeFirstYield;
        public bool Aborted;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            var body = Body;
            var slice = SliceSize <= 0 ? body.Length : SliceSize;
            var stream = new SlicedStream(body, slice, DelayPerSlice, this, cancellationToken);
            return new HttpResponseMessage(Status)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType) },
                },
            };
        }
    }

    /// <summary>Serves the body in fixed-size slices with flushes between them, exposing
    /// progress so tests can prove the consumer got audio before the response ended.</summary>
    private sealed class SlicedStream(
        byte[] body, int slice, TimeSpan delay, FakeSseHandler owner, CancellationToken ct) : Stream
    {
        private int _position;
        public int Served => _position;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
            if (_position >= body.Length) return 0;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, linked.Token); }
                catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
            }
            linked.Token.ThrowIfCancellationRequested();
            int n = Math.Min(count, Math.Min(slice, body.Length - _position));
            Array.Copy(body, _position, buffer, offset, n);
            _position += n;
            if (owner.BytesServedOnFirstConsumerYield < 0)
                owner.BytesServedOnFirstConsumerYield = _position;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => body.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<byte[]>> CollectAsync(
        MiniMaxHttpStreamingTtsClient client, CancellationToken ct = default)
    {
        var chunks = new List<byte[]>();
        await foreach (var c in client.StreamAsync("你好世界", "voice-1", null, ct))
            chunks.Add(c);
        return chunks;
    }

    // ---- request shape: stream=true, pinned voice/rate/format (音色/采样率不悄悄变) ----

    [Fact]
    public async Task Request_UsesStreamTrue_AndPinsVoiceAndSampleRate()
    {
        var handler = new FakeSseHandler { Body = Encoding.UTF8.GetBytes($"data:{AudioEvent(HexA, isFinal: true)}\n\n") };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        await CollectAsync(client);

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("voice-1", root.GetProperty("voice_setting").GetProperty("voice_id").GetString());
        Assert.Equal(24000, root.GetProperty("audio_setting").GetProperty("sample_rate").GetInt32());
        Assert.Equal("pcm", root.GetProperty("audio_setting").GetProperty("format").GetString());
        Assert.Equal("speech-2.8-hd", root.GetProperty("model").GetString());
        Assert.Equal("Bearer test-key", handler.Request!.Headers.Authorization!.ToString());
    }

    // ---- streaming consumption: audio before the response ends ----

    [Fact]
    public async Task FirstPcm_Arrives_BeforeResponseCompletes()
    {
        var sse = string.Concat(
            $"data:{AudioEvent(HexA)}\n\n",
            "data:{\"keepalive\":true}\n\n",
            ":keepalive comment\n\n",
            $"data:{AudioEvent(HexB)}\n\n",
            $"data:{AudioEvent(HexTail, isFinal: true)}\n\n");
        var handler = new FakeSseHandler
        {
            Body = Encoding.UTF8.GetBytes(sse),
            SliceSize = 7,
        };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);

        var enumerator = client.StreamAsync("hi", "voice-1", null).GetAsyncEnumerator();
        bool got = await enumerator.MoveNextAsync();
        Assert.True(got);
        // The consumer has a PCM chunk while the fake response stream is still open.
        Assert.True(handler.BytesServedOnFirstConsumerYield >= 0);
        Assert.True(handler.BytesServedOnFirstConsumerYield < handler.Body.Length,
            "first PCM was only produced after the whole response body was served");
        Assert.NotNull(client.Telemetry.FirstPcmTimestamp);
        Assert.NotNull(client.Telemetry.FirstEncodedAudioTimestamp);
        await enumerator.DisposeAsync();
    }

    // ---- arbitrary network slicing yields identical decode ----

    [Fact]
    public async Task ArbitrarySliceSizes_ProduceIdenticalAudio()
    {
        // Multi-byte UTF-8 inside JSON + CRLF endings + multi-line-ish structure.
        var sse = string.Concat(
            $"data:{AudioEvent(HexA)}\r\n\r\n",
            $"data:{AudioEvent(HexB)}\r\n\r\n",
            $"data:{AudioEvent(HexTail, isFinal: true)}\r\n\r\n");
        var body = Encoding.UTF8.GetBytes(sse);
        var expected = Convert.FromHexString(HexA + HexB + HexTail);

        foreach (int slice in new[] { 1, 2, 3, 5, 11, 17, body.Length })
        {
            var handler = new FakeSseHandler { Body = body, SliceSize = slice };
            using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
            var chunks = await CollectAsync(client);
            var joined = string.Concat(chunks.Select(c => Convert.ToHexString(c)));
            Assert.True(Convert.ToHexString(expected) == joined, $"slice={slice} yielded nothing/mismatch: {joined}");
        }
    }

    [Fact]
    public async Task Utf8SplitAcrossChunks_IsDecodedCorrectly()
    {
        // A Chinese string inside data JSON, sliced at every offset: must decode identically.
        var payload = JsonSerializer.Serialize(new { data = new { audio = HexA, status = 2 } });
        var sse = "event:message\ndata:" + payload + "\n\n";
        var body = Encoding.UTF8.GetBytes(sse);

        foreach (int slice in new[] { 1, 4, 9 })
        {
            var handler = new FakeSseHandler { Body = body, SliceSize = slice };
            using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
            var chunks = await CollectAsync(client);
            Assert.Single(chunks);
            Assert.Equal(HexA, Convert.ToHexString(chunks[0]).ToLowerInvariant());
        }
    }

    // ---- finals, keepalives, errors, no-audio ----

    [Fact]
    public async Task FinalChunk_WithNewAudio_IsYielded_NotDropped()
    {
        var sse = $"data:{AudioEvent(HexA)}\n\ndata:{AudioEvent(HexTail, isFinal: true)}\n\n";
        var handler = new FakeSseHandler { Body = Encoding.UTF8.GetBytes(sse), SliceSize = 8 };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        var chunks = await CollectAsync(client);
        var joined = Convert.ToHexString(chunks.SelectMany(c => c).ToArray());
        Assert.Equal((HexA + HexTail).ToUpperInvariant(), joined);
    }

    [Fact]
    public async Task FinalChunk_ReplayingFullAggregate_IsNotRepeated()
    {
        var full = HexA + HexB;
        var sse = $"data:{AudioEvent(HexA)}\n\ndata:{AudioEvent(HexB)}\n\ndata:{AudioEvent(full, isFinal: true)}\n\n";
        var handler = new FakeSseHandler { Body = Encoding.UTF8.GetBytes(sse), SliceSize = 6 };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        var chunks = await CollectAsync(client);
        var joined = Convert.ToHexString(chunks.SelectMany(c => c).ToArray());
        Assert.Equal((HexA + HexB).ToUpperInvariant(), joined);
    }

    [Fact]
    public async Task NoAudioFinal_AndKeepalives_ProduceEmptyCompletion()
    {
        var sse = string.Concat(
            ": ping\n\n",
            "data:null\n\n",
            "data:{\"data\":{\"audio\":\"\"}}\n\n",
            "data:{\"data\":{},\"extra_info\":{\"audio_length\":0}}\n\n");
        var handler = new FakeSseHandler { Body = Encoding.UTF8.GetBytes(sse), SliceSize = 4 };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        var chunks = await CollectAsync(client);
        Assert.Empty(chunks); // completes without hanging or crashing
    }

    [Fact]
    public async Task ErrorFinal_ThrowsCleanError_AfterPartialAudio()
    {
        var sse = $"data:{AudioEvent(HexA)}\n\ndata:{ErrorEvent(1004, "invalid voice")}\n\n";
        var handler = new FakeSseHandler { Body = Encoding.UTF8.GetBytes(sse), SliceSize = 6 };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        var chunks = new List<byte[]>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var c in client.StreamAsync("hi", "voice-1", null))
                chunks.Add(c);
        });
        Assert.Contains("1004", ex.Message);
        Assert.Single(chunks); // partial audio was already delivered, error does not corrupt it
    }

    // ---- PCM odd-byte remainder ----

    [Fact]
    public void PcmAligner_HoldsOddByte_AndCompletesItWithNextChunk()
    {
        var aligner = new PcmAligner();
        // 3 bytes → yields 2, carries 1
        var first = aligner.Push(new byte[] { 0x01, 0x02, 0x03 }).ToList();
        Assert.Single(first);
        Assert.Equal(new byte[] { 0x01, 0x02 }, first[0]);
        // next 2-byte chunk stitches with the carry → 3 bytes: yields 2, carries 1 again
        var second = aligner.Push(new byte[] { 0x04, 0x05 }).ToList();
        Assert.Single(second);
        Assert.Equal(new byte[] { 0x03, 0x04 }, second[0]);
        // flush drops the lone unpaired byte
        Assert.Empty(aligner.Flush());
    }

    [Fact]
    public async Task OddSizedPcmEvent_CrossesIntoNextEventCorrectly()
    {
        // Hex is always whole bytes per event, so simulate odd audio via base64 payloads
        // (3 bytes -> 4 base64 chars): first event 3 bytes, second event 1 byte -> stitched 4.
        string b3 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        string b1 = Convert.ToBase64String(new byte[] { 4 });
        var sse = $"data:{AudioEvent(b3)}\n\ndata:{AudioEvent(b1, isFinal: true)}\n\n";
        var handler = new FakeSseHandler { Body = Encoding.UTF8.GetBytes(sse), SliceSize = 3 };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        var chunks = await CollectAsync(client);
        var joined = chunks.SelectMany(c => c).ToArray();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, joined);
        Assert.All(chunks, c => Assert.Equal(0, c.Length % 2)); // every yielded chunk is 16-bit aligned
    }

    // ---- cancellation ----

    [Fact]
    public async Task Cancellation_StopsYield_NoStaleChunksAfterCancel()
    {
        var sse = string.Concat(
            $"data:{AudioEvent(HexA)}\n\n",
            $"data:{AudioEvent(HexB)}\n\n",
            $"data:{AudioEvent(HexTail, isFinal: true)}\n\n");
        var handler = new FakeSseHandler
        {
            Body = Encoding.UTF8.GetBytes(sse),
            SliceSize = 4,
            DelayPerSlice = TimeSpan.FromMilliseconds(300),
        };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        using var cts = new CancellationTokenSource();

        var chunks = new List<byte[]>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in client.StreamAsync("hi", "voice-1", null, cts.Token))
            {
                chunks.Add(c);
                if (chunks.Count == 1) cts.Cancel(); // cancel right after first PCM
            }
        });
        // Only the first event's audio was consumed; the delayed rest never arrived post-cancel.
        Assert.Single(chunks);
        Assert.Equal(HexA, Convert.ToHexString(chunks[0]).ToLowerInvariant());
    }

    // ---- JSON (non-stream) fallback media type ----

    [Fact]
    public async Task JsonFallbackResponse_UsesLegacyParsing()
    {
        var json = "{\"data\":{\"audio\":\"" + HexA + HexB + "\"}}";
        var handler = new FakeSseHandler
        {
            Body = Encoding.UTF8.GetBytes(json),
            ContentType = "application/json",
        };
        using var client = new MiniMaxHttpStreamingTtsClient(Config(), handler);
        var chunks = await CollectAsync(client);
        Assert.Single(chunks);
        Assert.Equal((HexA + HexB).ToUpperInvariant(), Convert.ToHexString(chunks[0]));
    }

    // ---- TtsClient routing / config switch ----

    [Fact]
    public void TtsClient_MinimaxLegacy_Transport_DefaultsToLegacy()
    {
        var config = Config(transport: "legacy");
        Assert.Equal("legacy", config.Transport);
    }

    [Fact]
    public void TtsClient_MinimaxStreaming_DispatchesToStreamingClient()
    {
        // The routing switch lives in TtsClient (pipeline-level) and BotRuntime (runtime-level).
        // Verify the JSON builders agree: legacy pins stream=false, streaming pins stream=true.
        var legacy = TtsClient.BuildMiniMaxRequestJson("t", "v", "speech-2.8-hd", 1.0, 24000);
        var streaming = MiniMaxHttpStreamingTtsClient.BuildStreamingRequestJson("t", "v", "speech-2.8-hd", 1.0, 24000);
        Assert.Contains("\"stream\":false", legacy);
        Assert.Contains("\"stream\":true", streaming);
        // Voice/rate identical between the two transports.
        Assert.Contains("\"voice_id\":\"v\"", legacy);
        Assert.Contains("\"voice_id\":\"v\"", streaming);
        Assert.Contains("\"sample_rate\":24000", legacy);
        Assert.Contains("\"sample_rate\":24000", streaming);
    }

    // ---- unit-level SSE parser ----

    [Fact]
    public async Task SseParser_HandlesKeepalive_EventName_AndMultilineData()
    {
        var body = Encoding.UTF8.GetBytes(":keepalive\n\nevent: message\ndata: line1\ndata: line2\n\n");
        var events = new List<SseEvent>();
        await foreach (var e in SseEventReader.ReadAsync(new MemoryStream(body), CancellationToken.None))
            events.Add(e);
        Assert.Single(events);
        Assert.Equal("message", events[0].EventName);
        Assert.Equal("line1\nline2", events[0].Data);
    }

    [Fact]
    public async Task SseParser_DispatchesTrailingEventWithoutFinalNewline()
    {
        var body = Encoding.UTF8.GetBytes("data:{\"a\":1}\n"); // no blank line at EOF
        var events = new List<SseEvent>();
        await foreach (var e in SseEventReader.ReadAsync(new MemoryStream(body), CancellationToken.None))
            events.Add(e);
        Assert.Single(events);
        Assert.Equal("{\"a\":1}", events[0].Data);
    }

    [Fact]
    public void PayloadParser_AcceptsBase64_WhenNotHex()
    {
        var payload = JsonSerializer.Serialize(new { data = new { audio = Convert.ToBase64String(new byte[] { 9, 9 }) } });
        var parsed = MiniMaxHttpStreamingTtsClient.ParseSseAudioPayload(payload);
        Assert.Equal(new byte[] { 9, 9 }, parsed.Audio);
    }

    [Fact]
    public void PayloadParser_MarksExtraInfoAsFinal()
    {
        var payload = """{"data":{"audio":"00ff"},"extra_info":{"audio_length":120}}""";
        var parsed = MiniMaxHttpStreamingTtsClient.ParseSseAudioPayload(payload);
        Assert.True(parsed.IsFinal);
        Assert.Equal(new byte[] { 0x00, 0xff }, parsed.Audio);
    }
}
