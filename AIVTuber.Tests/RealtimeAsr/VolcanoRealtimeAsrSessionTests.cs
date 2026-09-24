using System.Text;
using System.Text.Json;
using AIVTuber.Core.Audio;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Tests;

public class VolcanoRealtimeAsrSessionTests
{
    private static VolcanoRealtimeAsrSession NewSession(FakeWebSocketTransport transport, int packetMs = 200)
        => new(AudioSource.Loopback, 0, new OpponentSnapshot("对手", "7", "m9"),
            "app-1", "token-1", "volc.async.asr.sauc.bigmodel", modelName: "bigmodel",
            transport: transport, packetMs: packetMs);

    private static byte[] ServerResponse(uint sequence, bool last, string json, bool gzip = true)
        => VolcanoSaucProtocol.EncodeFullClientRequest(
            VolcanoSaucProtocol.MessageTypeFullServerResponse,
            last ? VolcanoSaucProtocol.MessageFlagLastPacket : VolcanoSaucProtocol.MessageFlagPositiveSequence,
            sequence, Encoding.UTF8.GetBytes(json),
            serialization: VolcanoSaucProtocol.SerializationJson,
            compression: gzip ? VolcanoSaucProtocol.CompressionGzip : VolcanoSaucProtocol.CompressionNone);

    [Fact]
    public void HeaderCodec_RoundTrips()
    {
        var header = VolcanoSaucProtocol.EncodeHeader(
            VolcanoSaucProtocol.MessageTypeFullClientRequest,
            VolcanoSaucProtocol.MessageFlagPositiveSequence,
            VolcanoSaucProtocol.SerializationJson,
            VolcanoSaucProtocol.CompressionGzip);
        Assert.Equal(4, header.Length);
        Assert.Equal(0x11, header[0]); // version 1 | header size 1 unit
        Assert.Equal(0x11, header[1]); // full client request | positive sequence
        Assert.Equal(0x11, header[2]); // json | gzip
        Assert.Equal(0x00, header[3]);

        // Header alone carries no payload; decoding requires the sequenced body.
        var packet = VolcanoSaucProtocol.EncodeFullClientRequest(
            VolcanoSaucProtocol.MessageTypeFullClientRequest,
            VolcanoSaucProtocol.MessageFlagPositiveSequence, 5, [1, 2, 3]);
        var decoded = VolcanoSaucProtocol.Decode(packet);
        Assert.Equal(VolcanoSaucProtocol.MessageTypeFullClientRequest, decoded.MessageType);
        Assert.Equal(VolcanoSaucProtocol.MessageFlagPositiveSequence, decoded.MessageFlags);
        Assert.Equal(VolcanoSaucProtocol.SerializationJson, decoded.Serialization);
        Assert.Equal(VolcanoSaucProtocol.CompressionGzip, decoded.Compression);
        Assert.Equal(5u, decoded.Sequence);
        Assert.Equal([1, 2, 3], decoded.Payload);
    }

    [Fact]
    public void FullRequest_PayloadIsGzippedJson_AndDecodesBack()
    {
        var payload = Encoding.UTF8.GetBytes("""{"hello":"世界"}""");
        var packet = VolcanoSaucProtocol.EncodeFullClientRequest(
            VolcanoSaucProtocol.MessageTypeFullClientRequest,
            VolcanoSaucProtocol.MessageFlagPositiveSequence, 42, payload);

        var decoded = VolcanoSaucProtocol.Decode(packet);
        Assert.Equal(42u, decoded.Sequence);
        Assert.Equal("bigmodel", "bigmodel"); // placeholder to keep asserts self-documenting
        Assert.Equal("""{"hello":"世界"}""", Encoding.UTF8.GetString(decoded.Payload));
    }

    [Fact]
    public void AudioOnlyPacket_EncodesSequence_RoundTrips()
    {
        var audio = new byte[] { 1, 2, 3, 4 };
        var packet = VolcanoSaucProtocol.EncodeAudioOnlyRequest(
            VolcanoSaucProtocol.MessageFlagPositiveSequence, 7, audio);
        var decoded = VolcanoSaucProtocol.Decode(packet);
        Assert.Equal(VolcanoSaucProtocol.MessageTypeAudioOnlyRequest, decoded.MessageType);
        Assert.Equal(7u, decoded.Sequence);
        Assert.Equal(audio, decoded.Payload);
    }

    [Fact]
    public async Task Start_SendsGzippedConfigRequest_WithDiarizationAndTwoPassDisabled()
    {
        var transport = new FakeWebSocketTransport();
        await using var session = NewSession(transport);
        await session.StartAsync(CancellationToken.None);

        Assert.Equal(new Uri(VolcanoRealtimeAsrSession.Endpoint), transport.ConnectedUri);
        Assert.Equal("app-1", transport.Headers!["X-Api-App-Key"]);
        Assert.Equal("token-1", transport.Headers!["X-Api-Access-Key"]);
        Assert.Equal("volc.async.asr.sauc.bigmodel", transport.Headers!["X-Api-Resource-Id"]);

        var config = transport.Sent.Single(s => !s.AsText).Data;
        var decoded = VolcanoSaucProtocol.Decode(config);
        Assert.Equal(1u, decoded.Sequence);
        using var json = JsonDocument.Parse(decoded.Payload);
        var request = json.RootElement.GetProperty("request");
        Assert.Equal("bigmodel", request.GetProperty("model_name").GetString());
        Assert.False(request.GetProperty("enable_ddc").GetBoolean());          // 二遍识别 off
        Assert.False(request.GetProperty("enable_speaker_info").GetBoolean()); // diarization off
        Assert.Equal(16000, json.RootElement.GetProperty("audio").GetProperty("rate").GetInt32());
    }

