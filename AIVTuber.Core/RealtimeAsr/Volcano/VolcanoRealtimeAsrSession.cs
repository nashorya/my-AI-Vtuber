using System.Text;
using System.Text.Json;
using AIVTuber.Core.Audio;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>
/// 豆包（火山引擎）双向流式 ASR session over
/// wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async, per documented [D4] semantics:
/// auth/resource id via WebSocket handshake headers, a full JSON client request (sequence 1),
/// sequenced audio-only packets (last packet flagged), and full server responses carrying
/// <c>result.texts[]</c> entries where <c>definite</c> marks vendor finals.
///
/// Per plan §RT-03: diarization / speaker attributes and the optional two-pass re-recognition
/// are disabled by default; this project's identity source is the physical mic/loopback split.
///
/// If the configured result mode never returns a sentence-level <c>definite=true</c> (observed
/// possibility per [D4] caveat), the client closes the audio endpoint and emits the latest
/// candidate snapshot as a presumed final — explicitly marked
/// <see cref="TranscriptFinalKind.ClientEndpointSnapshot"/>, never masquerading as a vendor final.
///
/// 未实测 against the live endpoint (no real app id / access key / resource id). All behaviour
/// in this repository is verified through fake WebSocket transports.
/// </summary>
public sealed class VolcanoRealtimeAsrSession : IRealtimeAsrSession
{
    public const string Endpoint = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async";

    private readonly IWebSocketTransport _transport;
    private readonly AudioSource _source;
    private readonly long _captureEpoch;
    private readonly OpponentSnapshot? _opponent;
    private readonly string _appId;
    private readonly string _accessToken;
    private readonly string _resourceId;
    private readonly string _modelName;
    private readonly int _packetMs;
    private readonly CancellationTokenSource _cts = new();

    private readonly byte[] _sendBuffer = new byte[RealtimeAsrOptions.BytesPerMs * 400];
    private int _sendBufferFill;
    private long _audioMsSent;
    private uint _sequence = 1; // 1 = config; audio packets start at 2
    private readonly long _sessionStartTick = Environment.TickCount64;
    private readonly HashSet<uint> _seenSequences = [];
    private int _openSegmentIndex = -1;
    private int _revisionCounter;

    // Latest not-yet-definite candidate (client endpoint snapshot material).
    private string _openSnapshot = string.Empty;
    private int _openSnapshotRevision = -1;
    private long _openSnapshotAudioEndMs;

    public string ProviderSessionId { get; }

    public VolcanoRealtimeAsrSession(
        AudioSource source,
        long captureEpoch,
        OpponentSnapshot? opponent,
        string appId,
        string accessToken,
        string resourceId,
        string modelName = "bigmodel",
        IWebSocketTransport? transport = null,
        int packetMs = 200)
    {
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("豆包实时ASR缺少 app id", nameof(appId));
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("豆包实时ASR缺少 access key", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(resourceId)) throw new ArgumentException("豆包实时ASR缺少 resource id", nameof(resourceId));
        _source = source;
        _captureEpoch = captureEpoch;
        _opponent = opponent;
        _appId = appId;
        _accessToken = accessToken;
        _resourceId = resourceId;
        _modelName = modelName;
        _packetMs = packetMs <= 0 ? 200 : packetMs;
        ProviderSessionId = Guid.NewGuid().ToString("N");
        _transport = transport ?? new ClientWebSocketTransport();
    }

    /// <summary>Handshake headers (auth). Keys are never logged.</summary>
    internal IReadOnlyDictionary<string, string> AuthHeaders() => new Dictionary<string, string>
    {
        ["X-Api-App-Key"] = _appId,
        ["X-Api-Access-Key"] = _accessToken,
        ["X-Api-Resource-Id"] = _resourceId,
        ["X-Api-Request-Id"] = ProviderSessionId,
    };

