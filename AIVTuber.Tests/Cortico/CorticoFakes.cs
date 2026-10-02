using System.Runtime.CompilerServices;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests.Cortico;

/// <summary>
/// Emulates the host side: each fed segment becomes one piece (clean text per Cortico grammar),
/// synthesized on request, played when upstream would ask. It proves routing, pacing hand-off and
/// the fallback rules — not rendering, real timing or real audio.
/// </summary>
internal sealed class FakeCortico : ICorticoPerformance
{
    public string ScriptGrammar => "【演出台本语法】<动作>随说随做；【动作】暂停说话做动作。点头 摇头 微笑";
    public int MaxHoldMs { get; set; } = 300;
    public bool IsAlive { get; set; } = true;
    /// <summary>When set, pieces are synthesized but never asked to play.</summary>
    public bool HoldPlay;
    public Task? FeedAcknowledgement;
    public readonly List<string> Feeds = [], Log = [];
    public int Begins, Interrupts;
    public Stage? Current;

    public static string Clean(string script) => CorticoScript.Clean(script);

    public Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct)
    {
        Interlocked.Increment(ref Begins);
        Current = new Stage(this, handler);
        return Task.FromResult<ICorticoStage>(Current);
    }

    public Task InterruptAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Interrupts);
        Current?.Cut();
        return Task.CompletedTask;
    }

    internal sealed class Stage(FakeCortico owner, ICorticoAudioHandler handler) : ICorticoStage
    {
        private long _next;
        private readonly Dictionary<long, (TaskCompletionSource Ready, TaskCompletionSource Done)> _pieces = [];
        private readonly List<Task> _runs = [];
        private readonly CancellationTokenSource _cut = new();
        public ICorticoAudioHandler Handler => handler;

        private void Note(string line) { lock (owner.Log) owner.Log.Add(line); }
        public void Cut() => _cut.Cancel();

        public Task FeedAsync(string script, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (owner.Feeds) owner.Feeds.Add(script);
            var text = Clean(script);
            if (text.Length == 0) return Task.CompletedTask;
            var id = Interlocked.Increment(ref _next);
            var entry = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                         new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (_pieces) _pieces[id] = entry;
            Task previous;
            lock (_runs) previous = _runs.Count > 0 ? _runs[^1] : Task.CompletedTask;
            // Upstream synthesizes pieces in performance order; only playback waits in the background.
            if (!_cut.IsCancellationRequested) handler.Synth(id, text);  // a cut host requests nothing more
            var run = Task.Run(async () =>
            {
                if (_cut.IsCancellationRequested) return;
                await entry.Item1.Task.WaitAsync(_cut.Token);
                await previous;                       // upstream plays pieces one after another
                // A holding host keeps the performance open until it is interrupted.
                if (owner.HoldPlay) { await Task.Delay(Timeout.Infinite, _cut.Token); return; }
                if (_cut.IsCancellationRequested) return;
                handler.Cue();
                handler.Play(id);
                await entry.Item2.Task.WaitAsync(_cut.Token);
            });
            lock (_runs) _runs.Add(run);
            return owner.FeedAcknowledgement ?? Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken ct)
        {
            Task[] runs;
            lock (_runs) runs = [.. _runs];
            await Task.WhenAll(runs).WaitAsync(ct);
            if (_cut.IsCancellationRequested) throw new InvalidOperationException("Cancelled");
        }

        private void Ready(long id) { lock (_pieces) if (_pieces.TryGetValue(id, out var p)) p.Ready.TrySetResult(); }
        private void Done(long id) { lock (_pieces) if (_pieces.TryGetValue(id, out var p)) { p.Ready.TrySetResult(); p.Done.TrySetResult(); } }

        public Task PcmAsync(long pieceId, int sampleRate, byte[] pcm) { Note($"pcm:{pieceId}"); Ready(pieceId); return Task.CompletedTask; }
        public Task SynthEndAsync(long pieceId) { Note($"synthEnd:{pieceId}"); Ready(pieceId); return Task.CompletedTask; }
        public Task SynthErrorAsync(long pieceId, string message) { Note($"synthError:{pieceId}"); Done(pieceId); return Task.CompletedTask; }
        public Task StartedAsync(long pieceId) { Note($"started:{pieceId}"); return Task.CompletedTask; }
        public Task EndedAsync(long pieceId) { Note($"ended:{pieceId}"); Done(pieceId); return Task.CompletedTask; }
        public Task StoppedAsync(long pieceId) { Note($"stopped:{pieceId}"); Done(pieceId); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _cut.Cancel(); return ValueTask.CompletedTask; }
    }
}

/// <summary>An LLM that streams the given raw chunks, optionally pausing after the first one.
/// With protocol "v2" the chunks go through the real <see cref="ReplyProtocolV2Parser"/>.</summary>
internal sealed class ChunkedLlm : ILlmClient, IReplyProtocolStream
{
    private readonly string protocol;
    private readonly string[] chunks;
    private readonly IEnumerable<string>? channels;
    public ChunkedLlm(string protocol, string[] chunks, IEnumerable<string>? channels = null)
    { this.protocol = protocol; this.chunks = chunks; this.channels = channels; }
    public ChunkedLlm(string protocol, params string[] chunks) : this(protocol, chunks, null) { }
    public Task? HoldAfterFirst;
    public readonly TaskCompletionSource FirstChunkSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Finished;
    public string ReplyProtocol => protocol;
    public event EventHandler<string>? OnSentenceReady { add { } remove { } }
    public event EventHandler<string>? OnEmotionDetected { add { } remove { } }
    public event EventHandler<string>? OnActionDetected { add { } remove { } }
    public event EventHandler<string>? OnPoseDetected { add { } remove { } }

