using System.Reflection;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;
using AIVTuber.Tests.Auth;

namespace AIVTuber.Tests.Distribution;

/// <summary>
/// U05 and the companion pause, entered through the production mic-segment path of a real
/// <see cref="BotRuntime"/>/<see cref="BotOrchestrator"/>. Only the vendor clients, the playback
/// device and the account are fakes.
/// </summary>
public sealed class RuntimeAsrHealthTests
{
    private sealed class ScriptAsr : IAsrClient
    {
        public Func<CancellationToken, Task<AsrResult>> Next = _ => Task.FromResult(new AsrResult("你好呀"));
        public int Calls;

        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Next(ct);
        }

        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(IAsyncEnumerable<byte[]> audio,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class SilentTts : ITtsClient
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new byte[320];
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly RuntimeCloudGateTests.FakeCloudAccess Cloud = new();
        public readonly ScriptAsr Asr = new();
        public readonly RuntimeCloudGateTests.ScriptedLlm Llm = new("好的。", null);
        public readonly List<string> Transcripts = [];
        public BotRuntime Runtime = null!;
        private AudioPlayer _player = null!;

        public static Harness Create()
        {
            var h = new Harness();
            h.Cloud.Grant();
            var config = new AppConfig();
            config.Asr.Streaming = false;
            h.Runtime = new BotRuntime(config, Path.GetTempPath());
            h.Runtime.UseCloudAccess(h.Cloud);
            h.Runtime.UserTranscript += (_, t) => { lock (h.Transcripts) h.Transcripts.Add(t); };
            h._player = new AudioPlayer();
            var orchestrator = new BotOrchestrator(h.Asr, h.Llm, new SilentTts(), h._player, new TtsConfig(), null, null,
                async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } },
                () => { }, triggerHotkeyAsync: null);
            Set(h.Runtime, "_orchestrator", orchestrator);
            Set(h.Runtime, "_conversation", new ConversationManager(config.Llm));
            typeof(BotRuntime).GetMethod("EnsureTurnGate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(h.Runtime, null);
            return h;
        }

        public Task ObserveMicAsync() =>
            (Task)typeof(BotRuntime).GetMethod("ObserveMicSegmentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Runtime, [LoudSegment()])!;

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            _player.Dispose();
        }
    }

    [Fact]
    public async Task CloudAsrHealth_NotDerivedFromLocalSidecar()
    {
        await using var h = Harness.Create();
        Assert.False(h.Runtime.LocalAsrActive);
        Assert.Equal(AsrHealth.Unknown, h.Runtime.CurrentAsrHealth); // not checked yet ≠ broken

        await h.ObserveMicAsync();

        Assert.Equal(1, h.Asr.Calls);
        Assert.Equal(AsrHealth.Ready, h.Runtime.CurrentAsrHealth);
    }

    [Fact]
    public async Task CloudAsrFailure_IsUnavailable_AndRecoversOnNextSuccess()
    {
        await using var h = Harness.Create();
        h.Asr.Next = _ => Task.FromException<AsrResult>(new HttpRequestException("503"));

        await Assert.ThrowsAsync<HttpRequestException>(h.ObserveMicAsync);
        Assert.Equal(AsrHealth.Unavailable, h.Runtime.CurrentAsrHealth);

        h.Asr.Next = _ => Task.FromResult(new AsrResult("好了"));
        await h.ObserveMicAsync();
        Assert.Equal(AsrHealth.Ready, h.Runtime.CurrentAsrHealth);
    }

    [Fact]
    public async Task InFlightRecognition_IsReportedAsRecognizing()
    {
        await using var h = Harness.Create();
        var release = new TaskCompletionSource<AsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Asr.Next = _ => release.Task;

        var observe = h.ObserveMicAsync();
        Assert.Equal(AsrHealth.Recognizing, h.Runtime.CurrentAsrHealth);
        release.SetResult(new AsrResult("嗯"));
        await observe;
        Assert.Equal(AsrHealth.Ready, h.Runtime.CurrentAsrHealth);
    }

    [Fact]
    public async Task PausedCompanion_SendsNothingToAsr_AndSaysPaused()
    {
        await using var h = Harness.Create();

        h.Runtime.SetCompanionPaused(true);
        await h.ObserveMicAsync();
        h.Runtime.AcceptTalkLine(new TalkLine(TalkIdentity.Danmaku, "观众", "在吗", "42"));
        await Task.Delay(300);

        Assert.Equal(0, h.Asr.Calls);
        Assert.Equal(0, h.Llm.Calls);
        Assert.Equal(AsrHealth.Paused, h.Runtime.CurrentAsrHealth);
        Assert.False(h.Runtime.MicMuted); // pausing is not muting the microphone

        h.Runtime.SetCompanionPaused(false);
        await h.ObserveMicAsync();
        Assert.Equal(1, h.Asr.Calls);
    }

    [Fact]
    public async Task SignedOut_ReportsPaused_NotBroken()
    {
        await using var h = Harness.Create();
        h.Cloud.Revoke("已退出登录");

        Assert.Equal(AsrHealth.Paused, h.Runtime.CurrentAsrHealth);
    }

    [Fact]
    public async Task AsrResultArrivingAfterRelogin_IsNotTreatedAsNewInput()
    {
        await using var h = Harness.Create();
        var release = new TaskCompletionSource<AsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Asr.Next = _ => release.Task; // ignores cancellation on purpose

        var observe = h.ObserveMicAsync();
        h.Cloud.Revoke("已退出登录");
        h.Cloud.Grant(); // a new login before the old recognition returns
        release.SetResult(new AsrResult("旧许可下说的话"));
        await observe;
        await Task.Delay(300);

        lock (h.Transcripts) Assert.Empty(h.Transcripts);
        Assert.Equal(0, h.Llm.Calls);
    }

    private static SpeechSegment LoudSegment()
    {
        var pcm = new byte[16000];
        for (var i = 0; i < pcm.Length; i += 2) BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)(i % 4 == 0 ? 12000 : -12000));
        return new SpeechSegment { AudioData = pcm, StartTime = DateTime.UtcNow.AddSeconds(-1), EndTime = DateTime.UtcNow };
    }

    private static void Set(BotRuntime runtime, string name, object value) =>
        typeof(BotRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);
}
