# RT-02 + RT-03：实时 ASR 会话契约、接线修正与厂商适配器（fake transport）

对应计划：`AGENT_PLAN_ASR_LLM_TTS_VISION.md` §1.1/1.2（现状事实）、§2.1（ASR 接入表）、§4.1（TranscriptUpdate 契约）、§5 RT-02/RT-03、§7 风险表。
分支：`feat/rt02-realtime-asr`。本文档描述本仓库当前实现状态；**所有厂商真实网络路径未实测（无真实 Key）**，行为证据全部来自 fake transport 契约测试。

## 1. RT-02：契约与接线

### 1.1 现状事实复核（与计划 §1.1 一致）

- `BotRuntime.StartAudio()` 的 `SpeechFrame` 只把帧写入 legacy Channel；`SpeechDetected` 之后
  `ObserveMicSegmentAsync()` / `ObserveLoopbackSegmentAsync()` 才关闭 Channel 并调用
  `TranscribeStreamAsync()` / `TranscribeAsync()` —— ASR 消费在说话结束之后才启动。
- `IAsrClient` 返回 `AsrResult(Text, Emotion)`，无法表达 utterance/revision/partial-final。

### 1.2 新增契约（`AIVTuber.Core/RealtimeAsr/`）

| 类型 | 职责 |
|---|---|
| `TranscriptUpdate` | §4.1 全字段：Source / CaptureEpoch / ProviderSessionId / SegmentId / Revision / TextSnapshot / IsFinal / AudioStartMs / AudioEndMs（本地单调时钟）/ ReceivedAt / OpponentSnapshot（取音频产生时快照）。额外 `FinalKind`：`VendorFinal` 与 `ClientEndpointSnapshot` 区分厂商 final 与客户端断点快照（后者绝不冒充厂商 final）。 |
| `IRealtimeAsrSession` | 启动/ready（`StartAsync`）、送帧、`ReadUpdatesAsync`（单一读者）、`FinishAudioAsync`（协议级结束音频）、`CancelAsync` / `DisposeAsync`。 |
| `IRealtimeAsrSessionFactory` | 按源建会话；两路各自独立 socket/session/缓冲/取消。 |
| `TranscriptAccumulator` | 同句 replace 不 append；旧 revision / 旧 CaptureEpoch / final 后更新一律拒收；final 每句只提交一次；跨 epoch 同名 SegmentId 不冲突。 |
| `RealtimeAsrPump` | 每源一个：捕获回调只做有界 Channel TryWrite（无网络 await）；首个**有声**帧建立会话，之后有声+静音全部送出以维持音频时间轴；VAD 只作并行观察/边界报告。 |

### 1.3 关键行为（`RealtimeAsrPump`）

- **会话建立策略（选定并测试）**：首个有声帧建立会话（非“启动监听即建立”，也非“等 SpeechDetected”）。
- **溢出处理**：Channel 容量按毫秒配置（`realtime.buffer_capacity_ms`，默认 4000ms）。`TryWrite` 失败 → 取消会话、epoch+1、`StreamBroken` 事件明确上报“断流”，**不悄悄 DropOldest**；后续有声帧重建会话。
- **静音时间轴**：会话期间静音帧照常发送；空闲断开按**音频时间**（非 wall-clock）计量，避免内录突发帧失真。
- **省费断开**：`idle_disconnect_ms`（默认 30s）无声即 `FinishAudioAsync` 结束会话；恢复讲话时回放预录缓冲（`preroll_ms`，默认 1000ms），恢复冷启动耗时计入 `RealtimeAsrMetrics.ColdStartSamples`。
- **epoch 语义**：每次会话拆除（溢出/静音断开/mute 间隙/设备重启）epoch 递增；旧会话迟到更新按 epoch 拒收（`Metrics.LateUpdateDrops`）。
- **两路独立**：麦克风/内录各自 pump + factory 会话实例，无共享“当前说话人”状态；内录会话创建时快照对手身份（OpponentSnapshot）。
- **指标（结构性，未引入打点基建）**：`FirstAudioSentAt`（asr_first_audio_sent 锚点）、SessionsStarted、IdleReconnects、ColdStartSamples、OverflowBreaks、LateUpdateDrops、FramesSent。

### 1.4 BotRuntime 接线（改动集中、可回滚）

