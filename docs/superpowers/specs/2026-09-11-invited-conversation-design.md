# Invited conversation — approved design

The user approved two comparison branches on 2026-09-11: an unchanged v0.3.3 baseline and a new implementation of a quiet, attentive friend who answers when invited. Implementation and verification are authorized; no further design gate is needed.

- Preserve the original dirty workspace. Use independent Git worktrees.
- Retain both speakers' recognized text and identities in bounded recent context even for silent, invalid, failed, or cancelled replies.
- Coalesce completed transcripts for 350 ms, capped at 1000 ms from the first accepted line. Human speech and other pending recognitions cannot extend that cap. AI generation/playback owns one turn; new input belongs to the next turn.
- Ask the same model once to decide and answer. Default to listening. Mere third-person mentions, acknowledgments, and silence do not invite speech; direct questions, contextual invitations, and follow-up questions do. A switch back to human conversation ends participation.
- Require a JSON object with boolean respond and string speech. Only a validated positive decision can reach TTS. Legacy PASS variants remain silent; malformed envelopes fail closed.
- New ordinary input never invalidates an answer already generating. Explicit stop, disposal, and PK match replacement still invalidate it. Preserve current microphone/loopback echo handling.
- Do not overwrite the user's persona or secrets. Put the interaction/output contract after the configured persona for runtime dialogue requests; memory extraction keeps its own output contract.
- Verify timing with an injected clock; verify TTS/public side effects with fake LLM/TTS; verify recent dialogue remains available after silence/error and while busy. Build both applications. Semantic conversation quality remains a manual live check, not a claim supported by unit tests.
