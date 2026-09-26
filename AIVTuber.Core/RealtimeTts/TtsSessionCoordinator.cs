using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Core.RealtimeTts;

/// <summary>取消屏障结果：服务端确认，还是超时后连接 epoch 重建（旧回包全部失效）。</summary>
public enum TtsCancelOutcome
{
    /// <summary>没有在途回合/文本，无需取消。</summary>
    NothingToCancel,
    /// <summary>收到服务端 task_cancel 确认。</summary>
    ServerConfirmed,
    /// <summary>确认超时：旧 socket 已断开，epoch 递增重建，旧连接的一切回包作废。</summary>
    EpochRebuilt,
}

/// <summary>会话级状态（单连接 epoch 内的宏观状态）。</summary>
public enum TtsSessionState
{
    /// <summary>尚未连接。</summary>
    Idle,
    /// <summary>连接中 / task_start 待确认。</summary>
    Starting,
    /// <summary>任务已建立，可接受文本。</summary>
    Ready,
    /// <summary>有在途回合文本/音频。</summary>
    Speaking,
    /// <summary>取消屏障中：本地待播已停，等待服务端 cancel 确认或 epoch 重建。</summary>
    Canceling,
    /// <summary>task_failed 或连接中断：下一回合前必须重建。</summary>
    Failed,
}

/// <summary>
/// MiniMax 双向 TTS 会话协调器（RT-06）。一个发送循环 + 一个接收循环维护明确会话状态。
///
/// 关键不变量：
///  1. 回合（本地 TurnGen）与连接 epoch 分开计数；音频只按「接收循环捕获的 epoch ==
///     当前 epoch && 无取消屏障 && 存在打开的投递单元」归属，服务端不带本地 TurnId，
///     绝不把每个新音频默认标为当前轮。
///  2. 取消屏障：CancelActiveTurnAsync 先停本地待播，再等 task_cancel 确认；确认丢失则
///     断旧 socket 重建（epoch++），旧连接迟到包全部丢弃。
///  3. flush 确认=本轮音频已下发（completeOk 单元），不等于已播放；播放侧结算由
///     RT-05 账本（ReplySegmentState）负责，本类不冒充。
///  4. 控制消息（start/flush/cancel/finish）走独立无界通道，可越过普通文本积压；
///     文本积压（预估待播秒数超上限）时发送循环暂停取文本。
///  5. 已收到/播放过音频的回合失败（task_failed/断线）不重发整段文本——不制造复读。
/// 协议字段形态未实测（见 TtsBidiProtocol 注释）；全部行为由 fake transport 测试锁定。
/// </summary>
public sealed class TtsSessionCoordinator : IDisposable
{
    private readonly IWebSocketTransportFactory _transportFactory;
    private readonly TtsSessionCoordinatorOptions _options;
    private readonly RealtimeTrace? _trace;
    private readonly object _gate = new();

    private long _epoch;                          // 连接 epoch：重建即递增
    private IWebSocketTransport? _transport;
    private CancellationTokenSource? _loopCts;
    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;

    private readonly Channel<string> _textQueue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
    // 控制通道无界：取消/flush 绝不能被普通文本积压挡住。
    private readonly Channel<string> _controlQueue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private TaskCompletionSource _startAck = NewTcs();
    private TaskCompletionSource<bool> _cancelAck = NewBoolTcs();
    // 初始即完成态 = 无取消屏障；只有 CancelActiveTurnAsync 落屏障时才重置为未完成。
    private TaskCompletionSource _barrierClear = CompletedTcs();

    private BidiTurn? _activeTurn;
    private BidiUnit? _activeUnit;
    private TtsSessionState _state = TtsSessionState.Idle;
    private double _backlogSeconds;
    private long _turnGen;
    private string _pinnedVoiceId;
    private readonly string _taskId = $"task-{Guid.NewGuid():N}";
    private readonly List<string> _sendLog = [];   // 诊断/测试：按序记录已发事件类型
    private bool _disposed;

