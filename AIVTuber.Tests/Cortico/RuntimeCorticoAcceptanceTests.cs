using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Bot.Turns;
using AIVTuber.Core.Config;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;
using AIVTuber.Tests.Auth;

namespace AIVTuber.Tests.Cortico;

/// <summary>
/// Acceptance from the production Runtime entry with Cortico + reply protocol v2:
/// <see cref="BotRuntime.AcceptTalkLine"/> → v2 turn manager → real <see cref="BotOrchestrator"/> →
/// real <see cref="LlmClient"/> (v2 parser; only HTTP is faked) → real <see cref="CorticoProcess"/> →
/// real Node host with the unmodified upstream Performer/Mixer/Backend → a fake VTS that records the
/// parameter frames and expressions each model would receive. Audio device is "none": timing and
/// the mouth envelope are real, the sound card is not. No real VTS, model rendering or vendor call.
/// </summary>
public sealed class RuntimeCorticoAcceptanceTests
{
    private static readonly string[] PackParams =
    [
        "FaceAngleX", "FaceAngleY", "FaceAngleZ", "MouthOpen", "MouthSmile", "EyeOpenLeft", "EyeOpenRight",
        "EyeRightX", "EyeRightY", "EyeLeftX", "EyeLeftY", "BrowLeftY", "BrowRightY", "CheekPuff",
    ];
    private static readonly string[] LiteWired = ["MouthOpen", "FaceAngleX", "EyeOpenLeft", "EyeOpenRight"];

