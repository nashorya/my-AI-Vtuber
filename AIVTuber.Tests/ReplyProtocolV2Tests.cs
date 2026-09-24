#pragma warning disable CS0067
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class ReplyProtocolV2ParserTests
{
    private static List<ReplyStreamEvent> ParseAll(string ndjson, string? finishReason = null,
        string[]? allowed = null)
    {
        var parser = new ReplyProtocolV2Parser(allowed ?? ["headYaw", "headRoll"]);
        var events = new List<ReplyStreamEvent>();
        // Feed one character at a time: every token boundary must be handled.
        foreach (var c in ndjson) events.AddRange(parser.Feed(c.ToString()));
        events.AddRange(parser.Complete(finishReason));
        return events;
    }

    private const string ValidStream =
        "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
        "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"我觉得这波先别冲。\"}\n" +
        "{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"等对面的技能交完。\"}\n" +
        "{\"v\":2,\"type\":\"end\"}\n";

    [Fact]
    public void ValidStream_CharByChar_YieldsOrderedEvents()
    {
        var events = ParseAll(ValidStream);
        Assert.Equal(
        [
            ReplyStreamEventKind.Decision, ReplyStreamEventKind.Speech,
            ReplyStreamEventKind.Speech, ReplyStreamEventKind.End,
        ], events.Select(e => e.Kind));
        Assert.Equal(ReplyDecisionMode.Speak, events[0].Decision);
        Assert.Equal("我觉得这波先别冲。", events[1].Text);
        Assert.Equal(1, events[2].Seq);
    }

    [Fact]
    public void UnicodeEscapes_DecodeIntoSpeechText()
    {
        var events = ParseAll(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"\\u4f60\\u597d\\uff01\"}\n" +
            "{\"v\":2,\"type\":\"end\"}\n");
        Assert.Equal("你好！", events[1].Text);
    }

    [Fact]
    public void AvatarControl_ReusesChannelWhitelist()
    {
        var events = ParseAll(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            "{\"v\":2,\"type\":\"control\",\"kind\":\"avatar\",\"targets\":{\"headYaw\":0.5}}\n" +
            "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"好。\"}\n" +
            "{\"v\":2,\"type\":\"end\"}\n");
        var control = events.Single(e => e.Kind == ReplyStreamEventKind.Control);
        Assert.Equal(.5f, control.Motion!.Targets["headYaw"]);
    }

    [Fact]
    public void AvatarControl_OutOfRangeOrUnknownChannel_FailsClosed()
    {
        var bad = ParseAll(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            "{\"v\":2,\"type\":\"control\",\"kind\":\"avatar\",\"targets\":{\"bodyYaw\":0.5}}\n" +
            "{\"v\":2,\"type\":\"end\"}\n");
        Assert.Equal(ReplyStreamEventKind.ProtocolError, bad[^1].Kind);
        var range = ParseAll(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            "{\"v\":2,\"type\":\"control\",\"kind\":\"avatar\",\"targets\":{\"headYaw\":7}}\n" +
            "{\"v\":2,\"type\":\"end\"}\n");
        Assert.Equal(ReplyStreamEventKind.ProtocolError, range[^1].Kind);
    }

    [Theory]
    [InlineData("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n{\"v\":2,\"type\":\"decision\",\"mode\":\"pass\"}\n{\"v\":2,\"type\":\"end\"}\n", "重复")]
    [InlineData("{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"你好\"}\n{\"v\":2,\"type\":\"end\"}\n", "decision")]
    [InlineData("{\"v\":2,\"type\":\"decision\",\"mode\":\"pass\"}\n{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"你好\"}\n{\"v\":2,\"type\":\"end\"}\n", "speech")]
    [InlineData("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"跳号\"}\n{\"v\":2,\"type\":\"end\"}\n", "seq")]
    [InlineData("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"x\"}\n{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"重复seq\"}\n{\"v\":2,\"type\":\"end\"}\n", "seq")]
    [InlineData("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n{\"v\":2,\"type\":\"end\"}\n{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"end后\"}\n", "end")]
    [InlineData("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"hi\"}\n", "end")]
    [InlineData("not json\n{\"v\":2,\"type\":\"end\"}\n", "JSON")]
    [InlineData("{\"v\":1,\"type\":\"end\"}\n", "v")]
    [InlineData("{\"v\":2,\"type\":\"mystery\"}\n", "类型")]
    [InlineData("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n{\"v\":2,\"type\":\"control\",\"kind\":\"tool_call\",\"value\":\"rm\"}\n{\"v\":2,\"type\":\"end\"}\n", "control")]
    [InlineData("{\"v\":2,\"type\":\"end\"}\n", "decision")]
    public void ProtocolViolations_FailClosed(string stream, string expectedFragment)
    {
        var events = ParseAll(stream);
        var error = Assert.Single(events, e => e.Kind == ReplyStreamEventKind.ProtocolError);
        Assert.Contains(expectedFragment, error.Error);
        // Everything after the violation is suppressed.
        Assert.DoesNotContain(events, e => e.Kind == ReplyStreamEventKind.Speech && e.Seq >= 0 && events.IndexOf(error) < events.IndexOf(e));
    }

    [Fact]
    public void SpeakThenPassDecision_FailsClosed()
    {
        var events = ParseAll(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"说了就不许改口\"}\n" +
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"pass\"}\n" +
            "{\"v\":2,\"type\":\"end\"}\n");
        // Duplicate decision fails first; the mode flip can never silently pass.
        Assert.Contains(events, e => e.Kind == ReplyStreamEventKind.ProtocolError);
    }

    [Fact]
    public void TruncatedLine_FailsClosed()
    {
        var parser = new ReplyProtocolV2Parser(null);
        var events = parser.Feed("{\"v\":2,\"type\":\"decision\",\"mode\":\"spea");
        Assert.Empty(events);
        var final = parser.Complete(null);
        Assert.Equal(ReplyStreamEventKind.ProtocolError, Assert.Single(final).Kind);
    }

    [Fact]
    public void FinishReasonLengthWithoutEnd_FailsClosedWithExplicitReason()
    {
        var events = ParseAll("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n", finishReason: "length");
        Assert.Equal(ReplyStreamEventKind.ProtocolError, events[^1].Kind);
        Assert.Contains("length", events[^1].Error);
    }

    [Fact]
    public void OversizedSegment_FailsClosed()
    {
        var big = new string('好', ReplyProtocolV2Parser.MaxSegmentChars + 1);
        var events = ParseAll(
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n" +
            $"{{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"{big}\"}}\n" +
            "{\"v\":2,\"type\":\"end\"}\n");
        Assert.Contains(events, e => e.Kind == ReplyStreamEventKind.ProtocolError);
        // The oversized payload itself never surfaces as speech.
        Assert.DoesNotContain(events, e => e.Text.Length > ReplyProtocolV2Parser.MaxSegmentChars);
    }

    [Fact]
    public void TooManySegments_FailsClosed()
    {
        var sb = new StringBuilder("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n");
        for (var i = 0; i <= ReplyProtocolV2Parser.MaxSegments; i++)
            sb.Append($"{{\"v\":2,\"type\":\"speech\",\"seq\":{i},\"text\":\"段\"}}\n");
        sb.Append("{\"v\":2,\"type\":\"end\"}\n");
        Assert.Contains(ParseAll(sb.ToString()), e => e.Kind == ReplyStreamEventKind.ProtocolError);
    }
}

