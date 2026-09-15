using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot;

internal enum TalkIdentity { Self, Opponent, Danmaku, System }

internal readonly record struct TalkLine(
    TalkIdentity Identity,
    string SpeakerName,
    string Text,
    string? SubjectUid,
    string? MatchId = null,
    DateTime StartedAt = default,
    Message? HistoryMessage = null);
