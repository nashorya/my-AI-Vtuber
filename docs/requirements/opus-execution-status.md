# 执行进度表（OPUS_EXECUTION_PLAN_AIVTUBER）

依据：`OPUS_EXECUTION_PLAN_AIVTUBER.md`、`REVIEW_PR25_AVATAR_AND_REALTIME.md`（S1）、
`REQUIREMENTS_AUTH_BUGFIX_PLUGINS_V1.md`（S2），2026-09-26 交付。

## 第 0 批：基线（2026-09-26）

| 项 | 实际值 |
|---|---|
| 工作分支 | `feat/simple-auth-distribution`（worktree，自 `main` 切出） |
| 起始 HEAD | `9c5756e`（Merge PR #24 cortico-live2d），与 S1 的“旧 main 基线”相同 |
| 最新 Release / 标签 | `v0.35`（2026-09-10）；`v0.36.0` 未被占用 |
| PR #25 `feat/realtime-cloud-pipeline` | `137ad70`，OPEN，**未合入 main** |
| 构建入口 | `AIVTuber.slnx`（Core `net10.0`，Tests `net10.0`，App `net10.0-windows` WPF）；`global.json` SDK 10.0.300 latestPatch；包锁文件启用 |
| CI | `.github/workflows/build-windows.yml` → `scripts/Invoke-VerificationBatch.ps1`（windows-latest，MinimumTests 200） |

### 基线测试

- macOS arm64，SDK 10.0.301：
  `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU`
  → 通过 609、失败 1、跳过 14，共 624。
  - 测试项目固定 `PlatformTarget=x64`（WebRtcVad 原生库），arm Mac 没有 x64 运行时，因此在命令行覆盖为 AnyCPU。
  - 唯一失败：`DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`。macOS 对 `localhost:1` 立即拒绝连接；测试假设 Windows 下连接会一直挂到 500ms 超时取消。属于平台差异，不是本批引入的问题。
- Windows：以本分支 draft PR 的 CI 结果为准，见第 A 批交付报告。
- 旧报告里的“777 通过”是 PR #25 分支作者侧的数字，不适用于 main，也不是本次结果。

### 当前生产运行链路（main@9c5756e）

```text
App.OnStartup → new BotRuntime(config) → MainWindow → BotRuntime.StartAsync()
  InitMemory（SQLite + 可选 ONNX 向量）→ VTS/Cortico → OBS → InitPipeline（ASR/LLM/TTS 客户端 + BotOrchestrator）
  → 弹幕桥 → StartAudio（麦克风 VAD / 内录 VAD）→ StartLocalAsrServerAsync（仅 asr.provider=local）
麦克风/内录 VAD.SpeechDetected → ObserveMic/LoopbackSegmentAsync → orchestrator.Transcribe(Stream)Async（云 ASR）
  → AcceptTalkLine → ConversationTurnGate.TurnReady → HandleTurnReadyAsync
  → orchestrator.ProcessTextAsync（LLM 流式 → 分句 → TTS → AudioPlayer.PlayChunksAsync）
  → OnReplyCommitted → CommitReply → MemoryExtractor.OnTurnAsync（云 LLM）
停止：StopSpeaking() / DisposeAsync() → orchestrator.Interrupt()
```

鉴权/插件入口：当前代码均无。Vision、TurnManagerV2、Realtime ASR/TTS、reply v2 均不在 main。

### R 编号复核（针对 main@9c5756e）

| ID | 状态 | 证据 | 目标批次 |
|---|---|---|---|
| R01 | 当前不可复现（代码不在 main） | `AIVTuber.Core/RealtimeAsr/` 在 main 不存在；问题代码仅在 PR #25 | B（在 PR #25 修复分支） |
| R02 | 同上 | 同上 | B |
| R03 | 同上 | `AIVTuber.Core/RealtimeTts/` 不存在于 main | B |
| R04 | 当前不可复现 | main 没有 `reply_protocol`/`StreamEventsAsync`；Cortico 走 legacy `StreamAsync`，没有 v2 冲突 | B |
| R05 | 部分相关，仍存在 | TurnManagerV2 不在 main；但 main 的 `BotOrchestrator.Interrupt()` 先同步等待在途任务（`CancelCurrentAsync().GetResult()`）再 `_stopPlayback()`，本地停声依赖云端调用响应取消 | A 修最小停止缺口；其余 C |
| R06 | 当前不可复现 | `RealtimeAsrPump` 不在 main | C |
| R07 | 当前不可复现 | 同上 | C |
| R08 | 当前不可复现；火山协议待官方对拍 | `VolcanoRealtimeAsrSession` 不在 main | B/C |
| R09a | 当前不可复现 | `Vision/` 不在 main | D |
| R09b | 未实现 | 无礼物头像入口 | E |
| R10 | 当前不可复现 | `VisionObservationWorker` 不在 main | D |
| R11a–c | 当前不可复现 | `TtsSessionCoordinator` 不在 main | C |
| R12 | 待复核（main 有等价风险点） | main `OnReplyCommitted` 的提交时机需另查；PR #25 的 v2 TtsChunks 问题不在 main | C（A 只回归停止场景） |
| R13 | 当前不可复现 | 视觉 system 消息只在 PR #25 | D/E |
| T01 | 仍适用 | main 的 runtime 测试靠反射注入字段；第 A 批新增测试走 `BotRuntime` 实例入口 | 每批 |

“当前不可复现”只表示问题代码不在 main，不表示 PR #25 上已修。修复在 PR #25 或其修复分支上做（第 B/C/D 批）。

## 第 A 批

见同目录 `batch-a-report.md`。
