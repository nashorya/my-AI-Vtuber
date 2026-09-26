using System.Reflection;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests.Auth;

/// <summary>
/// AUTH-02/04/06 through the production <see cref="BotRuntime"/> entry points. Only the
/// vendor clients, the playback device and the account state are fakes.
/// </summary>
public sealed class RuntimeCloudGateTests
{
    private static readonly TalkLine Line = new(TalkIdentity.Self, "我", "大肥鱼，说句话", null);

    private sealed class Harness : IAsyncDisposable
    {
        public readonly FakeCloudAccess Cloud = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstPlayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CountingAsr Asr = new();
        public ScriptedLlm Llm = null!;
        public int Played;
        public BotRuntime Runtime = null!;
        private AudioPlayer _player = null!;

        public static async Task<Harness> CreateAsync(bool allowed, bool stallLlm = false)
        {
            var h = new Harness();
            if (allowed) h.Cloud.Grant();
            var config = new AppConfig();
            config.Asr.Streaming = false;
            h.Runtime = new BotRuntime(config, Path.GetTempPath());
            h.Runtime.UseCloudAccess(h.Cloud);
            h._player = new AudioPlayer();
            h.Llm = new ScriptedLlm("好的。", stallLlm ? h.Release.Task : null);
            var orchestrator = new BotOrchestrator(
                h.Asr, h.Llm, new StallingTts(h.Release.Task),
                h._player, new TtsConfig(), null, null,
                async (chunks, ct) =>
                {
                    await foreach (var _ in chunks.WithCancellation(ct))
                    {
                        Interlocked.Increment(ref h.Played);
                        h.FirstPlayed.TrySetResult();
                    }
                },
                () => h.Stopped.TrySetResult(),
                triggerHotkeyAsync: null);
            Set(h.Runtime, "_orchestrator", orchestrator);
            Set(h.Runtime, "_conversation", new ConversationManager(config.Llm));
            // The production subscription: gate.TurnReady -> HandleTurnReadyAsync.
            typeof(BotRuntime).GetMethod("EnsureTurnGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(h.Runtime, null);
            await Task.CompletedTask;
            return h;
        }

        public async Task DrainAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Runtime.BackgroundTaskCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(0, Runtime.BackgroundTaskCount);
        }

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await Runtime.DisposeAsync();
            _player.Dispose();
        }
    }

    [Fact]
    public async Task NotSignedIn_TalkLinesNeverReachTheModel()
    {
        await using var h = await Harness.CreateAsync(allowed: false);

        h.Runtime.AcceptTalkLine(Line);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(0, h.Llm.Calls);
        Assert.Equal(0, h.Played);
    }

    [Fact]
    public async Task SignedIn_TalkLineIsAnsweredThroughTheTurnGate()
    {
        await using var h = await Harness.CreateAsync(allowed: true);
        h.Release.SetResult();

        h.Runtime.AcceptTalkLine(Line);
        await h.FirstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, h.Llm.Calls);
    }

    [Fact]
    public async Task NotSignedIn_MicSegmentIsNotSentToAsr()
    {
        await using var h = await Harness.CreateAsync(allowed: false);

        await ObserveMicAsync(h.Runtime, LoudSegment());

        Assert.Equal(0, h.Asr.Calls);
    }

    [Fact]
    public async Task SignedIn_MicSegmentIsTranscribed()
    {
        await using var h = await Harness.CreateAsync(allowed: true);

        await ObserveMicAsync(h.Runtime, LoudSegment());

        Assert.Equal(1, h.Asr.Calls);
    }

    [Fact]
    public async Task Revocation_DuringSpeech_StopsLocallyWithoutWaitingForTheProvider()
    {
        await using var h = await Harness.CreateAsync(allowed: true);
        var revoked = new List<string>();
        h.Runtime.CloudAccessRevoked += (_, reason) => revoked.Add(reason);

        h.Runtime.AcceptTalkLine(Line);
        await h.FirstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        h.Cloud.Revoke("账号已被停用，请联系运营");

        var stoppedInTime = await Task.WhenAny(h.Stopped.Task, Task.Delay(TimeSpan.FromSeconds(2))) == h.Stopped.Task;
        Assert.True(stoppedInTime, "playback kept going while the TTS provider ignored cancellation");
        Assert.False(h.Release.Task.IsCompleted);
        Assert.Contains("已被停用", Assert.Single(revoked));

        h.Release.SetResult();
        await h.DrainAsync();
        Assert.Equal(1, h.Played);
    }

    [Fact]
    public async Task Relogin_DoesNotRevivePreviouslyStartedWork()
    {
        await using var h = await Harness.CreateAsync(allowed: true, stallLlm: true);

        h.Runtime.AcceptTalkLine(Line);
        await h.Llm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Cloud.Revoke("已退出登录");
        h.Cloud.Grant();
        h.Release.SetResult();
        await h.DrainAsync();

        Assert.Equal(0, h.Played);
    }

    [Fact]
    public async Task Revocation_DropsQueuedInputsSoTheyDoNotPlayAfterRelogin()
    {
        await using var h = await Harness.CreateAsync(allowed: true);

        h.Runtime.AcceptTalkLine(Line);
        h.Cloud.Revoke("已退出登录");
        h.Cloud.Grant();
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(0, h.Llm.Calls);
    }

    private static SpeechSegment LoudSegment()
    {
        var pcm = new byte[16000];
        for (var i = 0; i < pcm.Length; i += 2) BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)(i % 4 == 0 ? 12000 : -12000));
        return new SpeechSegment { AudioData = pcm, StartTime = DateTime.UtcNow.AddSeconds(-1), EndTime = DateTime.UtcNow };
    }

    private static Task ObserveMicAsync(BotRuntime runtime, SpeechSegment seg) =>
        (Task)typeof(BotRuntime).GetMethod("ObserveMicSegmentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [seg])!;

    private static void Set(BotRuntime runtime, string name, object value) =>
        typeof(BotRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);

    internal sealed class FakeCloudAccess : ICloudAccess
    {
        private long _epoch;
        public bool IsAllowed { get; private set; }
        public long Epoch => Interlocked.Read(ref _epoch);
        public event Action<string>? Revoked;

        public void Grant()
        {
            Interlocked.Increment(ref _epoch);
            IsAllowed = true;
        }

        public void Revoke(string reason)
        {
            IsAllowed = false;
            Interlocked.Increment(ref _epoch);
            Revoked?.Invoke(reason);
        }
    }

    internal sealed class CountingAsr : IAsrClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new AsrResult(""));
        }

        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
            IAsyncEnumerable<byte[]> audioStream,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StallingTts(Task release) : ITtsClient
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(
            string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new byte[320];
            await release;
            yield return new byte[320];
        }
    }

    /// <summary>Structured reply; optionally stalls before answering and ignores cancellation.</summary>
    internal sealed class ScriptedLlm(string speech, Task? stallUntil) : ILlmClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected { add { } remove { } }
        public event EventHandler<string>? OnActionDetected { add { } remove { } }
        public event EventHandler<string>? OnPoseDetected { add { } remove { } }

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history, string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            if (stallUntil is not null) await stallUntil;
            var raw = $"{{\"respond\":true,\"speech\":\"{speech}\"}}";
            OnSentenceReady?.Invoke(this, raw);
            yield return raw;
        }
    }
}
