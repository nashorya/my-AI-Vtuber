using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Tests;

public sealed class RealtimeTraceTests
{
    [Fact]
    public void Disabled_trace_records_nothing()
    {
        var trace = RealtimeTrace.Disabled;
        trace.Mark(RealtimeTrace.Events.InputLastVoiced);
        Assert.False(trace.Enabled);
        Assert.Empty(trace.EventsSnapshot);
    }

    [Fact]
    public void Marks_use_monotonic_clock_and_preserve_ordering()
    {
        var clock = new FakeRealtimeClock();
        clock.Set(10_000);
        var trace = new RealtimeTrace(enabled: true, clock);

        var turn = trace.BeginTurn();
        trace.Mark(RealtimeTrace.Events.InputLastVoiced);
        clock.Advance(120);
        trace.Mark(RealtimeTrace.Events.AsrFirstAudioSent);
        clock.Advance(350);
        trace.Mark(RealtimeTrace.Events.AsrSegmentFinal);
        clock.Advance(80);
        trace.Mark(RealtimeTrace.Events.LlmRequest);
        clock.Advance(500);
        trace.Mark(RealtimeTrace.Events.LlmFirstContent);
        clock.Advance(300);
        trace.Mark(RealtimeTrace.Events.LlmDone);
        clock.Advance(50);
        trace.Mark(RealtimeTrace.Events.TtsRequest);
        clock.Advance(400);
        trace.Mark(RealtimeTrace.Events.TtsFirstEncodedAudio);
        clock.Advance(60);
        trace.Mark(RealtimeTrace.Events.TtsFirstPcm);
        clock.Advance(30);
        trace.Mark(RealtimeTrace.Events.PlaybackFirst);
        clock.Advance(900);
        trace.Mark(RealtimeTrace.Events.PlaybackEnd);

        var events = trace.EventsSnapshot;
        Assert.Equal(11, events.Count);
        Assert.All(events, e => Assert.Equal(turn, e.TurnId));
        // Strict monotonic ordering across the whole chain.
        for (int i = 1; i < events.Count; i++)
            Assert.True(events[i - 1].MonotonicMs < events[i].MonotonicMs,
                $"{events[i - 1].Event} ({events[i - 1].MonotonicMs}) must precede " +
                $"{events[i].Event} ({events[i].MonotonicMs})");
        // Wall time is correlation-only and never used for ordering assertions.
        Assert.NotEqual(default, events[0].WallUtc);

        // Cross-stage latency is directly computable from monotonic stamps.
        var lastVoiced = trace.FirstMonotonicMs(turn, RealtimeTrace.Events.InputLastVoiced);
        var playbackFirst = trace.FirstMonotonicMs(turn, RealtimeTrace.Events.PlaybackFirst);
        Assert.Equal(10_000, lastVoiced);
        Assert.Equal(11_890, playbackFirst);
    }

    [Fact]
    public void Full_chain_and_cancel_event_names_are_defined_and_distinct()
    {
        var all = new[]
        {
            RealtimeTrace.Events.InputFirst,
            RealtimeTrace.Events.InputLastVoiced,
            RealtimeTrace.Events.AsrConnectStart,
            RealtimeTrace.Events.AsrReady,
            RealtimeTrace.Events.AsrFirstAudioSent,
            RealtimeTrace.Events.AsrFirstPartial,
            RealtimeTrace.Events.AsrSegmentFinal,
            RealtimeTrace.Events.TurnCandidate,
            RealtimeTrace.Events.TurnCommitReady,
            RealtimeTrace.Events.LlmRequest,
            RealtimeTrace.Events.LlmFirstContent,
            RealtimeTrace.Events.LlmFirstSpeechSegment,
            RealtimeTrace.Events.LlmDone,
            RealtimeTrace.Events.TtsRequest,
            RealtimeTrace.Events.TtsFirstEncodedAudio,
            RealtimeTrace.Events.TtsFirstPcm,
            RealtimeTrace.Events.PlaybackFirst,
            RealtimeTrace.Events.PlaybackEnd,
            RealtimeTrace.Events.CancelRequested,
            RealtimeTrace.Events.CancelAcked,
            RealtimeTrace.Events.PlaybackStopped,
            RealtimeTrace.Events.VisionCapture,
            RealtimeTrace.Events.VisionResult,
            RealtimeTrace.Events.SnapshotUsed,
        };
        // Every plan RT-00 table row exists exactly once, snake_case, no payload channel.
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.All(all, name => Assert.Matches("^[a-z0-9_]+$", name));
    }

    [Fact]
    public void Event_records_carry_no_payload_channel_for_sensitive_data()
    {
        // Structural guarantee: the record type only exposes TurnId/Event/timestamps.
        var record = new RealtimeTraceEvent("t1", "llm_request", 5, DateTimeOffset.UtcNow);
        var props = record.GetType().GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "Event", "MonotonicMs", "TurnId", "WallUtc" }, props);
    }

    [Fact]
    public void BeginTurn_rotates_turn_ids()
    {
        var trace = new RealtimeTrace(true, new FakeRealtimeClock());
        var t1 = trace.BeginTurn();
        var t2 = trace.BeginTurn();
        Assert.NotEqual(t1, t2);
        trace.Mark("input_first");
        Assert.Equal(t2, trace.EventsSnapshot[^1].TurnId);
        Assert.Null(trace.FirstMonotonicMs(t1, "input_first"));
        Assert.NotNull(trace.FirstMonotonicMs(t2, "input_first"));
    }
}
