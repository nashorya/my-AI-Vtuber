using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot.Pacing;

/// <summary>
/// Cortico decides when each piece may start; the app synthesizes and plays it. Host requests
/// arrive through <see cref="ICorticoAudioHandler"/> on the IPC reader and never block it. When the
/// sidecar dies, its heartbeat lapses, it holds a ready piece past its longest legitimate beat plus
/// 2 s, or it aborts the reply (model switch), the rest of the reply plays voice-only through an
/// <see cref="ImmediatePacer"/> after the piece that is currently audible.
/// </summary>
internal sealed class CorticoPacer : ISpeechPacer, ICorticoAudioHandler
{
    internal const int HoldGraceMs = 2000;

    private sealed class Piece(long id, string text)
    {
        public long Id { get; } = id;
        public string Text { get; } = text;
        public PieceAudio? Audio;
        public bool Ready, Played;
        public CancellationTokenSource? PlayCts;
        public Task? Playback;
    }

    private readonly ICorticoPerformance _cortico;
    private readonly SpeechTurnPorts _ports;
    private readonly CancellationToken _turn;
    private readonly Func<long> _clock;
    private readonly TimeSpan _watchInterval;
    private readonly CancellationTokenSource _life = new();
    private readonly object _sync = new();
    private readonly SortedDictionary<long, Piece> _pieces = new();
    private readonly List<string> _segments = [];
    private int _segmentCursor, _charCursor;
    private readonly StringBuilder _pendingActions = new();
    private readonly TaskCompletionSource _fellBack = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _denied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ICorticoStage? _stage;
    private ImmediatePacer? _fallback;
    private Piece? _playing;
    private bool _fedSpeech;
    private long _lastProgress;
    private Task _watchdog = Task.CompletedTask;

    public CorticoPacer(ICorticoPerformance cortico, SpeechTurnPorts ports, CancellationToken turn,
        Func<long>? clock = null, TimeSpan? watchInterval = null)
    {
        _cortico = cortico;
        _ports = ports;
        _turn = turn;
        _clock = clock ?? (() => Environment.TickCount64);
        _watchInterval = watchInterval ?? TimeSpan.FromMilliseconds(250);
        _lastProgress = _clock();
    }

    // ── segments from the reply ────────────────────────────────────────────────────

    public async Task SubmitAsync(SpeechItem item, CancellationToken ct)
    {
        ImmediatePacer? fallback;
        lock (_sync) fallback = _fallback;
        var clean = CorticoScript.Clean(item.Text);
        if (fallback is not null) { fallback.Enqueue(new SpeechItem(clean)); return; }
        if (!LlmClient.IsSpeakableText(clean))
        {
            // Actions alone never start a turn: without a voice there is no gate. Keep them.
            _pendingActions.Append(item.Text);
            return;
        }
        if (_stage is null)
        {
            try
            {
                _stage = await _cortico.BeginAsync(this, ct).ConfigureAwait(false);
                Touch();
                if (_watchInterval != Timeout.InfiniteTimeSpan) _watchdog = Task.Run(WatchAsync);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                FallBack($"皮套未能开演（{ex.Message}）");
                lock (_sync) _fallback!.Enqueue(new SpeechItem(clean));
                return;
            }
        }
        var script = _pendingActions + item.Text;
        _pendingActions.Clear();
        lock (_sync) _segments.Add(clean);
        try
        {
            await _stage.FeedAsync(script, ct).ConfigureAwait(false);
            _fedSpeech = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            FallBack($"皮套未接收台词（{ex.Message}）");
        }
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_stage is not null && _pendingActions.Length > 0 && _fedSpeech && FallbackOrNull() is null && _ports.CanSpeak())
        {
            try { await _stage.FeedAsync(_pendingActions.ToString(), ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* trailing actions only */ }
        }
        if (_stage is not null)
        {
            var complete = _stage.CompleteAsync(ct);
            var first = await Task.WhenAny(complete, _fellBack.Task, _denied.Task).ConfigureAwait(false);
            if (first == complete)
            {
                try { await complete.ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested && !_denied.Task.IsCompleted)
                {
                    if (!_fellBack.Task.IsCompleted) FallBack($"皮套演出中断（{ex.Message}）");
                }
            }
        }
        if (_denied.Task.IsCompleted) return;
        var fallback = FallbackOrNull();
        if (fallback is not null) await fallback.CompleteAsync(ct).ConfigureAwait(false);
        Task? last;
        lock (_sync) last = _playing?.Playback;
        if (last is not null) await last.WaitAsync(ct).ConfigureAwait(false);
    }

    // ── host requests ──────────────────────────────────────────────────────────────

    void ICorticoAudioHandler.Synth(long pieceId, string text)
    {
        lock (_sync)
        {
            if (_fallback is not null || _turn.IsCancellationRequested || _pieces.ContainsKey(pieceId)) return;
            var piece = new Piece(pieceId, text);
            _pieces[pieceId] = piece;
            AdvanceCursor(text);
            var stage = _stage!;
            piece.Audio = PieceAudio.Start(text,
                ct => _ports.Synthesize(text, null, ct),
                chunk => stage.PcmAsync(pieceId, _ports.SampleRate, chunk),
                () => { lock (_sync) { piece.Ready = true; Touch(); } },
                failure => failure is null ? stage.SynthEndAsync(pieceId) : stage.SynthErrorAsync(pieceId, failure.Message),
                _turn);
        }
    }

    void ICorticoAudioHandler.CancelSynth(long pieceId)
    {
        lock (_sync)
        {
            if (_fallback is not null || !_pieces.Remove(pieceId, out var piece) || piece.Played) return;
            piece.Audio?.Cancel();
        }
    }

