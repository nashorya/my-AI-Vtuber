# RT-06: MiniMax 双向 WebSocket TTS 与取消屏障

分支 `feat/rt06-minimax-bidi`。目标：在 RT-01（HTTP 流式）与 RT-05（按段送 TTS + ReplyTurnV2 账本）之上，把已批准文字接入可复用的 `/ws/v1/t2a_v2_bidi` 双向会话，实现连接内取消屏障。**真实接口未实测（无 Key、账号开放性未验证）**：全部行为由 fake transport 契约测试锁定，协议字段形态以 fixture 为准，接入真实账号前必须对拍。

## 改动面

| 文件 | 改动 | 原因 |
|---|---|---|
| `AIVTuber.Core/RealtimeTts/TtsBidiProtocol.cs`（新增） | 消息构造/防御式解析、`MissingHostMessage`（未配置主机的明确报错+回退说明）、`IWebSocketTransportFactory` | 协议层独立成文件，fixture 锁定 |
| `AIVTuber.Core/RealtimeTts/TtsSessionCoordinator.cs`（新增） | 一个发送循环 + 一个接收循环；连接 epoch、回合代、投递单元三层归属；取消屏障；积压控制；保活；重建 | RT-06 主体 |
| `AIVTuber.Core/RealtimeTts/MiniMaxBidiTtsClient.cs`（新增） | `ITtsClient` + `IBidiTtsController`；实现 RT-05 管线按段消费（每次 `StreamAsync` = 一次 `task_continue`+`task_flush` 投递单元） | 接入现有编排层而不改其消费形态 |
| `AIVTuber.Core/Runtime/BotRuntime.cs` | `CreateTtsClient` minimax 增加 `transport="bidi"` 分支（传入 `_trace`） | 开关可达；缺 `bidi_host` 时构造即抛错 |
| `AIVTuber.Core/Bot/BotOrchestrator.cs` | ① `RunStreamingPipelineV2Async`：`_tts is IBidiTtsController` 时回合首尾调用 `BeginTurnAsync`/`EndTurn`；② `Interrupt()`：bidi 时等真实取消结果再打 `cancel_acked` / `cancel_epoch_rebuild`，legacy/streaming 保持本地代际标记 | 真正的 cancel_acked（RT-00 要求） |
| `AIVTuber.Core/Diagnostics/RealtimeTrace.cs` | 新事件常量 `tts_flush_acked`、`cancel_epoch_rebuild`；`cancel_acked` 语义收紧为“服务端已确认” | 打点语义区分 |
| `AIVTuber.Core/Config/AppConfig.cs` | `TtsConfig` 增加 `BidiHost`（默认空）、`BidiCancelAckTimeoutMs`(2000)、`BidiMaxBacklogSeconds`(30)、`BidiSecondsPerCharEstimate`(0.075)、`BidiKeepAliveIntervalMs`(0=关) | 配置项；主机名不猜 |
| `AIVTuber.Core/Runtime/ConfigDiff.cs` + `AIVTuber.Tests/ConfigDiffContractTests.cs` | bidi 配置字段变更映射 `RebuildTts`（热重载） | 配置迁移契约 |
| `config.json.template` | tts 段新增 bidi 字段（默认值） | 文档同步 |
| `AIVTuber.Tests/RealtimeTts/MiniMaxBidiTtsClientTests.cs`（新增） | 14 个 fake transport 测试 | 契约固定 |

## 开关与回滚

```jsonc
{ "tts": { "provider": "minimax", "transport": "bidi", "bidi_host": "<账号区域官方WSS主机>" } }
// 默认 transport="legacy"：行为与改动前完全一致；RT-01 的 "streaming" 路径原样保留。
```

- **未配置主机时明确报错，不静默切换**：构造 `MiniMaxBidiTtsClient` 即抛 `InvalidOperationException`，消息包含 `bidi_host` 配置指引与“显式设 `transport="streaming"` 回退”的说明（`TtsBidiProtocol.MissingHostMessage`）。计划未猜测国内/国际主机名。
- 回滚：`transport` 改回 `legacy`/`streaming`（热重载 `RebuildTts`），或删除 bidi 分支选择代码——编排层对非 bidi 客户端零改动。

