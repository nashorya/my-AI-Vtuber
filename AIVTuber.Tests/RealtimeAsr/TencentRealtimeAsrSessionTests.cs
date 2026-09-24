using System.Text;
using AIVTuber.Core.Audio;
using AIVTuber.Core.RealtimeAsr;

namespace AIVTuber.Tests;

public class TencentRealtimeAsrSessionTests
{
    private const string AppId = "1255000000";
    private const string SecretId = "AKIDtest";
    private const string SecretKey = "test-secret-key";

    private static TencentRealtimeAsrSession NewSession(FakeWebSocketTransport transport, int packetMs = 200)
        => new(AudioSource.Microphone, 3, new OpponentSnapshot("对面", "42", "m1"),
            AppId, SecretId, SecretKey, engineModel: "16k_zh", transport: transport, packetMs: packetMs);

    [Fact]
    public void Signature_MatchesGoldenVector()
    {
        // Independent vector computed with: printf '<string-to-sign>' | openssl dgst -sha1 -hmac '<key>' -binary | base64
        var canonical = TencentRealtimeSignature.CanonicalQuery(new Dictionary<string, string>
        {
            ["voice_id"] = "voicetest123",
            ["timestamp"] = "1700000000",
            ["secretid"] = "AKIDtest",
            ["engine_model_type"] = "16k_zh",
            ["needvad"] = "0",
            ["expired"] = "1700003600",
        });
        Assert.Equal(
            "engine_model_type=16k_zh&expired=1700003600&needvad=0&secretid=AKIDtest&timestamp=1700000000&voice_id=voicetest123",
            canonical);
        Assert.Equal("MxeRH2pcX3r3DSczAhI72531oWM=",
            TencentRealtimeSignature.Sign(SecretKey, TencentRealtimeSignature.StringToSign(canonical)));
    }

    [Fact]
    public void SignedUrl_ContainsAppIdAndSignature_NotTheSecretKey()
    {
        var session = NewSession(new FakeWebSocketTransport());
        var url = session.BuildSignedUrl(1700000000, 1700003600);

        Assert.StartsWith("wss://asr.cloud.tencent.com/asr/v2/1255000000?", url);
        Assert.Contains("signature=", url);
        Assert.DoesNotContain(SecretKey, url);
        Assert.Contains("secretid=AKIDtest", url);
        // Deterministic: same inputs, same signature.
        Assert.Equal(url, session.BuildSignedUrl(1700000000, 1700003600));
        Assert.NotEqual(url, session.BuildSignedUrl(1700000001, 1700003600));
    }

    [Fact]
    public async Task AudioIsSentInPacketSizedChunks_AndEndMessageClosesSession()
    {
        var transport = new FakeWebSocketTransport();
        await using var session = NewSession(transport, packetMs: 200);
        await session.StartAsync(CancellationToken.None);

        // 7 × 30ms frames = 210ms → one 200ms (6400 byte) packet, 10ms stays buffered.
        for (var i = 0; i < 7; i++)
            await session.SendFrameAsync(RealtimeAsrTestHelpers.Frame(30), CancellationToken.None);

        await session.FinishAudioAsync(CancellationToken.None);

        var binary = transport.Sent.Where(s => !s.AsText).Select(s => s.Data).ToList();
        var endMessages = transport.Sent.Where(s => s.AsText).Select(s => Encoding.UTF8.GetString(s.Data)).ToList();

        // Final 10ms flush + explicit end message.
        Assert.Contains("""{"type":"end"}""", endMessages);
        Assert.Contains(binary, b => b.Length == RealtimeAsrOptions.BytesPerMs * 200);
        Assert.Contains(binary, b => b.Length == RealtimeAsrOptions.BytesPerMs * 10);
        // Total audio bytes preserved — nothing truncated.
        Assert.Equal(7 * 30 * RealtimeAsrOptions.BytesPerMs, binary.Sum(b => b.Length));
    }

    [Fact]
    public async Task SliceTypes_MapToPartialThenVendorFinal_WithDedup()
    {
        var transport = new FakeWebSocketTransport();
        await using var session = NewSession(transport);
        await session.StartAsync(CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        var readerTask = Task.Run(async () =>
        {
            await foreach (var u in session.ReadUpdatesAsync(CancellationToken.None))
                updates.Add(u);
        });
        await transport.ConnectedTcs.Task;

        transport.PushServerMessage(
            $$"""{"code":0,"message":"success","voice_id":"v","message_id":1,"serial":1,"result":{"slice_type":1,"voice_text_str":"我觉"},"final":0}""");
        transport.PushServerMessage(
            $$"""{"code":0,"message":"success","voice_id":"v","message_id":2,"serial":1,"result":{"slice_type":1,"voice_text_str":"我觉得可以"},"final":0}""");
        // Duplicate message_id — must be deduped.
        transport.PushServerMessage(
            $$"""{"code":0,"message":"success","voice_id":"v","message_id":2,"serial":1,"result":{"slice_type":1,"voice_text_str":"我觉得可以"},"final":0}""");
        transport.PushServerMessage(
            $$"""{"code":0,"message":"success","voice_id":"v","message_id":3,"serial":1,"result":{"slice_type":2,"voice_text_str":"我觉得可以。"},"final":1}""");

        await RealtimeAsrTestHelpers.UntilAsync(() => updates.Count >= 3);

        Assert.Equal(3, updates.Count); // duplicate dropped
        Assert.Contains(updates, u => u.TextSnapshot == "我觉" && !u.IsFinal);
        Assert.Contains(updates, u => u.TextSnapshot == "我觉得可以" && !u.IsFinal);
        var final = updates.Single(u => u.IsFinal);
        Assert.Equal(TranscriptFinalKind.VendorFinal, final.FinalKind);
        Assert.Equal("1", final.SegmentId);
        // Same sentence: revisions increase monotonically.
        Assert.Equal(0, updates[0].Revision);
        Assert.Equal(1, updates[1].Revision);
        Assert.True(final.Revision > updates[1].Revision);
        // Source/epoch/opponent flow through for the pump to re-tag consumers.
        Assert.Equal(AudioSource.Microphone, final.Source);
        Assert.Equal("对面", final.OpponentSnapshot?.Name);

        await session.CancelAsync();
        await readerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerError_ThrowsInsteadOfHanging()
    {
        var transport = new FakeWebSocketTransport();
        await using var session = NewSession(transport);
        await session.StartAsync(CancellationToken.None);

        var reader = session.ReadUpdatesAsync(CancellationToken.None);
        var enumerator = reader.GetAsyncEnumerator();
        transport.PushServerMessage("""{"code":4008,"message":"invalid signature"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();
    }
}
