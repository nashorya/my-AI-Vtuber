# Realtime pipeline baseline (RT-00)

Branch: `feat/realtime-cloud-pipeline` · Baseline HEAD at time of writing: `9c5756e` (merge of PR #24 `codex/cortico-live2d`).
All file:line references are against the working tree of this branch **after** the RT-00 changes (tracing marks, `realtime` config section, cloud-only gate — none of which change pipeline behaviour with defaults).

This document records the *actual* call sequence of the current pipeline so later RT tasks (RT-01…RT-07) can prove where latency is introduced. Plan-fact IDs (R1…R8) refer to `AGENT_PLAN_ASR_LLM_TTS_VISION.md` §1.

## 1. End-to-end call sequence (voice input)

```text
WASAPI capture (MicrophoneCapture / LoopbackCapture / ProcessLoopbackCapture)
  → VadDetector.Feed                      [capture thread]
      SpeechFrame  → (streaming only) frames buffered into unbounded Channel<byte[]>
      SpeechDetected (post-speech silence, default 800 ms) → segment closed
  → BotRuntime.ObserveMicSegmentAsync / ObserveLoopbackSegmentAsync   ← ASR consumer starts HERE
      → channel completed, then BotOrchestrator.TranscribeStreamAsync / TranscribeAsync
  → AsrResult text → AcceptTalkLine → ConversationTurnGate.AddLine   (turn_candidate)
      → 350 ms merge window / flush → TurnReady
  → BotRuntime.HandleTurnReadyAsync       (turn_commit_ready)
      → BotOrchestrator.ProcessTextAsync(bypassWake: true, requireStructuredReply: true)
          → RunStreamingPipelineAsync
              producer: LlmClient.StreamAsync (tokens → rawAll buffer, EOF)
                        → ReplyClassifier.ClassifyTurn → whole Spoken → sentenceChannel
              consumer : TtsChunks → ITtsClient.StreamAsync(sentence)
                        → AudioPlayer.PlayChunksAsync → WaveOutEvent device
  → OnFirstSentenceToTts / OnAiStartSpeaking (fired on FIRST SYNTHESIZED CHUNK, not at TTS request)
  → loopback VAD un-mute, PlaybackFinished → OnAiStopSpeaking
```

## 2. Key latency facts, with file:line

### 2.1 ASR consumption starts only after the VAD segment closes (R1/R2 — still true)

- `AIVTuber.Core/Runtime/BotRuntime.cs` `StartAudio()` mic wiring:
  - `SpeechFrame` handler (BotRuntime.cs ~line 1057): only writes frames into `_micSpeechChannel` when `asr.streaming=true`; **no ASR network activity yet**.
  - `SpeechDetected` handler (~line 1065) is what calls `ObserveMicSegmentAsync`.
- `ObserveMicSegmentAsync` (BotRuntime.cs ~line 899): completes the channel *first* (`Interlocked.Exchange(ref _micSpeechChannel, null)` + `TryComplete()`), then calls `_orchestrator.TranscribeStreamAsync(...)` or `TranscribeAsync(...)`. Loopback path identical (`ObserveLoopbackSegmentAsync`, ~line 950).
- Consequence: even with `streaming=true`, the provider receives audio only after post-speech silence (default `post_speech_silence_ms=800`, AudioConfig). The 800 ms wait is part of measured "ASR latency" and must be reported (plan RT-00: input_last_voiced vs asr_first_audio_sent — now traced).
- `BotOrchestrator.CollectStreamedAsync` (BotOrchestrator.cs ~line 577) drains the provider stream to the end and concatenates all partials — no revision-aware contract (plan §1.2; RT-02 will replace).
- Note: `BotOrchestrator.ProcessSpeechStreamingAsync` / `ProcessLoopbackSpeechStreamingAsync` (~lines 506/543) exist but are **not called by BotRuntime anymore**; the live path goes `TranscribeStreamAsync → AcceptTalkLine → ConversationTurnGate`.

### 2.2 Post-ASR text merge: ConversationTurnGate (R4)

- `AIVTuber.Core/Bot/ConversationTurnGate.cs`: merges recognized lines for a default 350 ms window (max batch 1 s), then fires `TurnReady`.
- `BotRuntime.HandleTurnReadyAsync` (BotRuntime.cs ~line 826) builds history and calls `ProcessTextAsync(..., bypassWake: true, requireStructuredReply: true)` with a `CanCommit` closure checking orchestrator/gate identity, PK match revision. This is a *text-level* gate, not an acoustic both-sides-silent proof.

### 2.3 Two full-turn LLM buffers (R6/R7 — both still present)

1. **Orchestrator buffer** — `BotOrchestrator.RunStreamingPipelineAsync` producer (~line 661): accumulates every token into `rawAll`; only at LLM EOF runs `ReplyClassifier.ClassifyTurn`, then writes the **entire** `Spoken` string to `sentenceChannel` once. TTS therefore cannot start before LLM EOF.
2. **LlmClient structured-channel buffer** — `AIVTuber.Core/Pipeline/LlmClient.cs` `StreamAsync`: when `allowedChannels != null` (VTS continuous control enabled), tokens are appended to `buffer` (~line 145) and nothing is yielded until the stream ends; the whole reply is parsed as `AvatarReplyProtocol` and yielded as one string (~line 169). Additionally the orchestrator defers control-tag events (`_deferLlmEvents`, BotOrchestrator.cs ~line 686) until classification.

### 2.4 TTS buffering (R8, evolved)

- `AIVTuber.Core/Pipeline/TtsClient.cs` MiniMax branch (`MiniMaxSynthesizeAsync`): `stream=false`, full JSON body read, hex-decoded to one PCM block (~line 148). Non-streaming.
- Current default MiniMax wiring is `MiniMaxWsTtsClient` (`BotRuntime.CreateTtsClient`, BotRuntime.cs ~line 630); Fish path in `TtsClient.FishStreamAsync` already streams HTTP chunks.
- `OnFirstSentenceToTts` (BotOrchestrator.cs, inside `TtsChunks`, ~line 727) fires when the **first synthesized audio chunk is about to be yielded**, i.e. after TTS first response — not when the sentence was queued. RT-00's `tts_request` mark now records the true request start separately.

### 2.5 Loopback mute while speaking (self-hearing protection)

- `BotRuntime.InitPipeline` (BotRuntime.cs ~line 680): `OnAiStartSpeaking` sets `_loopbackVadMuted=true`, resets loopback VAD and abandons any in-flight loopback speech channel; `OnAiStopSpeaking` un-mutes. `FeedLoopback` (BotRuntime.cs ~line 236) drops frames while muted. This is safe half-duplex; RT-04 must not silently remove it.

### 2.6 Audio playback start

- `AIVTuber.Core/Audio/AudioPlayer.cs` `PlayChunksAsync` (streaming path): buffered write into the wave source; playback begins once the player has enough buffered audio. Actual soundcard output time is not measured anywhere — the new `playback_first` trace mark is explicitly a *software estimate* (plan §6.3 requires this label until real loopback measurement exists).

### 2.7 Local inference dependencies (cloud-only gate targets)

- Memory embedding: `BotRuntime.InitMemoryAsync` (BotRuntime.cs ~line 319) loads `EmbeddingEngine` (bge-small-zh ONNX, `AIVTuber.Core/Memory/EmbeddingEngine.cs`) when `models/bge-small-zh/{model.onnx,vocab.txt}` exist. **In `realtime.inference_mode=cloud_only` this load is now skipped** with a visible "非向量检索" degradation note (`MemoryVectorSearchDegraded`).
- Python ASR sidecar: `BotRuntime.StartLocalAsrServerAsync` (BotRuntime.cs ~line 1041) launches the managed sidecar when `asr.provider=local`. **Blocked in cloud_only mode** (`CloudOnlySidecarSkipped`, error surfaced via `PipelineError`).
- Gate logic: `AIVTuber.Core/Runtime/CloudOnlyGate.cs`.

## 3. Tracing (new in RT-00)

- `AIVTuber.Core/Diagnostics/RealtimeTrace.cs`: monotonic clock (`IRealtimeClock`, `SystemRealtimeClock` = `Environment.TickCount64` + wall UTC for correlation only), one `TurnId` per turn (`BeginTurn` in `Observe*SegmentAsync`), event-name constants for the full plan RT-00 table (input/asr/turn/llm/tts/playback/cancel/vision).
- Off by default (`realtime.trace_enabled=false`); records only turn id + event name + timestamps. No payload field exists — raw audio, transcripts, images and secrets cannot be logged by construction.
- Wired marks today: `input_last_voiced`, `asr_first_audio_sent`, `asr_segment_final` (BotRuntime), `turn_candidate`, `turn_commit_ready` (BotRuntime), `llm_request`, `llm_first_content`, `llm_done`, `tts_request`, `llm_first_speech_segment`, `tts_first_encoded_audio`, `tts_first_pcm`, `playback_first`, `playback_end`, `cancel_requested`, `cancel_acked`, `playback_stopped` (BotOrchestrator). Events without a source yet (`asr_connect_start/asr_ready`, `asr_first_partial`, `vision_*`, provider-level cancel ack) are defined constants awaiting RT-02/VIS-02 wiring.
- `cancel_acked` currently marks local generation supersession only; a real vendor-side cancellation ack arrives with RT-06.

## 4. Config

- New versioned `realtime` section (`RealtimeConfig`, `ConfigManager.MigrateRealtimeSection`), defaults all legacy/off; user config semantics unchanged. `inference_mode=cloud_only` activates the local-inference prohibitions above.