## 协议（fixture 锁定，未实测）

```text
client → server:
  {"type":"task_start","task_id":...,"model":...,"voice_setting":{...},"audio_setting":{"sample_rate":N,"format":"pcm","channel":1}}
  {"type":"task_continue","task_id":...,"text":...}
  {"type":"task_flush","task_id":...}     回合尾部（本实现：每个投递单元尾部）
  {"type":"task_cancel","task_id":...}    打断，等确认
  {"type":"task_finish","task_id":...}    正常收尾
server → client:
  {"type":"task_started"} / {"type":"audio","data":{"audio":"<hex|b64>","audio_format":"pcm","is_final":...}}
  {"type":"task_flushed"} / {"type":"task_canceled"} / {"type":"task_finished"} / {"type":"task_failed","code":...,"message":...}
```

确认顺序（start→continue→flush/cancel/finish）由测试 `ConfirmOrder_AndUri_AreLockedByFixture` 锁定。音频字符串 hex 优先、base64 回退（与 RT-01 同策略）；请求固定 PCM，`audio_format` 为 mp3 时走增量解码防御路径（复用 RT-01 的 `Mp3StreamingDecoder`/`PcmAligner`，按投递单元持有，不跨单元串流）。

## 核心设计

### 回合归属（TurnId ↔ 会话 generation）

服务端回包不携带本地 TurnId。归属规则：音频只在「接收循环捕获的连接 epoch == 当前 epoch && 无取消屏障 && 存在打开的投递单元」时写入该单元；旧 socket 迟到包、取消后残留、无主 straggler 一律丢弃，绝不默认标为“当前轮”。每个 `task_continue`+`task_flush` 是一个投递单元（`BidiUnit`），回合（`BidiTurn`，程序生成的代数）可含多个单元——对应 RT-05 一轮多段。

### 取消屏障

`CancelActiveTurnAsync`：
1. 锁内立即完成当前单元（本地待播停止）、清空排队文本（屏障落下即不补发）、落屏障（新回合必须等待）；
2. `task_cancel` 走独立控制通道（越过文本积压）；
3. 等待服务端确认（默认 2000ms）→ `ServerConfirmed`；
4. 超时 → 断旧 socket、epoch 递增、新连接重新 `task_start` → `EpochRebuilt`，旧连接回包全部失效。

编排层 `Interrupt()` 同步等待该结果：`ServerConfirmed` → trace `cancel_acked`；`EpochRebuilt` → trace `cancel_epoch_rebuild`。legacy/streaming 路径保持原本地代际标记（无厂商 ack 可观测），完全不变。

### flush 确认 ≠ 已播放

`task_flushed` = 该单元音频已下发；`tts_flush_acked` 打点只表达这一点。发送完成（flush ack）、厂商完成（`task_finished`）、播放器排空三层分离，播放结算仍在 RT-05 的 `ReplySegmentState`（generated/submittedToTts/played）账本。

### 会话配置稳定与重建

voice/model 等在 `task_start` 时固定；请求音色与会话不一致 → 安全重建（`EnsureVoiceAsync`，计划 §2.2：不为复用连接牺牲正确音色）。`task_failed` 或连接中断 → 会话 Failed，已收到/播放音频的文本**不重发**（不制造复读），下一回合自动在新 socket 重建。

### 积压与控制优先

预估待播秒数（字符数 × `BidiSecondsPerCharEstimate`）超过 `BidiMaxBacklogSeconds` 时发送循环暂停取文本并发 `BacklogStateChanged` 事件限制上游；flush 确认清零积压。控制消息（start/flush/cancel/finish）走无界通道，测试验证取消可越过文本积压。

### 空闲保活

`BidiKeepAliveIntervalMs > 0` 时发送循环空闲发送 `keepalive`。**默认关闭**：真实服务端是否接受该事件未验证，属“可测试的协议方式”，验证前不开启。

## 测试（AIVTuber.Tests/RealtimeTts/MiniMaxBidiTtsClientTests.cs，14 个）

