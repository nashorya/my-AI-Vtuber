using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.RealtimeAsr;
using AIVTuber.Core.RealtimeTts;

namespace AIVTuber.Tests.RealtimeTts;

// ---------------------------------------------------------------------------
// Fake 双向 transport：脚本化服务端行为（自动 ack / 吞 ack / 断线 / 迟到包）。
// 所有测试均为 fake transport 契约测试 —— 真实 MiniMax 端点未实测（无 Key）。
// ---------------------------------------------------------------------------

public sealed class FakeBidiTransport : IWebSocketTransport
{
    public Uri? ConnectedUri;
    public IReadOnlyDictionary<string, string>? Headers;
    public readonly List<(string Type, string Json)> Sent = [];
    public readonly List<string> ContinuedTexts = [];

    public bool AutoAckStart = true;
    public bool AutoAudioOnFlush = true;   // flush 时推送 2 个音频块再 ack
    public bool AutoAckFlush = true;
    public bool AutoAckCancel = true;
    public int DelayFlushAckMs;            // >0：延迟 ack（模拟 flush 期间插话）
    /// <summary>每个 transport 的音频身份标记（验证不串语音/迟到包归属）。</summary>
    public byte[] AudioChunk { get; set; } = [0x01, 0x02, 0x03, 0x04];

    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        ConnectedUri = uri;
        Headers = headers;
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, bool asText, CancellationToken cancellationToken)
    {
        var json = Encoding.UTF8.GetString(buffer.Span);
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString()!;
        if (type == "task_continue")
            ContinuedTexts.Add(doc.RootElement.GetProperty("text").GetString()!);
        lock (Sent) Sent.Add((type, json));

        switch (type)
        {
            case "task_start":
                if (AutoAckStart) PushServerMessage("""{"type":"task_started"}""");
                break;
            case "task_flush":
                if (AutoAudioOnFlush)
                {
                    PushAudio(AudioChunk);
                    PushAudio(AudioChunk);
                }
                if (AutoAckFlush)
                {
                    if (DelayFlushAckMs > 0)
                    {
                        // 服务端侧延迟 ack：异步推送，不阻塞客户端发送循环。
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(DelayFlushAckMs, CancellationToken.None);
                            PushServerMessage("""{"type":"task_flushed"}""");
                        });
                    }
                    else PushServerMessage("""{"type":"task_flushed"}""");
                }
                break;
            case "task_cancel":
                if (AutoAckCancel) PushServerMessage("""{"type":"task_canceled"}""");
                break;
            case "task_finish":
                PushServerMessage("""{"type":"task_finished"}""");
                break;
        }
    }

    public async ValueTask<WebSocketTransportResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var data = await _incoming.Reader.ReadAsync(cancellationToken);
            return new WebSocketTransportResult(data, true);
        }
        catch (ChannelClosedException)
        {
            return new WebSocketTransportResult([], true); // 连接关闭
        }
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _incoming.Writer.TryComplete();

    public void PushServerMessage(string json) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
    public void PushAudio(byte[] pcm, string format = "pcm") =>
        PushServerMessage("{\"type\":\"audio\",\"data\":{\"audio\":\"" + Convert.ToHexString(pcm) + "\",\"audio_format\":\"" + format + "\",\"is_final\":false}}");
    /// <summary>模拟服务端断线：关闭下行通道（已推送数据仍会先被读走）。</summary>
    public void SimulateDisconnect() => _incoming.Writer.TryComplete();

    public List<string> SentTypes { get { lock (Sent) return Sent.Select(s => s.Type).ToList(); } }
    public string SentJson(string type) { lock (Sent) return Sent.First(s => s.Type == type).Json; }
    public int CountOf(string type) { lock (Sent) return Sent.Count(s => s.Type == type); }
}

