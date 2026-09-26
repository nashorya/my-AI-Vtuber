# RT-04：Turn Manager v2 —— 分开“听见 / 被邀请 / 可以出声”

状态：已实现，**默认关闭**（`realtime.turn_manager_v2_enabled = false`）。关闭时全部输入仍走旧
`ConversationTurnGate` 路径，行为与改造前完全一致。

## 1. 动机与边界

旧路径只有一道闸：转写完成后固定等 350ms 合并文本，忙碌时排队，`canCommit` 只看
revision 号。它把“听见了”（ASR 完成）、“被邀请了”（是否点名/提问）和“可以出声了”
（双方静音、无取消）混在一个状态里（见计划 §1.3）。

v2 把这三件事拆成显式状态机，并给每个决策输出机器可读原因。

## 2. 状态机

```
Observing → Candidate → Preparing → Ready → Speaking → Observing
                ↑任何在途状态可 Cancel / Expire↑
```

| 状态 | 含义 | 进入条件 |
|---|---|---|
| Observing | 旁听 | 初始 / 取消 / 沉默决策后 |
| Candidate | 看到可能的邀请 | 收到 partial（点名候选、改口检测） |
| Preparing | 被邀请但对面（内录）仍在说话 | 邀请成立 + loopback 声学活动中；只准备不出声 |
| Ready | 生成/播放闸门打开 | 邀请成立且对手安静；`TurnReady` 事件携带 TurnContext |
| Speaking | 本轮音频播放中 | 调用方 `NoteSpeakingStarted(generationId)` |

取消原因（`TurnCancelReason`）：`StopCommand`、`HumanVoiceResumed`、`RetractedInvitation`、
`MatchChanged`、`TwoWayTalkExpired`、`Superseded`、`Disposed`。

实现为单一锁边界（所有状态迁移在 `_sync` 内串行），不与旧
`RequestCoordinator`/`ConversationTurnGate` 竞争发言权——v2 开启时 `AcceptTalkLine` 二选一路由。

## 3. TurnContext（计划 §4.2）

`AIVTuber.Core/Bot/Turns/TurnContracts.cs`：

- `TurnId` / `GenerationId`：程序生成（GenerationId 含每实例随机基址 + 单调序号），模型不可决定。
- `MatchId`：PK 场次；切场后旧 generation 一律不可提交。
- `InputSnapshotRevision`：参与本次决策的输入快照版本。
- `InvitationEvidence`：等级 + 原因码 + 命中信号。
- `InputSegments`：参与判断的源与句引用。
- `VisionSnapshotIds`：当前恒空，待 VIS-02 接入。
- `CancelReason`：取消时回填。

## 4. 邀请证据分级

`InvitationClassifier`（纯规则、无云端调用、不输出编造的概率）：

| 等级 | 例子 | 决策 |
|---|---|---|
| NameCall | “可缇，你怎么看？”（呼格 + 疑问） | 回应 |
| DirectQuestion | “你觉得呢”“你怎么看”（无名字也回应） | 回应 |
| ResponseToAiQuestion | AI 刚问“要继续吗？”，用户答“继续” | 回应 |
| ContinuousDialogue | 近窗口内 AI 发过言 + “那…”“为什么…”追问 | 回应 |
| WeakMention / third_person | “他说可缇挺好玩”（第三人称谈论） | **不**强制开口，仅保留候选 |
| None | 无任何证据 | 沉默 |

每个决策通过 `DecisionRecorded` 事件输出 `TurnDecision(Respond, Level, ReasonCode, Detail)`，
如 `name_call_question`、`third_person_mention`、`invitation_retracted`、`no_invitation_evidence`。

## 5. 关键规则落地

1. **partial 只做候选**：`ObservePartial` 仅置 Candidate 与改口检测，永不触发生成
   （试探生成 P1 才开，`speculative_generation_enabled` 默认 false，且 P0 打开也只做上下文准备；
   本实现未接入任何在途补文本的 Chat 能力，也不宣称可以）。
2. **server final 不再固定叠加 350ms**：单源 final 立即评估派发。仅两路冲突
   （250ms 小窗口，原因记 `merge=cross_source`）时等待合并。
