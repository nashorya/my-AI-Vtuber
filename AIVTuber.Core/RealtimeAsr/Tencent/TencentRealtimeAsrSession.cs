using System.Text.Json;
using AIVTuber.Core.Audio;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>
/// 腾讯云实时语音识别 session (wss://asr.cloud.tencent.com/asr/v2/{appid}), per documented
/// [D3] semantics: client-signed URL, ~200 ms binary audio packets, JSON results with
/// slice_type 1 (可变文本/partial) and 2 (稳态/final), session+serial dedup, and a JSON
/// end message that terminates the session.
///
/// 未实测 against the live endpoint — no real AppId/SecretKey was used. All behaviour in
/// this repository is verified through fake WebSocket transports.
/// </summary>
public sealed class TencentRealtimeAsrSession : IRealtimeAsrSession
{
    private readonly IWebSocketTransport _transport;
    private readonly string _appId;
    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly string _engineModel;
    private readonly AudioSource _source;
    private readonly long _captureEpoch;
    private readonly OpponentSnapshot? _opponent;
    private readonly int _packetMs;
    private readonly CancellationTokenSource _cts = new();

    private readonly byte[] _sendBuffer = new byte[RealtimeAsrOptions.BytesPerMs * 400]; // max 400ms packets
    private int _sendBufferFill;
    private long _audioMsSent;
    private readonly long _sessionStartTick = Environment.TickCount64;
    private readonly Dictionary<string, int> _revisions = new(StringComparer.Ordinal);
    private readonly HashSet<long> _seenMessageIds = [];
    private int _revisionCounter;

    public string ProviderSessionId { get; }

    public TencentRealtimeAsrSession(
        AudioSource source,
        long captureEpoch,
        OpponentSnapshot? opponent,
        string appId,
        string secretId,
        string secretKey,
        string engineModel = "16k_zh",
        IWebSocketTransport? transport = null,
        int packetMs = 200)
    {
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("腾讯实时ASR缺少 appid", nameof(appId));
        if (string.IsNullOrWhiteSpace(secretId)) throw new ArgumentException("腾讯实时ASR缺少 secretid", nameof(secretId));
        if (string.IsNullOrWhiteSpace(secretKey)) throw new ArgumentException("腾讯实时ASR缺少 secretkey", nameof(secretKey));
        _source = source;
        _captureEpoch = captureEpoch;
        _opponent = opponent;
        _appId = appId;
        _secretId = secretId;
        _secretKey = secretKey;
        _engineModel = engineModel;
        _packetMs = packetMs <= 0 ? 200 : packetMs;
        ProviderSessionId = Guid.NewGuid().ToString("N");
        _transport = transport ?? new ClientWebSocketTransport();
    }