- 配置 `realtime.streaming_asr_enabled`（**默认 false** = legacy 行为完全不变）。开启且 `asr.provider`
  为实时 provider 时，`StartAudio()` 创建两路 pump；麦克风/内录捕获回调同步喂帧（voicedHint 取自同帧 VAD 判定）。
- `SetMicMuted` / 内录 mute（AI 自身说话）→ `NotifyCaptureGap()`：拆会话 + epoch+1，防止 mute 间隙混入下一会话。
- 实时模式下 `Observe*SegmentAsync` 提前返回（VAD 仅报告边界日志），legacy Channel 写入跳过；**旧整段 IAsrClient 路径完整保留**为回滚路径。
- 实时 final 提交走与 legacy 相同的 `AcceptTalkLine` / `UserTranscript` / `LoopbackTranscript` / PK 缓冲入口；客户端断点快照在日志中明确标注“非厂商 final”。
- 配置错误（如开了实时但 provider 不支持）→ 明确报错并留在 legacy 路径，不静默。
- 热更新：`realtime.*` 任一变更 → `RestartAudio`（重建 pump）；`asr.secret_id/resource_id/hotwords` → `RebuildAsr`（ConfigDiff 契约测试已同步）。

### 1.5 验收测试（全部 fake，`AIVTuber.Tests/RealtimeAsr/`）

| 测试 | 证据 |
|---|---|
| `HalfUtterance_PartialFlowsBeforeAnySegmentEnd` | 全程不触发任何 SpeechDetected/段结束；fake 已收到字节前缀（`ReceivedBytes > 0`）并返回 partial（`PartialUpdate` 事件）；`FirstAudioSentAt` 非空（RT-02 验收锚点）。 |
| `TwoSources_DoNotCross` | 两路同时说话：各自 factory 独立会话、字节模式互不混入、final 的 Source 正确。 |
| `PartialRevisions_DoNotDuplicateIntoHistory` | “我觉→我觉得→我觉得可以”修订后仅一次 final 入史，重复 final 拒收。 |
| `LatePacketAfterReconnect_DoesNotCrossEpoch` | capture gap 重建（epoch+1）后，旧会话迟到 final 被拒（`LateUpdateDrops>=1`），不入史。 |
| `SilenceForever_MemoryStaysBounded_NoSessionCreated` | 2000 帧无声：不建会话；有声帧恢复时只回放 ≤ preroll 上限的帧。 |
| `LongUtterance_NotTruncated` | 60s 连续语音全部字节送达（不因小缓冲截断）。 |
| `IdleDisconnect_ReconnectsWithPreroll_AndRecordsColdStart` | 空闲断开（音频时间计量）→ 恢复时预录回放 + 冷启动进指标。 |
| `BufferOverflow_..._NotSilentDropOldest` | 消费变慢导致溢出：断流上报、epoch+1、后续重建会话。 |

## 2. RT-03：厂商适配器

传输抽象 `IWebSocketTransport`（可注入；真实实现 `ClientWebSocketTransport` 未实测）。两 provider 的工厂由 `BotRuntime.CreateRealtimeSessionFactory()` 按 `asr.provider` 创建，测试经 `RealtimeSessionFactoryOverride` 注入 fake。

### 2.1 腾讯 `tencent_realtime`

- 端点 `wss://asr.cloud.tencent.com/asr/v2/{appid}`；客户端签名按 [D3] 文档语义：
  参数按键排序 → `asr.cloud.tencent.com/asr/v2/?<query>` → HMAC-SHA1(SecretKey) → base64 → `signature` 参数。
  SecretKey 不进 URL/日志。golden 向量测试（openssl 独立计算）锁定算法。
- 约 200ms 二进制音频包（`realtime.send_packet_ms`），不足一包的余量保留，结束音频时 flush + `{"type":"end"}` 结束会话。
- 结果映射：`slice_type=1` → partial（revision 递增），`slice_type=2` → `VendorFinal`；`final=1` 结束整场识别；
  `message_id` 去重；`serial` 为 SegmentId；code≠0 抛错。
- 密钥配置：`asr.app_id` / `asr.secret_id` / `asr.api_key`(SecretKey)。

### 2.2 火山/豆包 `volcano_realtime`

- 端点 `wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async`；鉴权走 WS 握手头
  `X-Api-App-Key` / `X-Api-Access-Key` / `X-Api-Resource-Id` / `X-Api-Request-Id`（密钥不进日志）。