    /// <summary>触发时机：预估待播积压越过/回落阈值。上游可据此限制继续提交文本。</summary>
    public event Action<bool>? BacklogStateChanged;

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedTcs()
    {
        var tcs = NewTcs();
        tcs.TrySetResult();
        return tcs;
    }
    private static TaskCompletionSource<bool> NewBoolTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TtsSessionCoordinator(IWebSocketTransportFactory transportFactory, TtsSessionCoordinatorOptions options,
        RealtimeTrace? trace = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _trace = trace;
        _pinnedVoiceId = options.VoiceId;
    }

    public TtsSessionState State { get { lock (_gate) return _state; } }
    public long ConnectionEpoch { get { lock (_gate) return _epoch; } }
    public double EstimatedBacklogSeconds { get { lock (_gate) return _backlogSeconds; } }
    public bool IsBacklogged => EstimatedBacklogSeconds >= _options.MaxBacklogSeconds;
    /// <summary>按序发送的事件类型（测试/诊断；不含文本内容）。</summary>
    public IReadOnlyList<string> SentEventLog { get { lock (_gate) return _sendLog.ToArray(); } }
    public int RebuildCount { get { lock (_gate) return (int)_epoch; } }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>连接 + task_start 并等待确认。重复调用幂等（同一 epoch 只 start 一次）。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Task startWait;
        lock (_gate)
        {
            if (_state is TtsSessionState.Starting or TtsSessionState.Ready or TtsSessionState.Speaking)
            {
                startWait = _startAck.Task;
            }
            else
            {
                // Failed 后重启：旧连接作废，epoch 递增使旧 socket 迟到回包全部失效。
                if (_transport is not null) _epoch++;
                _state = TtsSessionState.Starting;
                _startAck = NewTcs();
                startWait = _startAck.Task;
                StartLoopsLocked();
            }
        }
        await startWait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>安全重建：断开旧连接（epoch++），新连接重新 task_start。用于取消确认超时、
    /// 会话失败、音色/会话配置变更（task_start 后会话配置稳定，不支持热改）。</summary>
    public async Task RebuildAsync(string reason, CancellationToken cancellationToken = default)
    {
        IWebSocketTransport? oldTransport;
        CancellationTokenSource? oldCts;
        Task oldSendLoop, oldReceiveLoop;
        Task startWait;
        lock (_gate)
        {
            oldTransport = _transport;
            oldCts = _loopCts;
            oldSendLoop = _sendLoop;
            oldReceiveLoop = _receiveLoop;
            _epoch++;
            _activeUnit?.CompleteAsDropped();
            _activeUnit = null;
            _activeTurn = null;
            _backlogSeconds = 0;
            DrainTextQueue();
            _state = TtsSessionState.Starting;
            _startAck = NewTcs();
            startWait = _startAck.Task;
            StartLoopsLocked();
        }
        // 旧循环已由 StartLoopsLocked 内的 Cancel 触发退出；等待其收尾后处置旧 CTS。
        if (oldTransport is not null)
        {
            try { await oldTransport.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 旧连接本来就不可靠 */ }
            oldTransport.Dispose();
        }
        try { await Task.WhenAll(oldSendLoop, oldReceiveLoop).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* 旧循环已随 epoch 失效 */ }
        oldCts?.Dispose();
        await startWait.WaitAsync(cancellationToken).ConfigureAwait(false);
        DebugLog.Write($"[RT-06] TTS 会话重建 reason={reason} epoch={_epoch}");
    }