    private static string Speak => Line(new { v = 2, type = "decision", mode = "speak" });
    private static string End => Line(new { v = 2, type = "end" });
    private static string Seg(int seq, string text) => Line(new { v = 2, type = "speech", seq, text });
    private static string Line(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    /// <summary>Scripted OpenAI-compatible SSE endpoint; records every request body.</summary>
    private sealed class SseHandler : HttpMessageHandler
    {
        public readonly Queue<string> Replies = new();
        public readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sent = await request.Content!.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add(sent);
            string reply;
            lock (Replies) reply = Replies.Count > 0 ? Replies.Dequeue() : Speak + Seg(0, "嗯。") + End;
            var body = new StringBuilder();
            foreach (var line in reply.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                body.Append("data: ").Append(JsonSerializer.Serialize(new
                    { choices = new[] { new { delta = new { content = line + "\n" }, finish_reason = (string?)null } } })).Append("\n\n");
            body.Append("data: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
        }
    }

    /// <summary>App TTS as Cortico's synthesis source: PCM16 tone, counted; can hold one text.</summary>
    private sealed class ToneTts : ITtsClient
    {
        public readonly List<string> Texts = [];
        public string? HoldText;
        public readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Texts) Texts.Add(text);
            if (text == HoldText) { Held.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var pcm = new byte[16000 * 2 * 7 / 10];
            for (var i = 0; i < pcm.Length / 2; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(6000 * Math.Sin(i / 6.0)));
            yield return pcm;
        }
    }

    private sealed class FakeVtsProcess : IAsyncDisposable
    {
        private readonly Process _process;
        public int Port { get; private set; }
        private FakeVtsProcess(Process process) => _process = process;
        public static async Task<FakeVtsProcess> StartAsync(string sidecar, string model)
        {
            var start = new ProcessStartInfo("node") { WorkingDirectory = sidecar, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--import", "tsx", "tests/fake-vts-ctl.ts", model }) start.ArgumentList.Add(arg);
            var self = new FakeVtsProcess(Process.Start(start)!);
            var first = JsonNode.Parse((await self._process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)))!)!;
            self.Port = (int)first["port"]!;
            return self;
        }
        private async Task<JsonNode> AskAsync(object command)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
            await _process.StandardInput.FlushAsync();
            return JsonNode.Parse((await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!)!;
        }
        public Task<JsonNode> StatsAsync(int since = 0) => AskAsync(new { cmd = "stats", since });
        public Task LoadAsync(string name, string[] inputs) => AskAsync(new { cmd = "load", name, inputs });
        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { _process.Kill(true); }
            _process.Dispose();
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly RuntimeCloudGateTests.FakeCloudAccess Cloud = new();
        public readonly SseHandler Http = new();
        public readonly ToneTts Tts = new();
        public readonly List<string> Captions = [];
        public readonly List<string> Errors = [];
        public readonly List<string> Diagnostics = [];
        public int Starts, Stops;
        public BotRuntime Runtime = null!;
        public BotOrchestrator Orchestrator = null!;
        public CorticoProcess Cortico = null!;
        public FakeVtsProcess Vts = null!;
        public LlmClient Llm = null!;
        private AudioPlayer _player = null!;
        private string _temp = null!;

        public static async Task<Harness> CreateAsync(string sidecar)
        {
            var h = new Harness();
            h._temp = Path.Combine(Path.GetTempPath(), "cortico-accept-" + Guid.NewGuid().ToString("N"));
            var live2d = Path.Combine(h._temp, "Live2DModels");
            WriteModel(live2d, "RigFull", PackParams, DedicatedProfile(sidecar, "RigFull"));
            WriteModel(live2d, "RigLite", LiteWired, null);
            h.Vts = await FakeVtsProcess.StartAsync(sidecar, "RigFull");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            h.Cortico = await CorticoProcess.StartAsync(new CorticoOptions
                { Enabled = true, SidecarPath = sidecar, AudioDevice = "none", Live2dDir = live2d },
                h._temp, new VtsConfig { Host = "127.0.0.1", Port = h.Vts.Port }, () => h.Tts,
                () => new TtsConfig { SampleRate = 16000 }, line => { lock (h.Diagnostics) h.Diagnostics.Add(line); }, deadline.Token);

            h.Cloud.Grant();
            var config = new AppConfig();
            config.Asr.Streaming = false;
            config.Llm.ReplyProtocol = "v2";
            config.Realtime.TurnManagerV2Enabled = true;
            config.Identity.SelfName = "小明";
            config.Interaction.WakeKeywords = ["可缇"];
            h.Runtime = new BotRuntime(config, Path.GetTempPath());
            h.Runtime.UseCloudAccess(h.Cloud);
            h.Runtime.PipelineError += (_, e) => { lock (h.Errors) h.Errors.Add(e); };
            Set(h.Runtime, "_cortico", h.Cortico);
            // The production prompt for this configuration (Cortico present) and the production LLM client.
            var systemPrompt = (string)typeof(BotRuntime).GetMethod("BuildLlmSystemPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(h.Runtime, null)!;
            h.Llm = new LlmClient(systemPrompt, null, h.Http, "v2", scriptMarkup: true);
            h._player = new AudioPlayer();
            h.Orchestrator = new BotOrchestrator(new RuntimeCloudGateTests.CountingAsr(), h.Llm, new ThrowingTts(), h._player,
                new TtsConfig(), null, null,
                (_, _) => throw new InvalidOperationException("the app player must not play in Cortico mode"),
                () => { }, triggerHotkeyAsync: null) { Cortico = h.Cortico };
            h.Orchestrator.OnSentenceReady += (_, t) => { lock (h.Captions) h.Captions.Add(t); };
            h.Orchestrator.OnError += (_, e) => { lock (h.Errors) h.Errors.Add(e); };
            h.Orchestrator.OnAiStartSpeaking += (_, _) => Interlocked.Increment(ref h.Starts);
            h.Orchestrator.OnAiStopSpeaking += (_, _) => Interlocked.Increment(ref h.Stops);
            Set(h.Runtime, "_orchestrator", h.Orchestrator);
            Set(h.Runtime, "_conversation", new ConversationManager(config.Llm));
            typeof(BotRuntime).GetMethod("EnsureTurnGate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(h.Runtime, null);
            h.Runtime.WireOrchestrator(h.Orchestrator);
            return h;
        }

        public void Say(string text) => Runtime.AcceptTalkLine(new TalkLine(TalkIdentity.Self, "小明", text, null));

        public async Task DrainAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Runtime.BackgroundTaskCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Cortico.DisposeAsync();
            await Vts.DisposeAsync();
            _player.Dispose();
            try { Directory.Delete(_temp, true); } catch (IOException) { }
        }
    }

    private sealed class ThrowingTts : ITtsClient
    {
        public IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion, CancellationToken ct = default) =>
            throw new InvalidOperationException("the orchestrator must not synthesize in Cortico mode");
    }

    private static void Set(BotRuntime runtime, string name, object value) =>
        typeof(BotRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);

