namespace AIVTuber.Core.Bot.Turns;

/// <summary>
/// Rule-based invitation grading (plan §5 RT-04 rule 2). Distinguishes:
/// 点名 (name call), 明确提问 (direct question), 上一轮 AI 发问后的回应, 连续对话, 弱提及.
/// This is deliberately heuristic and observable — no cloud model is called per partial,
/// and no invented probabilities are produced.
/// </summary>
internal static class InvitationClassifier
{
    // Second-person question openers that address the AI even without a name.
    private static readonly string[] DirectQuestionMarkers =
    [
        "你觉得", "你怎么看", "你怎么想", "你怎么说", "你呢", "你来说", "你来", "你听", "你看行", "你认为", "你感觉",
        "可以吗", "好不好", "行不行", "对吧", "是不是", "帮我说", "帮我念", "帮我回答", "替我说",
    ];

    // Vocative separators right after the name: "可缇，" / "大肥鱼？" pattern.
    private const string VocativeSeparators = "，,。！!？?、 :：";

    // Phrases that retract an invitation mid-utterance or right after ("不，我不是问你").
    private static readonly string[] RetractionPhrases =
    [
        "不是问你", "不是跟你说", "没叫你", "不用你回", "别回答", "当我没说", "不是对你", "没问你",
    ];

    // Bare acknowledgements that follow an AI question without naming it again.
    private static readonly string[][] ResponseAfterAiQuestionPatterns =
    [
        ["继续"], ["好"], ["行"], ["可以"], ["嗯"], ["对"], ["是的"], ["要"], ["不要"], ["不用了"], ["说说"], ["讲讲"], ["来吧"],
    ];

    /// <summary>Grades the invitation evidence of the pending input. The latest line dominates;
    /// earlier buffered lines provide the continuation context.</summary>
    public static TurnDecision Classify(
        IReadOnlyList<(TalkLine Line, TurnSource Source)> lines,
        IReadOnlyList<string> selfNames,
        string? lastAssistantMessage,
        long nowMs,
        long aiSpokeAtMs,
        long conversationWindowMs) =>
        Classify(lines, new ClassifierContext(selfNames, lastAssistantMessage, nowMs, aiSpokeAtMs, conversationWindowMs));

    /// <summary>
    /// Grades the pending input. Being named is neither required nor sufficient: a vocative name
    /// is strong evidence, a third-person mention is not, and an ambiguous or continuing input
    /// inside an active exchange is handed to the main model's speak/pass decision instead of
    /// being dropped by a keyword list.
    /// </summary>
    public static TurnDecision Classify(IReadOnlyList<(TalkLine Line, TurnSource Source)> lines, ClassifierContext ctx)
    {
        if (lines.Count == 0)
            return TurnDecision.Silence(InvitationLevel.None, "empty_input");

        var latest = lines[^1].Line.Text ?? "";
        var source = lines[^1].Source;
        var text = latest.Trim();

        // Retraction check first: it can neutralise an otherwise strong invitation.
        if (ContainsRetraction(text))
            return TurnDecision.Silence(InvitationLevel.None, "invitation_retracted",
                $"retraction_phrase_in_latest");

        var (name, vocative) = FindNameUsage(text, ctx.SelfNames);
        var inConversation = ctx.AiSpokeAtMs > 0 && ctx.NowMs - ctx.AiSpokeAtMs <= ctx.ConversationWindowMs;
        // The AI was talking with this party last. With no record of whom it answered, only the
        // streamer / chat count; the opponent's talk is addressed to the streamer by default.
        var sameParty = ctx.LastAiTurnSources is { Count: > 0 } sources
            ? sources.Contains(source)
            : source != TurnSource.Loopback;
        // The streamer has been talking with the opponent more recently than with the AI.
        var talkingWithOpponent = source == TurnSource.Microphone && ctx.LastOpponentFinalMs >= 0 &&
            ctx.LastOpponentFinalMs > ctx.AiSpokeAtMs &&
            ctx.NowMs - ctx.LastOpponentFinalMs <= ctx.ConversationWindowMs;

        // 1) 点名: the name is used as a vocative — with or without punctuation.
        if (name is not null && vocative)
        {
            if (LooksLikeQuestion(text))
                return TurnDecision.RespondAt(InvitationLevel.NameCall, "name_call_question",
                    $"name={name};question_marker");
            return TurnDecision.RespondAt(InvitationLevel.NameCall, "name_call_vocative", $"name={name}");
        }

        // 2) 明确提问, no name. "你觉得…" right after the opponent spoke may be addressed to the
        //    opponent: it is not dropped, but the model decides instead of forcing an answer.
        var questionMarker = DirectQuestionMarkers.FirstOrDefault(m => text.Contains(m, StringComparison.Ordinal));
        if (questionMarker is not null && LooksLikeQuestion(text))
        {
            if (talkingWithOpponent)
                return TurnDecision.RespondAt(InvitationLevel.SemanticDecision, "ambiguous_addressee",
                    $"marker={questionMarker};opponent_spoke_last");
            return TurnDecision.RespondAt(InvitationLevel.DirectQuestion, "direct_question",
                $"marker={questionMarker}");
        }

        // 3) 上一轮 AI 发问后的回应 from the party the AI was talking to.
        if (sameParty &&
            IsResponseAfterAiQuestion(text, ctx.LastAssistantMessage, ctx.NowMs, ctx.AiSpokeAtMs, ctx.ConversationWindowMs))
            return TurnDecision.RespondAt(InvitationLevel.ResponseToAiQuestion, "response_after_ai_question",
                $"short_reply={Truncate(text)}");

        if (name is not null)
        {
            // 5) 弱提及: name appears mid-sentence — likely third-person talk. Do not force speech,
            //    but record it so the turn manager can keep it as candidate context.
            if (LooksLikeThirdPerson(text, name))
                return TurnDecision.Silence(InvitationLevel.WeakMention, "third_person_mention",
                    $"name={name};third_person_pattern");
            return TurnDecision.Silence(InvitationLevel.WeakMention, "weak_name_mention", $"name={name}");
        }

        if (inConversation && sameParty && LooksLikeFollowUp(text))
            return TurnDecision.RespondAt(InvitationLevel.ContinuousDialogue, "continuous_dialogue_followup",
                "recent_exchange+followup_marker");

        // 4) Possibly continuing the exchange with the AI, but no marker: the model decides.
        //    The opponent's unaddressed talk is not handed over this way.
        if (inConversation && sameParty && source != TurnSource.Loopback && !talkingWithOpponent)
            return TurnDecision.RespondAt(InvitationLevel.SemanticDecision, "possible_continuation",
                "recent_exchange_same_party");

        return TurnDecision.Silence(InvitationLevel.None, "no_invitation_evidence");
    }