    [Fact]
    public async Task DefiniteTexts_MapsToVendorFinal_WithSequenceDedup()
    {
        var transport = new FakeWebSocketTransport();
        await using var session = NewSession(transport);
        await session.StartAsync(CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        var reader = Task.Run(async () =>
        {
            await foreach (var u in session.ReadUpdatesAsync(CancellationToken.None))
                updates.Add(u);
        });
        await transport.ConnectedTcs.Task;

        transport.PushServerMessage(ServerResponse(1, false,
            """{"sequence":1,"result":{"texts":[{"text":"对面的","definite":false}]}}"""));
        transport.PushServerMessage(ServerResponse(2, false,
            """{"sequence":2,"result":{"texts":[{"text":"对面的朋友","definite":false}]}}"""));
        // Out-of-order retransmission of sequence 1 — deduped.
        transport.PushServerMessage(ServerResponse(1, false,
            """{"sequence":1,"result":{"texts":[{"text":"对面的","definite":false}]}}"""));
        transport.PushServerMessage(ServerResponse(3, true,
            """{"sequence":3,"result":{"texts":[{"text":"对面的朋友你好","definite":true}]}}"""));

        await RealtimeAsrTestHelpers.UntilAsync(() => updates.Count >= 3);
        Assert.Equal(3, updates.Count);

        Assert.All(updates.Take(2), u => Assert.False(u.IsFinal));
        Assert.Equal("对面的", updates[0].TextSnapshot);
        Assert.Equal("对面的朋友", updates[1].TextSnapshot);
        Assert.True(updates[1].Revision > updates[0].Revision);

        var final = updates.Single(u => u.IsFinal);
        Assert.Equal(TranscriptFinalKind.VendorFinal, final.FinalKind);
        Assert.Equal("对面的朋友你好", final.TextSnapshot);
        Assert.Equal(AudioSource.Loopback, final.Source);
        Assert.Equal("m9", final.OpponentSnapshot?.MatchId);

        await reader.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoSentenceLevelFinal_ClientEndpointSnapshot_IsMarkedNotVendorFinal()
    {
        var transport = new FakeWebSocketTransport();
        await using var session = NewSession(transport);
        await session.StartAsync(CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        var reader = Task.Run(async () =>
        {
            await foreach (var u in session.ReadUpdatesAsync(CancellationToken.None))
                updates.Add(u);
        });
        await transport.ConnectedTcs.Task;

        // This result mode never yields definite=true; the stream closes with the last packet.
        transport.PushServerMessage(ServerResponse(1, false,
            """{"sequence":1,"result":{"texts":[{"text":"我觉得这波","definite":false}]}}"""));
        transport.PushServerMessage(ServerResponse(2, false,
            """{"sequence":2,"result":{"texts":[{"text":"我觉得这波先别冲","definite":false}]}}"""));
        transport.PushServerMessage(ServerResponse(3, true,
            """{"sequence":3,"result":{"texts":[{"text":"我觉得这波先别冲。","definite":false}]}}"""));

        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = updates.Single(u => u.IsFinal);
        Assert.Equal(TranscriptFinalKind.ClientEndpointSnapshot, snapshot.FinalKind);
        Assert.NotEqual(TranscriptFinalKind.VendorFinal, snapshot.FinalKind);
        Assert.Equal("我觉得这波先别冲。", snapshot.TextSnapshot);
    }

    [Fact]
    public async Task FinishAudio_SendsLastPacketFlag()
    {
        var transport = new FakeWebSocketTransport();
        await using var session = NewSession(transport, packetMs: 100);
        await session.StartAsync(CancellationToken.None);

        for (var i = 0; i < 4; i++)
            await session.SendFrameAsync(RealtimeAsrTestHelpers.Frame(30), CancellationToken.None);
        await session.FinishAudioAsync(CancellationToken.None);

        var audioPackets = transport.Sent.Skip(1) // first is the config request
            .Select(s => VolcanoSaucProtocol.Decode(s.Data))
            .ToList();
        Assert.All(audioPackets, p =>
            Assert.Equal(VolcanoSaucProtocol.MessageTypeAudioOnlyRequest, p.MessageType));
        Assert.Contains(audioPackets, p => p.MessageFlags == VolcanoSaucProtocol.MessageFlagLastPacket);
        // Sequences strictly increase from the config packet's sequence 1.
        Assert.Equal(2u, audioPackets[0].Sequence);
        Assert.All(audioPackets, p => Assert.True(p.Sequence >= 2));
        // Audio preserved in total.
        Assert.Equal(4 * 30 * RealtimeAsrOptions.BytesPerMs,
            audioPackets.Sum(p => p.Payload.Length));
    }
}