    public async IAsyncEnumerable<string> StreamAsync(List<Message> history, string userInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (protocol == "v2") throw new InvalidOperationException("reply_protocol=v2 已启用：请使用 StreamEventsAsync");
        for (var i = 0; i < chunks.Length; i++)
        {
            yield return chunks[i];
            if (i == 0) { FirstChunkSent.TrySetResult(); if (HoldAfterFirst is not null) await HoldAfterFirst.WaitAsync(cancellationToken); }
        }
        Finished = true;
    }

    public async IAsyncEnumerable<ReplyStreamEvent> StreamEventsAsync(List<Message> history, string userInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (protocol != "v2") throw new InvalidOperationException("StreamEventsAsync 仅在 reply_protocol=v2 时可用。");
        var parser = new ReplyProtocolV2Parser(channels ?? []);
        for (var i = 0; i < chunks.Length; i++)
        {
            foreach (var ev in parser.Feed(chunks[i])) yield return ev;
            if (parser.Failed) yield break;
            if (i == 0) { FirstChunkSent.TrySetResult(); if (HoldAfterFirst is not null) await HoldAfterFirst.WaitAsync(cancellationToken); }
        }
        foreach (var ev in parser.Complete("stop")) yield return ev;
        Finished = true;
    }
}

/// <summary>Protocol v2 lines for scripted LLM replies.</summary>
internal static class V2
{
    public static string Speak => Line(new { v = 2, type = "decision", mode = "speak" });
    public static string Pass => Line(new { v = 2, type = "decision", mode = "pass" });
    public static string Thought(string text) => Line(new { v = 2, type = "decision", mode = "thought", text });
    public static string End => Line(new { v = 2, type = "end" });
    public static string Seg(int seq, string text) => Line(new { v = 2, type = "speech", seq, text });
    public static string Emotion(string value) => Line(new { v = 2, type = "control", kind = "emotion", value });
    public static string Avatar(string channel, float value) =>
        Line(new { v = 2, type = "control", kind = "avatar", targets = new Dictionary<string, float> { [channel] = value } });
    public static ChunkedLlm Say(params string[] segments) =>
        new("v2", [Speak, .. segments.Select((s, i) => Seg(i, s)), End]);
    private static string Line(object o) => System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}

/// <summary>Player stand-ins: drain the audio and fire the first-PCM callback like the real player.</summary>
internal static class TestPlay
{
    public static Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> Into(
        List<byte>? sink = null, Action<byte[]>? each = null, Func<Task>? hold = null) =>
        async (chunks, ct, first) =>
        {
            var started = false;
            try
            {
                await foreach (var chunk in chunks.WithCancellation(ct))
                {
                    if (!started) { started = true; first?.Invoke(); }
                    each?.Invoke(chunk);
                    if (sink is not null) lock (sink) sink.AddRange(chunk);
                }
                if (hold is not null) await hold().WaitAsync(ct);
            }
            catch (OperationCanceledException) { }
        };
}

/// <summary>TTS whose "audio" is the UTF-8 of the text, so played bytes read back as speech.</summary>
internal sealed class TextTts : ITtsClient
{
    public readonly List<string> Texts = [];
    public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (Texts) Texts.Add(text);
        await Task.Yield();
        yield return System.Text.Encoding.UTF8.GetBytes(text);
    }
}

/// <summary>
/// Test-only bridge for scripted LLM fakes written as whole plain-text replies: the reply is
/// classified like a v2 turn — 【PASS】 → decision pass, a lone full-width note → thought,
/// anything else → decision speak with that text as its one speech segment (the orchestrator's
/// own content isolation still judges it). Keeps those tests' intent after legacy removal.
/// </summary>
internal static class LegacyAsV2
{
    public static async IAsyncEnumerable<ReplyStreamEvent> Events(IAsyncEnumerable<string> tokens,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var raw = new System.Text.StringBuilder();
        await foreach (var token in tokens.WithCancellation(ct)) raw.Append(token);
        var text = raw.ToString().Trim();
        var classified = AIVTuber.Core.Bot.ReplyClassifier.Classify(text);
        switch (classified.Kind)
        {
            case AIVTuber.Core.Bot.ReplyKind.Pass:
                yield return ReplyStreamEvent.DecisionEvent(ReplyDecisionMode.Pass);
                break;
            case AIVTuber.Core.Bot.ReplyKind.InnerThought:
                yield return ReplyStreamEvent.DecisionEvent(ReplyDecisionMode.Thought, classified.Thought);
                break;
            default:
                yield return ReplyStreamEvent.DecisionEvent(ReplyDecisionMode.Speak);
                yield return ReplyStreamEvent.SpeechEvent(0, text);
                break;
        }
        yield return ReplyStreamEvent.EndEvent();
    }
}
