using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;
using AIVTuber.Core.Voice;
using AIVTuber.Tests.Auth;

namespace AIVTuber.Tests.Distribution;

/// <summary>V01–V03 through <see cref="BotRuntime.CreateVoicePreview"/> / <see cref="BotRuntime.CreateVoiceCatalog"/>.
/// The TTS vendor and the sound card are fakes; the assertions look at the voice parameter the
/// adapter actually received and the audio that actually reached the output.</summary>
public sealed class VoicePreviewTests : IAsyncDisposable
{
    private readonly RuntimeCloudGateTests.FakeCloudAccess _cloud = new();
    private readonly BotRuntime _runtime;
    private readonly DistributionProfile _profile;
    private readonly FakeTts _tts = new();
    private readonly List<FakeOutput> _outputs = [];
    private readonly List<PreviewStatus> _statuses = [];
    private readonly VoicePreviewService _preview;

    public VoicePreviewTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"a2p-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, DistributionProfile.DirectoryName));
        File.WriteAllText(Path.Combine(dir, DistributionProfile.DirectoryName, DistributionProfile.FileName),
            StreamerConfigTests.Profile());
        _profile = DistributionProfile.TryLoad(dir)!;
        var config = new ConfigManager(Path.Combine(dir, "config.json")) { Profile = _profile }.Load();
        _runtime = new BotRuntime(config, dir, _ => Task.CompletedTask);
        _runtime.UseCloudAccess(_cloud, _profile);
        _cloud.Grant();
        _preview = _runtime.CreateVoicePreview(
            _ => { var o = new FakeOutput(); lock (_outputs) _outputs.Add(o); return o; },
            cfg => { _tts.LastConfig = cfg; return _tts; });
        _preview.StatusChanged += s => { lock (_statuses) _statuses.Add(s); };
    }

    public async ValueTask DisposeAsync()
    {
        _tts.ReleaseAll();
        _preview.Dispose();
        await _runtime.DisposeAsync();
    }

    [Fact]
    public async Task VoicePreview_DoesNotApplyCandidate()
    {
        var events = 0;
        _runtime.SentenceReady += (_, _) => events++;
        _runtime.UserTranscript += (_, _) => events++;
        _runtime.AiStartSpeaking += (_, _) => events++;

        var status = await _preview.PreviewAsync("b");

        Assert.Equal(PreviewState.Finished, status.State);
        Assert.Equal(StreamerConfigTests.VoiceB, Assert.Single(_tts.VoiceIdsRequested));      // the parameter the vendor got
        Assert.Equal(StreamerConfigTests.VoiceB, _tts.LastConfig!.VoiceId);                    // frozen per-preview settings
        Assert.Equal(VoicePreviewService.SampleText, Assert.Single(_tts.Texts));               // fixed sentence, no LLM
        Assert.Equal(StreamerConfigTests.VoiceA, _runtime.CurrentConfig.Tts.VoiceId);          // formal voice unchanged
        Assert.All(Assert.Single(_outputs).Played, c => Assert.Equal("b", Tag(c)));
        Assert.Equal(0, events); // no subtitles, transcript or formal speaking events
    }

    [Fact]
    public async Task VoicePreview_LateA_DoesNotOverrideB()
    {
        _tts.StallVoice(StreamerConfigTests.VoiceA); // A is slow and ignores cancellation

        var a = _preview.PreviewAsync("a");
        await _tts.Entered(StreamerConfigTests.VoiceA);
        var b = await _preview.PreviewAsync("b");
        _tts.ReleaseAll();
        var aStatus = await a;

        Assert.Equal(PreviewState.Finished, b.State);
        Assert.Equal(PreviewState.Stopped, aStatus.State);
        lock (_outputs)
            Assert.DoesNotContain(_outputs.SelectMany(o => o.Played), c => Tag(c) == "a");
        lock (_statuses)
        {
            // The page never sees A finish or play after B was requested.
            Assert.DoesNotContain(_statuses, s => s.RequestId == aStatus.RequestId &&
                                                  s.State is PreviewState.Playing or PreviewState.Finished);
            Assert.Equal(b.RequestId, _statuses.Last().RequestId);
        }
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("stop")]
    public async Task VoicePreview_Revoked_StopsLocally(string how)
    {
        _tts.StallVoiceAfterFirstChunk(StreamerConfigTests.VoiceB);

        var running = _preview.PreviewAsync("b");
        await _tts.Entered(StreamerConfigTests.VoiceB);
        await WaitUntil(() => { lock (_outputs) return _outputs.Count == 1 && _outputs[0].Played.Count == 1; });

        if (how == "revoke") _cloud.Revoke("已退出登录");
        else _preview.Stop();

        // Local stop does not wait for the vendor, which is still stalled.
        var status = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PreviewState.Stopped, status.State);
        Assert.True(_outputs[0].StopCalled);
        _tts.ReleaseAll();
        await Task.Delay(100);
        Assert.Single(_outputs[0].Played);
    }

    [Fact]
    public async Task VoicePreview_NeedsSignIn()
    {
        _cloud.Revoke("已退出登录");

        var status = await _preview.PreviewAsync("b");

        Assert.Equal(PreviewState.Rejected, status.State);
        Assert.Empty(_tts.VoiceIdsRequested);
    }

    [Fact]
    public async Task VoicePreview_WhileCompanionSpeaks_IsRefusedInsteadOfInterrupting()
    {
        _runtime.StateTracker.SpeakingStarted(Environment.TickCount64);

        var status = await _preview.PreviewAsync("b");

        Assert.Equal(PreviewState.Rejected, status.State);
        Assert.Contains("暂停陪播", status.Message);
        Assert.Empty(_tts.VoiceIdsRequested);
    }

    [Fact]
    public async Task VoicePreview_UnknownChoice_IsNotSubstituted()
    {
        var status = await _preview.PreviewAsync("nope");

        Assert.Equal(PreviewState.Rejected, status.State);
        Assert.Empty(_tts.VoiceIdsRequested);
    }

    [Fact]
    public async Task VoicePreview_VendorFailure_IsMappedWithoutRawText()
    {
        _tts.FailWith = new HttpRequestException("401 {\"base_resp\":{\"status_msg\":\"invalid api key sk-abcdef123456\"}}",
            null, HttpStatusCode.Unauthorized);

        var status = await _preview.PreviewAsync("b");

        Assert.Equal(PreviewState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.DoesNotContain("sk-", status.Message);
        Assert.DoesNotContain("base_resp", status.Message);
        Assert.Contains("管理员", status.Message);
    }

    // ── V01 catalog ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VoiceListFailure_PreservesCurrentSelection()
    {
        var vm = NewVm();
        vm.Working.Tts.VoiceId = StreamerConfigTests.VoiceB;
        var catalog = _runtime.CreateVoiceCatalog(_ => new ThrowingSource());

        var result = await catalog.RefreshAsync(vm);

        Assert.False(result.Checked);
        Assert.Equal("暂时无法加载音色列表，已保留当前音色。", result.Error!.UserMessage);
        Assert.Equal("b", vm.SelectedVoice?.Id);
        Assert.Equal(StreamerConfigTests.VoiceB, vm.Working.Tts.VoiceId);
    }

    [Fact]
    public async Task VoiceList_MarksCatalogEntriesFromVendor_AndNeverAddsVendorOnlyVoices()
    {
        var vm = NewVm();
        var handler = new RecordingHandler("""
            { "system_voice": [ { "voice_id": "vendor-voice-aaaa", "voice_name": "A" }, { "voice_id": "not-in-catalog" } ],
              "voice_cloning": [], "base_resp": { "status_code": 0, "status_msg": "success" } }
            """);
        var catalog = _runtime.CreateVoiceCatalog(p => VoiceCatalogService.DefaultSource(p, new HttpClient(handler)));

        var result = await catalog.RefreshAsync(vm);

        Assert.True(result.Checked);
        Assert.Equal(MiniMaxVoiceAvailabilitySource.Endpoint, handler.Request!.RequestUri);
        Assert.Equal("Bearer sk-test-tts-cccc3333", handler.Request.Headers.Authorization!.ToString());
        Assert.Contains("\"voice_type\":\"all\"", handler.Body);
        var json = System.Text.Json.JsonSerializer.Serialize(vm.BuildStreamerDraft());
        Assert.Contains("\"choiceId\":\"a\"", json);
        Assert.Contains("\"availability\":\"available\"", json);
        Assert.Contains("\"availability\":\"unavailable\"", json); // b is not on the account
        Assert.DoesNotContain("not-in-catalog", json);
    }

    [Fact]
    public async Task VoiceList_NotSignedIn_DoesNotCallVendor()
    {
        _cloud.Revoke("已退出登录");
        var handler = new RecordingHandler("{}");
        var catalog = _runtime.CreateVoiceCatalog(p => VoiceCatalogService.DefaultSource(p, new HttpClient(handler)));

        var result = await catalog.RefreshAsync(NewVm());

        Assert.False(result.Checked);
        Assert.Null(handler.Request);
    }

    private ConfigViewModel NewVm() =>
        new(_runtime.CurrentConfig, [], _ => { }, _ => Task.CompletedTask) { Profile = _profile };

    private static string Tag(byte[] chunk) => Encoding.UTF8.GetString(chunk);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class FakeTts : ITtsClient
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, TaskCompletionSource> _entered = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<string> _stallBefore = [];
        private readonly HashSet<string> _stallAfterFirst = [];
        public readonly List<string> VoiceIdsRequested = [];
        public readonly List<string> Texts = [];
        public TtsConfig? LastConfig;
        public Exception? FailWith;

        public void StallVoice(string voice) => _stallBefore.Add(voice);
        public void StallVoiceAfterFirstChunk(string voice) => _stallAfterFirst.Add(voice);
        public void ReleaseAll() => _release.TrySetResult();

        public Task Entered(string voice) => EnteredSource(voice).Task.WaitAsync(TimeSpan.FromSeconds(5));

        private TaskCompletionSource EnteredSource(string voice)
        {
            lock (_sync)
            {
                if (!_entered.TryGetValue(voice, out var tcs))
                    _entered[voice] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return tcs;
            }
        }

        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (_sync) { VoiceIdsRequested.Add(voiceId); Texts.Add(text); }
            EnteredSource(voiceId).TrySetResult();
            if (FailWith is not null) throw FailWith;
            var tag = Encoding.UTF8.GetBytes(voiceId == StreamerConfigTests.VoiceA ? "a" : "b");
            if (_stallBefore.Contains(voiceId)) await _release.Task; // deliberately ignores ct
            yield return tag;
            if (_stallAfterFirst.Contains(voiceId)) await _release.Task;
            yield return tag;
        }
    }

    private sealed class FakeOutput : IPreviewAudioOutput
    {
        private readonly CancellationTokenSource _stop = new();
        public readonly List<byte[]> Played = [];
        public bool StopCalled;

        public async Task PlayAsync(IAsyncEnumerable<byte[]> pcm, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            // Like AudioPlayer: a stop ends playback now, even if the source is still stalled.
            var feed = Task.Run(async () =>
            {
                await foreach (var chunk in pcm) lock (Played) Played.Add(chunk);
            });
            try { await feed.WaitAsync(linked.Token); }
            catch (OperationCanceledException) { }
        }

        public void Stop() { StopCalled = true; _stop.Cancel(); }
        public void Dispose() { }
    }

    private sealed class ThrowingSource : IVoiceAvailabilitySource
    {
        public Task<IReadOnlySet<string>> GetUsableVoiceIdsAsync(TtsConfig tts, CancellationToken ct) =>
            throw new HttpRequestException("connection reset by peer; Authorization: Bearer sk-secret-000000");
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string Body = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
