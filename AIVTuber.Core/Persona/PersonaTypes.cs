namespace AIVTuber.Core.Persona;

/// <summary>What the streamer wrote, plus the identity fields already set on the live page.</summary>
internal sealed record PersonaDraftInput(string Description, IReadOnlyList<string> ExistingAiNames, string ExistingStreamerTitle);

/// <summary>One follow-up question. Id is "aiName", "streamerTitle" or "style".</summary>
internal sealed record PersonaQuestion(string Id, string Text, IReadOnlyList<string> Suggestions);

/// <summary>The streamer's answer; a null Text means the question was skipped.</summary>
internal sealed record PersonaAnswer(string QuestionId, string? Text);

/// <summary>Questions still open (empty = compose right away) and identity the description already names.</summary>
internal sealed record PersonaCheckResult(IReadOnlyList<PersonaQuestion> Questions, string? FoundAiName, string? FoundStreamerTitle);

/// <summary>A generated persona with the identity fields it implies. Supplemented lists lines the
/// generator added on its own, so the preview can mark them.</summary>
internal sealed record PersonaComposeResult(
    string Persona, IReadOnlyList<string> AiNames, string StreamerTitle, IReadOnlyList<string> Supplemented);

/// <summary>A failure whose message is written for the streamer.</summary>
internal sealed class PersonaAssistantException(string userMessage) : Exception(userMessage);
