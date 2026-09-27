# Cortico 退回视觉层：App 出声，Cortico 定节拍并跟随演出

日期：2026-09-27
起点：`403471d`（分支 `claude/new-session-i4wnxx`）
上游：`sidecar/cortico/UPSTREAM.json`，Pal-AI-Lab/cortico-world-vtuber @ `67aa5aec`，AGPL-3.0-or-later
需求来源：`AIVTUBER_REQUIREMENTS_CONSOLIDATED.md` 第二、四节（该文档由 agent 整理，本 spec 中的取舍均经用户逐条确认）

## 1. 背景与目标

`403471d` 把 Cortico 定为唯一演出层：启用时 App 播放器不初始化，Node host 逐片向 App 要整段 PCM，再由 Node 的 `DeviceAudioSink` 出声。这和需求第四节相反，也让反复测试过的 App 语音链路（TTS → `AudioPlayer` → 监听 / 虚拟麦 / 字幕）在 Cortico 模式下被旁路。

目标：

- **声音只走 App 一条链路**。Node 不出声、不调用 TTS 厂商。
- **开口时机由 Cortico 决定**（用户选定 A2）：句间停顿、`【】` 阻断式动作做完再说，沿用上游规则。
- **视觉跟随 App 的实际播放**：口型、重音动作、`<>` 锚点以 App 回报的开播时刻为零点。
- **皮套故障不拖垮声音**：Cortico 不响应时 App 自行播完本轮。
- 借此把三条回复管线合成一条，只保留 v2 协议。

非目标：上游文件任何改动；动作算法重写；识图（第 2 步单独 spec）；本地 ASR、ONNX、自研 VTS 连续控制、PNG 像素形象、Python 弹幕桥的去留（另行决定）。

## 2. 已确认的决策

| # | 决策 | 用户选择 |
|---|---|---|
| D1 | 开口时机由谁决定 | Cortico 决定，App 出声（A2） |
| D2 | 首片 40 字切片门槛 | 保持上游原样，不在接入层提前切 |
| D3 | Cortico 不响应时 | App 不再等待，本轮剩余内容按普通模式播完，界面提示皮套异常 |
| D4 | 切皮套时正在播的句子 | 念完；本轮其余句子走普通模式 |
| D5 | 兜底期间谁控制皮套 | 无人控制（嘴不动），不临时启用旧 VTS 控制器 |
| D6 | 回复协议 | 只保留 v2，删除 legacy 管线，默认 `reply_protocol` 改为 `v2` |

## 3. 上游 Cortico 的结构（本设计依赖的部分）

- **L2 解析** `parser.ts`：`【…】` 阻断块切片，`<…>` 片内锚点，`[…]` 语气词；正文累计 ≥40 字后遇句末标点吐片。
- **L2.5 编排** `orchestrator.ts` `Performer`：按拍排时间（`BOUNDARY_*`、`GAP_CAP_MS=1200`、`【】` 等待 pulse clip 时长）。依赖注入 `PerformerTts`（`synth` / `synthStream`）与 `AudioSink`（`play` / `beginStream` / `stop` / `cut`）。
  - 流式片 `pieceReady`：首段包络已开始（第一块 PCM 到达）即可开播；`playSeg` 通过 `AudioStreamSession`（`started` / `ended` / `push` / `end` / `abort`）边收边播。
  - 开播后：`mixer.speechStart(envelope.at, startedAt)` 驱动口型；`scheduleAccentProsody` 按包络峰值加点头/挑眉；`<>` 锚点按字数比例或对齐单元定时触发。
- **L3 混音台** `mixer.ts`：60Hz 逐参数叠加；`MouthOpen` 由包络覆盖写入，下行 τ=40ms。
- **L4 注入** `backend.ts`：`InjectParameterData`，两帧在途 + 最新候补帧；按模型档案换算，实机没有的参数不发。

结论：把 host 注入给 `Performer` 的 TTS 与音频两个接口换成"代理"，即可在不改上游的前提下实现 A2。

## 4. 架构

```
LLM v2 事件 ─► CorticoReplyAdapter.FromV2 ─► 片段序列（台本片段 / PASS / 心里话 / 协议错误）
                                                   │
                                     ┌─────────────┴──────────────┐
                                     │         节拍器 IPacer        │
                                     │  ImmediatePacer │ CorticoPacer │
                                     └─────────────┬──────────────┘
                                                   ▼
                     TTS（_tts.StreamAsync，流式） ─► AudioPlayer ─► 扬声器 / 监听 / 虚拟麦
                                                   │ 播放事件（started / ended / stopped）
                                                   ▼
                                     视觉跟随者（Cortico host，或 Cortico 关闭时的旧 VTS 路径）
```

- **`ImmediatePacer`**：Cortico 未启用、启动失败或本轮已兜底时使用。App 自己按句切分干净文本，TTS 首块到达即播。
- **`CorticoPacer`**：把台本片段送进 host；host 的上游 `Performer` 切片、排拍，通过下文协议让 App 合成与播放。
- 两种节拍器共用同一套：回合上下文、`canCommit` 检查、字幕/历史提交、bidi TTS `BeginTurn`/`EndTurn`、`finally` 收尾（`OnAiStopSpeaking`）。

