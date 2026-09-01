using AIVTuber.Core.LiveStream;

namespace AIVTuber.Core.Memory;

/// <summary>In-memory buffer of original opponent/assistant pairs for one PK match.</summary>
public sealed class PkMatchBuffer
{
    private readonly object _lock = new();
    private string? _matchId;
    private PkOpponent? _opponent;
    private string? _pendingOpponentText;
    private string _pendingSource = "loopback";
    private readonly List<string> _assistantChunks = [];
    private readonly List<BufferedPkTurn> _turns = [];

    public string? MatchId
    {
        get { lock (_lock) return _matchId; }
    }

    public PkOpponent? Opponent
    {
        get { lock (_lock) return _opponent; }
    }

    public bool IsActive
    {
        get { lock (_lock) return _matchId is not null; }
    }

    /// <summary>Opens a new match buffer; discards any previous unfinished match.</summary>
    public string Start(PkOpponent opponent)
    {
        ArgumentNullException.ThrowIfNull(opponent);
        lock (_lock)
        {
            _matchId = Guid.NewGuid().ToString("N");
            _opponent = opponent;
            _pendingOpponentText = null;
            _assistantChunks.Clear();
            _turns.Clear();
            return _matchId;
        }
    }

    /// <summary>Records opponent ASR / announce text. Flushes any prior unpaired assistant chunks.</summary>
    public void NoteOpponentSpeech(string text, string source = "loopback")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            if (_matchId is null) return;
            FlushAssistantLocked();
            _pendingOpponentText = text.Trim();
            _pendingSource = source;
        }
    }

    /// <summary>Accumulates assistant sentence chunks for the current opponent utterance.</summary>
    public void NoteAssistantChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            if (_matchId is null) return;
            _assistantChunks.Add(text.Trim());
        }
    }

    /// <summary>Closes the current assistant reply into a paired turn (if opponent text exists).</summary>
    public void EndAssistantReply()
    {
        lock (_lock)
        {
            if (_matchId is null) return;
            FlushAssistantLocked();
        }
    }

    public IReadOnlyList<BufferedPkTurn> Snapshot()
    {
        lock (_lock)
        {
            FlushAssistantLocked();
            return _turns.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _matchId = null;
            _opponent = null;
            _pendingOpponentText = null;
            _assistantChunks.Clear();
            _turns.Clear();
        }
    }

    private void FlushAssistantLocked()
    {
        if (_assistantChunks.Count == 0) return;
        var assistant = string.Join("", _assistantChunks).Trim();
        _assistantChunks.Clear();
        if (string.IsNullOrWhiteSpace(assistant)) return;
        if (string.IsNullOrWhiteSpace(_pendingOpponentText)) return;

        _turns.Add(new BufferedPkTurn(
            Index: _turns.Count,
            OpponentText: _pendingOpponentText!,
            AssistantText: assistant,
            Source: _pendingSource,
            Ts: DateTime.UtcNow.ToString("o")));
        _pendingOpponentText = null;
    }
}

/// <summary>One buffered pair before agentic curation / persistence.</summary>
public sealed record BufferedPkTurn(
    int Index,
    string OpponentText,
    string AssistantText,
    string Source,
    string Ts);