    private static void WriteModel(string live2d, string name, string[] wired, JsonNode? profile)
    {
        var dir = Path.Combine(live2d, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".vtube.json"), JsonSerializer.Serialize(new
        {
            Name = name,
            ParameterSettings = wired.Select(i => new { Input = i, OutputLive2D = "Param" + i, Smoothing = 0 }),
        }));
        if (profile is not null) File.WriteAllText(Path.Combine(dir, "cortico.profile.json"), profile.ToJsonString());
    }

    /// <summary>The upstream example profile re-targeted to RigFull, with its head-pitch axis
    /// wired inverted at half strength — a mapping no other rig shares.</summary>
    private static JsonNode DedicatedProfile(string sidecar, string name)
    {
        var profile = JsonNode.Parse(File.ReadAllText(Path.Combine(sidecar, "upstream/models/examples/cortico.profile.json")))!;
        profile["id"] = name;
        profile["label"] = name;
        profile["vtsModelName"] = name;
        profile["wiring"]!["FaceAngleY"] = new JsonObject { ["invert"] = true, ["scale"] = 0.5 };
        profile["fx"] = new JsonObject();
        profile["keepExpressions"] = new JsonArray();
        return profile;
    }

    private static string? Sidecar()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "sidecar/cortico/host.ts")))
            directory = directory.Parent;
        if (directory is null) return null;
        var sidecar = Path.Combine(directory.FullName, "sidecar/cortico");
        return Directory.Exists(Path.Combine(sidecar, "node_modules/tsx")) ? sidecar : null;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("condition not met before timeout");
            await Task.Delay(20);
        }
    }

    private static double Peak(JsonNode stats, string id) => stats["peak"]?[id]?.GetValue<double>() ?? 0;
    private static double Max(JsonNode stats, string id) => stats["range"]?[id]?[1]?.GetValue<double>() ?? 0;
    private static double Min(JsonNode stats, string id) => stats["range"]?[id]?[0]?.GetValue<double>() ?? 0;

    [SkippableFact]
    public async Task V2Reply_FromTheRuntimeEntry_IsPerformedByCortico_OnceWithMatchingMouthAndAction()
    {
        var sidecar = Sidecar();
        Skip.If(sidecar is null, "Run npm ci in sidecar/cortico first (Node.js 22+)");
        await using var h = await Harness.CreateAsync(sidecar!);
        h.Http.Replies.Enqueue(Speak + Seg(0, "<微笑>你好呀，我是可缇。") + Seg(1, "【用力点头】很高兴见到你。") + End);
        var before = (int)(await h.Vts.StatsAsync())["total"]!;

        h.Say("可缇，你好");
        await Until(() => Volatile.Read(ref h.Stops) == 1);
        await h.DrainAsync();

        // Prompt and parser agree: v2 envelope, script markup in speech, no control lines.
        var body = JsonNode.Parse(Assert.Single(h.Http.Requests))!;
        var request = string.Join("\n", body["messages"]!.AsArray().Select(m => (string?)m!["content"] ?? ""));
        Assert.Contains("演出台本语法", request);
        Assert.Contains("不要输出 control 行", request);
        Assert.DoesNotContain("需要动作：", request);
        Assert.DoesNotContain("只输出一个 JSON 对象", request);
        Assert.DoesNotContain("控制走 control 行", request); // the turn-history policy matches too

        // One voice: each piece synthesized once, clean text only, one speaking start.
        Assert.Equal(["你好呀，我是可缇。", "很高兴见到你。"], h.Tts.Texts);
        Assert.Equal(["你好呀，我是可缇。", "很高兴见到你。"], h.Captions);
        Assert.Equal(1, h.Starts);
        Assert.Empty(h.Errors);

        // Mouth and action reached the loaded rig through its own mapping (inverted, half-strength nod).
        var stats = await h.Vts.StatsAsync(before);
        Assert.Equal(["RigFull"], stats["models"]!.AsArray().Select(m => (string)m!));
        Assert.True(Peak(stats, "MouthOpen") > 0.1, stats.ToJsonString());
        Assert.True(Max(stats, "FaceAngleY") > 8 && Min(stats, "FaceAngleY") > -5, stats.ToJsonString());
    }

    [SkippableFact]
    public async Task StopDuringThePerformance_StopsVoiceAndAction_AndTheNextTurnWorks()
    {
        var sidecar = Sidecar();
        Skip.If(sidecar is null, "Run npm ci in sidecar/cortico first (Node.js 22+)");
        await using var h = await Harness.CreateAsync(sidecar!);
        h.Tts.HoldText = "这句不该被听到。";
        h.Http.Replies.Enqueue(Speak + Seg(0, "【拼命摇头】不行不行。") + Seg(1, "这句不该被听到。") + End);

        var turnStart = (int)(await h.Vts.StatsAsync())["total"]!;
        h.Say("可缇，讲个故事");
        // Stop as soon as the head shake (a 1.3 s gesture) is visibly under way.
        var deadline = Environment.TickCount64 + 15000;
        while (Peak(await h.Vts.StatsAsync(turnStart), "FaceAngleX") < 5)
        {
            Assert.True(Environment.TickCount64 < deadline, "the shake never started");
            await Task.Delay(10);
        }
        h.Runtime.StopSpeaking();
        await h.DrainAsync();

        await Task.Delay(300); // a gesture under way fades out (200 ms) instead of snapping
        var afterStop = (int)(await h.Vts.StatsAsync())["total"]!;
        await Task.Delay(800);
        var quiet = await h.Vts.StatsAsync(afterStop);
        // Idle life (slow wander, gaze settling) may keep a small steady offset; the shake swings
        // ±20° (measured: span ≈ 43° without the fix, ≤ 5° with it). No swing, no large excursion.
        Assert.True(Max(quiet, "FaceAngleX") - Min(quiet, "FaceAngleX") < 12 && Peak(quiet, "FaceAngleX") < 10,
            "head shake continued after stop: " + quiet.ToJsonString());
        Assert.True(Peak(quiet, "MouthOpen") < 0.05, "mouth moved after stop: " + quiet.ToJsonString());
        Assert.DoesNotContain("这句不该被听到。", h.Captions);
        Assert.True(h.Starts <= 1);
        Assert.Empty(h.Errors);

        h.Http.Replies.Enqueue(Speak + Seg(0, "好，换个话题。") + End);
        var startsBefore = Volatile.Read(ref h.Starts);
        h.Say("可缇，换个话题");
        await Until(() => Volatile.Read(ref h.Starts) == startsBefore + 1);
        await Until(() => h.Captions.Contains("好，换个话题。"));
    }

    [SkippableFact]
    public async Task SwitchingToARigWithoutProfileOrBodyAxes_CutsTheOldTurn_AndOnlyItsWiredInputsAreDriven()
    {
        var sidecar = Sidecar();
        Skip.If(sidecar is null, "Run npm ci in sidecar/cortico first (Node.js 22+)");
        await using var h = await Harness.CreateAsync(sidecar!);

        // A turn in flight on RigFull when the streamer loads another rig in VTS.
        h.Tts.HoldText = "说到一半。";
        h.Http.Replies.Enqueue(Speak + Seg(0, "【用力点头】说到一半。") + End);
        h.Say("可缇，你在吗");
        await h.Tts.Held.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await h.Vts.LoadAsync("RigLite", PackParams); // VTS lists every default input; the model file wires four
        await Until(() => { lock (h.Diagnostics) return h.Diagnostics.Any(d => d.Contains("\"model\":\"RigLite\"")); });
        await h.DrainAsync();
        Assert.Empty(h.Errors); // a turn cut by a model switch is not a failure
        Assert.DoesNotContain("说到一半。", h.Captions);

        string status;
        lock (h.Diagnostics) status = h.Diagnostics.Last(d => d.Contains("\"model\":\"RigLite\""));
        Assert.Contains("\"mode\":\"conservative\"", status);
        var before = (int)(await h.Vts.StatsAsync())["total"]!;

        h.Tts.HoldText = null;
        h.Http.Replies.Enqueue(Speak + Seg(0, "【用力点头,拼命摇头】换好了。") + End);
        h.Say("可缇，换好了吗");
        await Until(() => h.Captions.Contains("换好了。"));
        await h.DrainAsync();
        await Task.Delay(300);

        var lite = await h.Vts.StatsAsync(before);
        Assert.Equal(["RigLite"], lite["models"]!.AsArray().Select(m => (string)m!));
        var ids = lite["ids"]!.AsArray().Select(i => (string)i!).ToHashSet();
        Assert.True(ids.IsSubsetOf(LiteWired), "unwired inputs were driven on RigLite: " + string.Join(",", ids));
        Assert.True(Peak(lite, "MouthOpen") > 0.1, lite.ToJsonString());
        Assert.True(Peak(lite, "FaceAngleX") > 2, "shake on the axis the rig has: " + lite.ToJsonString());
        Assert.Empty(h.Errors);
    }
}
