using System.Threading.Channels;
using AIVTuber.Core.Audio;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>
/// Owns the realtime ASR wiring for ONE physical audio source (plan §RT-02).
///
/// Capture callbacks hand frames to <see cref="OnCapturedFrame"/> which only performs a
/// non-blocking bounded-channel write (never awaits network I/O). A single consumer task
/// feeds a provider session. Sessions are established on the first VOICED frame (never by
/// waiting for a VAD segment end); once active, every frame — silence included — is sent to
/// keep the audio timeline intact. VAD remains a parallel observer only.
///
/// Long silence finishes the session (cost saving); a rolling pre-roll buffer is replayed
/// when speech resumes, and the resumption cold-start latency is recorded. A capture gap
/// (mute / device restart) or a channel overflow tears the session down, bumps the capture
/// epoch (late packets from the old session are dropped) and — for overflow — reports an
/// explicit stream break instead of silently dropping oldest frames.
/// </summary>
public sealed class RealtimeAsrPump : IAsyncDisposable
{
    private readonly AudioSource _source;
    private readonly IRealtimeAsrSessionFactory _factory;
    private readonly Func<OpponentSnapshot?>? _opponentProvider;
    private readonly RealtimeAsrOptions _options;
    private readonly Channel<FrameItem> _channel;
    private readonly Queue<byte[]> _preroll = new();
    private readonly object _sync = new();
    private readonly TranscriptAccumulator _accumulator = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;

    private readonly record struct FrameItem(byte[] Data, bool Voiced, long CapturedAt);

    private long _epoch;                        // guarded by _sync
    private bool _overflow;                     // guarded by _sync
    private bool _captureGap;                   // guarded by _sync
    private long _prerollBytes;                 // guarded by _sync
    private IRealtimeAsrSession? _session;      // consumer-thread only
    private long _sessionEpoch;                 // consumer-thread only
    private OpponentSnapshot? _sessionOpponent; // snapshot taken at session creation
    private bool _firstAudioSentThisSession;
    private long _firstVoicedSinceIdle;         // consumer-thread only
    private int _silenceAudioMs;                // consumer-thread only