    /// <summary>The full client request payload (sequence 1 config). Exposed for tests.</summary>
    internal byte[] EncodeConfigRequest()
    {
        // Diarization / speaker info and the optional two-pass re-recognition stay OFF
        // (plan §RT-03): the physical mic/loopback split is the identity source.
        var config = $$"""
{
  "user": { "appid": {{Json(_appId)}}, "uid": "ai-vtuber" },
  "audio": { "format": "pcm", "rate": 16000, "bits": 16, "channel": 1 },
  "request": {
    "model_name": {{Json(_modelName)}},
    "enable_punc": true,
    "enable_ddc": false,
    "enable_speaker_info": false,
    "enable_itn": true
  }
}
""";
        return VolcanoSaucProtocol.EncodeFullClientRequest(
            VolcanoSaucProtocol.MessageTypeFullClientRequest,
            VolcanoSaucProtocol.MessageFlagPositiveSequence,
            sequence: 1,
            payload: Encoding.UTF8.GetBytes(config),
            serialization: VolcanoSaucProtocol.SerializationJson,
            compression: VolcanoSaucProtocol.CompressionGzip);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _transport.ConnectAsync(new Uri(Endpoint), AuthHeaders(), cancellationToken).ConfigureAwait(false);
        await _transport.SendAsync(EncodeConfigRequest(), asText: false, cancellationToken).ConfigureAwait(false);
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
                await FlushPacketAsync(VolcanoSaucProtocol.MessageFlagPositiveSequence, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private async ValueTask FlushPacketAsync(byte flags, CancellationToken cancellationToken)
    {
        // Whole packet-sized sequenced chunks; only the final (last-packet) send may carry a
        // sub-packet remainder.
        var packetBytes = _packetMs * RealtimeAsrOptions.BytesPerMs;
        while (_sendBufferFill >= packetBytes && flags == VolcanoSaucProtocol.MessageFlagPositiveSequence)
        {
            var chunk = VolcanoSaucProtocol.EncodeAudioOnlyRequest(flags, ++_sequence, _sendBuffer.AsSpan(0, packetBytes));
            await _transport.SendAsync(chunk, asText: false, cancellationToken).ConfigureAwait(false);
            _audioMsSent += _packetMs;
            Array.Copy(_sendBuffer, packetBytes, _sendBuffer, 0, _sendBufferFill - packetBytes);
            _sendBufferFill -= packetBytes;
        }
        if (_sendBufferFill == 0) return;
        var packet = VolcanoSaucProtocol.EncodeAudioOnlyRequest(flags, ++_sequence, _sendBuffer.AsSpan(0, _sendBufferFill));
        await _transport.SendAsync(packet, asText: false, cancellationToken).ConfigureAwait(false);
        _audioMsSent += _sendBufferFill / RealtimeAsrOptions.BytesPerMs;
        _sendBufferFill = 0;
    }

    public async ValueTask FinishAudioAsync(CancellationToken cancellationToken)
    {
        // Final audio packet carries the negative-sequence (last packet) flag.
        await FlushPacketAsync(VolcanoSaucProtocol.MessageFlagLastPacket, cancellationToken).ConfigureAwait(false);
        var emptyLast = VolcanoSaucProtocol.EncodeAudioOnlyRequest(VolcanoSaucProtocol.MessageFlagLastPacket, ++_sequence, []);
        await _transport.SendAsync(emptyLast, asText: false, cancellationToken).ConfigureAwait(false);
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
        var sessionOver = false;
        while (!sessionOver)
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
                break; // transport closed

            VolcanoSaucProtocol.DecodedMessage decoded;
            try
            {
                decoded = VolcanoSaucProtocol.Decode(message.Data);
            }
            catch (Exception)
            {
                continue; // malformed frame — skip rather than kill the session
            }

            if (decoded.MessageType == VolcanoSaucProtocol.MessageTypeError)
                throw new InvalidOperationException(
                    $"豆包实时ASR错误: {Encoding.UTF8.GetString(decoded.Payload)}");
            if (decoded.MessageType != VolcanoSaucProtocol.MessageTypeFullServerResponse)
                continue;

            if (!_seenSequences.Add(decoded.Sequence))
                continue; // duplicate / out-of-order response — dedup by sequence

            JsonDocument doc;
            try { doc = JsonDocument.Parse(decoded.Payload); }
            catch (JsonException) { continue; }
            using var __ = doc;

            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("texts", out var texts) ||
                texts.ValueKind != JsonValueKind.Array)
                continue;

            var audioEnd = _sessionStartTick + _audioMsSent;
            foreach (var entry in texts.EnumerateArray())
            {
                var text = entry.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var definite = entry.TryGetProperty("definite", out var d) && d.GetBoolean();

                if (definite)
                {
                    _openSegmentIndex++;
                    yield return Build(
                        _openSegmentIndex.ToString(), _revisionCounter++, text,
                        isFinal: true, TranscriptFinalKind.VendorFinal, audioEnd);
                    _openSnapshot = string.Empty;
                    _openSnapshotRevision = -1;
                }
                else
                {
                    if (_openSnapshotRevision < 0) _openSegmentIndex++;
                    _openSnapshot = text;
                    _openSnapshotRevision = _revisionCounter++;
                    _openSnapshotAudioEndMs = audioEnd;
                    yield return Build(
                        _openSegmentIndex.ToString(), _openSnapshotRevision, text,
                        isFinal: false, TranscriptFinalKind.None, audioEnd);
                }
            }

            // Server marks the last response with the negative-sequence flag.
            if (decoded.MessageFlags == VolcanoSaucProtocol.MessageFlagLastPacket)
                sessionOver = true;
        }

        // This result mode yielded no sentence-level vendor final for the open segment:
        // close it with an explicit client-endpoint snapshot — a presumed candidate, NOT a
        // vendor final (plan §RT-03). Callers can see the distinction via FinalKind.
        if (_openSnapshotRevision >= 0 && !string.IsNullOrWhiteSpace(_openSnapshot))
        {
            yield return Build(
                _openSegmentIndex.ToString(), _openSnapshotRevision, _openSnapshot,
                isFinal: true, TranscriptFinalKind.ClientEndpointSnapshot, _openSnapshotAudioEndMs);
        }
    }

    private TranscriptUpdate Build(string segmentId, int revision, string text, bool isFinal,
        TranscriptFinalKind finalKind, long audioEndMs) => new(
        Source: _source,
        CaptureEpoch: _captureEpoch,
        ProviderSessionId: ProviderSessionId,
        SegmentId: segmentId,
        Revision: revision,
        TextSnapshot: text,
        IsFinal: isFinal,
        AudioStartMs: _sessionStartTick,
        AudioEndMs: audioEndMs,
        ReceivedAt: Environment.TickCount64,
        OpponentSnapshot: _opponent,
        FinalKind: finalKind);

    private static string Json(string value) => JsonSerializer.Serialize(value);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* ignore */ }
        _transport.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Creates per-source Volcano (豆包) realtime sessions.</summary>
public sealed class VolcanoRealtimeAsrSessionFactory(
    string appId,
    string accessToken,
    string resourceId,
    string modelName = "bigmodel",
    Func<IWebSocketTransport>? transportFactory = null,
    int packetMs = 200) : IRealtimeAsrSessionFactory
{
    public IRealtimeAsrSession Create(AudioSource source, long captureEpoch, OpponentSnapshot? opponent)
        => new VolcanoRealtimeAsrSession(source, captureEpoch, opponent, appId, accessToken, resourceId,
            modelName, transportFactory?.Invoke(), packetMs);
}