    /// <summary>True when the text contains a phrase that retracts a just-offered invitation.</summary>
    public static bool ContainsRetraction(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var phrase in RetractionPhrases)
            if (text.Contains(phrase, StringComparison.Ordinal))
                return true;
        return false;
    }

    // Characters that may directly follow a vocative name without punctuation ("可缇你觉得呢").
    private const string VocativeFollowers = "你您呀啊啦在快来帮说听";
    // Characters that make a trailing name third-person ("我跟可缇" / "他叫可缇").
    private const string ThirdPersonLeaders = "他她它跟和对叫给让被";
    private const string TrailingPunctuation = "。，,！!？?～~…. ";

    private static (string? Name, bool Vocative) FindNameUsage(string text, IReadOnlyList<string> selfNames)
    {
        var core = text.TrimEnd(TrailingPunctuation.ToCharArray());
        foreach (var raw in selfNames)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var after = idx + name.Length;

            if (idx == 0)
            {
                // The bare name on its own is a call, punctuation or not ("可缇").
                if (core.Length == name.Length) return (name, true);
                // Name at utterance start followed by a separator or a second-person follower.
                if (after < text.Length &&
                    (VocativeSeparators.Contains(text[after]) || VocativeFollowers.Contains(text[after])))
                    return (name, true);
            }
            // Name immediately before a question mark ("你觉得呢，可缇？").
            if (after < text.Length && text[after] is '？' or '?')
                return (name, true);
            // Name closing the sentence ("你觉得呢可缇"), unless a third-person cue precedes it.
            if (idx > 0 && after == core.Length && !ThirdPersonLeaders.Contains(text[idx - 1]))
                return (name, true);
            return (name, false);
        }
        return (null, false);
    }

    private static bool LooksLikeQuestion(string text) =>
        text.Contains('？') || text.Contains('?') ||
        text.Contains("吗", StringComparison.Ordinal) ||
        DirectQuestionMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));

    private static bool LooksLikeThirdPerson(string text, string name)
    {
        // Third-person cues adjacent to the name: "他说X", "X挺好玩", "跟X说" etc.
        var idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var before = idx == 0 ? "" : text[(Math.Max(0, idx - 2))..idx];
        if (before.Contains('他') || before.Contains('她') || before.Contains('跟') || before.Contains('对'))
            return true;
        var afterIdx = idx + name.Length;
        var after = afterIdx < text.Length ? text.Substring(afterIdx, Math.Min(3, text.Length - afterIdx)) : "";
        return after.StartsWith("挺") || after.StartsWith("好像") || after.StartsWith("真") || after.StartsWith("说");
    }

    private static bool IsResponseAfterAiQuestion(
        string text, string? lastAssistantMessage, long nowMs, long aiSpokeAtMs, long windowMs)
    {
        if (string.IsNullOrEmpty(lastAssistantMessage)) return false;
        if (nowMs - aiSpokeAtMs > windowMs) return false;
        if (!lastAssistantMessage.TrimEnd().EndsWith('？') && !lastAssistantMessage.TrimEnd().EndsWith('?'))
            return false;
        var trimmed = text.Trim().TrimEnd('。', '，', '！', '!', '？', '?');
        if (trimmed.Length > 10) return false;
        foreach (var group in ResponseAfterAiQuestionPatterns)
            if (group.Length == 1 && trimmed == group[0])
                return true;
        return false;
    }

    private static bool LooksLikeFollowUp(string text) =>
        text.Contains("那", StringComparison.Ordinal) ||
        text.Contains("为什么", StringComparison.Ordinal) ||
        text.Contains("怎么", StringComparison.Ordinal) ||
        text.Contains("然后呢", StringComparison.Ordinal);

    private static string Truncate(string s) => s.Length <= 20 ? s : s[..20] + "…";
}

/// <summary>Everything the classifier may use besides the text itself.</summary>
internal sealed record ClassifierContext(
    IReadOnlyList<string> SelfNames,
    string? LastAssistantMessage,
    long NowMs,
    long AiSpokeAtMs,
    long ConversationWindowMs,
    IReadOnlyCollection<TurnSource>? LastAiTurnSources = null,
    long LastOpponentFinalMs = -1);