public sealed class FakeBidiTransportFactory : IWebSocketTransportFactory
{
    public readonly List<FakeBidiTransport> Created = [];
    public IWebSocketTransport Create()
    {
        var t = new FakeBidiTransport();
        lock (Created) Created.Add(t);
        return t;
    }
    public FakeBidiTransport First { get { lock (Created) return Created[0]; } }
    public FakeBidiTransport Latest { get { lock (Created) return Created[^1]; } }
}

public sealed class MiniMaxBidiTtsClientTests
{

    private static TtsConfig Cfg(double maxBacklogSeconds = 30, int keepAliveMs = 0) => new()
    {
        Provider = "minimax",
        Transport = "bidi",
        ApiKey = "test-key",
        VoiceId = "voice-a",
        Model = "",
        SampleRate = 24000,
        BidiHost = "tts.example.test",
        BidiCancelAckTimeoutMs = 400,
        BidiMaxBacklogSeconds = maxBacklogSeconds,
        BidiKeepAliveIntervalMs = keepAliveMs,
    };

    private static MiniMaxBidiTtsClient Client(TtsConfig cfg, FakeBidiTransportFactory factory,
        RealtimeTrace? trace = null) => new(cfg, trace, factory);

    private static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var deadline = Environment.TickCount64 + timeout.Value.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "条件等待超时");
    }

    private static async Task<List<byte[]>> ConsumeAsync(IAsyncEnumerable<byte[]> stream)
    {
        var chunks = new List<byte[]>();
        await foreach (var c in stream) chunks.Add(c);
        return chunks;
    }

    /// <summary>边收边入列（断言“取消前已交付”时不能用整体 AddRange）。</summary>
    private static async Task CollectIntoAsync(IAsyncEnumerable<byte[]> stream, List<byte[]> sink)
    {
        await foreach (var c in stream) sink.Add(c);
    }

    // ---------------------------------------------------------------- fixture 锁定

    [Fact]
    public async Task ConfirmOrder_AndUri_AreLockedByFixture()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);

        var chunks = await ConsumeAsync(tts.StreamAsync("你好，世界", "voice-a", null));

        var t = factory.First;
        // 确认顺序：start → continue → flush（本测试无 cancel/finish）。
        Assert.Equal(["task_start", "task_continue", "task_flush"], t.SentTypes);
        // 主机来自配置，不猜主机名；路径为官方双向路径。
        Assert.Equal("wss://tts.example.test/ws/v1/t2a_v2_bidi", t.ConnectedUri!.ToString());
        Assert.Equal("Bearer test-key", t.Headers!["Authorization"]);
        // task_start 固定会话配置（voice/model/采样率）。
        var startJson = t.SentJson("task_start");
        Assert.Contains("\"voice_id\":\"voice-a\"", startJson);
        Assert.Contains("\"model\":\"speech-2.8-hd\"", startJson);
        Assert.Contains("\"sample_rate\":24000", startJson);
        // flush 前下发的 2 块音频全部到达（音频已下发 ≠ 已播放，由上层账本结算）。
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(4, c.Length));
        // 单 socket：一次投递不重建连接。
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task MissingHost_ThrowsWithExplicitFallbackGuidance()
    {
        var cfg = Cfg();
        cfg.BidiHost = "";
        var ex = Assert.Throws<InvalidOperationException>(() => Client(cfg, new FakeBidiTransportFactory()));
        // 明确报“未配置”，并给出显式回退说明（不静默切换传输）。
        Assert.Contains("bidi_host", ex.Message);
        Assert.Contains("streaming", ex.Message);
    }

    [Fact]
    public void BuildUri_EmptyHost_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TtsBidiProtocol.BuildUri(" "));
        Assert.Equal("wss://h.example/ws/v1/t2a_v2_bidi",
            TtsBidiProtocol.BuildUri("h.example", "/ws/v1/t2a_v2_bidi").ToString());
    }

    // ---------------------------------------------------------------- 取消屏障

    [Fact]
    public async Task Cancel_AudioInterleavedAroundBarrier_LocalStopsAndNextTurnWorksOnSameSocket()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);
        await tts.BeginTurnAsync(CancellationToken.None);
        var t = factory.First;
        t.AutoAudioOnFlush = false; // 服务端按住音频，模拟在途合成
        t.AutoAckFlush = false;     // flush 未确认：投递单元保持打开

        var collected = new List<byte[]>();
        var consume = Task.Run(async () =>
        {
            try { await CollectIntoAsync(tts.StreamAsync("第一句", "voice-a", null), collected); }
            catch (OperationCanceledException) { }
        });
        await UntilAsync(() => t.CountOf("task_flush") == 1);

        // 取消前最后一刻到达的音频：属于旧回合，可以交付（消费者随取消停播）。
        t.PushAudio([0x0A, 0x0B]);
        await UntilAsync(() => collected.Count == 1);

        var outcome = await tts.CancelPendingAsync();

        Assert.Equal(TtsCancelOutcome.ServerConfirmed, outcome);
        // 取消后服务端残留继续下发：必须被丢弃，不得进入任何回合。
        t.PushAudio([0x0C, 0x0D]);
        await Task.Delay(100);
        Assert.Single(collected);

        await consume.WaitAsync(TimeSpan.FromSeconds(5));
        // 取消发生在 flush 之前，但下一轮不因它卡死：同 socket 继续。
        t.AutoAudioOnFlush = true;
        t.AutoAckFlush = true;
        await tts.BeginTurnAsync(CancellationToken.None);
        var second = await ConsumeAsync(tts.StreamAsync("第二句", "voice-a", null));
        Assert.Equal(2, second.Count);
        Assert.Single(factory.Created);
        // 顺序锁定：cancel 在第一轮 flush 之后、第二轮 continue 之前。
        var types = t.SentTypes;
        Assert.Equal("task_cancel", types[3]);
        Assert.Equal("task_continue", types[4]);
    }

    [Fact]
    public async Task CancelAckLost_RebuildsEpoch_OldSocketLatePacketsNeverReachNewTurn()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);
        await tts.BeginTurnAsync(CancellationToken.None);
        var old = factory.First;
        old.AutoAudioOnFlush = false;
        old.AutoAckCancel = false; // 吞掉 cancel 确认

        var consume = Task.Run(async () =>
        {
            try { await ConsumeAsync(tts.StreamAsync("被打断的句子", "voice-a", null)); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        });
        await UntilAsync(() => old.CountOf("task_flush") == 1);

        var outcome = await tts.CancelPendingAsync();

        Assert.Equal(TtsCancelOutcome.EpochRebuilt, outcome);
        Assert.Equal(2, factory.Created.Count); // 旧 socket 已断，新 socket 新建

        // 回归用例：旧 socket 上的迟到音频不得归入新回合。
        old.PushAudio([0xEE, 0xEE]);
        var @new = factory.Latest;
        @new.AudioChunk = [0x0B, 0x0B];
        await tts.BeginTurnAsync(CancellationToken.None);
        var fresh = await ConsumeAsync(tts.StreamAsync("新回合", "voice-a", null));
        Assert.Equal(2, fresh.Count);
        Assert.All(fresh, c => Assert.True(c.AsSpan().SequenceEqual<byte>([0x0B, 0x0B])));
        // 新 socket 上重新 task_start；旧 socket 没有再收到任何文本。
        Assert.Equal(1, old.CountOf("task_continue"));
        Assert.Equal(1, @new.CountOf("task_start"));
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InterruptDuringSlowFlush_NextTurnIsNotStuckBehindBarrier()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);
        await tts.BeginTurnAsync(CancellationToken.None);
        var t = factory.First;
        t.DelayFlushAckMs = 60_000; // flush 确认迟迟不来，期间用户插话

        var consume = Task.Run(async () =>
        {
            try { await ConsumeAsync(tts.StreamAsync("慢速回合", "voice-a", null)); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        });
        await UntilAsync(() => t.CountOf("task_flush") == 1);

        var outcome = await tts.CancelPendingAsync();

        Assert.Equal(TtsCancelOutcome.ServerConfirmed, outcome);
        // 下一轮不必等上一轮 flush 确认。
        t.DelayFlushAckMs = 0;
        await tts.BeginTurnAsync(CancellationToken.None);
        var next = await ConsumeAsync(tts.StreamAsync("下一轮", "voice-a", null));
        Assert.Equal(2, next.Count);
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---------------------------------------------------------------- 失败与断线

    [Fact]
    public async Task TaskFailed_FailsClosed_AndDoesNotReplayDeliveredText()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);
        await tts.BeginTurnAsync(CancellationToken.None);
        var t = factory.First;
        t.AutoAudioOnFlush = false;
        t.AutoAckFlush = false;

        var collected = new List<byte[]>();
        var consume = Task.Run(async () =>
        {
            try { await CollectIntoAsync(tts.StreamAsync("会失败的句子", "voice-a", null), collected); }
            catch (InvalidOperationException ex)
            {
                Assert.Contains("voice not found", ex.Message);
            }
        });
        await UntilAsync(() => t.CountOf("task_flush") == 1);

        t.PushAudio([0x01, 0x02]);          // 已收到部分音频
        await UntilAsync(() => collected.Count == 1);
        t.PushServerMessage("""{"type":"task_failed","code":1004,"message":"voice not found"}""");

        await consume.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(collected);
        await Task.Delay(100);
        // 已收到音频后不得重发整段文本：没有自动补发的 task_continue。
        Assert.Equal(1, t.CountOf("task_continue"));

        // 下一回合在新 socket 上重建（旧会话 Failed）。
        await tts.BeginTurnAsync(CancellationToken.None);
        var next = await ConsumeAsync(tts.StreamAsync("重试回合", "voice-a", null));
        Assert.Equal(2, next.Count);
        Assert.Equal(2, factory.Created.Count);
    }

    [Fact]
    public async Task FirstAudioThenDisconnect_KeepsDeliveredAudio_NoResubmit_RebuildsForNextTurn()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);
        await tts.BeginTurnAsync(CancellationToken.None);
        var t = factory.First;
        t.AutoAudioOnFlush = false;
        t.AutoAckFlush = false;

        var collected = new List<byte[]>();
        var consume = Task.Run(async () =>
        {
            try { await CollectIntoAsync(tts.StreamAsync("首包后断网", "voice-a", null), collected); }
            catch (InvalidOperationException ex) { Assert.Contains("connection_lost", ex.Message); }
        });
        await UntilAsync(() => t.CountOf("task_flush") == 1);

        t.PushAudio([0x05, 0x06]);
        await UntilAsync(() => collected.Count == 1);
        t.SimulateDisconnect();             // 首包之后服务端断线

        await consume.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(collected);
        await Task.Delay(100);
        Assert.Equal(1, t.CountOf("task_continue")); // 不重发整段

        // 长静默/断线后重连：新 socket、新 task_start。
        await tts.BeginTurnAsync(CancellationToken.None);
        var next = await ConsumeAsync(tts.StreamAsync("重连回合", "voice-a", null));
        Assert.Equal(2, next.Count);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(1, factory.Latest.CountOf("task_start"));
    }

    // ---------------------------------------------------------------- 会话管理

    [Fact]
    public async Task RapidVoiceChange_RebuildsSessionWithNewPinnedVoice()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);

        await ConsumeAsync(tts.StreamAsync("旧音色", "voice-a", null));
        // task_start 后会话配置稳定：换音色 = 安全重建，不在旧连接上热改。
        await ConsumeAsync(tts.StreamAsync("新音色", "voice-b", null));

        Assert.Equal(2, factory.Created.Count);
        Assert.Contains("\"voice_id\":\"voice-a\"", factory.First.SentJson("task_start"));
        Assert.Contains("\"voice_id\":\"voice-b\"", factory.Latest.SentJson("task_start"));
    }

    [Fact]
    public async Task TwoAdjacentTurns_SameSocket_StrayAudioDoesNotCrossTurns()
    {
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory);
        await tts.BeginTurnAsync(CancellationToken.None);
        var t = factory.First;

        var first = await ConsumeAsync(tts.StreamAsync("第一轮", "voice-a", null));
        Assert.Equal(2, first.Count);

        // 第一轮 flush 确认后、第二轮提交前到达的音频：无主 straggler，丢弃。
        t.PushAudio([0x09, 0x09]);
        await Task.Delay(60);

        await tts.BeginTurnAsync(CancellationToken.None);
        var second = await ConsumeAsync(tts.StreamAsync("第二轮", "voice-a", null));

        Assert.Single(factory.Created);   // 紧邻两轮同 socket，不重建
        Assert.Equal(2, second.Count);
        Assert.All(second, c => Assert.True(c.AsSpan().SequenceEqual<byte>(t.AudioChunk)));
    }

    // ---------------------------------------------------------------- 积压与保活

    [Fact]
    public async Task Backlog_PausesTextSending_ControlMessagesBypass()
    {
        var factory = new FakeBidiTransportFactory();
        var coordinator = new TtsSessionCoordinator(factory, new TtsSessionCoordinatorOptions
        {
            Host = "tts.example.test",
            ApiKey = "k",
            VoiceId = "voice-a",
            MaxBacklogSeconds = 0.01,            // 第一段文本(0.009s)不积压，第二段越过阈值
            SecondsPerCharEstimate = 0.001,
            CancelAckTimeout = TimeSpan.FromMilliseconds(400),
        });
        using var _ = coordinator;

        await coordinator.StartAsync(CancellationToken.None);
        var t = factory.First;
        t.AutoAudioOnFlush = false;
        t.AutoAckFlush = false;                  // flush 不确认：积压无法清零

        var backlogged = false;
        coordinator.BacklogStateChanged += b => { if (b) backlogged = true; };

        await coordinator.SubmitTextAsync("第一段会积压的文本", CancellationToken.None);
        await UntilAsync(() => t.CountOf("task_continue") == 1);
        await coordinator.SubmitTextAsync("第二段应被暂停的文本", CancellationToken.None);
        await UntilAsync(() => backlogged);
        await Task.Delay(150);

        // 文本积压：第二段 continue 没有发出。
        Assert.Equal(1, t.CountOf("task_continue"));
        // 控制消息越过积压：取消立刻上线。
        var outcome = await coordinator.CancelActiveTurnAsync();
        Assert.Equal(TtsCancelOutcome.ServerConfirmed, outcome);
        await UntilAsync(() => t.CountOf("task_cancel") == 1);
        Assert.Equal(1, t.CountOf("task_continue")); // 取消不排队等文本排空
    }

    [Fact]
    public async Task KeepAlive_SentOnlyWhenEnabled()
    {
        var off = new FakeBidiTransportFactory();
        using (var tts = Client(Cfg(), off))
        {
            await tts.BeginTurnAsync(CancellationToken.None);
            await Task.Delay(120);
            Assert.Equal(0, off.First.CountOf("keepalive")); // 默认关闭（协议未验证）
        }

        var on = new FakeBidiTransportFactory();
        using (var tts = Client(Cfg(keepAliveMs: 20), on))
        {
            await tts.BeginTurnAsync(CancellationToken.None);
            await UntilAsync(() => on.First.CountOf("keepalive") >= 1);
        }
    }

    // ---------------------------------------------------------------- 打点

    [Fact]
    public async Task Trace_RecordsTtsRequestEncodedPcmAndFlushAck()
    {
        var trace = new RealtimeTrace(enabled: true, new FakeRealtimeClock());
        var factory = new FakeBidiTransportFactory();
        using var tts = Client(Cfg(), factory, trace);

        await tts.BeginTurnAsync(CancellationToken.None);
        await ConsumeAsync(tts.StreamAsync("打点", "voice-a", null));

        var names = trace.EventsSnapshot.Select(e => e.Event).ToList();
        Assert.Contains(RealtimeTrace.Events.TtsRequest, names);
        Assert.Contains(RealtimeTrace.Events.TtsFirstEncodedAudio, names);
        Assert.Contains(RealtimeTrace.Events.TtsFirstPcm, names);
        Assert.Contains(RealtimeTrace.Events.TtsFlushAcked, names);
        // 顺序：request < first_encoded_audio < first_pcm。
        Assert.True(trace.EventsSnapshot.First(e => e.Event == RealtimeTrace.Events.TtsRequest).MonotonicMs <=
                    trace.EventsSnapshot.First(e => e.Event == RealtimeTrace.Events.TtsFirstEncodedAudio).MonotonicMs);
    }

    [Fact]
    public async Task OrchestratorInterrupt_MarksRealCancelAck_FromBidiController()
    {
        await RunInterruptTraceTest(TtsCancelOutcome.ServerConfirmed,
            expectAcked: true, expectEpochRebuild: false);
        await RunInterruptTraceTest(TtsCancelOutcome.EpochRebuilt,
            expectAcked: false, expectEpochRebuild: true);
    }

    private static async Task RunInterruptTraceTest(TtsCancelOutcome outcome, bool expectAcked, bool expectEpochRebuild)
    {
        var trace = new RealtimeTrace(enabled: true, new FakeRealtimeClock());
        using var player = new AudioPlayer();
        var tts = new ControlledBidiTts(outcome);
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), new NoopLlm(), tts, player, new TtsConfig(), null, null,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } },
            () => { }, triggerHotkeyAsync: null);
        orchestrator.Trace = trace;

        orchestrator.Interrupt();

        var names = trace.EventsSnapshot.Select(e => e.Event).ToList();
        Assert.Contains(RealtimeTrace.Events.CancelRequested, names);
        Assert.Contains(RealtimeTrace.Events.PlaybackStopped, names);
        if (expectAcked)
        {
            Assert.Contains(RealtimeTrace.Events.CancelAcked, names);
            Assert.DoesNotContain(RealtimeTrace.Events.CancelEpochRebuild, names);
        }
        if (expectEpochRebuild)
        {
            Assert.Contains(RealtimeTrace.Events.CancelEpochRebuild, names);
            Assert.DoesNotContain(RealtimeTrace.Events.CancelAcked, names);
        }
        await Task.CompletedTask;
    }

    private sealed class ControlledBidiTts(TtsCancelOutcome outcome) : ITtsClient, IBidiTtsController
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task BeginTurnAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void EndTurn() { }
        public Task<TtsCancelOutcome> CancelPendingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(outcome);
    }

    private sealed class NoopLlm : ILlmClient
    {
        public event EventHandler<string>? OnEmotionDetected { add { } remove { } }
        public event EventHandler<string>? OnActionDetected { add { } remove { } }
        public event EventHandler<string>? OnPoseDetected { add { } remove { } }
        public event EventHandler<string>? OnSentenceReady { add { } remove { } }
        public IAsyncEnumerable<string> StreamAsync(List<Message> history, string userInput, CancellationToken ct)
            => AsyncEnumerableEmpty();
        private static async IAsyncEnumerable<string> AsyncEnumerableEmpty()
        {
            await Task.CompletedTask;
            yield break;
        }
        public void Dispose() { }
    }

    private sealed class UnusedAsr : IAsrClient
    {
        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
            => Task.FromResult(new AsrResult("unused"));
        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
            IAsyncEnumerable<byte[]> audioStream,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
