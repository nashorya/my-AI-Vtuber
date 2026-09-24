# Implementation plan

1. Create compare/v0.3.3 at the verified remote tag and feat/invited-conversation at current HEAD; copy pending tracked fixes only into the latter.
2. Replace dual-silence scheduling with bounded transcript coalescing, preserving manual cancellation and sequential replies.
3. Record accepted input independently of reply outcome; pass a consistent history snapshot excluding the current turn to avoid duplicates.
4. Add the invitation prompt and strict structured reply parsing, including legacy silent-marker protection.
5. Add deterministic scheduler, classifier, orchestrator and runtime context regression tests. Run the Core tests and app build; diagnose any baseline failures separately.
6. Build both variants into separate output directories and document the two branches and local startup paths. Keep credentials and runtime data out of Git.

The writing-plans skill is not installed in this environment; this concrete plan follows the user-approved scope directly.