## 5. App ↔ host 协议

现有的 `perform` / `feed` / `end` / `interrupt`（带栅栏）/ `prepare` 保留。`tts` 与 `authorize` 两个回调删除，改为以下消息。所有消息带 `requestId`（回合）与 `pieceId`（片，host 内单调递增）。

| 方向 | 消息 | 含义 |
|---|---|---|
| host → App | `synth {requestId, pieceId, text}` | 上游 `synthStream` 被调用：请 App 开始流式合成这段干净文本。上游会提前预合成 |
| App → host | `pcm {requestId, pieceId, sampleRate, data}` | TTS 每到一块 PCM16 就转发一份（base64）。App 同时留一份待播 |
| App → host | `synthEnd {requestId, pieceId, durationMs}` / `synthError {…, message}` | 合成结束或失败 |
| host → App | `play {requestId, pieceId}` | 上游 `beginStream` 被调用：到了上游算出的开口时刻 |
| App → host | `started {requestId, pieceId}` | 该片第一块 PCM 真正写入声卡（`PlayChunksAsync` 的 first-PCM 回调） |
| App → host | `ended {requestId, pieceId}` / `stopped {requestId, pieceId}` | 播完 / 被停（含 App 拒绝开口） |
| host → App | `cancelSynth {requestId, pieceId}` | 上游中止该片合成（`signal` abort）：App 取消该片 TTS 流、丢弃缓冲 |
| host → App | `stop {requestId, pieceId}` | 上游中止该片播放（`AudioStreamSession.abort`，如上游 `preempt`）：App 停止该片播放并回 `stopped` |
| host → App | `aborted {requestId, reason}` | 皮套侧中止本轮（切模型、重连、host 内部错误） |
| host → App | `status {…, maxHoldMs}` | 现有 1s 心跳；`init` 后附带当前 pack 的最长合法等待（见 §6.2） |

host 侧：

- `tts.synthStream(text, sink, signal)`：发 `synth`；收到 `pcm` 时首块用上游 `StreamingEnvelope` 建包络并 `sink.begin({sampleRate, envelope})`，之后逐块 `sink.pcm(chunk)` 并喂包络；`synthEnd` 时返回 `TtsPiece`。`signal` 中止时发 `cancelSynth`。
- `audio.beginStream(sampleRate, text)`：发 `play`，返回 `AudioStreamSession`：`started` 在收到 `started` 时以 host 本地 `now()` resolve；`ended` 在 `ended`/`stopped` 时 resolve；`push`/`end` 为空操作（音频在 App）；`abort` 发 `stop`。
- `audio.play` / `synth`（整段路径）不再使用：`streamEnabled: () => true`，`alignEnabled: () => false` 不变。
- 删除 `DeviceAudioSink`、`cortico.json` 的 `audioDevice`、`pcm16ToWav`/`decodeWav` 整段路径。

App 侧（`CorticoPacer`）：

- 收到 `synth`：对该片启动 `_tts.StreamAsync`，块写入片缓冲并转发 `pcm`。
- 收到 `play`：先用 `canCommit` 与回合代号判断。不能说 → 回 `stopped`，走 §6.1 中断；能说 → 把片缓冲接入播放器（已到的立即播，后续边到边播），首块出声时回 `started` 并**此时**写字幕与历史（`OnReplyCommitted`、`PublishSpokenSentence`、首次 `OnAiStartSpeaking`）。
- 同一回合各片顺序播放；上游保证同一时刻只有一片在播。
- 旧回合或未知 `pieceId` 的 `synth`/`play` 直接忽略；已作废回合的 TTS 流立即取消。

## 6. 打断、切换与兜底

### 6.1 App 发起的停止（停止、暂停、账号失效、PK 切场、有人接话）

1. `BeginInterrupt` 先停 `AudioPlayer`（沿用 `b73a44c` 的"先停本地播放"）。
2. 对正在播的片回 `stopped`，再发带栅栏的 `interrupt`。
3. host 走现有中断：丢弃排队、复位持续状态、动作 200ms 淡出；`stopped` 触发 `speechEnd`，嘴约 40ms 内合上。
4. 未播出的已合成音频丢弃，不写字幕与历史。

### 6.2 兜底（D3）

满足任一条件，本轮切换为 `ImmediatePacer`，界面提示"皮套异常，本轮仅语音"：

- host 进程退出或 stdio 断开；
- 心跳 `status` 超过 2 秒未到；
- 某片已就绪（首块 PCM 已到）且上一片已播完，此后超过 `maxHoldMs + 2000ms` 仍未收到 `play`。

`maxHoldMs` 由 host 按当前 pack 计算：`GAP_CAP_MS + SAME_BEAT_GESTURE_DELAY_MS + 最长 pulse clip 时长`（示例 pack 最长 3000ms，即约 4.5s）。**这是对 D3 中"2 秒"的精确化**：上游 `【】` 阻断块会合法地等待整段动作（最长约 3.3s），若从合成完成起计 2 秒会误判；因此 2 秒容差计在上游最长合法等待之后。

