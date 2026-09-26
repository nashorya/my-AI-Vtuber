using AIVTuber.Core.Audio;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Tests;

/// <summary>
/// RT-02 acceptance tests on the pump wiring: partial flows while the utterance is still
/// being spoken (before any VAD SpeechDetected), two sources stay separate, revisions do not
/// duplicate into history, late packets do not cross epochs, memory stays bounded during
/// silence, and long utterances are not truncated.
/// </summary>
public class RealtimeAsrPumpTests
{
    private static RealtimeAsrOptions NoIdle(params object?[] _) => new()
    {
        IdleDisconnectMs = 0,
        BufferCapacityMs = 4000,
        PrerollMs = 300,
    };

    private static TranscriptUpdate Upd(long epoch, string sess, string seg, int rev, string text, bool final = false) =>
        new(AudioSource.Microphone, epoch, sess, seg, rev, text, final, 0, 0, Environment.TickCount64);

    [Fact]
    public async Task HalfUtterance_PartialFlowsBeforeAnySegmentEnd()
    {
        var factory = new FakeRealtimeAsrSessionFactory { AutoPartialAtBytes = 30 * RealtimeAsrOptions.BytesPerMs };
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory, options: NoIdle());

        var partialTcs = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.PartialUpdate += (_, u) => partialTcs.TrySetResult(u);