    private void StartLoopsLocked()
    {
        _loopCts?.Cancel(); // 旧循环停止；CTS 由 RebuildAsync/Dispose 在循环退出后处置
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _transport = _transportFactory.Create();
        var epoch = _epoch;
        _sendLoop = Task.Run(() => SendLoopAsync(_transport, epoch, ct));
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_transport, epoch, ct));
    }

    // ------------------------------------------------------------------ turns

    /// <summary>开启一个本地回合：等待取消屏障清空并确保会话可用。会话失败时先重建。</summary>
    public async Task BeginTurnAsync(CancellationToken cancellationToken)
    {
        await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        Task barrier;
        lock (_gate)
        {
            barrier = _barrierClear.Task;
        }
        await barrier.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_state == TtsSessionState.Failed)
                throw new InvalidOperationException("TTS 会话处于 Failed 状态，须先重建");
            _activeTurn = new BidiTurn(Interlocked.Increment(ref _turnGen));
            _state = TtsSessionState.Speaking;
        }
    }

    /// <summary>回合结束（本地记账）：之后的迟到音频不再归入该回合。不发任何协议消息。</summary>
    public void EndTurn()
    {
        lock (_gate)
        {
            _activeUnit?.CompleteAsDropped();
            _activeUnit = null;
            _activeTurn = null;
            if (_state == TtsSessionState.Speaking) _state = TtsSessionState.Ready;
        }
    }

    /// <summary>请求的音色与会话不一致：task_start 后会话配置稳定，须重建连接。</summary>
    public Task EnsureVoiceAsync(string voiceId, CancellationToken cancellationToken)
    {
        bool rebuild;
        lock (_gate)
        {
            rebuild = !string.Equals(voiceId, _pinnedVoiceId, StringComparison.OrdinalIgnoreCase);
            if (rebuild) _pinnedVoiceId = voiceId;
        }
        return rebuild ? RebuildAsync("voice_change", cancellationToken) : Task.CompletedTask;
    }

    /// <summary>投递一段已批准文本（task_continue + task_flush），返回该投递单元。
    /// 音频随到随出；单元在 flush 确认后完成。积压超限时在发送前等待。</summary>
    public async Task<BidiUnit> SubmitTextAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("TTS 文本不能为空", nameof(text));

        await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        Task barrier;
        lock (_gate) barrier = _barrierClear.Task;
        await barrier.WaitAsync(cancellationToken).ConfigureAwait(false);

        var unit = new BidiUnit();
        lock (_gate)
        {
            if (_state == TtsSessionState.Failed)
                throw new InvalidOperationException("TTS 会话处于 Failed 状态，须先重建");
            if (_activeTurn is null)
                _activeTurn = new BidiTurn(Interlocked.Increment(ref _turnGen)); // 未显式 BeginTurn 的兜底
            unit.TurnGen = _activeTurn.Gen;
            unit.Epoch = _epoch;
            _activeUnit = unit;
            _state = TtsSessionState.Speaking;
            _backlogSeconds += text.Length * _options.SecondsPerCharEstimate;
            var nowBacklogged = _backlogSeconds >= _options.MaxBacklogSeconds;
            if (nowBacklogged != _wasBacklogged)
            {
                _wasBacklogged = nowBacklogged;
                BacklogStateChanged?.Invoke(nowBacklogged);
            }
        }

        await _textQueue.Writer.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        return unit;
    }

    private bool _wasBacklogged;

    // ------------------------------------------------------------------ cancel barrier

    /// <summary>取消屏障：立即结束本地待播（complete 单元），发送 task_cancel（越过文本积压），
    /// 等待服务端确认；超时则重建连接 epoch。返回前不切换新回合。</summary>
    public async Task<TtsCancelOutcome> CancelActiveTurnAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        timeout ??= _options.CancelAckTimeout;
        TaskCompletionSource<bool> ack;
        bool hadActivity;
        lock (_gate)
        {
            hadActivity = _activeTurn is not null || _activeUnit is not null || _backlogSeconds > 0;
            _activeUnit?.CompleteAsCancelled();
            _activeUnit = null;
            _activeTurn = null;
            _backlogSeconds = 0;
            if (_wasBacklogged) { _wasBacklogged = false; BacklogStateChanged?.Invoke(false); }
            if (!hadActivity || _state is not (TtsSessionState.Ready or TtsSessionState.Speaking))
            {
                DrainTextQueue();
                return TtsCancelOutcome.NothingToCancel;
            }
            _state = TtsSessionState.Canceling;
            _barrierClear = NewTcs();          // 屏障落下：新回合必须等它清空
            _cancelAck = ack = NewBoolTcs();
            DrainTextQueue();                  // 屏障落下即清空排队文本：不得在取消后补发
            _controlQueue.Writer.TryWrite(TtsBidiProtocol.BuildTaskCancel(_taskId));
        }

        bool confirmed;
        try
        {
            await ack.Task.WaitAsync(timeout.Value, cancellationToken).ConfigureAwait(false);
            confirmed = true;
        }
        catch (TimeoutException)
        {
            confirmed = false;
        }

        if (confirmed)
        {
            lock (_gate)
            {
                _state = TtsSessionState.Ready;
                DrainTextQueue();
            }
            _barrierClear.TrySetResult();
            return TtsCancelOutcome.ServerConfirmed;
        }

        // 确认丢失：断旧 socket 重建，旧连接回包全部失效。
        await RebuildAsync("cancel_ack_timeout", cancellationToken).ConfigureAwait(false);
        lock (_gate) _state = TtsSessionState.Ready;
        _barrierClear.TrySetResult();
        return TtsCancelOutcome.EpochRebuilt;
    }

    /// <summary>正常收尾：task_finish + 关闭。</summary>
    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state is TtsSessionState.Idle or TtsSessionState.Failed) return;
            _controlQueue.Writer.TryWrite(TtsBidiProtocol.BuildTaskFinish(_taskId));
        }
        await Task.Delay(1, cancellationToken).ConfigureAwait(false); // 让发送循环取出（收尾尽力而为）
    }

    private void DrainTextQueue()
    {
        while (_textQueue.Reader.TryRead(out _)) { }
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        Task wait;
        bool needStart;
        lock (_gate)
        {
            needStart = _state is TtsSessionState.Idle or TtsSessionState.Failed;
            wait = _startAck.Task;
        }
        if (needStart)
            await StartAsync(cancellationToken).ConfigureAwait(false);
        else if (!wait.IsCompleted)
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ loops

    private async Task SendLoopAsync(IWebSocketTransport transport, long epoch, CancellationToken ct)
    {
        try
        {
            var uri = TtsBidiProtocol.BuildUri(_options.Host, _options.Path);
            var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_options.ApiKey}" };
            await transport.ConnectAsync(uri, headers, ct).ConfigureAwait(false);
            lock (_gate) _controlQueue.Writer.TryWrite(TtsBidiProtocol.BuildTaskStart(
                _taskId, _options.Model, _pinnedVoiceId, _options.Speed, _options.SampleRate, _options.Emotion));
        }
        catch (Exception ex)
        {
            MarkStartFailed(epoch, ex);
            return;
        }

        var lastSend = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 控制消息永远优先于普通文本积压。
                if (_controlQueue.Reader.TryRead(out var control))
                {
                    await transport.SendAsync(Encoding.UTF8.GetBytes(control), asText: true, ct).ConfigureAwait(false);
                    lastSend = Stopwatch.GetTimestamp();
                    NoteSent(control);
                    continue;
                }

                // 文本积压超限：暂停发送文本（控制通道仍可越过）。
                bool backlogged;
                lock (_gate) backlogged = _backlogSeconds >= _options.MaxBacklogSeconds;
                if (!backlogged && _textQueue.Reader.TryRead(out var text))
                {
                    await transport.SendAsync(
                        Encoding.UTF8.GetBytes(TtsBidiProtocol.BuildTaskContinue(_taskId, text)), true, ct).ConfigureAwait(false);
                    lastSend = Stopwatch.GetTimestamp();
                    NoteSent(null);
                    // 单元边界：每段 continue 后跟一个 flush，音频以 flush 确认收束。
                    await transport.SendAsync(
                        Encoding.UTF8.GetBytes(TtsBidiProtocol.BuildTaskFlush(_taskId)), true, ct).ConfigureAwait(false);
                    NoteSent("task_flush");
                    continue;
                }

                var controlWait = _controlQueue.Reader.WaitToReadAsync(ct).AsTask();
                var textWait = backlogged ? Task.Delay(50, ct) : _textQueue.Reader.WaitToReadAsync(ct).AsTask();
                var tick = Task.Delay(20, ct); // 空闲时也周期醒来：保活/积压状态检查
                _ = await Task.WhenAny(controlWait, textWait, tick).ConfigureAwait(false);

                // 空闲保活（可测试的协议方式）：默认关闭，间隔未与真实服务端验证。
                if (_options.KeepAliveInterval != Timeout.InfiniteTimeSpan &&
                    Stopwatch.GetElapsedTime(lastSend).TotalMilliseconds >= _options.KeepAliveInterval.TotalMilliseconds)
                {
                    await transport.SendAsync(
                        Encoding.UTF8.GetBytes(TtsBidiProtocol.BuildKeepAlive()), true, ct).ConfigureAwait(false);
                    lastSend = Stopwatch.GetTimestamp();
                    NoteSent("keepalive");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                MarkStartFailed(epoch, null); // 发送失败：连接不可用，等重建
                break;
            }
        }
    }

    private void NoteSent(string? jsonOrNull)
    {
        string evt = jsonOrNull is null
            ? "task_continue"
            : ExtractType(jsonOrNull);
        lock (_gate) _sendLog.Add(evt);
    }

    private static string ExtractType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() is { } s ? s : "?";
        }
        catch { return "?"; }
    }

    private async Task ReceiveLoopAsync(IWebSocketTransport transport, long epoch, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WebSocketTransportResult result;
            try
            {
                result = await transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                HandleDisconnect(epoch);
                break;
            }

            if (result.Data.Length == 0)
            {
                HandleDisconnect(epoch); // 连接关闭
                break;
            }

            if (!TtsBidiProtocol.TryParseServer(result.Data, out var message))
                continue; // 非完整 JSON：忽略，不断会话

            switch (message.Kind)
            {
                case TtsBidiServerMessageKind.TaskStarted:
                    if (IsCurrentEpoch(epoch)) _startAck.TrySetResult();
                    break;

                case TtsBidiServerMessageKind.Audio:
                    RouteAudio(epoch, message);
                    break;

                case TtsBidiServerMessageKind.TaskFlushed:
                    lock (_gate)
                    {
                        if (_activeUnit is { Epoch: var e } unit && e == epoch)
                        {
                            unit.CompleteOk();       // 本轮音频已下发 ≠ 已播放
                            _activeUnit = null;
                            _backlogSeconds = 0;
                            if (_wasBacklogged) { _wasBacklogged = false; BacklogStateChanged?.Invoke(false); }
                            if (_state == TtsSessionState.Speaking) _state = TtsSessionState.Ready;
                        }
                        // 无活动单元的 flush 确认（迟到/重复）：忽略，不影响下一轮。
                    }
                    break;

                case TtsBidiServerMessageKind.TaskCanceled:
                    lock (_gate)
                    {
                        if (_state == TtsSessionState.Canceling)
                        {
                            _state = TtsSessionState.Ready;
                            DrainTextQueue();
                        }
                    }
                    _cancelAck.TrySetResult(true);
                    break;

                case TtsBidiServerMessageKind.TaskFinished:
                    // 服务端确认收尾；本端无需额外动作（Dispose 负责关闭传输）。
                    break;

                case TtsBidiServerMessageKind.TaskFailed:
                    HandleTaskFailed(epoch, message);
                    break;

                case TtsBidiServerMessageKind.KeepAlive:
                case TtsBidiServerMessageKind.Ignored:
                default:
                    break;
            }
        }
    }

    private void RouteAudio(long epoch, in TtsBidiServerMessage message)
    {
        // 归属规则：epoch 相同 && 无取消屏障 && 存在打开的单元；否则一律丢弃
        //（旧 socket 迟到包 / 取消后残留 / 无主 straggler 不归新轮）。
        BidiUnit? target;
        lock (_gate)
        {
            if (_epoch != epoch) return;                       // 旧连接回包：全部失效
            if (_state == TtsSessionState.Canceling) return;   // 取消屏障中：丢弃
            target = _activeUnit;
        }
        if (target is null) return;                            // 迟到/无主音频：不归当前轮

        if (message.Audio is not { Length: > 0 } encoded) return;
        TelemetryMarkEncoded();
        var format = message.AudioFormat ?? "pcm";
        foreach (var pcm in target.Decode(encoded, format, _options.SampleRate))
            DeliverAudio(target, pcm);
    }

    private void DeliverAudio(BidiUnit unit, byte[] pcm)
    {
        if (pcm.Length == 0) return;
        TelemetryMarkPcm();
        unit.Deliver(pcm);
        lock (_gate)
        {
            if (_activeTurn is { } turn && turn.Gen == unit.TurnGen)
                turn.AnyAudioDelivered = true;
        }
    }

    private void HandleTaskFailed(long epoch, TtsBidiServerMessage message)
    {
        lock (_gate)
        {
            if (_epoch != epoch) return;
            var unit = _activeUnit;
            _activeUnit = null;
            _activeTurn = null;
            _state = TtsSessionState.Failed;
            _backlogSeconds = 0;
            DrainTextQueue();
            // 不重发已部分下发音频的整段文本（不制造复读）。
            unit?.CompleteAsFailed(message.ErrorCode, message.ErrorMessage ?? "task_failed");
        }
        _cancelAck.TrySetResult(false); // 若恰逢取消等待：失败也算屏障解除依据
        DebugLog.Write($"[RT-06] task_failed code={message.ErrorCode} msg={message.ErrorMessage}");
    }

    private void HandleDisconnect(long epoch)
    {
        lock (_gate)
        {
            if (_epoch != epoch) return; // 已被重建取代
            var unit = _activeUnit;
            _activeUnit = null;
            _activeTurn = null;
            _state = TtsSessionState.Failed;
            _backlogSeconds = 0;
            DrainTextQueue();
            // 已收到音频后断网：回合失败，不重发整段。
            unit?.CompleteAsFailed(-1, "connection_lost");
            _startAck.TrySetException(new InvalidOperationException("TTS 连接中断"));
        }
        _cancelAck.TrySetResult(false);
        _barrierClear.TrySetResult();
    }

    private void MarkStartFailed(long epoch, Exception? ex)
    {
        lock (_gate)
        {
            if (_epoch != epoch) return;
            _state = TtsSessionState.Failed;
            _startAck.TrySetException(ex ?? new InvalidOperationException("TTS 连接失败"));
        }
        _barrierClear.TrySetResult();
    }

    private bool IsCurrentEpoch(long epoch)
    {
        lock (_gate) return _epoch == epoch;
    }

    // Telemetry 由 MiniMaxBidiTtsClient 挂接（RT-00 打点：tts_first_encoded_audio / tts_first_pcm）。
    internal Action? OnFirstEncodedAudio;
    internal Action? OnFirstPcm;
    private bool _encodedMarked, _pcmMarked;
    private void TelemetryMarkEncoded()
    {
        if (_encodedMarked) return;
        _encodedMarked = true;
        OnFirstEncodedAudio?.Invoke();
    }
    private void TelemetryMarkPcm()
    {
        if (_pcmMarked) return;
        _pcmMarked = true;
        OnFirstPcm?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _activeUnit?.CompleteAsDropped();
            _activeUnit = null;
            _activeTurn = null;
        }
        _loopCts?.Cancel();
        using var closeCts = new CancellationTokenSource(500);
        try
        {
            if (_state is TtsSessionState.Ready or TtsSessionState.Speaking)
                _transport?.SendAsync(Encoding.UTF8.GetBytes(TtsBidiProtocol.BuildTaskFinish(_taskId)),
                    true, closeCts.Token).GetAwaiter().GetResult();
        }
        catch { /* 尽力而为的收尾 */ }
        try { _transport?.CloseAsync(closeCts.Token).GetAwaiter().GetResult(); }
        catch { }
        _loopCts?.Dispose();
        _transport?.Dispose();
    }

    // ------------------------------------------------------------------ nested types

    /// <summary>本地回合（TurnGen 程序生成，模型/服务端不能决定）。</summary>
    private sealed class BidiTurn
    {
        public readonly long Gen;
        public bool AnyAudioDelivered;
        public BidiTurn(long gen) => Gen = gen;
    }

    /// <summary>一次 task_continue+task_flush 投递单元：音频随到随读，flush 确认后完成。</summary>
    public sealed class BidiUnit
    {
        private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _flushAck = NewTcs();
        // 解码器按单元持有：状态（MP3 帧残留 / PCM 奇字节）不得跨回合/跨单元串流。
        private IAudioDecoder? _decoder;
        private readonly PcmAligner _aligner = new();

        internal long TurnGen;
        internal long Epoch;
        internal string? FailReason;
        internal int FailCode;
        private int _completed; // 0=open, 1=ok, 2=cancelled, 3=failed, 4=dropped

        /// <summary>单元音频流（接收循环写入）。</summary>
        public IAsyncEnumerable<byte[]> ReadAudioAsync(CancellationToken cancellationToken)
            => _audio.Reader.ReadAllAsync(cancellationToken);

        /// <summary>flush 确认（本轮音频已下发，不等于已播放）。</summary>
        public Task FlushAcked => _flushAck.Task;

        /// <summary>unit 结束原因；null=仍在途或正常 flush 完成后由 FlushAcked 表达。</summary>
        public string? Failure => FailReason;

        /// <summary>按声明的传输格式增量解码为 PCM16，保持样本对齐。</summary>
        internal IEnumerable<byte[]> Decode(byte[] encoded, string format, int sampleRate)
        {
            if (format.Contains("mp3", StringComparison.OrdinalIgnoreCase))
                _decoder ??= new Mp3StreamingDecoder(sampleRate);
            else
                _decoder ??= PassthroughDecoder.Instance;
            foreach (var raw in _decoder.Decode(encoded))
                foreach (var aligned in _aligner.Push(raw))
                    yield return aligned;
        }

        internal void Deliver(byte[] pcm) => _audio.Writer.TryWrite(pcm);
        internal void CompleteOk()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _audio.Writer.TryComplete();
            _flushAck.TrySetResult();
        }
        internal void CompleteAsCancelled()
        {
            if (Interlocked.Exchange(ref _completed, 2) != 0) return;
            _audio.Writer.TryComplete();
            _flushAck.TrySetCanceled();
        }
        internal void CompleteAsFailed(int code, string reason)
        {
            if (Interlocked.Exchange(ref _completed, 3) != 0) return;
            FailCode = code; FailReason = reason;
            _audio.Writer.TryComplete();
            _flushAck.TrySetException(new InvalidOperationException($"MiniMax 双向 TTS 失败（{code}）：{reason}"));
        }
        internal void CompleteAsDropped()
        {
            if (Interlocked.Exchange(ref _completed, 4) != 0) return;
            _audio.Writer.TryComplete();
            _flushAck.TrySetCanceled();
        }
    }
}