兜底后：已合成未播的片按原顺序直接播；尚未送 TTS 的文本由 App 从已收到的 v2 speech 段按句继续；向 host 发 `interrupt` 清掉本轮视觉。下一回合重新使用 `CorticoPacer`（host 仍不可用时直接用 `ImmediatePacer`）。

### 6.3 host 发起的中止（D4）

切皮套、模型重载、VTS 重连时 host 沿用现有逻辑（中断在演、清旧模型候补帧与表情定时器、新映射确定前不开新轮），并发 `aborted`。App：正在播的片**照常播完**（不回 `stopped`），其余片按 §6.2 兜底方式播完。

### 6.4 皮套控制方唯一（D5）

Cortico 启用时旧 VTS 控制器与连续控制不初始化，兜底期间也不启用。旧路径只在 Cortico 未启用或启动失败（整次运行降级，现有逻辑）时存在。`adapt.ts` 的档案 / 保守适配 / 缺通道跳过不变。

## 7. 管线合并与清理

- `BotOrchestrator`：删除 `RunStreamingPipelineAsync`（legacy）与 `RunCorticoAsync`；`RunStreamingPipelineV2Async` 重构为唯一管线，节拍器按 §4 注入。v2 原有规则保留：逐段 `ReplyClassifier` 校验、协议错误 fail closed、每段开播时才提交、PASS / 心里话只记日志与历史。
- `CorticoReplyAdapter.FromLegacy` 与 `LegacyScriptReader` 删除；`IdentityPrompt`、`CorticoPrompt`、`ReplyProtocolV2.Prompt` 中仅服务 legacy 的分支删除。
- `AppConfig.Llm.ReplyProtocol` 默认值与 `config.json.template` 改为 `v2`；配置迁移把 `legacy` 视为 `v2`，并记一次诊断日志。
- 四个 `Process*Async` 入口合并为一个私有方法（参数：取转写的函数、转写事件、日志前缀）。
- legacy 专用的 `_deferLlmEvents`、`_deferredEmotions/Actions/Poses`、`FlushDeferredControls`、`ApplyStagedControls` 随 legacy 删除；`[kind:value]` 解析只留一处。
- `_playChunksAsync` 与 `_playWithStart` 合并为一个带 first-PCM 回调的播放委托。
- 删除无引用代码：`Avatar/CompositeAvatarController.cs`、`Avatar/LayeredBreathFollow.cs`（及其测试）、`App/ConfigWizard.cs`、`App/PageService.cs`、`MicLevelToWidthConverter`。
- `LlmClient` 内部的 legacy 流解析不动（记忆提取等仍在使用）。

## 8. 测试

| 层级 | 用例 |
|---|---|
| host（`sidecar/cortico/tests`，替换 `authorized` / `ttsTexts` 断言） | `play` 前不出现开口；收到 `started` 后 `MouthOpen` 有值；`ended`/`stopped` 后嘴合上；`【点头】` 片的 `play` 晚于动作时长；流式包络来自上游 `StreamingEnvelope`；`aborted` 在切模型时发出；上游哈希校验测试照常通过 |
| App 单元测试 | 两种节拍器下字幕/历史只在 `started` 后写入；`play` 时 `canCommit` 为假 → `stopped` 且不提交；心跳中断 / 进程退出 / 超过 `maxHoldMs+2s` 三种兜底；`aborted` 时当前片播完、其余走普通模式；停止时先停播放器再发 `stopped` 与带栅栏 `interrupt`；旧回合 `play` 被忽略；每片 TTS 只调用一次；Cortico 模式下 `PcmChunkPlayed` 到达虚拟麦；PASS / 心里话不进入 host |
| Runtime 集成（改造现有 Cortico 验收用例） | 真实 `BotRuntime` + 真实 Node host + 假 VTS + 假播放器：声音经 App 播放器输出且只有一份；播放期间 `MouthOpen` 有值；停止后 300ms 内无口型、无新动作，下一轮正常；运行中切皮套：当前句播完，其余句无口型播完 |
| 配置 | `legacy` 配置加载后按 `v2` 运行 |
| Windows 实机清单 | 真实 VTS + MiniMax：首句出声延迟对比 `403471d`（记录 `RealtimeTrace`）；监听、虚拟麦、OBS 字幕在 Cortico 模式下正常；运行中结束 Node 进程，确认本轮语音继续、提示出现、下一轮恢复或保持仅语音 |

## 9. 风险

- **首句延迟**：A2 + 40 字门槛会比现有普通模式晚。以 `RealtimeTrace` 量化，作为实机清单项；D2 已接受。
- **TTS 慢于实时**：`play` 到来时若缓冲不足会卡顿。MiniMax 通常快于实时；卡顿作为实机观察项，不在本步处理。
- **时钟**：`started` 以 host 收到消息的时刻为零点，stdio 往返通常 <5ms，可接受。
- **v2 切换**：默认协议变化影响所有部署；Windows 实机清单必须以 v2 跑完整轮次后再发布。