        // The speaker is STILL talking — no VAD segment end ever fires in this test.
        for (var i = 0; i < 10; i++)
            pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x55), voicedHint: true);

        var partial = await partialTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Evidence that the fake ASR received the audio prefix and produced a partial —
        // without any SpeechDetected / segment completion anywhere.
        Assert.Equal("我觉", partial.TextSnapshot);
        var session = Assert.Single(factory.Sessions);
        Assert.True(session.ReceivedBytes > 0);
        Assert.True(session.StartCount >= 1);
        Assert.True(pump.Metrics.FirstAudioSentAt.Count >= 1); // asr_first_audio_sent
    }

    [Fact]
    public async Task TwoSources_DoNotCross()
    {
        var micFactory = new FakeRealtimeAsrSessionFactory();
        var loopFactory = new FakeRealtimeAsrSessionFactory();
        await using var micPump = new RealtimeAsrPump(AudioSource.Microphone, micFactory, options: NoIdle());
        await using var loopPump = new RealtimeAsrPump(AudioSource.Loopback, loopFactory, options: NoIdle());

        var micFinal = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loopFinal = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        micPump.FinalCommitted += (_, u) => micFinal.TrySetResult(u);
        loopPump.FinalCommitted += (_, u) => loopFinal.TrySetResult(u);

        // Both talk at the same time with different audio patterns.
        for (var i = 0; i < 20; i++)
        {
            micPump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x01), voicedHint: true);
            loopPump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x02), voicedHint: true);
        }

        await RealtimeAsrTestHelpers.UntilAsync(() => micFactory.Sessions.Count == 1 && loopFactory.Sessions.Count == 1);
        micFactory.Last.Emit(Upd(0, micFactory.Last.ProviderSessionId, "1", 0, "mic 说", final: true));
        loopFactory.Last.Emit(Upd(0, loopFactory.Last.ProviderSessionId, "1", 0, "loop 说", final: true));

        var m = await micFinal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var l = await loopFinal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AudioSource.Microphone, m.Source);
        Assert.Equal(AudioSource.Loopback, l.Source);
        Assert.Equal("mic 说", m.TextSnapshot);
        Assert.Equal("loop 说", l.TextSnapshot);

        // No source ever received the other source's bytes.
        Assert.All(micFactory.Last.ReceivedFrames, f => Assert.All(f, b => Assert.Equal(0x01, b)));
        Assert.All(loopFactory.Last.ReceivedFrames, f => Assert.All(f, b => Assert.Equal(0x02, b)));
    }

    [Fact]
    public async Task PartialRevisions_DoNotDuplicateIntoHistory()
    {
        var factory = new FakeRealtimeAsrSessionFactory();
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory, options: NoIdle());
        var finals = new System.Collections.Concurrent.ConcurrentQueue<TranscriptUpdate>();
        pump.FinalCommitted += (_, u) => finals.Enqueue(u);

        pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30), voicedHint: true);
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count == 1);
        var s = factory.Last;
        var sess = s.ProviderSessionId;

        // "我觉得" → "我觉得可以" — revisions of the SAME sentence, then a final.
        s.Emit(Upd(0, sess, "1", 0, "我觉"));
        s.Emit(Upd(0, sess, "1", 1, "我觉得"));
        s.Emit(Upd(0, sess, "1", 2, "我觉得可以"));
        s.Emit(Upd(0, sess, "1", 3, "我觉得可以", final: true));
        s.Emit(Upd(0, sess, "1", 3, "我觉得可以", final: true)); // duplicate final (retransmit)

        await RealtimeAsrTestHelpers.UntilAsync(() => finals.Count == 1);
        await Task.Delay(100); // let any duplicate arrive if it were (wrongly) accepted
        var final = Assert.Single(finals);
        Assert.Equal("我觉得可以", final.TextSnapshot);
    }

    [Fact]
    public async Task LatePacketAfterReconnect_DoesNotCrossEpoch()
    {
        var factory = new FakeRealtimeAsrSessionFactory();
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory, options: NoIdle());

        var finals = new System.Collections.Concurrent.ConcurrentQueue<TranscriptUpdate>();
        pump.FinalCommitted += (_, u) => finals.Enqueue(u);

        pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30), voicedHint: true);
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count == 1);
        var first = factory.Last;

        // Capture gap (mute / device restart) tears the session down and bumps the epoch.
        // The frame that observes the gap is discarded with the backlog, so keep feeding —
        // the next voiced frame opens the replacement session.
        pump.NotifyCaptureGap();
        for (var i = 0; i < 5 && factory.Sessions.Count < 2; i++)
        {
            pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x22), voicedHint: true);
            await Task.Delay(30);
        }
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count == 2);
        var second = factory.Last;
        Assert.NotEqual(first.ProviderSessionId, second.ProviderSessionId);
        Assert.Equal(1, second.CaptureEpoch);

        // A late final from the OLD connection arrives after the reconnect.
        first.Emit(Upd(0, first.ProviderSessionId, "9", 0, "迟到结果", final: true));

        await RealtimeAsrTestHelpers.UntilAsync(() => pump.Metrics.LateUpdateDrops >= 1);
        await Task.Delay(100);
        Assert.Empty(finals);
    }

    [Fact]
    public async Task SilenceForever_MemoryStaysBounded_NoSessionCreated()
    {
        var factory = new FakeRealtimeAsrSessionFactory();
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory, options: new RealtimeAsrOptions
        {
            IdleDisconnectMs = 0,
            PrerollMs = 300,
            BufferCapacityMs = 4000,
        });

        // Long unvoiced input: no session is ever opened (cost), and the pre-roll ring is
        // capped so memory cannot grow with input length.
        for (var i = 0; i < 2000; i++)
            pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x00), voicedHint: false);

        await Task.Delay(200);
        Assert.Empty(factory.Sessions);

        // The first voiced frame opens a session; only the bounded pre-roll (~300ms) plus the
        // fresh frame is delivered — not the whole silent backlog.
        pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0xFF), voicedHint: true);
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count == 1);
        var s = factory.Last;
        await RealtimeAsrTestHelpers.UntilAsync(() => s.ReceivedFrames.Count > 0);
        await Task.Delay(100);
        Assert.True(s.ReceivedFrames.Count <= (300 / 30) + 2,
            $"preroll not bounded: {s.ReceivedFrames.Count} frames replayed");
        Assert.Equal(0xFF, s.ReceivedFrames[^1][0]); // newest frame delivered last
    }

    [Fact]
    public async Task LongUtterance_NotTruncated()
    {
        var factory = new FakeRealtimeAsrSessionFactory();
        // Real capture delivers frames in real time; the test feeds the 60s utterance far
        // faster, so the buffer must hold the whole utterance to prove it is not truncated
        // by the sending path (small-buffer silent truncation was the legacy failure mode).
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory, options: new RealtimeAsrOptions
        {
            IdleDisconnectMs = 0,
            PrerollMs = 300,
            BufferCapacityMs = 120000,
        });

        const int frames = 2000; // 60s of voiced audio
        var expectedBytes = 0L;
        for (var i = 0; i < frames; i++)
        {
            var f = RealtimeAsrTestHelpers.Frame(30);
            expectedBytes += f.Length;
            pump.OnCapturedFrame(f, voicedHint: true);
        }

        // The first voiced frame is delivered via the pre-roll replay (not counted as a
        // separate send) — every byte must still arrive exactly once.
        await RealtimeAsrTestHelpers.UntilAsync(() => pump.Metrics.FramesSent >= frames - 1, TimeSpan.FromSeconds(10));
        var s = Assert.Single(factory.Sessions);
        Assert.Equal(expectedBytes, s.ReceivedBytes);
        Assert.True(s.FinishCount == 0); // still mid-utterance, connection never closed by VAD
    }

    [Fact]
    public async Task IdleDisconnect_ReconnectsWithPreroll_AndRecordsColdStart()
    {
        var factory = new FakeRealtimeAsrSessionFactory();
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory, options: new RealtimeAsrOptions
        {
            IdleDisconnectMs = 100, // 100ms of unvoiced audio ends the session
            PrerollMs = 300,
            BufferCapacityMs = 4000,
        });

        pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x31), voicedHint: true);
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count == 1);
        var first = factory.Last;

        // Silence long enough to idle-disconnect (measured in audio time).
        for (var i = 0; i < 6; i++)
            pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x00), voicedHint: false);
        await RealtimeAsrTestHelpers.UntilAsync(() => first.FinishCount == 1);

        // Speech resumes: a NEW session is created and receives the pre-roll first.
        pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x32), voicedHint: true);
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count == 2);
        var second = factory.Last;
        await RealtimeAsrTestHelpers.UntilAsync(() => second.ReceivedFrames.Count > 0);
        Assert.Equal(0x32, second.ReceivedFrames[^1][0]);
        // The pre-roll replay includes tail of the previous silence + the new voiced frame.
        Assert.True(second.ReceivedFrames.Count <= (300 / 30) + 2);

        // Cold-start latency of the resumption entered the metrics.
        await RealtimeAsrTestHelpers.UntilAsync(() => pump.Metrics.ColdStartSamples.Count >= 2);
        Assert.All(pump.Metrics.ColdStartSamples, ms => Assert.True(ms >= 0));
    }

    [Fact]
    public async Task BufferOverflow_StopsRebuildsAndReportsBreak_NotSilentDropOldest()
    {
        var factory = new FakeRealtimeAsrSessionFactory { SendDelay = TimeSpan.FromMilliseconds(50) };
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory, options: new RealtimeAsrOptions
        {
            IdleDisconnectMs = 0,
            PrerollMs = 300,
            BufferCapacityMs = 120, // ~4 frames capacity
        });

        var breaks = new System.Collections.Concurrent.ConcurrentQueue<string>();
        pump.StreamBroken += (_, reason) => breaks.Enqueue(reason);

        // A live session must exist before the flood: otherwise, when the consumer task starts
        // late, the overflow tears down nothing and only one session is ever created.
        pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30), voicedHint: true);
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count == 1);

        for (var i = 0; i < 40; i++)
            pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30), voicedHint: true);

        await RealtimeAsrTestHelpers.UntilAsync(() => breaks.Count >= 1);
        Assert.Contains("断流", breaks.First());
        Assert.True(pump.Metrics.OverflowBreaks >= 1);

        // After the break, the pump rebuilds (a later session is created with a new epoch).
        factory.SendDelay = TimeSpan.Zero;
        for (var i = 0; i < 50 && factory.Sessions.Count < 2; i++)
        {
            pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30, fill: 0x66), voicedHint: true);
            await Task.Delay(50);
        }
        await RealtimeAsrTestHelpers.UntilAsync(() => factory.Sessions.Count >= 2);
        Assert.True(factory.Sessions[^1].CaptureEpoch > factory.Sessions[0].CaptureEpoch);
    }
}