    /// <summary>Builds the signed URL (exposed for tests). Secret key is not part of it.</summary>
    public string BuildSignedUrl(long unixTimestamp, long expired)
    {
        var parameters = new Dictionary<string, string>
        {
            ["secretid"] = _secretId,
            ["timestamp"] = unixTimestamp.ToString(),
            ["expired"] = expired.ToString(),
            ["engine_model_type"] = _engineModel,
            ["voice_id"] = ProviderSessionId,
            ["needvad"] = "0",
        };
        var query = TencentRealtimeSignature.BuildSignedQuery(parameters, _secretKey);
        return $"wss://{TencentRealtimeSignature.Host}/asr/v2/{Uri.EscapeDataString(_appId)}?{query}";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = BuildSignedUrl(now, now + 3600);
        await _transport.ConnectAsync(new Uri(url), headers: null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < pcm16k.Length)
        {
            var take = Math.Min(pcm16k.Length - offset, _sendBuffer.Length - _sendBufferFill);
            pcm16k.Span.Slice(offset, take).CopyTo(_sendBuffer.AsSpan(_sendBufferFill));
            _sendBufferFill += take;
            offset += take;
            if (_sendBufferFill >= _packetMs * RealtimeAsrOptions.BytesPerMs)
                await FlushPacketsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends whole packet-sized chunks, keeping the sub-packet remainder buffered so
    /// the provider receives ~packetMs frames (documented pacing).</summary>
    private async ValueTask FlushPacketsAsync(CancellationToken cancellationToken)
    {
        var packetBytes = _packetMs * RealtimeAsrOptions.BytesPerMs;
        while (_sendBufferFill >= packetBytes)
        {
            await _transport.SendAsync(_sendBuffer.AsMemory(0, packetBytes), asText: false, cancellationToken)
                .ConfigureAwait(false);
            _audioMsSent += _packetMs;
            Array.Copy(_sendBuffer, packetBytes, _sendBuffer, 0, _sendBufferFill - packetBytes);
            _sendBufferFill -= packetBytes;
        }
    }

    /// <summary>Flushes everything including a sub-packet remainder (end of audio).</summary>
    private async ValueTask FlushAllAsync(CancellationToken cancellationToken)
    {
        await FlushPacketsAsync(cancellationToken).ConfigureAwait(false);
        if (_sendBufferFill == 0) return;
        await _transport.SendAsync(_sendBuffer.AsMemory(0, _sendBufferFill), asText: false, cancellationToken)
            .ConfigureAwait(false);
        _audioMsSent += _sendBufferFill / RealtimeAsrOptions.BytesPerMs;
        _sendBufferFill = 0;
    }

    public async ValueTask FinishAudioAsync(CancellationToken cancellationToken)
    {
        await FlushAllAsync(cancellationToken).ConfigureAwait(false);
        // The documented end message terminates this recognition session; the connection
        // is not reusable for another task afterwards.
        await _transport.SendAsync("""{"type":"end"}"""u8.ToArray(), asText: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CancelAsync()
    {
        _cts.Cancel();
        try { await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* abort path */ }
    }

    public async IAsyncEnumerable<TranscriptUpdate> ReadUpdatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var reader = RunReaderAsync(linked.Token);
        await foreach (var update in reader)
            yield return update;
    }

    private async IAsyncEnumerable<TranscriptUpdate> RunReaderAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var audioEnd = _sessionStartTick;
        while (true)
        {
            WebSocketTransportResult message;
            try
            {
                message = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            if (message.Data.Length == 0)
                yield break; // transport closed

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(message.Data);
            }
            catch (JsonException)
            {
                continue; // ignore malformed frame rather than killing the session
            }
            using var _ = doc;
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new InvalidOperationException($"腾讯实时ASR错误 code={code.GetInt32()} message={msg}");
            }

            if (root.TryGetProperty("message_id", out var messageId) &&
                !_seenMessageIds.Add(messageId.GetInt64()))
                continue; // duplicate message — dedup by message_id

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("voice_text_str", out var textEl)) continue;

            var text = textEl.GetString();
            if (string.IsNullOrEmpty(text)) text = string.Empty;
            var sliceType = result.TryGetProperty("slice_type", out var st) ? st.GetInt32() : 1;
            var serial = root.TryGetProperty("serial", out var s) ? s.GetInt64() : 0;
            var isFinalFlag = root.TryGetProperty("final", out var f) && f.GetInt32() == 1;

            var segmentId = serial.ToString();
            var revision = _revisionCounter++;
            var finalKind = sliceType == 2 ? TranscriptFinalKind.VendorFinal : TranscriptFinalKind.None;
            if (_revisions.TryGetValue(segmentId, out var lastRev) && revision <= lastRev)
                revision = lastRev + 1;
            _revisions[segmentId] = revision;

            // Map the provider's cumulative audio clock onto the local capture timeline.
            audioEnd = _sessionStartTick + _audioMsSent;
            var audioStart = _revisions.Count > 0 && revision == 0 ? audioEnd : audioEnd;

            yield return new TranscriptUpdate(
                Source: _source,
                CaptureEpoch: _captureEpoch,
                ProviderSessionId: ProviderSessionId,
                SegmentId: segmentId,
                Revision: revision,
                TextSnapshot: text,
                IsFinal: finalKind == TranscriptFinalKind.VendorFinal,
                AudioStartMs: audioStart,
                AudioEndMs: audioEnd,
                ReceivedAt: Environment.TickCount64,
                OpponentSnapshot: _opponent,
                FinalKind: finalKind);

            if (isFinalFlag)
                yield break; // recognition complete — session over
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* ignore */ }
        _transport.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Creates per-source Tencent realtime sessions.</summary>
public sealed class TencentRealtimeAsrSessionFactory(
    string appId,
    string secretId,
    string secretKey,
    string engineModel = "16k_zh",
    Func<IWebSocketTransport>? transportFactory = null,
    int packetMs = 200) : IRealtimeAsrSessionFactory
{
    public IRealtimeAsrSession Create(AudioSource source, long captureEpoch, OpponentSnapshot? opponent)
        => new TencentRealtimeAsrSession(source, captureEpoch, opponent, appId, secretId, secretKey,
            engineModel, transportFactory?.Invoke(), packetMs);
}
