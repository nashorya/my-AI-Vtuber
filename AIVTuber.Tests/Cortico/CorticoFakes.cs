using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests.Cortico;

/// <summary>Records what reaches the performance layer. Clean text follows Cortico's grammar
/// (【…】 and &lt;…&gt; removed); each speakable feed is authorized with that text, as the host does
/// per piece. It proves routing and gating only — not rendering, timing or real audio.</summary>
internal sealed class FakeCortico : ICorticoPerformance
{
    public string ScriptGrammar => "【演出台本语法】<动作>随说随做；【动作】暂停说话做动作。点头 摇头 微笑";
    public readonly List<string> Feeds = [];
    public readonly List<string> Heard = [];
    public int Begins, Interrupts, Starts, Disposed;
    public bool HoldPlayback;
    public readonly TaskCompletionSource FirstFeed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? BeforeAuthorize;

    public static string Clean(string script) => Regex.Replace(script, @"【[^】]*】|<[^>]*>", "").Trim();

    public Task<string> PrepareAsync(string script, CancellationToken ct) => Task.FromResult(Clean(script));

    public Task<ICorticoStage> BeginAsync(Func<string, bool> authorize, Action started, CancellationToken ct)
    {
        Interlocked.Increment(ref Begins);
        return Task.FromResult<ICorticoStage>(new Stage(this, authorize, started));
    }

    public Task InterruptAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Interrupts);
        return Task.CompletedTask;
    }

    private sealed class Stage(FakeCortico owner, Func<string, bool> authorize, Action started) : ICorticoStage
    {
        private bool _denied;

        public Task FeedAsync(string script, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (owner.Feeds) owner.Feeds.Add(script);
            owner.FirstFeed.TrySetResult();
            var text = Clean(script);
            if (text.Length == 0) return Task.CompletedTask;
            owner.BeforeAuthorize?.Invoke();
            if (!authorize(text)) { _denied = true; return Task.CompletedTask; }
            lock (owner.Heard) owner.Heard.Add(text);
            Interlocked.Increment(ref owner.Starts);
            started();
            owner.Playing.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken ct)
        {
            if (owner.HoldPlayback) await Task.Delay(Timeout.Infinite, ct);
            if (_denied) throw new InvalidOperationException("Turn superseded");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.Disposed);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>An LLM that streams the given raw chunks, optionally pausing after the first one.
/// With protocol "v2" the chunks go through the real <see cref="ReplyProtocolV2Parser"/>.</summary>
internal sealed class ChunkedLlm(string protocol, params string[] chunks) : ILlmClient, IReplyProtocolStream
{
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
        var parser = new ReplyProtocolV2Parser([]);
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