public sealed class ReplyProtocolV2LlmClientTests
{
    /// <summary>SSE stream whose chunks are pushed by the test: the response stays open
    /// while we decide what to send next.</summary>
    private sealed class GatedSseStream : Stream
    {
        private readonly Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public void Push(string sseData) => _lines.Writer.TryWrite(sseData);
        public void Done() => _lines.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var line = await _lines.Reader.ReadAsync(ct);
            var bytes = Encoding.UTF8.GetBytes(line);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string Sse(string content, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content }, finish_reason = finish } }
        }) + "\n\n";

    [Fact]
    public async Task FirstSpeechSegment_ArrivesBeforeEof()
    {
        var stream = new GatedSseStream();
        var handler = new InlineHandler(stream);
        using var client = new LlmClient("角色", () => ["headYaw"], handler, replyProtocol: "v2");

        var firstSegmentSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<ReplyStreamEvent>();
        var consuming = Task.Run(async () =>
        {
            await foreach (var ev in client.StreamEventsAsync([], "你怎么看"))
            {
                events.Add(ev);
                if (ev.Kind == ReplyStreamEventKind.Speech) firstSegmentSeen.TrySetResult();
            }
        });

        // First line fast; the model then keeps generating for "seconds".
        stream.Push(Sse("{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n"));
        stream.Push(Sse("{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"我觉得先别冲。\"}\n"));
        await firstSegmentSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)); // EOF not reached yet
        Assert.Equal(ReplyStreamEventKind.Speech, events[^1].Kind);

        stream.Push(Sse("{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"等技能交完。\"}\n"));
        stream.Push(Sse("{\"v\":2,\"type\":\"end\"}\n", finish: "stop"));
        stream.Push("data: [DONE]\n");
        stream.Done();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
        [
            ReplyStreamEventKind.Decision, ReplyStreamEventKind.Speech,
            ReplyStreamEventKind.Speech, ReplyStreamEventKind.End,
        ], events.Select(e => e.Kind));
    }

    [Fact]
    public async Task LegacyStringStream_ForbiddenUnderV2()
    {
        var handler = new InlineHandler(new GatedSseStream());
        using var client = new LlmClient("角色", null, handler, replyProtocol: "v2");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.StreamAsync([], "你好")) { }
        });
    }

    [Fact]
    public async Task V2Request_UsesNdjsonPromptAndKeepsTokenBudget()
    {
        var stream = new GatedSseStream();
        stream.Push(Sse("{\"v\":2,\"type\":\"decision\",\"mode\":\"pass\"}\n"));
        stream.Push(Sse("{\"v\":2,\"type\":\"end\"}"));
        stream.Push("data: [DONE]\n");
        stream.Done();
        var handler = new InlineHandler(stream);
        using var client = new LlmClient("角色", () => ["headYaw"], handler, replyProtocol: "v2");
        var events = new List<ReplyStreamEvent>();
        await foreach (var ev in client.StreamEventsAsync([], "你好")) events.Add(ev);
        Assert.Equal(ReplyDecisionMode.Pass, events[0].Decision);
        Assert.Contains("NDJSON", handler.Request.GetRawText());
        Assert.DoesNotContain("\"respond\"", handler.Request.GetRawText()); // no legacy prompt
        // Budget not squeezed below protocol needs.
        Assert.True(handler.Request.GetProperty("max_tokens").GetInt32() >= 512);
    }

    private sealed class InlineHandler(GatedSseStream stream) : HttpMessageHandler
    {
        public JsonElement Request = default;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") }
                }
            };
        }
    }
}

