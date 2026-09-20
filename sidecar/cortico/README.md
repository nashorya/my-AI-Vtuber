# Cortico Live2D experiment

Branch `codex/cortico-live2d`, based on the local PK race-fix checkout. This is an opt-in experiment for AVATAR-03/04/06/07/08/09/10/11/12, not completion of their real-model acceptance gates.

## What is original

`upstream/` contains byte-identical source files from Pal-AI-Lab/cortico-world-vtuber, pinned in `UPSTREAM.json` (including a SHA-256 for each file). The original parser, Performer, Mixer, default performance pack, mouth envelope, model profile loader/converter, VTS client/backend and native audio sink run as TypeScript in a child process. No curve or mixing algorithm was translated into C# or retuned. The accompanying upstream license is **AGPL-3.0-or-later**; notices are retained. This experimental integration is not an MIT-only import.

`host.ts` is our adapter, not part of upstream. It speaks JSON lines to the desktop app. The app supplies its existing TTS PCM16 mono audio without forwarding API credentials. Node owns playback and VTS; the old VTS writer and NAudio playback are not invoked for these replies. ASR, identity, PASS/thought decisions and PK memory stay in the app.

## Deliberate differences / current limits

- The adapter uses upstream's **non-streaming synthesis path**. Each speech piece is collected from our streaming TTS before upstream plays it. This adds first-audio latency; use it to compare movement, not latency.
- No VoxCPM2 runtime/weights or forced aligner is installed. Inline action timing uses upstream's non-aligned fallback. No exact word alignment is claimed.
- The model gets the original vocabulary and bracket grammar, adapted to our plain-text reply protocol rather than Cortico tool calls. VoxCPM2 voice tags are not requested from our providers.
- The original full Cortico WebUI/World, OBS overlay, runtime downloader and diagnostics panels are not hosted. Existing app subtitles show clean text per turn. Audio output is selected by `audioDevice` name here; the app's NAudio device index and virtual-mic tap do not control this backend. Choose a cable device directly if required.
- This is a restart-only backend selection. `cortico.json` is separate from the normal settings UI. VTS host changes and TTS changes can still be applied through the app.
- Upstream default calibration is a fallback, not evidence a model is correctly wired. A missing or mismatched explicit profile fails closed for parameter injection. Model adaptation and visual acceptance must use a copy of the model.

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