    void ICorticoAudioHandler.Play(long pieceId)
    {
        Piece? piece;
        lock (_sync)
        {
            if (_fallback is not null || _denied.Task.IsCompleted || !_pieces.TryGetValue(pieceId, out piece) || piece.Played) return;
            piece.Played = true;
        }
        if (!_ports.CanSpeak())
        {
            piece.Audio?.Cancel();
            lock (_sync) _pieces.Remove(pieceId);
            _ = _stage!.StoppedAsync(pieceId);
            _denied.TrySetResult();
            return;
        }
        StartPlayback(piece);
    }

    void ICorticoAudioHandler.Stop(long pieceId)
    {
        lock (_sync)
            if (_pieces.TryGetValue(pieceId, out var piece)) piece.PlayCts?.Cancel();
    }

    void ICorticoAudioHandler.Cue() { lock (_sync) Touch(); }

    void ICorticoAudioHandler.Aborted(string reason) => FallBack("皮套切换，本轮其余内容仅语音");

    // ── playback ───────────────────────────────────────────────────────────────────

    private void StartPlayback(Piece piece)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_turn);
        var stage = _stage!;
        lock (_sync)
        {
            piece.PlayCts = cts;
            _playing = piece;
            piece.Playback = Task.Run(async () =>
            {
                var completed = false;
                try
                {
                    await _ports.Play(Committed(piece, cts.Token), cts.Token, () =>
                    {
                        _ports.OnFirstPcm();
                        _ = stage.StartedAsync(piece.Id);
                    }).ConfigureAwait(false);
                    completed = !cts.IsCancellationRequested;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ports.Warn($"[Cortico] 这一句播放失败：{ex.Message}");
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_playing == piece) _playing = null;
                        _pieces.Remove(piece.Id);
                        Touch();
                    }
                    _ = completed ? stage.EndedAsync(piece.Id) : stage.StoppedAsync(piece.Id);
                    cts.Dispose();
                }
            });
        }
    }

    /// <summary>Captions and history for this piece when its first chunk is handed to the player.</summary>
    private async IAsyncEnumerable<byte[]> Committed(Piece piece, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var first = true;
        await foreach (var chunk in piece.Audio!.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (first) { first = false; _ports.Commit(CorticoScript.Normalize(piece.Text)); }
            yield return chunk;
        }
    }

    // ── fallback ───────────────────────────────────────────────────────────────────

    private ImmediatePacer? FallbackOrNull() { lock (_sync) return _fallback; }

    private void Touch() => _lastProgress = _clock();

    /// <summary>Why the reply should go voice-only now, or null. Internal for tests.</summary>
    internal string? CheckStall()
    {
        if (!_cortico.IsAlive) return "皮套进程无响应";
        lock (_sync)
        {
            if (_fallback is not null || _playing is not null) return null;
            if (!_pieces.Values.Any(p => p.Ready && !p.Played)) return null;
            return _clock() - _lastProgress > _cortico.MaxHoldMs + HoldGraceMs ? "皮套超时未开口" : null;
        }
    }

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(_watchInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false))
                if (CheckStall() is { } reason) { FallBack(reason); return; }
        }
        catch (OperationCanceledException) { }
    }

    private void FallBack(string reason)
    {
        ImmediatePacer fallback;
        List<Piece> replay;
        string remaining;
        lock (_sync)
        {
            if (_fallback is not null) return;
            replay = _pieces.Values.Where(p => !p.Played).OrderBy(p => p.Id).ToList();
            foreach (var p in replay) _pieces.Remove(p.Id);
            remaining = RemainingText();
            // The piece that is audible now finishes first: the player stops whatever plays when it starts.
            fallback = _fallback = new ImmediatePacer(_ports, _turn, after: _playing?.Playback);
        }
        _ports.Warn($"[Cortico] 皮套异常，本轮仅语音：{reason}");
        foreach (var p in replay) fallback.Enqueue(new SpeechItem(CorticoScript.Normalize(p.Text), Audio: p.Audio));
        if (LlmClient.IsSpeakableText(remaining)) fallback.Enqueue(new SpeechItem(remaining));
        _fellBack.TrySetResult();
        _ = Task.Run(async () =>
        {
            try { await _cortico.InterruptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* the sidecar may be gone */ }
        });
    }

    /// <summary>Moves past the text of a requested piece within the fed segments.</summary>
    private void AdvanceCursor(string pieceText)
    {
        var key = CorticoScript.Normalize(pieceText);
        if (key.Length == 0) return;
        for (var i = _segmentCursor; i < _segments.Count; i++)
        {
            var from = i == _segmentCursor ? _charCursor : 0;
            var at = _segments[i].IndexOf(key, from, StringComparison.Ordinal);
            if (at < 0) continue;
            _segmentCursor = i;
            _charCursor = at + key.Length;
            return;
        }
    }

    /// <summary>Fed text that no piece was requested for yet.</summary>
    private string RemainingText()
    {
        var sb = new StringBuilder();
        for (var i = _segmentCursor; i < _segments.Count; i++)
        {
            var text = _segments[i];
            sb.Append(i == _segmentCursor ? text[Math.Min(_charCursor, text.Length)..] : text).Append(' ');
        }
        return CorticoScript.Normalize(sb.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        try { await _watchdog.ConfigureAwait(false); } catch { }
        lock (_sync)
            foreach (var piece in _pieces.Values)
            {
                if (!piece.Played) piece.Audio?.Cancel();
                piece.PlayCts?.Cancel();
            }
        if (_stage is not null) await _stage.DisposeAsync().ConfigureAwait(false);
        var fallback = FallbackOrNull();
        if (fallback is not null) await fallback.DisposeAsync().ConfigureAwait(false);
        _life.Dispose();
    }
}