3. **对面仍在说话**：Preparing——准备但不出声；对手安静 400ms 后才 `TurnReady`；
   持续两路讲话 4s 上限后过期（`TwoWayTalkExpired`），过期后按新上下文重决策，
   **不排迟到回复队列**；Ready 期间新 final 直接 `Superseded` 旧 generation。
4. **人声恢复**：麦克风活动立即抑制新播放（`CanCommit=false`）；持续超过噪声门
   （300ms）才整体取消——轻微噪声不会反复撕碎 AI 语音。
5. **停止指令**：`NoteStopCommand()` 立即取消一切在途状态。
6. **PK 切场**：`NoteMatchChanged` 取消在途 generation 并更新 MatchId，旧转写不套新对手。
7. **优先级**：`TurnManagerOptions` 显式配置 Master/Danmaku/Opponent 优先级，
   默认沿用现有 `RequestCoordinator.PriorityOf` 的产品意图（手动>麦克风>弹幕>内录）。

## 6. 半双工限制（loopback mute 现状保持）

现有 `BotRuntime` 在 AI 出声期间 mute 内录 VAD（`_loopbackVadMuted`），防止把自己的
TTS 当成对面说话。v2 **不取消该 mute、不加 AEC、不把识别到的自己声音当新邀请**。
后果：AI 说话期间听不到对面插话，直到本句播放结束。这是显式选择的安全半双工；
若未来要验证真双工，需先证明内录可隔离 AI 自身输出（进程捕获/专用设备路由），
不在本任务范围。

## 7. 配置与回滚

```jsonc
"realtime": {
  "turn_manager_v2_enabled": false,   // 默认 legacy
  "speculative_generation_enabled": false
}
```

回滚 = 把开关设回 false（或从 config 删除该节）。v2 关闭时新代码不参与任何路径；
开启时旧 gate 不再接收输入（不会双重发言）。

## 8. BotRuntime 接线（最小化）

- `EnsureTurnGate()`：flag 开启时惰性创建 `TurnManagerV2`，`SelfNames` 来自
  `identity.self_name + interaction.wake_keywords`。
- `AcceptTalkLine`：按 flag 路由到 `AddFinal`（源由 TalkIdentity 映射）或旧 `gate.AddLine`。
- `HandleTurnReadyV2Async`：镜像旧 `HandleTurnReadyAsync`，`CanCommit` 额外绑定
  `manager.CanCommit(generationId)`（人声/切场/改口/停止全部走这里拦截）。
- VAD `SpeechFrame`/`SpeechDetected` → `NoteVoiceActivity`；PK 开始/结束/手动新场 →
  `NoteMatchChanged`；`StopSpeaking` → `NoteStopCommand`；Speak 提交 →
  `NoteAssistantMessage`（供“AI 发问后回应”证据）。

## 9. 测试

`AIVTuber.Tests/TurnManagerV2Tests.cs`（19 例，全部 fake clock / manual scheduler，零真实计时器）：
点名 vs 第三人称、AI 发问后无名字回应、改口提交前取消（final 与 partial 两条路径）、
麦克风 final 先到对面还在说、两路持续讲话过期不排队、生成中人声恢复取消、
短噪声不撕碎 AI 语音、PK 切场旧 generation 不可提交、单源 final 零合并延迟、
两路冲突小窗口合并并记录原因、partial 永不派发、停止指令立即取消、
TurnContext 契约字段、配置默认关闭。

## 10. 未验证 / 遗留

- 未做真实双路音频的端到端实测（需真机声卡与厂商 Key，属 RT-07 范畴）。
- 邀请分级为中文启发式（呼格、疑问标记、第三人称模式），召回率/误报率需 RT-03 语料
  标注集评估；协议外情况保持沉默，不编造置信度。
- `Speaking` 状态尚未与 orchestrator 播放事件双向同步（v2 内部以 CompleteTurn 收口，
  行为正确，但播放中的新输入打断仍靠人声恢复/停止路径）。
- loopback mute 期间对面插话不可见（见 §6）。
