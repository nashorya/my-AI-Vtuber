using System.Threading.Channels;
using AIVTuber.Core.Audio;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Tests;

/// <summary>Scriptable fake transport: records sends, plays scripted server messages.</summary>
public sealed class FakeWebSocketTransport : IWebSocketTransport
{
    public Uri? ConnectedUri;
    public IReadOnlyDictionary<string, string>? Headers;
    public readonly List<(byte[] Data, bool AsText)> Sent = [];
    public readonly Channel<byte[]> Incoming = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    public readonly TaskCompletionSource ConnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan SendDelay = TimeSpan.Zero;

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        ConnectedUri = uri;
        Headers = headers;
        ConnectedTcs.TrySetResult();
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, bool asText, CancellationToken cancellationToken)
    {
        if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, cancellationToken);
        lock (Sent) Sent.Add((buffer.ToArray(), asText));
    }

    public async ValueTask<WebSocketTransportResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var data = await Incoming.Reader.ReadAsync(cancellationToken);
            return new WebSocketTransportResult(data, true);
        }
        catch (ChannelClosedException)
        {
            return new WebSocketTransportResult(Array.Empty<byte>(), true);
        }
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        Incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void PushServerMessage(string json) => Incoming.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(json));
    public void PushServerMessage(byte[] data) => Incoming.Writer.TryWrite(data);

    public void Dispose() { }
}

/// <summary>Fake realtime session: records frames, emits scripted/auto updates.</summary>
public sealed class FakeRealtimeAsrSession : IRealtimeAsrSession
{
    private readonly Channel<TranscriptUpdate> _updates = Channel.CreateUnbounded<TranscriptUpdate>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _lock = new();
    private bool _autoPartialFired;

    public AudioSource Source { get; set; }
    public long CaptureEpoch { get; set; }
    public string ProviderSessionId { get; } = Guid.NewGuid().ToString("N");
    public OpponentSnapshot? Opponent { get; set; }

    public readonly List<byte[]> ReceivedFrames = [];
    public long ReceivedBytes;
    public int StartCount, FinishCount, CancelCount, DisposeCount;
    public volatile bool Started;
    public TimeSpan SendDelay = TimeSpan.Zero;
    /// <summary>When ReceivedBytes crosses this, auto-emit one partial (half-utterance fixture).</summary>
    public long AutoPartialAtBytes = long.MaxValue;
    public string AutoPartialText = "我觉";
    public string AutoPartialSegmentId = "1";
    public Func<FakeRealtimeAsrSession, CancellationToken, Task>? OnFrame;

    public Task Start()
    {
        StartCount++;
        Started = true;
        return Task.CompletedTask;
    }

    public void Emit(TranscriptUpdate update) => _updates.Writer.TryWrite(update);
    public void Complete() => _updates.Writer.TryComplete();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        Started = true;
        return Task.CompletedTask;
    }

    public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken cancellationToken)
    {
        if (!Started) throw new InvalidOperationException("session not started");
        if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, cancellationToken);
        lock (_lock)
        {
            var copy = pcm16k.ToArray();
            ReceivedFrames.Add(copy);
            ReceivedBytes += copy.Length;
            var cross = ReceivedBytes >= AutoPartialAtBytes && !_autoPartialFired;
            if (cross) _autoPartialFired = true;
            if (cross)
                Emit(new TranscriptUpdate(Source, CaptureEpoch, ProviderSessionId, AutoPartialSegmentId,
                    0, AutoPartialText, IsFinal: false, 0, ReceivedBytes / 32,
                    Environment.TickCount64, Opponent));
        }
        if (OnFrame is not null) await OnFrame(this, cancellationToken);
    }

    public ValueTask FinishAudioAsync(CancellationToken cancellationToken)
    {
        FinishCount++;
        return ValueTask.CompletedTask;
    }

    public Task CancelAsync()
    {
        CancelCount++;
        // Deliberately does NOT complete the updates channel: cancelling stops our sends,
        // but (like a real provider socket) late server packets may still arrive — the
        // fixtures rely on emitting after cancellation.
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<TranscriptUpdate> ReadUpdatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var u in _updates.Reader.ReadAllAsync(cancellationToken))
            yield return u;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Factory producing fake sessions; records every session created.</summary>
public sealed class FakeRealtimeAsrSessionFactory : IRealtimeAsrSessionFactory
{
    private readonly object _lock = new();
    public readonly List<FakeRealtimeAsrSession> Sessions = [];
    public TimeSpan SendDelay = TimeSpan.Zero;
    public long AutoPartialAtBytes = long.MaxValue;
    public Action<FakeRealtimeAsrSession>? Configure;

    public IRealtimeAsrSession Create(AudioSource source, long captureEpoch, OpponentSnapshot? opponent)
    {
        var session = new FakeRealtimeAsrSession
        {
            Source = source,
            CaptureEpoch = captureEpoch,
            Opponent = opponent,
            SendDelay = SendDelay,
            AutoPartialAtBytes = AutoPartialAtBytes,
        };
        Configure?.Invoke(session);
        lock (_lock) Sessions.Add(session);
        return session;
    }

    public FakeRealtimeAsrSession Last => Sessions[^1];
}

public static class RealtimeAsrTestHelpers
{
    public static byte[] Frame(int ms = 30, byte fill = 0x11)
        => Enumerable.Repeat(fill, ms * RealtimeAsrOptions.BytesPerMs).ToArray();

    /// <summary>Polls until the predicate holds (timeout 5s) — the pump runs on its own task.</summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met before timeout");
    }
}