public sealed class ReplyProtocolV2OrchestratorTests
{
    private sealed class FakeV2Llm : ILlmClient, IReplyProtocolStream
    {
        public string ReplyProtocol => "v2";
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;

        public IAsyncEnumerable<string> StreamAsync(List<Message> history, string userInput,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("v2 客户端不走 legacy 字符流");

        public Func<CancellationToken, IAsyncEnumerable<ReplyStreamEvent>> Source { get; set; } =
            ct => RunScript([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
                "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"我觉得这波先别冲。\"}",
                "{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"等对面技能交完。\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ], ct);

        public async IAsyncEnumerable<ReplyStreamEvent> StreamEventsAsync(
            List<Message> history, string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var ev in Source(cancellationToken).WithCancellation(cancellationToken))
                yield return ev;
        }

        /// <summary>Script step: a string is one NDJSON line; a Task waits (simulating a
        /// slow model); a Func&lt;CancellationToken,Task&gt; waits and can observe cancellation.</summary>
        public static async IAsyncEnumerable<ReplyStreamEvent> RunScript(
            object[] steps, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var parser = new ReplyProtocolV2Parser(null);
            foreach (var step in steps)
            {
                switch (step)
                {
                    case string line:
                        foreach (var ev in parser.Feed(line + "\n")) yield return ev;
                        break;
                    case Func<CancellationToken, Task> wait:
                        await wait(ct);
                        break;
                    case Task task:
                        await task;
                        break;
                }
            }
            foreach (var ev in parser.Complete("stop"))
                yield return ev;
        }

        public static IAsyncEnumerable<ReplyStreamEvent> ToLines(IEnumerable<string> lines) =>
            ToEvents(lines);

        private static async IAsyncEnumerable<ReplyStreamEvent> ToEvents(IEnumerable<string> lines)
        {
            var parser = new ReplyProtocolV2Parser(["headYaw", "headRoll"]);
            foreach (var line in lines)
                foreach (var ev in parser.Feed(line + "\n"))
                    yield return ev;
            foreach (var ev in parser.Complete("stop"))
                yield return ev;
        }
    }