    public RealtimeAsrPump(
        AudioSource source,
        IRealtimeAsrSessionFactory factory,
        Func<OpponentSnapshot?>? opponentProvider = null,
        RealtimeAsrOptions? options = null)
    {
        _source = source;
        _factory = factory;
        _opponentProvider = opponentProvider;
        _options = options ?? RealtimeAsrOptions.Default;
        var capacityFrames = Math.Max(1, _options.BufferCapacityMs * RealtimeAsrOptions.BytesPerMs / 960); // ~30ms frames
        _channel = Channel.CreateBounded<FrameItem>(new BoundedChannelOptions(capacityFrames)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // producers use TryWrite; overflow handled explicitly
            AllowSynchronousContinuations = false,
        });
        _consumer = Task.Run(() => RunAsync(_cts.Token));
    }

    public AudioSource Source => _source;
    public RealtimeAsrMetrics Metrics { get; } = new();
    public TranscriptAccumulator Accumulator => _accumulator;

    /// <summary>Accepted non-final updates (revised snapshots — replace semantics).</summary>
    public event EventHandler<TranscriptUpdate>? PartialUpdate;
    /// <summary>Fully accepted final segments; each fires exactly once per segment.</summary>
    public event EventHandler<TranscriptUpdate>? FinalCommitted;
    /// <summary>Explicit stream-break report (e.g. input buffer overflow). Not silent.</summary>
    public event EventHandler<string>? StreamBroken;

    /// <summary>
    /// Capture-thread entry point. Non-blocking: bounded-channel TryWrite only.
    /// <paramref name="voicedHint"/> comes from the VAD observer for the same frame.
    /// </summary>
    public void OnCapturedFrame(byte[] pcm16k, bool voicedHint)
    {
        ArgumentNullException.ThrowIfNull(pcm16k);
        if (_cts.IsCancellationRequested) return;
        FrameItem item;
        lock (_sync)
        {
            if (_overflow) return; // already torn down; wait for the consumer to rebuild
            item = new FrameItem(pcm16k, voicedHint, Environment.TickCount64);
        }
        if (!_channel.Writer.TryWrite(item))
        {
            // Queue capacity exceeded: the consumer cannot keep up. Stop, rebuild, report.
            lock (_sync) _overflow = true;
            Metrics.OverflowBreaks++;
            var reason = $"[实时ASR] 输入缓冲超过 {_options.BufferCapacityMs}ms（source={_source}），" +
                         "停止本次会话并重建（断流上报，不悄悄丢弃旧帧）";
            StreamBroken?.Invoke(this, reason);
            AIVTuber.Core.Diagnostics.DebugLog.Write(reason);
        }
    }

    /// <summary>The capture timeline broke (mute gap, device restart, route change):
    /// tear down the session and bump the epoch so late packets cannot leak into the new one.</summary>
    public void NotifyCaptureGap()
    {
        lock (_sync) _captureGap = true;
    }

    private long CurrentEpoch { get { lock (_sync) return _epoch; } }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                FrameItem frame;
                try
                {
                    frame = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (ChannelClosedException) { break; }
                catch (OperationCanceledException) { break; }

                bool needsTeardown;
                lock (_sync) needsTeardown = _overflow || _captureGap;
                if (needsTeardown)
                {
                    await DrainAndTeardownAsync(cancel: true, ct).ConfigureAwait(false);
                    continue; // frame discarded with the rest of the overflowed backlog
                }

                PushPreroll(frame.Data);

                if (frame.Voiced && _session is null)
                {
                    if (_firstVoicedSinceIdle == 0) _firstVoicedSinceIdle = frame.CapturedAt;
                    await StartSessionAsync(ct).ConfigureAwait(false);
                    if (_session is not null)
                        continue; // the pre-roll replay already carried this exact frame
                }

                if (_session is null) continue;

                if (!_firstAudioSentThisSession)
                {
                    _firstAudioSentThisSession = true;
                    if (Metrics.FirstAudioSentAt.Count < 64) Metrics.FirstAudioSentAt.Add(Environment.TickCount64);
                }
                await _session.SendFrameAsync(frame.Data, ct).ConfigureAwait(false);
                Metrics.FramesSent++;

                // Idle disconnect is measured in AUDIO time (ms of unvoiced audio since the
                // last voiced frame), matching the audio timeline the session sees — not
                // wall-clock, which bursts of loopback frames would distort.
                _silenceAudioMs = frame.Voiced ? 0 : _silenceAudioMs + frame.Data.Length / RealtimeAsrOptions.BytesPerMs;
                if (_options.IdleDisconnectMs > 0 && _silenceAudioMs >= _options.IdleDisconnectMs)
                {
                    // Cost saving: finish the (long-silent) session. The pre-roll ring keeps
                    // recent audio, so resumption replays it and its cold-start is measured.
                    Metrics.IdleReconnects++;
                    _silenceAudioMs = 0;
                    await FinishSessionAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[实时ASR] source={_source} 消费循环异常: {ex.Message}");
            StreamBroken?.Invoke(this, $"[实时ASR] source={_source} 消费循环异常: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await DrainAndTeardownAsync(cancel: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task StartSessionAsync(CancellationToken ct)
    {
        OpponentSnapshot? opponent = null;
        try { opponent = _opponentProvider?.Invoke(); }
        catch { /* snapshot is best-effort */ }
        long epoch;
        lock (_sync) epoch = _epoch;
        var session = _factory.Create(_source, epoch, opponent);
        var startedAt = Environment.TickCount64;
        await session.StartAsync(ct).ConfigureAwait(false);
        if (_firstVoicedSinceIdle > 0)
            Metrics.NoteColdStart(startedAt - _firstVoicedSinceIdle);
        Metrics.SessionsStarted++;
        _session = session;
        _sessionEpoch = epoch;
        _sessionOpponent = opponent;
        _firstAudioSentThisSession = false;

        // Replay the pre-roll buffer (oldest first) so reconnects don't clip utterance starts.
        byte[][] replay;
        lock (_sync) replay = [.. _preroll];
        foreach (var f in replay)
            await session.SendFrameAsync(f, ct).ConfigureAwait(false);
        if (replay.Length > 0 && Metrics.FirstAudioSentAt.Count < 64)
            Metrics.FirstAudioSentAt.Add(Environment.TickCount64);

        _ = Task.Run(() => ReadSessionUpdatesAsync(session, epoch, opponent, ct), ct);
    }

    private async Task ReadSessionUpdatesAsync(
        IRealtimeAsrSession session, long epoch, OpponentSnapshot? opponent, CancellationToken ct)
    {
        try
        {
            await foreach (var raw in session.ReadUpdatesAsync(ct).ConfigureAwait(false))
            {
                if (epoch != CurrentEpoch)
                {
                    // Late packet from a torn-down session — never crosses the epoch boundary.
                    Metrics.LateUpdateDrops++;
                    continue;
                }
                var update = raw with
                {
                    Source = _source,
                    CaptureEpoch = epoch,
                    OpponentSnapshot = opponent,
                };
                var acceptance = _accumulator.Apply(update, out var final);
                if (acceptance == TranscriptAcceptance.Accepted)
                {
                    if (update.IsFinal) FinalCommitted?.Invoke(this, final!);
                    else PartialUpdate?.Invoke(this, update);
                }
                else
                {
                    AIVTuber.Core.Diagnostics.DebugLog.Write(
                        $"[实时ASR] 拒收更新 source={_source} seg={update.SegmentId} rev={update.Revision} " +
                        $"epoch={update.CaptureEpoch} 原因={acceptance}");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown or session teardown */ }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[实时ASR] source={_source} 会话读取异常: {ex.Message}");
        }
        finally
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Gracefully finishes the current session (end-of-audio). Updates keep flowing
    /// until the provider closes; the session disposes itself in the reader task.</summary>
    private async Task FinishSessionAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        _firstVoicedSinceIdle = 0;
        lock (_sync) _epoch = _sessionEpoch + 1; // reconnects get a fresh epoch
        try { await session.FinishAudioAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[实时ASR] 结束音频失败 source={_source}: {ex.Message}");
            try { await session.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    /// <summary>Cancels the current session and discards the pending backlog. Used on
    /// overflow / capture gaps / shutdown. Always bumps the epoch.</summary>
    private async Task DrainAndTeardownAsync(bool cancel, CancellationToken ct)
    {
        while (_channel.Reader.TryRead(out _)) { } // discard overflowed backlog
        lock (_sync)
        {
            _overflow = false;
            _captureGap = false;
        }
        var session = _session;
        _session = null;
        _firstVoicedSinceIdle = 0;
        lock (_sync) _epoch++;
        if (session is null) return;
        try { await session.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[实时ASR] 取消会话失败 source={_source}: {ex.Message}");
        }
        _ = ct;
    }

    private void PushPreroll(byte[] frame)
    {
        lock (_sync)
        {
            _preroll.Enqueue(frame);
            _prerollBytes += frame.Length;
            var cap = (long)_options.PrerollMs * RealtimeAsrOptions.BytesPerMs;
            while (_prerollBytes > cap && _preroll.Count > 0)
                _prerollBytes -= _preroll.Dequeue().Length;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _consumer.ConfigureAwait(false); } catch { /* observed inside */ }
        _cts.Dispose();
    }
}