- 二进制协议 `VolcanoSaucProtocol`：4 字节 default header（version|headerSize, type|flags, serialization|compression）、
  全量请求带 4 字节大端序号 + payload 长度（JSON+gzip）、audio-only 包带序号、**最后一包 flags=LastPacket（负序号）**；
  编解码有 round-trip fixture。
- 配置请求（seq=1）：`enable_ddc=false`（二遍识别关）、`enable_speaker_info=false`（diarization 关）——
  身份来源是物理麦克风/内录分路，不用厂商说话人分离（计划边界 2）。
- 结果映射：`result.texts[].definite=true` → `VendorFinal`；`definite=false` → partial。**若该模式始终无句级 definite=true**
  （[D4] 明确不能假定有），流结束后以最新候选快照提交，标记 `FinalKind=ClientEndpointSnapshot`（非厂商 final）。
- 响应按 `sequence` 去重/抗乱序。
- 密钥配置：`asr.app_id` / `asr.api_key`(AccessKey) / `asr.resource_id`。

### 2.3 同语料对比框架（测试基建）

`RealtimeAsrProviderComparisonTests`：同一 fixture（同帧序列 + 同文本脚本）经两个 provider 适配器（各自 fake transport）
运行，产出 `ProviderComparisonRecord`（provider / partial 数 / vendor final 数 / 客户端快照 final 数 / 最终文本）。
不上真实网络；真实选型需真实账号后按计划 §RT-03 的对比维度补测。

### 2.4 配置

- `asr.provider` 新增 `tencent_realtime` / `volcano_realtime` 选项位（默认仍 `aliyun`）。选了实时 provider 但走 legacy
  整段路径时，`RealtimeOnlyAsrClient` 立即抛 `NotSupportedException`（响亮失败，不静默改道）。
- 热词预留：`asr.hotwords`（AI 昵称/对手称呼候选）。**尚未接厂商热词能力**，当前仅配置位 + RebuildAsr 触发。
- 未引入打点基建（另一分支负责）。

## 3. 测试与构建证据

- `dotnet build AIVTuber.slnx`：0 错误。
- `dotnet test AIVTuber.slnx`：653 total，652 通过，1 跳过，0 失败（连续 3 次；首次全套运行出现过 1 次未复现的
  时序性失败，疑似并行负载下既有/新增 timing 测试抖动，建议 CI 观察）。
- 新增测试 67 个（含 ConfigDiff 契约更新），全部走 fake transport / fake session。

## 4. 未验证事项（明确阻塞，不编造）

1. 两个 provider 的**真实网络路径全部未测**：无真实 appid/secretid/secretkey/resource id。签名算法、二进制头、
   gzip、结束语义均按 [D3]/[D4] 文档语义实现，用 fixture 验证；真实响应字段名/嵌套若有出入需在拿到 Key 后修正。
2. 真实厂商时钟/时间戳 → 本地时间轴映射为近似（会话起点 + 已发音频毫秒），未与真实 word-level 时间戳核对。
3. BotRuntime 级端到端（真实声卡 → pump → 会话）未实机验证：单元验收在 pump 层完成（fake 注入点
   `RealtimeSessionFactoryOverride` 已具备）。
4. 热词未接厂商能力；客户端断点快照的最终语义需在真实豆包资源上确认该模式是否真的无句级 final。
5. 打点（P50/P95 等延迟统计）不在本分支（结构性指标已就位：FirstAudioSentAt / ColdStartSamples 等）。

## 5. 遗留风险（非“无”）

- **溢出即断流**是保守策略：慢网 + 长突发下会频繁重建会话（每次都是冷启动成本）；未实现“降码率/合并帧”的软背压。
- **mute 间隙 epoch 递增**：AI 频繁说话/打断时内录会话反复重建，预录回放缓解开头丢字但会重复发送边界附近音频，
  厂商按音频计费时这是额外成本；回声隔离（AI 自声不进内录会话）仍依赖既有 mute 机制，未验证双工。
- 客户端断点快照 final 的**修订竞态**：快照提交后真实 vendor final 才到 → 当前按“final 后拒收”丢弃 vendor final
  （保守），极端情况下历史里留的是快照文本而非厂商确认文本。
- 火山 response JSON 的字段名（`result.texts[].definite` 等）按文档语义假设，未实测。
- 长时间运行稳定性（socket 泄漏、reader 任务生命周期）只有单元级覆盖，未经 30–60 分钟直播级验证（RT-07 范围）。
