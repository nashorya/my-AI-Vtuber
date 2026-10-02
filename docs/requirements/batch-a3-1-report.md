# A3 第 0 批 + A3-1 交付记录：接话链路闭环

依据：`PR27_A3_OPUS_PLAN_ANIME_LIQUID_GLASS_V1_1.md`（第 0 批 + A3-1）
起始 HEAD：`297fa80`（`claude/new-session-i4wnxx`，PR nashorya/my-AI-Vtuber#27），工作树干净；main 为 `7f321c4`，本分支已包含。
范围：只做第 0 批复核和 A3-1。A3-2（音色全集）、A3-3（视觉）未开始。版本号未改（仍 `0.36.0-rc.2`），未打 tag，未发 Release。

## 第 0 批：计划事实复核（针对 297fa80）

| 编号 | 复核结论 | 证据 |
|---|---|---|
| F01 | 成立：旧门和 v2 两个入口都以 `bypassWake:true` 调 `ProcessTextAsync`，WakeGate 不是主回合必经门槛 | `BotRuntime.HandleTurnReadyAsync` / `HandleTurnReadyV2Async` |
| F02 | 成立：`realtime.turn_manager_v2_enabled` 与 `llm.reply_protocol` 是两个开关，默认 false / legacy | `config.json.template`、`RealtimeConfig` |
| F03 | 成立：两路实时 ASR 的 `PartialUpdate` 只写日志；`TurnCancelled` 也只写日志，取消不会停到真实生成或待播 | `StartAudio` 中的订阅 |
| F04 | 成立：`SelfNames = [Identity.SelfName, ..WakeKeywords]`，而 `Identity.SelfName` 是 AI 对麦克风使用者（主播）的称呼；系统提示里「你的名字与别名」用的是 `WakeKeywords` | `EnsureTurnGate`、`IdentityPrompt.ProtocolAppendix` |
| F05 | 成立：主播页写「听到唤醒词才开口」，与主回合绕过 WakeGate 的实际行为不一致 | `streamer.html` |
| 新 N1 | 成立：v2 入口没有账号 / 暂停检查，账号失效时也不清理 v2 状态机；对面仍在说时停在 Preparing 的旧输入会在重新登录后被派发 | 新测试 `InputHeldWhileTheOpponentTalks_IsNotDispatchedAfterSignOutAndRelogin` 在去掉修复时失败 |
| 新 N2 | **部分不成立**：「在途 v2 回复在重新登录后复活」在 297fa80 上复现不了，因为吊销时 `OnCloudRevoked` 已中断 orchestrator，旧代次输出不会提交。仍补了代次检查作为多一层保护，但不据此宣称修了一个已发生的缺陷 | `Relogin_DoesNotReviveAnOldV2Reply` 去掉任一层都通过，只有 N1 场景能区分 |
| 新 N3 | 成立：v2 判定「不回应」或取消时清空缓冲，但这些行仍留在 `_queuedInputs`，之后构建上下文时被永久排除 | `LinesNotAnswered_AreReleased_SoTheyStayInHistory` |
| 新 N4 | 成立：`TurnReady` / `TurnCancelled` 等事件在状态机锁内触发，处理函数会拿 `_talkInputSync`，与 `AcceptTalkLine` 的加锁顺序相反，存在死锁风险 | `Events_AreRaisedOutsideTheStateLock` |
| 新 N5 | 成立：计时器不绑定回合 / 语音片段：旧回合的过期计时器会取消后来的新回合；旧噪声门计时器会把新的一段短噪声当成持续人声 | `OldExpiryTimer_*`、`OldNoiseGateTimer_*` |
| 新 N6 | 成立：`NoteSpeakingStarted` 从未被调用，状态机停在 Ready；AI 播放时新来一句会「取代」正在播放的回合 | `NewInputWhileSpeaking_*` |
| 新 N7 | 成立：`StopSpeaking` 调用同步 `Interrupt()`，会等厂商收尾；厂商不响应取消时，调用方（界面按钮所在的 UI 线程）被卡住 | 新 Runtime 停止测试在旧实现下挂起 |

## A3-1 修改