public sealed class TtsSessionCoordinatorOptions
{
    /// <summary>账号区域对应的官方 WSS 主机（必填；默认空 → 启动明确报“未实测需配置”）。</summary>
    public required string Host { get; init; }
    public string Path { get; init; } = TtsBidiProtocol.DefaultPath;
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "speech-2.8-hd";
    /// <summary>task_start 时固定的音色；会话中途不变更，需变更时安全重建。</summary>
    public required string VoiceId { get; init; }
    public double Speed { get; init; } = 1.0;
    public int SampleRate { get; init; } = 24000;
    public string? Emotion { get; init; }
    /// <summary>取消屏障等待服务端 task_cancel 确认的超时；超时即断线重建。</summary>
    public TimeSpan CancelAckTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>预估待播音频超过该秒数时暂停发送文本并限制上游。</summary>
    public double MaxBacklogSeconds { get; init; } = 30;
    /// <summary>每字符预估语音秒数（积压估算用）。</summary>
    public double SecondsPerCharEstimate { get; init; } = 0.075;
    /// <summary>空闲保活间隔。默认 InfiniteTimeSpan=关闭：keepalive 事件未与真实服务端验证。</summary>
    public TimeSpan KeepAliveInterval { get; init; } = Timeout.InfiniteTimeSpan;
}
