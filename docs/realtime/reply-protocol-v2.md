# 回复协议 v2（LLM 安全分段流式输出，RT-05）

状态：已实现，默认关闭（`llm.reply_protocol = "legacy"`）。legacy 路径保持原样，作为回滚开关。
基线分支：`feat/rt05-reply-protocol-v2`。

## 目的

旧链路有两处整轮缓冲：`LlmClient` 在 `allowedChannels != null` 分支把整个结构化回复读完才解析；
`BotOrchestrator.RunStreamingPipelineAsync` 累积 `rawAll` 到模型流结束才 `ClassifyTurn()`，然后才送 TTS。
协议 v2 让第一段已批准文字进入 TTS 时，主 LLM 仍可继续生成后续内容——同时不放宽
PASS／心里话／reasoning／括号隔离。

## 协议（计划 4.3 节）

模型输出为 NDJSON，每行一个完整 JSON 对象：

```jsonl
{"v":2,"type":"decision","mode":"speak"}
{"v":2,"type":"control","kind":"emotion","value":"happy"}
{"v":2,"type":"control","kind":"avatar","targets":{"headYaw":0.5}}
{"v":2,"type":"speech","seq":0,"text":"我觉得这波先别冲。"}
{"v":2,"type":"speech","seq":1,"text":"等对面的技能交完。"}
{"v":2,"type":"end"}
```

结构校验（`AIVTuber.Core/Pipeline/ReplyProtocolV2.cs`，逐行独立验证，不做半截 JSON 增量解码）：

- 第一行必须是 `decision`；重复 decision、未知 mode → fail closed。
- `speech` 只在 `decision=speak` 后接受；`seq` 由程序校验必须从 0 连续递增（不盲信模型自报值）。
  每段上限 `MaxSegmentChars=300`，段数上限 `MaxSegments=8`。
- `pass`／`thought` 不产生 TTS、字幕或动作；`thought.text` 只进私有日志（心里话路径）。
- `control` 白名单只有两种：`kind:"emotion"`（字符串）与 `kind:"avatar"`（targets 载荷复用
  `AvatarReplyProtocol.TryParseAvatarPayload`，与 legacy JSON 协议同一通道白名单/取值范围校验，
  动作接口未重做）。控制不阻塞 speech 释放。
- 最后一行必须是 `end`；end 之后任何非空输出 → fail closed。
- `finish_reason=length` 且无 end、HTTP 流中断、行中间截断、流结束无 end → 全部 fail closed，
  以一个终态 `ProtocolError` 事件报告（见下）。

### fail closed 语义

`ReplyStreamEventKind.ProtocolError` 是终态事件。收到后：不再向 TTS／字幕／motion 释放任何新内容，
`BotOrchestrator` 通过 `OnError` 报告“[LLM] 回复协议v2已终止（fail closed）”。已经提交并在播的段落
继续播完（已播语音无法撤销），但历史/字幕按段结算，不会把未播放的段落写成“已说过”。

## 流水线（解除两层缓冲）

- `LlmClient.StreamEventsAsync`（新，`IReplyProtocolStream`）：按 SSE delta 增量喂
  `ReplyProtocolV2Parser`，每凑齐一行就 yield 已验证事件——不等全文。诊断原文只旁路累积
  前 4000 字符写 DebugLog。`max_tokens=512`：长度靠提示词+预算管理，不压到截断协议的程度
  （decision+end 外壳约占几十 token）。
- `BotOrchestrator.RunStreamingPipelineV2Async`（新分支，仅当 `ReplyProtocol == "v2"`）：
  每收到一个通过内容隔离的 speech 段，立即写入段 Channel（容量 3，背压）；TTS 消费端不等 LLM EOF。
  内容隔离沿用 `ReplyClassifier.Classify` 逐段执行：`（心里话）`剥离、控制标签剥离、
  括号不平衡 → 协议违规 fail closed。不向 TTS 发 JSON/role/tool/reasoning/括号内容。
- 旧完整 JSON 分支（`allowedChannels != null` 全文缓冲）原样保留为回滚路径。
  两套解析器不会同时产生副作用：v2 模式下 `LlmClient.StreamAsync`（legacy 字符流）直接抛
  `InvalidOperationException`；v2 关闭时 orchestrator 走完全不变的 legacy 路径（同一门禁测试通过）。

