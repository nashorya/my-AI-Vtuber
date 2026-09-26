using System.Net;
using System.Text;
using System.Threading.Channels;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Bot.Turns;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.RealtimeAsr;
using AIVTuber.Core.Vision;

namespace AIVTuber.Tests;

/// <summary>
/// RT-07 integration gates (plan §6.1) — all offline, fake transports only.
///
/// Composes the real production pieces introduced by RT-01..RT-06 / VIS-02 behind their
/// fake infra (FakeRealtimeAsr, stub HttpMessageHandler for LLM/TTS, scripted VLM) and
/// proves the structural pipeline properties end-to-end:
///   gate 1  ASR receives a prefix and can publish a partial while input is still flowing;
///   gate 2  first approved speech segment reaches TTS before the LLM stream EOFs;
///   gate 3  TTS audio is consumable before the HTTP response completes;
///   gate 4  a pending (hanging) VLM request never delays the voice turn;
///   gate 5  a cancelled generation produces no new public output afterwards;
/// switch matrix: every new switch ON at once still completes one fake turn
/// (all-OFF equivalence is covered by the pre-existing suite running on default config).
/// </summary>
public sealed class RealtimeGateIntegrationTests
{
    // ---------- shared fakes ----------

    /// <summary>SSE body whose chunks are pushed by the test; stays open until Done().</summary>
    private sealed class GatedSseStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        public void Push(string s) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(s));
        public void Done() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var chunk = await _chunks.Reader.ReadAsync(ct);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GatedHttpHandler : HttpMessageHandler
    {
        public readonly GatedSseStream Stream = new();
        public HttpRequestMessage? Request;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(Stream)
                {
                    Headers = { ContentType = new("text/event-stream") },
                },
            });
        }
    }

    private sealed class FakeVisionClock : IVisionClock
    {
        public long NowMs { get; set; }
    }

    /// <summary>Minimal hash-scripted capture source (same shape as VisionObservationTests).</summary>
    private sealed class ScriptedHashCaptureSource : IWindowCaptureSource
    {
        public WindowIdentity? Probe;
        public Queue<ulong> Hashes = new();
        public int CaptureCalls;
        public FakeVisionClock? Clock { get; set; }

        public WindowIdentity? ProbeWindow(long hwnd) => Probe;

        public CaptureResult Capture(WindowTarget target, VisionConfig config)
        {
            CaptureCalls++;
            if (Hashes.Count == 0) return CaptureResult.Fail(CaptureStatus.WindowClosed, "script empty");
            var hash = Hashes.Dequeue();
            return CaptureResult.Ok(new CapturedFrame
            {
                FrameId = $"f-{CaptureCalls}-{hash}",
                SourceWindowId = target.SourceWindowId,
                CaptureEpoch = target.CaptureEpoch,
                CapturedAtMs = Clock!.NowMs,
                ContentHash = hash,
                ChangedSinceLast = true,
                Jpeg = [0xFF, 0xD8, 0x01],
            });
        }

        public void Dispose() { }
    }

    private sealed class HangingVisionClient : IVisionClient
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;

        public async Task<VisionClientResult> ObserveAsync(VisionFrameRequest request, CancellationToken ct)
        {
            Requests++;
            var completed = await Task.WhenAny(Release.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            Returned.TrySetResult();
            if (completed != Release.Task) throw new OperationCanceledException(ct);
            return VisionClientResult.Fail(VisionClientStatus.HttpError, "released");
        }

        public void Dispose() { }
    }

    private static string Sse(string content) =>
        "data: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content } } },
        }) + "\n\n";

    private static string TtsAudioEvent(string hex, bool isFinal = false) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?> { ["audio"] = hex },
            ["is_final"] = isFinal,
        });

    private static TtsConfig StreamingTtsConfig() => new()
    {
        Provider = "minimax",
        ApiKey = "test-key",
        VoiceId = "voice-1",
        Model = "speech-2.8-hd",
        Transport = "streaming",
        SampleRate = 24000,
    };

    private static VisionConfig EnabledVisionConfig() => new()
    {
        Enabled = true,
        Model = "fake-vision-model",
        MinRequestIntervalMs = 0,
    };

    // ---------- switch matrix: all new switches ON ----------

    [Fact]
    public async Task SwitchMatrix_AllNewSwitchesOn_CompletesFakeEndToEndTurn()
    {
        // Config matrix — every RT/VIS switch explicitly on (all fake transports).
        var realtime = new RealtimeConfig
        {
            InferenceMode = "cloud_only",
            StreamingAsrEnabled = true,          // RT-02/03
            TurnManagerV2Enabled = true,         // RT-04
            SpeculativeGenerationEnabled = false, // plan: default closed even in the ON matrix
            TraceEnabled = true,
        };
        var ttsConfig = StreamingTtsConfig();     // RT-01 streaming transport
        var visionConfig = EnabledVisionConfig(); // VIS-01/02

        // --- vision layer: background VLM request hangs for the whole test (gate 4) ---
        var clock = new FakeVisionClock();
        var captureSource = new ScriptedHashCaptureSource { Clock = clock };
        using var capture = new WindowCaptureService(captureSource, visionConfig, clock);
        using var vision = new HangingVisionClient();
        using var worker = new VisionObservationWorker(visionConfig, capture, vision, clock);
        captureSource.Probe = new WindowIdentity(1, 42, "game", "Game", 800, 600);
        capture.SelectWindow(1);
        captureSource.Hashes.Enqueue(100);
        capture.Tick();
        var visionPump = Task.Run(() => worker.PumpAsync(CancellationToken.None)); // hangs, in flight

        // --- gate 1: streaming ASR publishes a partial while frames are still flowing ---
        var factory = new FakeRealtimeAsrSessionFactory();
        await using var pump = new RealtimeAsrPump(AIVTuber.Core.Audio.AudioSource.Microphone, factory);
        var partialSeen = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalSeen = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.PartialUpdate += (_, u) => partialSeen.TrySetResult(u);
        pump.FinalCommitted += (_, u) => finalSeen.TrySetResult(u);

        var halfUtterance = RealtimeAsrTestHelpers.Frame(300);
        factory.AutoPartialAtBytes = halfUtterance.Length; // partial fires mid-utterance
        pump.OnCapturedFrame(halfUtterance, voicedHint: true);

        var partial = await partialSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(partial.IsFinal);
        Assert.Equal("我觉", partial.TextSnapshot);
        // Frames keep flowing — the utterance is NOT finished yet at this point.

        // --- turn manager v2 turns the final into a Ready turn (name call + question) ---
        var managerClock = new FakeTurnClock();
        using var scheduler = new ManualTurnScheduler();
        using var manager = new TurnManagerV2(
            new TurnManagerOptions { SelfNames = ["可缇"] }, managerClock, scheduler);
        var turnReady = new TaskCompletionSource<(TurnContextV2 Ctx, IReadOnlyList<TalkLine> Lines)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TurnReady += (ctx, lines) => turnReady.TrySetResult((ctx, lines));
        manager.ObservePartial(TurnSource.Microphone, partial.TextSnapshot);

        var session = factory.Last;
        session.Emit(new TranscriptUpdate(
            AIVTuber.Core.Audio.AudioSource.Microphone, session.CaptureEpoch, session.ProviderSessionId,
            "1", 1, "可缇，你怎么看？", IsFinal: true,
            0, halfUtterance.Length * 2 / RealtimeAsrOptions.BytesPerMs,
            Environment.TickCount64, session.Opponent));
        session.Complete();

        var final = await finalSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(final.IsFinal);

        manager.AddFinal(new TalkLine(TalkIdentity.Self, "使用者", final.TextSnapshot, null),
            TurnSource.Microphone);
        var (ctx, lines) = await turnReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(manager.CanCommit(ctx.GenerationId));

        // --- gates 2+3: reply protocol v2 first speech → streaming TTS first PCM, both
        // before their upstream (LLM EOF / HTTP response completion) — while VLM hangs ---
        var llmHandler = new GatedHttpHandler();
        using var llm = new LlmClient("角色", () => ["headYaw"], llmHandler, replyProtocol: "v2");
        var ttsHandler = new GatedHttpHandler();
        using var tts = new MiniMaxHttpStreamingTtsClient(StreamingTtsConfig(), ttsHandler);

        var firstSpeech = new TaskCompletionSource<ReplyStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPcm = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ttsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submittedTexts = new List<string>();

        var llmTask = Task.Run(async () =>
        {
            await foreach (var ev in llm.StreamEventsAsync([], "可缇，你怎么看？"))
            {
                if (ev.Kind == ReplyStreamEventKind.Speech && manager.CanCommit(ctx.GenerationId))
                {
                    lock (submittedTexts) submittedTexts.Add(ev.Text!);
                    if (firstSpeech.Task.IsCompleted == false) firstSpeech.TrySetResult(ev);
                }
            }
            llmDone.TrySetResult();
        });

        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n"));
        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"我觉得这波先别冲。\"}\n"));
        var speech0 = await firstSpeech.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // As soon as the first segment is approved, TTS is invoked — LLM still streaming.
        var ttsTask = Task.Run(async () =>
        {
            await foreach (var chunk in tts.StreamAsync(speech0.Text!, ttsConfig.VoiceId, null))
                if (firstPcm.Task.IsCompleted == false) firstPcm.TrySetResult(chunk);
            ttsDone.TrySetResult();
        });
        ttsHandler.Stream.Push($"data:{TtsAudioEvent("0011ff7f")}\n\n");
        await firstPcm.Task.WaitAsync(TimeSpan.FromSeconds(5));   // gate 3: before response completes
        Assert.False(llmDone.Task.IsCompleted);                    // gate 2: before LLM EOF

        // Finish both streams.
        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"等对面的技能交完。\"}\n"));
        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"end\"}\n"));
        llmHandler.Stream.Push("data: [DONE]\n");
        llmHandler.Stream.Done();
        await llmDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ttsHandler.Stream.Push($"data:{TtsAudioEvent("22334455", isFinal: true)}\n\n");
        ttsHandler.Stream.Done();
        await ttsDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The hanging VLM never blocked anything (gate 4) and received exactly one request.
        Assert.Equal(1, vision.Requests);
        Assert.False(vision.Returned.Task.IsCompleted, "VLM request should still be hanging");
        await visionPump; // pump itself returned after dispatching the single in-flight request
        vision.Release.TrySetResult();

        lock (submittedTexts)
        {
            Assert.Equal(["我觉得这波先别冲。", "等对面的技能交完。"], submittedTexts);
        }
        manager.CompleteTurn(ctx.GenerationId);
        Assert.Equal("cloud_only", realtime.InferenceMode); // matrix sanity
    }

    // ---------- gate 5: cancelled generation produces no new public output ----------

    [Fact]
    public async Task Gate5_CancelledGeneration_ProducesNoNewPublicOutput()
    {
        var managerClock = new FakeTurnClock();
        using var scheduler = new ManualTurnScheduler();
        using var manager = new TurnManagerV2(
            new TurnManagerOptions { SelfNames = ["可缇"] }, managerClock, scheduler);
        var turnReady = new TaskCompletionSource<TurnContextV2>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TurnReady += (ctx, _) => turnReady.TrySetResult(ctx);
        manager.AddFinal(new TalkLine(TalkIdentity.Self, "使用者", "可缇，你怎么看？", null),
            TurnSource.Microphone);
        var ctx = await turnReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(manager.CanCommit(ctx.GenerationId));

        var llmHandler = new GatedHttpHandler();
        using var llm = new LlmClient("角色", () => ["headYaw"], llmHandler, replyProtocol: "v2");
        using var tts = new MiniMaxHttpStreamingTtsClient(StreamingTtsConfig(), new GatedHttpHandler());

        var submitted = new List<string>();
        var firstSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmTask = Task.Run(async () =>
        {
            await foreach (var ev in llm.StreamEventsAsync([], "可缇，你怎么看？"))
                if (ev.Kind == ReplyStreamEventKind.Speech && manager.CanCommit(ctx.GenerationId))
                {
                    lock (submitted) submitted.Add(ev.Text!);
                    firstSpeech.TrySetResult();
                }
            llmDone.TrySetResult();
        });

        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n"));
        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"我觉得这波先别冲。\"}\n"));
        await firstSpeech.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (submitted) Assert.Equal(["我觉得这波先别冲。"], submitted);

        // Human voice resumed → generation cancelled. Old CanCommit-gated submit path must
        // reject everything the (still-open) LLM stream produces afterwards.
        manager.NoteVoiceActivity(TurnSource.Microphone, active: true);
        // advance through the noise gate so the human-voice cancellation is fully applied
        scheduler.Elapse(managerClock, 1000);
        Assert.False(manager.CanCommit(ctx.GenerationId));

        var before = submitted.Count;
        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"等对面的技能交完。\"}\n"));
        llmHandler.Stream.Push(Sse("{\"v\":2,\"type\":\"end\"}\n"));
        llmHandler.Stream.Push("data: [DONE]\n");
        llmHandler.Stream.Done();
        await llmDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (submitted) Assert.Equal(before, submitted.Count); // no new public output after cancel
        await llmTask; // no unobserved faults
    }
}
