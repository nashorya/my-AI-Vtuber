using System.Runtime.CompilerServices;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.RealtimeTts;

/// <summary>
/// 让编排层（RT-05 管线 / Interrupt）参与双向会话回合与取消屏障的控制器接口。
/// legacy/streaming TTS 不实现该接口，编排层行为完全不变。
/// </summary>
public interface IBidiTtsController
{
    /// <summary>回合开始：确保会话可用、等待取消屏障清空、分配本地回合代。</summary>
    Task BeginTurnAsync(CancellationToken cancellationToken);

    /// <summary>回合结束（本地记账，不发协议消息）：之后的迟到音频不归该回合。</summary>
    void EndTurn();

    /// <summary>取消屏障（见 TtsSessionCoordinator.CancelActiveTurnAsync）。</summary>
    Task<TtsCancelOutcome> CancelPendingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// MiniMax 双向 WebSocket TTS（RT-06）。实现 ITtsClient 以复用 RT-05 按段消费：
/// 每次 StreamAsync 调用 = 一次 task_continue + task_flush 投递单元，音频随到随出，
/// flush 确认即单元收束（音频已下发≠已播放，播放结算在 ReplySegmentState 账本）。
/// 选择方式：tts.provider="minimax" 且 tts.transport="bidi"；未配置 tts.bidi_host 时
/// 构造即抛出明确错误（含回退说明），绝不静默切换到其他传输。
/// 【未实测】真实端点未用 Key 验证（协议字段见 TtsBidiProtocol 注释），行为由 fake transport 测试锁定。
/// </summary>
public sealed class MiniMaxBidiTtsClient : ITtsClient, IBidiTtsController, IDisposable
{
    private readonly TtsConfig _config;
    private readonly RealtimeTrace? _trace;
    private readonly IWebSocketTransportFactory _transportFactory;
    private readonly object _gate = new();
    private TtsSessionCoordinator? _coordinator;

    /// <summary>RT-00 打点钩子（tts_first_encoded_audio / tts_first_pcm），与 RT-01 同形。</summary>
    public TtsStreamingTelemetry Telemetry { get; } = new();

    public MiniMaxBidiTtsClient(TtsConfig config, RealtimeTrace? trace = null,
        IWebSocketTransportFactory? transportFactory = null)
    {
        _config = config;
        _trace = trace;
        _transportFactory = transportFactory ?? new ClientWebSocketTransportFactory();
        // 主机名不猜：缺省即明确报“未配置/未实测”并给出显式回退说明（不静默切换）。
        if (string.IsNullOrWhiteSpace(config.BidiHost))
            throw new InvalidOperationException(TtsBidiProtocol.MissingHostMessage);
    }

    private TtsSessionCoordinator GetOrCreateCoordinator()
    {
        lock (_gate)
        {
            if (_coordinator is { } existing) return existing;
            var coordinator = new TtsSessionCoordinator(_transportFactory, new TtsSessionCoordinatorOptions
            {
                Host = _config.BidiHost,
                ApiKey = _config.ApiKey,
                Model = string.IsNullOrWhiteSpace(_config.Model) ? "speech-2.8-hd" : _config.Model,
                VoiceId = _config.VoiceId,
                Speed = _config.Speed,
                SampleRate = _config.SampleRate,
                CancelAckTimeout = TimeSpan.FromMilliseconds(_config.BidiCancelAckTimeoutMs),
                MaxBacklogSeconds = _config.BidiMaxBacklogSeconds,
                SecondsPerCharEstimate = _config.BidiSecondsPerCharEstimate,
                KeepAliveInterval = _config.BidiKeepAliveIntervalMs > 0
                    ? TimeSpan.FromMilliseconds(_config.BidiKeepAliveIntervalMs)
                    : System.Threading.Timeout.InfiniteTimeSpan,
            }, _trace);
            coordinator.OnFirstEncodedAudio += () =>
            {
                Telemetry.MarkFirstEncodedAudio();
                _trace?.Mark(RealtimeTrace.Events.TtsFirstEncodedAudio);
            };
            coordinator.OnFirstPcm += () =>
            {
                Telemetry.MarkFirstPcm();
                _trace?.Mark(RealtimeTrace.Events.TtsFirstPcm);
            };
            return _coordinator = coordinator;
        }
    }

    public async IAsyncEnumerable<byte[]> StreamAsync(
        string text,
        string voiceId,
        string? emotion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var coordinator = GetOrCreateCoordinator();
        await coordinator.EnsureVoiceAsync(string.IsNullOrWhiteSpace(voiceId) ? _config.VoiceId : voiceId, cancellationToken)
            .ConfigureAwait(false);

        _trace?.Mark(RealtimeTrace.Events.TtsRequest);
        var unit = await coordinator.SubmitTextAsync(text, cancellationToken).ConfigureAwait(false);

        bool drained = false;
        try
        {
            await foreach (var pcm in unit.ReadAudioAsync(cancellationToken).ConfigureAwait(false))
                yield return pcm;
            drained = true;
        }
        finally
        {
            if (!drained)
            {
                // 消费者取消或提前放弃枚举：立即进入取消屏障（停本地待播 → 等服务端确认/epoch 重建），
                // 不等旧音频到达，也不重发文本。
                await coordinator.CancelActiveTurnAsync(cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        // flush 确认 = 本轮音频已下发（≠已播放）；取消路径已在上方屏障内处理。
        try
        {
            await unit.FlushAcked.WaitAsync(cancellationToken).ConfigureAwait(false);
            _trace?.Mark(RealtimeTrace.Events.TtsFlushAcked);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && unit.Failure is not null)
        {
            throw new InvalidOperationException($"MiniMax 双向 TTS 单元失败：{unit.Failure}");
        }
    }

    public async Task BeginTurnAsync(CancellationToken cancellationToken)
    {
        var coordinator = GetOrCreateCoordinator();
        await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        await coordinator.BeginTurnAsync(cancellationToken).ConfigureAwait(false);
    }

    public void EndTurn() => _coordinator?.EndTurn();

    public Task<TtsCancelOutcome> CancelPendingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => _coordinator?.CancelActiveTurnAsync(timeout, cancellationToken) ??
           Task.FromResult(TtsCancelOutcome.NothingToCancel);

    public void Dispose()
    {
        lock (_gate)
        {
            _coordinator?.Dispose();
            _coordinator = null;
        }
    }
}
