using AIVTuber.Core.Audio;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Tests;

public class TranscriptAccumulatorTests
{
    private static TranscriptUpdate Update(int epoch, string seg, int rev, string text, bool final = false) =>
        new(AudioSource.Microphone, epoch, "sess", seg, rev, text, final, 0, 0, Environment.TickCount64);

    [Fact]
    public void SameSentenceUpdates_ReplaceNotAppend()
    {
        var acc = new TranscriptAccumulator();
        var a = acc.Apply(Update(0, "1", 0, "我觉"), out _);
        var b = acc.Apply(Update(0, "1", 1, "我觉得可以"), out _);

        Assert.Equal(TranscriptAcceptance.Accepted, a);
        Assert.Equal(TranscriptAcceptance.Accepted, b);
        // Candidate snapshots replace; nothing is appended to any history yet.
        Assert.Empty(acc.CommittedFinals);
        Assert.Equal("我觉得可以", acc.CurrentCandidate("sess", "1"));
    }

    [Fact]
    public void Final_CommittedExactlyOnce()
    {
        var acc = new TranscriptAccumulator();
        acc.Apply(Update(0, "1", 0, "我觉"), out _);
        acc.Apply(Update(0, "1", 1, "我觉得可以", final: true), out var final);
        Assert.NotNull(final);
        Assert.Equal("我觉得可以", final!.TextSnapshot);

        acc.Apply(Update(0, "1", 2, "我觉得可以", final: true), out var dup);
        Assert.Equal(TranscriptAcceptance.RejectedDuplicateFinal, acc.Apply(Update(0, "1", 2, "我觉得可以", final: true), out dup));
        acc.Apply(Update(0, "1", 3, "late partial"), out _);
        Assert.Single(acc.CommittedFinals);
    }

    [Fact]
    public void OlderRevision_Rejected()
    {
        var acc = new TranscriptAccumulator();
        acc.Apply(Update(0, "1", 2, "revision two"), out _);
        Assert.Equal(TranscriptAcceptance.RejectedStaleRevision, acc.Apply(Update(0, "1", 1, "revision one"), out _));
        // Equal revision (retransmission) is also rejected.
        Assert.Equal(TranscriptAcceptance.RejectedStaleRevision, acc.Apply(Update(0, "1", 2, "revision two"), out _));
        Assert.Equal("revision two", acc.CurrentCandidate("sess", "1"));
    }

    [Fact]
    public void OlderEpoch_Rejected()
    {
        var acc = new TranscriptAccumulator();
        acc.Apply(Update(1, "1", 0, "new epoch"), out _);
        Assert.Equal(TranscriptAcceptance.RejectedStaleEpoch, acc.Apply(Update(0, "1", 5, "late from old connection"), out _));
        Assert.Equal(TranscriptAcceptance.RejectedStaleEpoch, acc.Apply(Update(0, "1", 5, "late final", final: true), out var f));
        Assert.Null(f);
        Assert.Empty(acc.CommittedFinals);
    }

    [Fact]
    public void SameSegmentId_AcrossEpochs_DoesNotCollide()
    {
        var acc = new TranscriptAccumulator();
        acc.Apply(Update(0, "1", 0, "old epoch text", final: true), out _);
        var acceptance = acc.Apply(Update(1, "1", 0, "new epoch text", final: true), out var final);
        Assert.Equal(TranscriptAcceptance.Accepted, acceptance);
        Assert.NotNull(final);
        Assert.Equal(2, acc.CommittedFinals.Count);
    }
}