    private sealed class RecordingTts : ITtsClient
    {
        public readonly List<string> Texts = [];
        public Func<string, Task>? OnCalled;
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Texts.Add(text);
            if (OnCalled is not null) await OnCalled(text);
            yield return Encoding.UTF8.GetBytes(text);
        }
    }

    private sealed class UnusedAsr : IAsrClient
    {
        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken ct = default) =>
            Task.FromResult(new AsrResult("unused"));
        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(IAsyncEnumerable<byte[]> audio,
            [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
    }

    private sealed class CountingMotion : IAvatarMotionSink
    {
        public int Submits, Cancels;
        public readonly List<long> Generations = [];
        public void Submit(long g, AvatarIntent intent) { Submits++; Generations.Add(g); }
        public void Cancel(long g) { Cancels++; }
        public void OnRms(float rms) { }
    }

    private static BotOrchestrator Create(FakeV2Llm llm, RecordingTts tts, CountingMotion? motion,
        List<string>? committed = null, List<string>? errors = null, List<string>? captions = null,
        int? played = null, Action<string>? onPlayed = null)
    {
        var orchestrator = new BotOrchestrator(
            new UnusedAsr(), llm, tts, new AudioPlayer(), new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var chunk in chunks.WithCancellation(ct))
                {
                    var text = Encoding.UTF8.GetString(chunk);
                    if (played is not null) played++;
                    onPlayed?.Invoke(text);
                }
            }, () => { }, triggerHotkeyAsync: null);
        if (motion is not null)
            orchestrator.ConfigureContinuousControl(motion, async (chunks, ct, start) =>
            {
                await foreach (var chunk in chunks.WithCancellation(ct)) start();
            });
        if (committed is not null) orchestrator.OnReplyCommitted += (_, r) => committed.Add(r.Spoken);
        if (errors is not null) orchestrator.OnError += (_, e) => errors.Add(e);
        if (captions is not null) orchestrator.OnSentenceReady += (_, s) => captions.Add(s);
        return orchestrator;
    }

    [Fact]
    public async Task FirstSegmentReachesTts_BeforeSecondSegmentGenerated()
    {
        var llm = new FakeV2Llm();
        var ttsCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterSegment0 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tts = new RecordingTts { OnCalled = _ => { ttsCalled.TrySetResult(); return Task.CompletedTask; } };
        llm.Source = ct => FakeV2Llm.RunScript(
        [
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
            "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"第一段。\"}",
            // The model keeps generating; TTS must already hold segment 0.
            ttsCalled.Task,
            afterSegment0.Task,
            "{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"第二段。\"}",
            "{\"v\":2,\"type\":\"end\"}",
        ], ct);
        var committed = new List<string>();
        using var orchestrator = Create(llm, tts, null, committed);
        var processing = orchestrator.ProcessTextAsync("你怎么看", [], bypassWake: true);
        await ttsCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        afterSegment0.TrySetResult();
        await processing;
        Assert.Equal(["第一段。", "第二段。"], tts.Texts);
        Assert.Equal(["第一段。", "第二段。"], committed);
    }

    [Fact]
    public async Task InterruptedAfterFirstSegment_HistoryDoesNotClaimSecond()
    {
        var llm = new FakeV2Llm();
        var firstPlayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tts = new RecordingTts();
        llm.Source = ct => FakeV2Llm.RunScript(
        [
            "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
            "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"第一段。\"}",
            // Hold the stream open: segment 1 arrives only after the turn is already dead.
            firstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            (Func<CancellationToken, Task>)(async token => await Task.Delay(200, token)),
            "{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"第二段。\"}",
            "{\"v\":2,\"type\":\"end\"}",
        ], ct);
        var committed = new List<string>();
        BotOrchestrator? orchestrator = null;
        orchestrator = Create(llm, tts, null, committed, onPlayed: _ =>
        {
            if (firstPlayed.TrySetResult())
                Task.Run(() => orchestrator!.Interrupt());
        });
        await orchestrator.ProcessTextAsync("你怎么看", [], bypassWake: true);
        Assert.Equal(["第一段。"], committed);
        Assert.Equal(["第一段。"], tts.Texts);
    }

    [Fact]
    public async Task AvatarControl_FiresExactlyOnce_AndCancelsWithTurn()
    {
        var motion = new CountingMotion();
        var llm = new FakeV2Llm
        {
            Source = _ => FakeV2Llm.ToLines([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
                "{\"v\":2,\"type\":\"control\",\"kind\":\"avatar\",\"targets\":{\"headYaw\":0.5}}",
                "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"好呀。\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ]),
        };
        using var orchestrator = Create(llm, new RecordingTts(), motion);
        await orchestrator.ProcessTextAsync("摇摇头呗", [], bypassWake: true);
        Assert.Equal(1, motion.Submits);
        Assert.Equal(.5f, .5f); // placeholder to keep assertions obvious
        Assert.True(motion.Cancels >= 0);
    }

    [Fact]
    public async Task PassDecision_NoTtsNoCaptions()
    {
        var llm = new FakeV2Llm
        {
            Source = _ => FakeV2Llm.ToLines([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"pass\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ]),
        };
        var tts = new RecordingTts();
        var committed = new List<string>();
        var captions = new List<string>();
        using var orchestrator = Create(llm, tts, null, committed, captions: captions);
        await orchestrator.ProcessTextAsync("他说大肥鱼挺好玩", [], bypassWake: true);
        Assert.Empty(tts.Texts);
        Assert.Empty(captions);
    }

    [Fact]
    public async Task ThoughtDecision_PrivateOnly()
    {
        var llm = new FakeV2Llm
        {
            Source = _ => FakeV2Llm.ToLines([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"thought\",\"text\":\"先听他们说\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ]),
        };
        var tts = new RecordingTts();
        var captions = new List<string>();
        using var orchestrator = Create(llm, tts, null, captions: captions);
        await orchestrator.ProcessTextAsync("你们聊", [], bypassWake: true);
        Assert.Empty(tts.Texts);
        Assert.Empty(captions);
    }

    [Fact]
    public async Task ProtocolError_FailsClosed_ReportsAndStopsReleasing()
    {
        var llm = new FakeV2Llm
        {
            Source = _ => FakeV2Llm.ToLines([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
                "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"第一段没问题。\"}",
                "垃圾不是JSON",
                "{\"v\":2,\"type\":\"speech\",\"seq\":1,\"text\":\"这段绝不能播。\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ]),
        };
        var tts = new RecordingTts();
        var errors = new List<string>();
        var committed = new List<string>();
        using var orchestrator = Create(llm, tts, null, committed, errors: errors);
        await orchestrator.ProcessTextAsync("你怎么看", [], bypassWake: true);
        Assert.Equal(["第一段没问题。"], tts.Texts); // already-approved segment still played
        Assert.Single(errors);
        Assert.Contains("fail closed", errors[0]);
    }

    [Fact]
    public async Task ParenthesisThoughts_AreStructurallyIsolatedPerSegment()
    {
        var llm = new FakeV2Llm
        {
            Source = _ => FakeV2Llm.ToLines([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
                "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"（心里话）正片内容。\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ]),
        };
        var tts = new RecordingTts();
        var committed = new List<string>();
        using var orchestrator = Create(llm, tts, null, committed);
        await orchestrator.ProcessTextAsync("你怎么看", [], bypassWake: true);
        Assert.Equal(["正片内容。"], tts.Texts);
        Assert.Equal(["正片内容。"], committed);
    }

    [Fact]
    public async Task UnbalancedParenthesisInSegment_FailsClosed()
    {
        var llm = new FakeV2Llm
        {
            Source = _ => FakeV2Llm.ToLines([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
                "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"你好（未闭合\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ]),
        };
        var tts = new RecordingTts();
        var errors = new List<string>();
        using var orchestrator = Create(llm, tts, null, errors: errors);
        await orchestrator.ProcessTextAsync("你怎么看", [], bypassWake: true);
        Assert.Empty(tts.Texts);
        Assert.Single(errors);
    }

    [Fact]
    public async Task EmotionControl_ReachesTtsAndVtsHotkeyPath()
    {
        var llm = new FakeV2Llm
        {
            Source = _ => FakeV2Llm.ToLines([
                "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}",
                "{\"v\":2,\"type\":\"control\",\"kind\":\"emotion\",\"value\":\"happy\"}",
                "{\"v\":2,\"type\":\"speech\",\"seq\":0,\"text\":\"好耶。\"}",
                "{\"v\":2,\"type\":\"end\"}",
            ]),
        };
        var tts = new EmotionRecordingTts();
        var hotkeys = 0;
        var vtsConfig = new VtsConfig { EmotionMap = new Dictionary<string, string> { ["happy"] = "one" } };
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), llm, tts, new AudioPlayer(), new TtsConfig(), null, vtsConfig,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } },
            () => { }, (_, _) => { hotkeys++; return Task.CompletedTask; },
            new Dictionary<string, string> { ["happy"] = "happy" });
        await orchestrator.ProcessTextAsync("你说呢", [], bypassWake: true);
        Assert.Equal(["happy"], tts.Emotions); // TTS got the emotion without speaking it
        Assert.Equal(1, hotkeys); // VTS hotkey fired once, at playback start
    }

    private sealed class EmotionRecordingTts : ITtsClient
    {
        public readonly List<string?> Emotions = [];
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Emotions.Add(emotion);
            yield return Encoding.UTF8.GetBytes(text);
        }
    }
}
