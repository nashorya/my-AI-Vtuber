# Cortico Live2D experiment

Branch `codex/cortico-live2d`, based on the local PK race-fix checkout. This is an opt-in experiment for AVATAR-03/04/06/07/08/09/10/11/12, not completion of their real-model acceptance gates.

## What is original

`upstream/` contains byte-identical source files from Pal-AI-Lab/cortico-world-vtuber, pinned in `UPSTREAM.json` (including a SHA-256 for each file). The original parser, Performer, Mixer, default performance pack, mouth envelope, model profile loader/converter, VTS client/backend and native audio sink run as TypeScript in a child process. No curve or mixing algorithm was translated into C# or retuned. The accompanying upstream license is **AGPL-3.0-or-later**; notices are retained. This experimental integration is not an MIT-only import.

`host.ts` is our adapter, not part of upstream. It speaks JSON lines to the desktop app. The app supplies its existing TTS PCM16 mono audio without forwarding API credentials. Node owns playback and VTS; the old VTS writer and NAudio playback are not invoked for these replies. ASR, identity, PASS/thought decisions and PK memory stay in the app.

## Deliberate differences / current limits

- **Cortico is the only performance layer while enabled.** Both reply protocols are adapted in the app (`CorticoReplyAdapter`): legacy replies are a Cortico script (`【PASS】` alone / one full-width note alone / script), protocol v2 carries the script in `speech.text` with no control lines. The prompt is composed per protocol (`CorticoPrompt`, `IdentityPrompt.InvitationPolicyFor(..., cortico)`, `ReplyProtocolV2.Prompt(..., scriptMarkup)`), so the prompt always matches the parser. PASS, thoughts, the protocol envelope, legacy `[emotion:]` tags and TTS bracket tags never reach Cortico or TTS.
- **Streaming feed.** A turn is `perform` (no script) → `ready` → `feed` per approved segment → `end`. Each segment becomes its own upstream round, so the first sentence is synthesized and performed while the LLM is still writing. Within a segment, upstream's own non-streaming synthesis path is used (each piece collected from our TTS before it plays).
- **Authorization per piece.** Before each piece becomes audible the app is asked with the piece's clean text; the app commits exactly that text to history/captions. A denied piece interrupts the rest of the turn (upstream would otherwise keep acting without a voice). Actions alone never start a turn.
- **Stop.** `interrupt` (fenced to the performances that existed when it was sent) preempts queued rounds, returns held emotion/pose/gaze states to neutral (`beginExternalTimeline`) and fades gestures already under way over 200 ms (`StoppableMixer` in `host.ts`; upstream would play them to the end). Stop, pause, sign-out and exit all reach it.
- **Per-model adaptation** (`adapt.ts`). A dedicated `cortico.profile.json` (configured for, or matched by name to, the loaded model) keeps its own mapping/range/direction/neutral/strength; inputs the model file does not wire are added to `unsupported`, so that motion is skipped instead of failing the turn. Without a dedicated profile a **conservative** adaptation drives only inputs the model's `.vtube.json` shows as connected, at 0.6× strength, no expression files, eyelids left to the model; without a readable model file only the mouth is driven. The status line lists `driven` and `skipped` (with reasons). Another rig's profile is never applied.
- **Model switch.** A `ModelLoadedEvent` interrupts the current turn, blocks new turns and expression pulses until the new rig's mapping is resolved, drops expression pulses opened for the previous rig, and clears leftover expressions on the new rig.
- No VoxCPM2 runtime/weights or forced aligner is installed. Inline action timing uses upstream's non-aligned fallback.
- The original full Cortico WebUI/World, OBS overlay, runtime downloader and diagnostics panels are not hosted. Audio output is selected by `audioDevice` name here; the app's NAudio device index and virtual-mic tap do not control this backend.
- This is a restart-only backend selection (`cortico.json`). If the sidecar fails to start, that run falls back to the plain VTS path as a **temporary degradation** (prompt, parser and player switch together); it is not Cortico compatibility.

## 当前验证结果（2026-09-20）

- Windows App Release 交叉构建成功，0 warnings / 0 errors；构建输出包含 sidecar 源码、锁文件和安装脚本。
- TypeScript typecheck 通过；Node 集成测试 2/2 通过。
- .NET 相关测试 18/18 通过，包括真实 Node 子进程 / 假 VTS / PCM 往返、取消后再运行，以及原有 PK/回复/生命周期回归。
- 在 Apple Silicon 上运行托管测试前，以 `-t:Rebuild -p:PlatformTarget=AnyCPU` 重编译测试，再以 `--no-build -- RunConfiguration.TargetPlatform=ARM64` 执行；没有改变仓库的 Windows x64 发布目标。
- 没有在真实 Windows 声卡或 Live2D 模型上验收；没有调用真实 LLM/TTS。

## Windows setup

Build/publish this branch, then run from the resulting app directory (Node.js 22+ must already be installed):

```powershell
powershell -ExecutionPolicy Bypass -File .\sidecar\cortico\Setup-Cortico.ps1 `
  -Live2dDir 'D:\VTube Studio\VTube Studio_Data\StreamingAssets\Live2DModels' `
  -ModelProfile 'VTS-YourModel' -AudioDevice 'CABLE Input'
```

Use `-ModelProfile auto` for initial exploration; calibrated profiles are recommended before comparing appearance. Empty `AudioDevice` means the default system playback device. The script installs the pinned npm dependencies/native audio module and creates `cortico.json` next to the app without replacing existing configuration. It does not download AI models or invoke paid inference. Normal app conversation uses the configured providers as before.

Follow [the original model adaptation guide](upstream/models/LIVE2D-ADAPTATION.md) to create a model **copy** and its `cortico.profile.json`; the repository contains no user's Live2D assets. Load that copy in VTS. Restart the app and authorize **AIVTuber Cortico Preview**. Its token stays locally in `.cortico-vts-token` (gitignored). Do not run the legacy app or another AI controller against the model at the same time.

### Preview motion without LLM/TTS cost

Stop AIVTuber first so there is only one VTS controller. From `sidecar/cortico`:

```powershell
node --import tsx preview.ts --config ..\..\cortico.json
node --import tsx preview.ts --config ..\..\cortico.json --script '【看向镜头,微笑】【点头】【摇头】【Reset】'
```

This runs the original motion engine, but deliberately has no audio. Only action tags are accepted. Use the app conversation to check mouths and speech timing.

### Back to our previous backend

Set `enabled` to `false` in `cortico.json` and restart the app. No original Live2D model files are changed by the integration.

## Validation

```sh
npm ci
npm run typecheck
npm test
```

Tests verify vendor hashes and launch the real host against an authenticated fake VTS. They exercise original curve/mouth output, clean speech extraction, pre-play authorization rejection, cancellation while TTS is pending, and the next turn after cancellation. They do not prove actual model rendering or Windows native audio.

Real-model acceptance: inspect neutral face, point/nod/shake/gaze/blink, speech mouth sync, interrupt, stop/restart, and model change; then run long enough to expose jitter and delayed frames. Log lines are prefixed `[Cortico]` in the app debug log. Do not equate fake VTS success with visual acceptance.