| 编号 | 修改 | 文件 |
|---|---|---|
| TURN-02 | 生产订阅 `WireRealtimePump` 把两路 partial 送进状态机；候选按（物理来源, capture epoch, segment）记，同一句的修订只更新一条候选、旧修订乱序到达会被忽略，不累计「邀请权重」；partial 不触发生成；final 关闭该来源的候选 | `BotRuntime.WireRealtimePump/HandleRealtimePartial`、`TurnManagerV2.ObservePartial` |
| TURN-03 | 名字不带标点也算点名（「可缇」「可缇你觉得呢」「你觉得呢可缇」）；第三人称提及（「他说可缇…」「我刚跟可缇」）不算；AI 发问后的「继续」只认 AI 上一轮回应的那一方；主播刚和对面说完话时的「你觉得呢」不再被当成问 AI，交给主模型的 speak/pass 决定；会话窗口内没有关键词的接续也交给主模型（新级别 `SemanticDecision`），不被关键词表永久挡住；对面主播对主播说的话默认不算在和 AI 聊 | `InvitationClassifier`、`TurnContracts` |
| TURN-04 | AI 呼唤名只来自 `interaction.wake_keywords`，不再包含 `identity.self_name`（主播称呼原义不变，不迁移旧配置）；主播页把该字段标成「AI 的名字和别名」 | `BotRuntime.AiCallNames`、`streamer.html` |
| TURN-05 | 取消映射到真实管线：未开始播放的回合取消其生成（先本地停，再后台等厂商）；正在播放的回合遇到软原因（人声恢复、被新输入取代、双方持续对话超时）只挡住后续句子，不硬切当前句；停止 / 暂停 / 账号失效 / 切场一律生效。`StopSpeaking` 改为本地立即停、厂商收尾在后台，下一回合开始前最多等 3 秒上一次中断的收尾（保留 main 的双向 TTS 取消屏障语义）。v2 入口补上与旧门相同的账号代次和暂停检查；账号失效、暂停时清空 v2 状态机。AI 真正开始出声时标记 Speaking，播放中来的新输入等本回合结束后再判断 | `BotRuntime.OnV2TurnCancelled/StopSpeaking/StartInterrupt/HandleTurnReadyV2Async/WireOrchestrator`、`TurnManagerV2` |
| TURN-06 | 计时器绑定到创建它的回合 / 语音片段 / 缓冲，旧计时器不再作用于新回合；未派发的行通过 `LinesReleased` 退出排队，保留在上下文里；事件改为出锁后按序触发；PK 文案改为「优先旁听：被叫到名字、被提问，或在继续和 AI 聊天时才接话」 | `TurnManagerV2`、`streamer.html` |
| 结构 | orchestrator 的事件订阅抽成 `BotRuntime.WireOrchestrator`，生产 `InitPipeline` 与测试走同一段代码；`CommitReply` 在记忆提取器未初始化时跳过而不是抛空引用 | `BotRuntime` |

旧门（`ConversationTurnGate`，v2 开关默认关闭时的路径）行为未改，仍可回退；一个输入只进入其中一个门。

## 测试

命令：`dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj`（Linux x64，.NET SDK 10.0.112，本地临时调整 global.json，未提交）

- 全量：通过 945、失败 1、跳过 14，共 960。唯一失败仍是基线就有的 `DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_*`（Linux/macOS 与 Windows 的套接字差异，Windows CI 上通过）。
- 新增 `Turns/TurnManagerA3Tests`（18 个，假时钟 + 手动调度器）与 `Turns/RuntimeTurnV2Tests`（9 个，真实 `BotRuntime` + `BotOrchestrator`，走 `WireRealtimePump`、`AcceptTalkLine`、`WireOrchestrator`；只替换厂商客户端、声卡和账号）。原有 `TurnManagerV2Tests`、`RealtimeGateIntegrationTests`、旧门测试全部未改、全部通过。
- `dotnet build App/App.csproj -p:EnableWindowsTargeting=true`：0 错误。

失败注入（逐项把修复改回旧行为，只跑对应测试）：

| 改回 | 结果 |
|---|---|
| `SelfNames` 重新包含主播称呼 | `StreamerName_*` 失败 |
| 取消只写日志 | `SupersedingInputBeforeAudio_*` 失败 |
| 不标记 Speaking | `NewInputWhileSpeaking_*` 失败 |
| partial 只写日志 | `RuntimePartials_*` 失败 |
| 计时器不绑定回合 | `OldExpiryTimer_*` 失败 |
| 事件在锁内触发 | `Events_AreRaisedOutsideTheStateLock` 失败 |
| 账号失效时不清空 v2 状态机 | `InputHeldWhileTheOpponentTalks_IsNotDispatchedAfterSignOutAndRelogin` 失败 |
| v2 回调去掉账号代次检查 | 相关测试仍通过：orchestrator 中断与状态机清空已各自挡住，属于多一层保护（见 N2） |
| 暂停时不单独取消 v2 | 相关测试仍通过：暂停会走 `StopSpeaking`，其中已取消 v2；这一行只是把原因标成 Paused |
| `StopSpeaking` 同步等厂商 | 新 Runtime 停止测试挂起（厂商故意不响应取消） |

## 验证层级

- 静态复核：上表 F01–F05、N1–N7。
- 离线组件测试：状态机与分类器，全部计时由假时钟驱动。
- 生产入口 + 假网络 / 假声卡：见上。
- **未做**：真实厂商流式 ASR 的 partial 形态（修订号、segment 是否稳定）、Windows 实际播放与打断体感、PK 真实场景、v2 开关在实播中的效果。v2 仍默认关闭（`realtime.turn_manager_v2_enabled=false`）。

## 配置与回滚

- 不改任何配置默认值，不迁移主播配置、记忆或私有 Key。AI 的名字仍写在 `interaction.wake_keywords`；原来只靠 `identity.self_name` 被「点名」的配置，现在需要把 AI 名字写进 `wake_keywords` 才算点名。
- 回滚：`git revert` 本批提交；或保持 `turn_manager_v2_enabled=false` 继续走旧门（旧门只受 `StopSpeaking` 改为后台收尾这一处影响）。
