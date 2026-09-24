using AIVTuber.Core.Audio;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Tests;

/// <summary>
/// Same-fixture comparison harness (plan §RT-03): one scripted audio/update fixture runs
/// through BOTH provider adapters (fake transports — no real network) and yields a
/// comparable per-provider record. Real-provider benchmarking needs real keys/accounts and
/// is explicitly out of scope here.
/// </summary>
public class RealtimeAsrProviderComparisonTests
{
    public sealed record ProviderComparisonRecord(
        string Provider,
        int UpdateCount,
        int PartialCount,
        int VendorFinalCount,
        int ClientEndpointFinalCount,
        string FinalText);

    private static async Task<List<TranscriptUpdate>> RunFixtureAsync(
        string provider, IRealtimeAsrSessionFactory factory, AudioSource source)
    {
        var session = factory.Create(source, 0, new OpponentSnapshot("对手", "1", "m1"));
        var updates = new List<TranscriptUpdate>();
        var reader = Task.Run(async () =>
        {
            await foreach (var u in session.ReadUpdatesAsync(CancellationToken.None))
                updates.Add(u);
        });
        await session.StartAsync(CancellationToken.None);
        foreach (var frameSize in new[] { 30, 30, 30, 30, 30, 30, 30 })
            await session.SendFrameAsync(RealtimeAsrTestHelpers.Frame(frameSize, 0x7A), CancellationToken.None);
        await session.FinishAudioAsync(CancellationToken.None);
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
        return updates;
    }

    [Fact]
    public async Task SameFixture_BothProviders_ProduceComparableRecords()
    {
        var tencentTransport = new FakeWebSocketTransport();
        var tencent = RunFixtureAsync("tencent_realtime", new TencentRealtimeAsrSessionFactory(
            "app", "sid", "skey", transportFactory: () => tencentTransport), AudioSource.Microphone);

        var volcanoTransport = new FakeWebSocketTransport();
        var volcano = RunFixtureAsync("volcano_realtime", new VolcanoRealtimeAsrSessionFactory(
            "app", "token", "res", transportFactory: () => volcanoTransport), AudioSource.Microphone);

        // Script the server responses once the transports are connected (a fixed 210ms fixture
        // speech — "我觉得这波先别冲。" split across partials then a final).
        await RealtimeAsrTestHelpers.UntilAsync(() => tencentTransport.ConnectedTcs.Task.IsCompleted &&
                                                     volcanoTransport.ConnectedTcs.Task.IsCompleted);
        tencentTransport.PushServerMessage(
            """{"code":0,"message":"success","voice_id":"v","message_id":1,"serial":1,"result":{"slice_type":1,"voice_text_str":"我觉得"},"final":0}""");
        tencentTransport.PushServerMessage(
            """{"code":0,"message":"success","voice_id":"v","message_id":2,"serial":1,"result":{"slice_type":2,"voice_text_str":"我觉得这波先别冲。"},"final":1}""");
        // Volcano result mode without sentence-level definite=true (harder mode).
        volcanoTransport.PushServerMessage(VolcanoSaucProtocol.EncodeFullClientRequest(
            VolcanoSaucProtocol.MessageTypeFullServerResponse,
            VolcanoSaucProtocol.MessageFlagLastPacket, 1,
            System.Text.Encoding.UTF8.GetBytes(
                """{"sequence":1,"result":{"texts":[{"text":"我觉得这波先别冲。","definite":false}]}}""")));

        var tencentUpdates = await tencent;
        var volcanoUpdates = await volcano;

        var records = new List<ProviderComparisonRecord>
        {
            ToRecord("tencent_realtime", tencentUpdates),
            ToRecord("volcano_realtime", volcanoUpdates),
        };

        // The comparison record exists for both providers over the same fixture.
        Assert.Equal(2, records.Count);
        Assert.Equal("我觉得这波先别冲。", records[0].FinalText);
        Assert.Equal(1, records[0].VendorFinalCount);
        Assert.Equal("我觉得这波先别冲。", records[1].FinalText);
        Assert.Equal(1, records[1].ClientEndpointFinalCount); // this mode's final is a snapshot, not vendor final
        Assert.Equal(0, records[1].VendorFinalCount);

        static ProviderComparisonRecord ToRecord(string provider, List<TranscriptUpdate> updates) => new(
            provider,
            updates.Count,
            updates.Count(u => !u.IsFinal),
            updates.Count(u => u.IsFinal && u.FinalKind == TranscriptFinalKind.VendorFinal),
            updates.Count(u => u.IsFinal && u.FinalKind == TranscriptFinalKind.ClientEndpointSnapshot),
            FinalText: updates.Last(u => u.IsFinal).TextSnapshot);
    }
}