## 状态账本与取消

每轮一个 `ReplyTurnV2` 实例（非共享实例字段）：

- 段状态：`Generated → SubmittedToTts → Played`。**只有 `Played`（该段音频实际开始产出）的段
  才触发 `OnReplyCommitted` + 字幕**。两段只播第一段便中断时，历史只记第一段；未播放段只写
  interrupted 诊断日志。
- `_deferLlmEvents`/`_deferredEmotions` 等延迟列表是 legacy 路径的 orchestrator 实例级状态。
  检查结论：`RequestCoordinator` 串行化请求 + `Interrupt()`/producer finally 都会复位并清空，
  且所有 handler 先过 `CurrentEventContext()`（generation 校验），陈旧轮次的事件被丢弃，不构成
  跨请求泄漏。v2 路径完全不触碰这些共享字段——延迟控制/情绪全部放在每轮独立的 `ReplyTurnV2` 里。
- 动作 exactly-once：播放前的 avatar 控制事件先暂存，在首个 PCM（`FirstPcmRead`）时提交一次
  （`MotionFlushed` 防重）；播放开始后的控制事件即时 `Submit`。全部绑定同一 `avatarGeneration`，
  随语音轮次 `Cancel`。
- 情绪控制：进 TTS 的情绪在段生成时刻快照（`PeekEmotion`）；VTS 热键在对应段开播时
  `DrainUnappliedEmotions` 逐个应用，随段取消不会提前触发。

## 配置与回滚

```jsonc
{ "llm": { "reply_protocol": "v2" } }   // 默认 "legacy"
```

- `legacy`：与改造前行为完全一致（本轮全量回归 623 项测试通过）。
- `v2`：主对话 LLM 走 NDJSON 流式。memory/PK-curator LLM 始终 legacy。
- `IdentityPrompt.InvitationPolicyFor(replyProtocol)` 按协议选择邀请策略文本；
  精确 NDJSON 语法和通道表由 `ReplyProtocolV2.Prompt` 注入。

## 测试证据（AIVTuber.Tests/ReplyProtocolV2Tests.cs，33 项）

- LLM 首行快、后续延迟数秒：`FirstSpeechSegment_ArrivesBeforeEof` 在 EOF 前观察到 speech 事件；
  orchestrator 级 `FirstSegmentReachesTts_BeforeSecondSegmentGenerated` 证明第二段尚未生成时
  TTS 已收到第一段。
- decision 跨 token／Unicode 转义／括号隔离／截断：parser 逐字符喂入测试、`\uXXXX` 解码、
  `（心里话）正片`只播正片、不闭合括号 fail closed、行中间截断 fail closed。
- 两段只播第一段便中断：`InterruptedAfterFirstSegment_HistoryDoesNotClaimSecond`。
- 动作 exactly-once：`AvatarControl_FiresExactlyOnce_AndCancelsWithTurn`。
- 协议违规 fail closed 全套：重复 decision、speech 先于 decision、seq 跳号/重复、end 后输出、
  无 end、非 JSON 行、v 错误、未知类型、白名单外 control、超长段、超段数、speak 后改 pass。
- v2 关闭时与现有行为完全一致：全仓既有测试套件（623 通过）未改动语义。

## 未验证事项与遗留风险

- 未用真实厂商 Key 做 v2 端到端实测（提示词遵从度、真实 SSE 分块边界、DeepSeek/Gemini 对
  NDJSON 输出的稳定性）——只有 fake transport 证据。
- 首段仍以“行”为粒度（不做半截 JSON 增量解码，按计划第一版）。若模型单段很长，首段进 TTS
  时刻晚于段内第一个句号；后续可按计划加严格增量字符串解码。
- `OnReplyCommitted` 按段触发：多段会话在 ConversationManager 里成为多条 assistant 消息
  （语义等价），记忆抽取会按段多次入队。
- first_content/first_speech_segment 打点挂接点已留（`LlmClient.StreamEventsAsync` 的
  DebugLog 处与 `OnFirstSentenceToTts`），正式指标等 RT-00 打点基建（另一分支）合入。
- TTS 仍为既有非/半流式实现：本任务只解除 LLM 侧缓冲；RT-01/RT-06 才改 TTS 传输。
