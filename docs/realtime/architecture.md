# Realtime pipeline architecture（合并视图 · RT-07）

分支 `feat/realtime-cloud-pipeline`。本文件是 RT-00~RT-07 + VIS-01/02 合并后的整体架构图、
开关矩阵与回滚路径；细节见各分文档。

## 1. 合并后链路（开关全开视图）

```text
麦克风 PCM ─┬─ VadDetector（并行观察，只报告本地有声边界）
            └─ RealtimeAsrPump(麦克风) ── 厂商实时会话（腾讯/火山）── partial/final ─┐
                                                                                   │
内录 PCM ───┬─ VadDetector（AI 说话时安全半双工 mute 保留）                          │
            └─ RealtimeAsrPump(内录) ── 独立 socket/session/epoch ── partial/final ─┤
                                                                                   ▼
弹幕 / PK 场次事实 ───────────────────────────────────────────► TurnManagerV2（听见/被邀请/可出声）
选定窗口 → WindowCaptureService → VisionObservationWorker ──► 视觉快照（旁路，非阻塞）
                                                                                   │
                                                              LlmClient（reply_protocol=v2，
                                                              NDJSON 事件流：decision/speech/control/end）
                                                                                   │
                                              第一段已批准 speech（先于 LLM EOF）
                                                                                   ▼
                                    TtsSessionCoordinator / MiniMaxHttpStreamingTtsClient
                                    （transport: legacy | streaming | bidi，取消屏障）
                                                                                   ▼
                                              编解码 → AudioPlayer（现有播放层）
                                                                                   ▼
                                     播放边界结算字幕 / 既有 motion 适配（不重做）
```

- 关闭全部新开关时，链路与 RT-00 baseline（见 baseline.md）逐字节一致：VAD 切段后
  `ObserveMicSegmentAsync` → 旧 `ConversationTurnGate` → 整轮 LLM → 旧 TTS。
- 每层职责、数据契约与测试证据见分文档：[baseline.md](baseline.md) ·
  [asr-rt02-03.md](asr-rt02-03.md) · [turn-manager.md](turn-manager.md) ·
  [reply-protocol-v2.md](reply-protocol-v2.md) · [tts-rt01.md](tts-rt01.md) ·
  [tts-rt06.md](tts-rt06.md) · [vision.md](vision.md) · [provider-contracts.md](provider-contracts.md)。

## 2. 层与开关矩阵

| 层 | 开关（config.json） | 默认 | 新路径 | 回滚（改回即回旧路径） |
|---|---|---|---|---|
| 观察（ASR） | `realtime.streaming_asr_enabled` | false | 每源 `RealtimeAsrPump` + 腾讯/火山实时会话 | 关闭后回到 VAD 切段后整段识别 |
| 观察（ASR 参数） | `realtime.buffer_capacity_ms` / `preroll_ms` / `idle_disconnect_ms` / `send_packet_ms` | 4000/1000/30000/200 | 有界缓冲、预录回放、省费断开 | 仅在 streaming 开启时生效 |
| 回合 | `realtime.turn_manager_v2_enabled` | false | `TurnManagerV2`（邀请证据/取消/过期） | 关闭后用旧 `ConversationTurnGate` |
| 回合（试探生成） | `realtime.speculative_generation_enabled` | false | 提前试探生成（P1，默认关闭） | 保持 false |
| 生成 | `llm.reply_protocol` | legacy | v2 NDJSON 分段流式 | 改回 legacy |
| TTS | `tts.transport` | legacy | streaming（RT-01）/ bidi（RT-06） | 改回 legacy（TtsClient 保留 stream=false 代码级回退） |
| TTS（bidi 参数） | `tts.bidi_host` / `bidi_*` | 空/off | 双向会话主机与取消屏障参数 | `bidi_host` 空=显式报错不静默切换 |
| 视觉 | `vision.enabled` | false | 窗口捕获 + 豆包识图旁路观察 | 关闭后无任何视觉代码路径 |
| 发行门禁 | `realtime.inference_mode` | legacy | `cloud_only`：禁 Python sidecar / 本地 ONNX embedding / 本地权重 | 改回 legacy 恢复本地能力 |
| 打点 | `realtime.trace_enabled` | false | 单调时钟链路打点（无敏感载荷） | 关闭即零开销路径 |

矩阵测试：`AIVTuber.Tests/RealtimeGateIntegrationTests.cs`
`SwitchMatrix_AllNewSwitchesOn_CompletesFakeEndToEndTurn`（全开 + fake 完成一回合）；
全关等价性由既有 772+ 测试套件在默认配置上通过来证明。

## 3. 取消与账本（跨层不变量）

- `TurnContextV2.GenerationId` 由程序生成，模型不能决定；提交前过 `CanCommit(generationId)`。
- 取消同时清：未提交文本（v2 parser 状态）、TTS 服务端积压（bidi cancel 屏障/HTTP 取消）、
  本地待播 PCM、未触发字幕/控制事件。已入设备硬件缓冲的声音不能逻辑撤回（尾音待实机测量）。
- 字幕/历史按真实播放边界结算：`generated / submittedToTts / enqueuedToPlayer / played` 分离，
  被取消且从未播放的整段不写进"已经说过"。

## 4. cloud-only 门禁

`CloudOnlyGate`（单点真理）：cloud_only 禁止 Python ASR sidecar、本地 bge-small-zh ONNX embedding、
本地权重下载；memory 明显降级为非向量检索。新增模块均不绕过：实时 ASR 只接受
tencent_realtime/volcano_realtime（配置错误则显式报错并回落 legacy 路径，而 legacy 路径本身又被
cloud_only 门禁拦住 sidecar）；MiniMax streaming/bidi 与豆包视觉全部是厂商网络客户端；
GDI 窗口捕获是非模型图像采集。见 `AIVTuber.Tests/CloudOnlyGateTests.cs`。

## 5. 已知边界（遗留）

- 真实厂商对拍未做（无 Key）；真实时延未测（见 benchmark-report.md）。
- 窗口选择 UI / WGC 升级 / 实机长跑：见 TODO.md「实时化改造」遗留清单。
- loopback 半双工 mute 保留；双工需先验证内录隔离 AI 自身输出。