计划 RT-06 测试小节逐项对应：

| 场景 | 测试 |
|---|---|
| 确认顺序/URI/鉴权/task_start 固定会话配置 | `ConfirmOrder_AndUri_AreLockedByFixture` |
| 缺主机明确报错+回退说明 | `MissingHost_ThrowsWithExplicitFallbackGuidance`、`BuildUri_EmptyHost_Throws` |
| 取消前后交错回包（取消前音频可交付、取消后残留丢弃、同 socket 下一轮正常） | `Cancel_AudioInterleavedAroundBarrier_LocalStopsAndNextTurnWorksOnSameSocket` |
| cancel 确认丢失（epoch 重建 + **旧 socket 迟到包不归新轮**回归用例） | `CancelAckLost_RebuildsEpoch_OldSocketLatePacketsNeverReachNewTurn` |
| flush 期间用户插话（下一轮不被上一轮 flush 卡死） | `InterruptDuringSlowFlush_NextTurnIsNotStuckBehindBarrier` |
| task_failed（fail closed、不重发已交付文本、下轮重建） | `TaskFailed_FailsClosed_AndDoesNotReplayDeliveredText` |
| 首包后断网（保留已交付、不重发、下轮新 socket） | `FirstAudioThenDisconnect_KeepsDeliveredAudio_NoResubmit_RebuildsForNextTurn`（含长静默重连语义） |
| 快速换音色重建（新旧 task_start 各自固定音色） | `RapidVoiceChange_RebuildsSessionWithNewPinnedVoice` |
| 两轮紧邻同 socket 不串语音（straggler 丢弃） | `TwoAdjacentTurns_SameSocket_StrayAudioDoesNotCrossTurns` |
| 文本积压暂停 + 控制消息越过积压 | `Backlog_PausesTextSending_ControlMessagesBypass` |
| 保活默认关、显式开后才发 | `KeepAlive_SentOnlyWhenEnabled` |
| 打点（tts_request/first_encoded_audio/first_pcm/flush_acked 及顺序） | `Trace_RecordsTtsRequestEncodedPcmAndFlushAck` |
| Interrupt 真实 cancel_acked / epoch 重建分别标记 | `OrchestratorInterrupt_MarksRealCancelAck_FromBidiController` |

## 验证状态

- `dotnet build AIVTuber.slnx`：通过（0 error）。
- `dotnet test AIVTuber.slnx`：**758 通过 / 0 失败 / 1 跳过**（基线 739+1；新增 14 个 RT-06 测试 + 5 个配置契约扩充）。
- **未实测事项**：真实 WSS 主机与路径、`task_id` 是否必填、audio 载荷位置、ack 事件名、cancel/flush 确认语义、保活事件接受度、真实断流率与重连时延。接入真实账号时先对拍 fixture，不一致处修改 `TtsBidiProtocol` 与测试，不得靠猜补齐。

## 遗留风险

- **协议字段全靠 fixture 假设**（计划 [D2] 文档语义 + 防御式解析）：真实端点字段不同时，解析层需要一轮对拍修正；`task_cancelled` 英式拼写等别名防御不能替代真实验证。
- **取消尾音与硬件缓冲**：取消屏障保证云端不再投新音频、本地待播立即停，但已进入设备硬件缓冲的声音不能撤回（计划 §4.2），实际尾音长度需实测。
- **每单元 flush 的粒度**：本实现把 RT-05 的每个已批准文本段作为 continue+flush 单元（flush 兼作“请尽快合成当前批次”的时延手段）；若真实协议 flush 语义更重（如结束整任务），需改为仅回合尾 flush + 单元以 `is_final` 收束。
- **Interrupt 同步阻塞**：编排层 `Interrupt()` 同步等待取消结果，最长阻塞 `BidiCancelAckTimeoutMs`（默认 2s）——超时即 epoch 重建，不会无限等。
- 上游限制目前只有 `BacklogStateChanged` 事件与 RT-05 的有界通道（容量 3）；没有实现主动向上游 LLM 施压的背压协议（属 RT-04/RT-07 范围）。
