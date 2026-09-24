# PK 身份、双静音回合与不打断 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PK 能稳定拿到对面主播；说话时不被麦/对面/弹幕打断；等双方都静音再开一轮 LLM；模型能分清使用者 / 对方主播 / 弹幕并带上各自记忆；被点到但不该开口时只记（心里话），完全路过则 【PASS】。

**Architecture:** 先修弹幕桥→`PkOpponent` 这条抓取链（短号/长号、无 uid 也能宣布）。再在 VAD 之上加 `DualPartyTurnGate`：麦和内录各自 ASR，但只有「AI 没在说话 + 两边都静音满 N ms」才合成一轮。`RequestCoordinator` 在 Speaking 期间不再被麦抢占（截停按钮除外）。身份与输出协议由代码拼进第二段 system，设置页只填三个名字。`ReplyClassifier` 在送 TTS 之前分流 Speak / InnerThought / Pass。

**Tech Stack:** C# / .NET 10, xUnit, 现有 `VadDetector` / `RequestCoordinator` / `ConversationManager` / `danmaku-bridge`（Go，`blivedm-go`）+ Python 回退。

## Global Constraints

- 设置页、监控页不要对话式小字副标题；三个身份控件只靠标签本身说清楚。
- API Key、`config.json` 密钥字段不进 git。
- 截停按钮仍立即 `Interrupt()`；自动输入不得打断正在播放的 TTS。
- 心里话只用全角括号 `（）`；路过标记是 `【PASS】`。
- 输入包装不再用「（你的创造者对你说）」这种和心里话冲突的全角括号当协议；改成 `使用者：` / `对方主播：` / `直播间弹幕：` 键值行。
- Gemini / DeepSeek 请求形态本计划不改。
- 每完成一个 Task 跑对应测试；未要求前不要 commit。
- 现有 PK 唤起门（`WakeGate`）与本计划的 `【PASS】` / 心里话重叠：接线后 **LLM 回合不再被关键词提前掐掉**。关键词只可继续用于「要不要把弹幕放进 gate」，不得在 ASR 之后、入队之前截停 TTS（今天麦是先入队取消再 `AllowSpeak`，必须改掉）。
- `VadDetector.IsSpeaking` 已有但无人读；gate 的麦/内录说话态优先接这个属性，不必再发明一套 onset 事件。
- 打断发生在 **VAD 段结束**（`postSpeechSilenceMs` 静音后），不是第一声。Task 4 要挡的是这段结束后的 `Enqueue` 取消，不是 onset barge-in（本来就没有）。
- PK 开场 `FeedPkText` 今天会立刻 `ProcessTextAsync` 并打断 TTS；必须改走 gate。`DanmakuSelector` 已在说话时暂停，但选中后仍会入队打断——同样改 `AddLine`。
- 内录转写目前 **不进** `ConversationManager` 历史；对面记忆只在 PK 结束后进 `pk_turns`，下一轮回复 **不会** 自动召回。Task 6 必须按对面 uid 检索 facts，并在 `BuildMessages` 时附带 `PkTurnRepository.ListByOpponentAsync` 最近几条（有 uid 才查）。

---

## File Structure

- **Modify** `danmaku-bridge/pk.go` + `danmaku-bridge/main.go` + `danmaku-bridge/pk_test.go` — 启动时把配置房间号解析成 canonical `room_id`；START/PRE 用短号+长号比对；无 uid 也推送；打印原始 cmd 前 400 字。
- **Modify** `danmaku_bridge.py` + `test_danmaku_bridge.py` — 与 Go 行为对齐（仍是 exe 缺失时的回退）。
- **Modify** `AIVTuber.Core/LiveStream/PkOpponent.cs` — `TryParse` 允许「有用户名、无 uid」。
- **Create** `AIVTuber.Core/Bot/DualPartyTurnGate.cs` — 双路说话状态 + 行缓冲 + 双静音定时冲刷。
- **Create** `AIVTuber.Core/Bot/ReplyClassifier.cs` — 分类 Speak / InnerThought / Pass。
- **Create** `AIVTuber.Core/Bot/IdentityPrompt.cs` — 三个身份的名字解析与本轮 user 文本拼装；代码内固定协议段。
- **Modify** `AIVTuber.Core/Config/AppConfig.cs` — `IdentityConfig`（使用者名、对面名覆盖、弹幕栏标签）。
- **Modify** `AIVTuber.Core/Bot/RequestCoordinator.cs` + `BotOrchestrator.cs` + `BotRuntime.cs` — 说话中只缓冲、不取消；截停除外。
- **Modify** `AIVTuber.Core/Bot/ConversationManager.cs` + `MemoryExtractor.cs` — 按身份取记忆；PASS 不入库。
- **Modify** `App/WebUi/wwwroot/index.html` + `ConfigViewModel.cs` — 三个名字输入框。
- **Test** `AIVTuber.Tests/PkOpponentTests.cs`、`DualPartyTurnGateTests.cs`、`ReplyClassifierTests.cs`、`IdentityPromptTests.cs`、`RequestCoordinatorTests.cs`。

```
麦 VAD ──ASR──► DualPartyTurnGate ──双方静音──► IdentityPrompt ──► Orchestrator
内录 VAD ─ASR──►        ▲                              │
弹幕 / PK 开场 ─────────┘                              ▼
                                          ReplyClassifier
                                          ├ Speak → TTS + 可入库
                                          ├ InnerThought → 历史 + 可入库，不 TTS
                                          └ Pass → 丢弃，不 TTS 不入库
```

---

### Task 1: PK 对面信息必须能进 C#

现状（已确认会丢对面）：

1. `PkOpponent.TryParse`（`PkOpponent.cs` 52 行）**没有 uid 直接 false**。Go 桥 `main.go` 105 行同样 `UID == ""` 就不 POST。
2. 配置里常填**短号**，PK payload 是**长号**。`extractOpponentRoomID` 两边都不等于 `ROOM_ID` 时会把 `init` 当成对面——可能推自己或 fetch 失败。
3. `room_init` / `Master/info` 经常被风控；hint 里其实已有 `uname`，却被上面两条规则扔掉。
4. 桥日志不够：失败时看不到原始 cmd。

**Files:**

- Modify: `AIVTuber.Core/LiveStream/PkOpponent.cs`
- Modify: `danmaku-bridge/pk.go`, `danmaku-bridge/main.go`, `danmaku-bridge/pk_test.go`
- Modify: `danmaku_bridge.py`, `test_danmaku_bridge.py`
- Test: `AIVTuber.Tests/PkOpponentTests.cs`

**Interfaces:**

- Consumes: 现有 `pkPush` / `PkOpponent` JSON `{uid,username,follower,roomid}`
- Produces: `TryParse` 在 `username` 非空时成功（`uid` 可空串）；`extractOpponentRoomID(payload, selfRoomIDs []int)` 对短号+长号任一匹配即判定本房间

- [ ] **Step 1: 写失败测试（C# 无 uid 也应解析）**

在 `PkOpponentTests.cs` 追加：

```csharp
[Fact]
public void TryParse_AcceptsUsernameWithoutUid()
{
    Assert.True(PkOpponent.TryParse(
        """{"username":"笑笑","follower":12,"roomid":545068}""", out var pk));
    Assert.Equal("笑笑", pk!.Username);
    Assert.Equal("", pk.Uid);
    Assert.Equal(545068, pk.RoomId);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --filter FullyQualifiedName~TryParse_AcceptsUsernameWithoutUid`

Expected: FAIL（当前 `Uid` 为空即 false）

- [ ] **Step 3: 改 `TryParse`**

把

```csharp
if (push is null || string.IsNullOrEmpty(push.Uid)) return false;
```

改成：

```csharp
if (push is null) return false;
var name = (push.Username ?? "").Trim();
if (string.IsNullOrEmpty(push.Uid) && name.Length == 0) return false;
```

`Username` 空时仍默认 `"对面主播"`（已有逻辑），但必须至少有 `uid` 或 `username` 或 `roomid != 0`。

- [ ] **Step 4: Go — 短号+长号 + 无 uid 也推**

`pk.go` 增加：

```go
func extractOpponentRoomIDAny(payload any, self []int) *int {
    // 与现逻辑相同，但 self 用集合：init/match 命中任一 self 则另一边是对面
}

func containsRoom(self []int, room int) bool {
    for _, id := range self {
        if id != 0 && id == room {
            return true
        }
    }
    return false
}
```

`main.go` 启动后立刻：

```go
canonical := resolveRoomIDs(httpClient, roomID) // room_init → data.room_id + data.short_id + 原始 roomID
```

`onStart`：

- 打日志：`cmd` + raw 截断 400 字（`RegisterCustomEventHandler` 的 `raw`）
- `hint` 无 uid 也 `postJSON`（C# 已能收）
- 删除 `opponent.UID == "" { return }` 这条硬拒绝

`resolveRoomIDs`：GET `room/v1/Room/room_init?id=`，读 `data.room_id` 与 `data.short_id`，失败则只用配置值。

追加 Go 测试（`pk_test.go`）：

```go
func TestExtractOpponentRoomID_ShortVsLong(t *testing.T) {
    payload := parseJSON(t, `{"data":{"init_info":{"init_id":21347320,"uid":1,"uname":"自己"},"match_info":{"match_id":545068,"uid":2,"uname":"对面"}}}`)
    got := extractOpponentRoomIDAny(payload, []int{12345, 21347320})
    if got == nil || *got != 545068 {
        t.Fatalf("got %v", got)
    }
}
```

Python `extract_opponent_room_id` 同样改成接受 `self_room_ids: set[int]`，并改 `test_danmaku_bridge.py`。

- [ ] **Step 5: 跑测试**

```
dotnet test AIVTuber.Tests --filter FullyQualifiedName~PkOpponent
cd danmaku-bridge && go test ./...
python -m pytest test_danmaku_bridge.py -q
```

Expected: 全部 PASS。改完后必须**重新编译并覆盖**运行目录里的 `danmaku_bridge.exe`（C# 优先起 exe，源码改了 exe 不更新等于没修）。

- [ ] **Step 6: 手工验收（实现后）**

开 PK，看调试日志应出现 `[弹幕桥] [PK] opponent: …` 以及监控页对面名字。若仍没有，日志里应有 `[PK] raw=...`，把原始 JSON 留下再扩字段（`uid` 有时在 `uid`/`mid`/`uinfo.uid`）。

---

### Task 2: `ReplyClassifier`（PASS / 心里话 / 开口）

**Files:**

- Create: `AIVTuber.Core/Bot/ReplyClassifier.cs`
- Create: `AIVTuber.Tests/ReplyClassifierTests.cs`

**Interfaces:**

- Produces:

```csharp
internal enum ReplyKind { Speak, InnerThought, Pass }

internal readonly record struct ClassifiedReply(
    ReplyKind Kind,
    string Spoken,   // 给 TTS；Pass/InnerThought 为空
    string Thought,  // 去掉括号的心里话；Speak 时也可带旁注
    string History); // 写入对话历史的原文（PASS 为空）
```

`ReplyClassifier.Classify(string raw)`：

1. 先 `LlmClient.StripControlTags` + `StripActionText` + `StripPartialTags`，再 `Trim`。
2. 若全文（忽略空白）是 `【PASS】` 或 `[PASS]` 或 `【pass】` → `Pass`，`History=""`。
3. 若全文被一对全角括号包住（`（`…`）`），中间没有未闭合的开口句 → `InnerThought`，`Thought=内文`，`Spoken=""`，`History=原括号行`。
4. 否则：`Spoken=去掉全角括号块后的正文`；括号块进入 `Thought`；`History=raw 去控制标记后的文本`。若 `Spoken` 对 `IsSpeakableText` 为 false 且 `Thought` 非空 → 降为 `InnerThought`。
5. 半角 `()` 不当心里话（避免英文夹注误伤）。

- [ ] **Step 1: 写失败测试**

```csharp
public class ReplyClassifierTests
{
    [Fact]
    public void Pass_IsExactFullwidthMark()
    {
        var r = ReplyClassifier.Classify("【PASS】");
        Assert.Equal(ReplyKind.Pass, r.Kind);
        Assert.Equal("", r.Spoken);
        Assert.Equal("", r.History);
    }

    [Fact]
    public void InnerThought_FullwidthParensOnly()
    {
        var r = ReplyClassifier.Classify("（他们在聊游戏，没点我）");
        Assert.Equal(ReplyKind.InnerThought, r.Kind);
        Assert.Equal("他们在聊游戏，没点我", r.Thought);
        Assert.Equal("", r.Spoken);
        Assert.Equal("（他们在聊游戏，没点我）", r.History);
    }

    [Fact]
    public void Speak_IgnoresTrailingThought()
    {
        var r = ReplyClassifier.Classify("等一下哈[emotion:happy]（这人好急）");
        Assert.Equal(ReplyKind.Speak, r.Kind);
        Assert.Equal("等一下哈", r.Spoken);
        Assert.Equal("这人好急", r.Thought);
    }

    [Fact]
    public void Speak_NormalLine()
    {
        var r = ReplyClassifier.Classify("你好啊[emotion:neutral]");
        Assert.Equal(ReplyKind.Speak, r.Kind);
        Assert.Equal("你好啊", r.Spoken);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter FullyQualifiedName~ReplyClassifierTests`

Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现 `ReplyClassifier.Classify`**

最小实现：Trim → 去标记 → PASS 正则 `^\s*[【\[]PASS[】\]]\s*$` IgnoreCase → 全文匹配 `^（([^（）]*)）$` → 否则用 `（([^（）]*)）` 抽出 Thought，剩下当 Spoken。

- [ ] **Step 4: 测试通过**

Run: 同上。Expected: PASS

---

### Task 3: `DualPartyTurnGate`（两边都不说话才交卷）

**Files:**

- Create: `AIVTuber.Core/Bot/DualPartyTurnGate.cs`
- Create: `AIVTuber.Tests/DualPartyTurnGateTests.cs`

**Interfaces:**

```csharp
internal enum TalkIdentity { Self, Opponent, Danmaku }

internal readonly record struct TalkLine(
    TalkIdentity Identity,
    string SpeakerName,
    string Text,
    string? SubjectUid);

internal sealed class DualPartyTurnGate : IDisposable
{
    public DualPartyTurnGate(TimeSpan dualSilence, IMonotonicClock? clock = null);
    public void SetMicSpeaking(bool speaking);
    public void SetLoopbackSpeaking(bool speaking);
    public void SetAiSpeaking(bool speaking);
    public void AddLine(TalkLine line);
    public event Action<IReadOnlyList<TalkLine>>? TurnReady;
}
```

冲刷条件（全部满足才 `TurnReady`）：

- `!AiSpeaking`
- `!MicSpeaking && !LoopbackSpeaking` 已持续 `dualSilence`（用测试里可推进的 clock，或 `Task.Delay` + 可注入 `Func<TimeSpan>`）
- 缓冲里至少一行非空 `Text`

AI 正在说话时：`AddLine` 仍入缓冲，**不启动静音计时**。`SetAiSpeaking(false)` 后重新从双静音开始计。

为了单测不要真等秒级：构造函数接收 `TimeSpan dualSilence`，测试用 `TimeSpan.FromMilliseconds(20)` + `Task.Delay(50)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Flushes_OnlyAfterBothSilent()
{
    IReadOnlyList<TalkLine>? got = null;
    var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(30));
    gate.TurnReady += lines => got = lines;
    gate.SetMicSpeaking(true);
    gate.AddLine(new(TalkIdentity.Self, "我", "你好", null));
    await Task.Delay(60);
    Assert.Null(got); // 麦还在说
    gate.SetMicSpeaking(false);
    gate.SetLoopbackSpeaking(false);
    await Task.Delay(60);
    Assert.NotNull(got);
    Assert.Equal("你好", got![0].Text);
}

[Fact]
public async Task DoesNotFlush_WhileAiSpeaking()
{
    IReadOnlyList<TalkLine>? got = null;
    var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(20));
    gate.TurnReady += lines => got = lines;
    gate.SetAiSpeaking(true);
    gate.AddLine(new(TalkIdentity.Opponent, "对面", "哈喽", "u2"));
    gate.SetMicSpeaking(false);
    gate.SetLoopbackSpeaking(false);
    await Task.Delay(50);
    Assert.Null(got);
    gate.SetAiSpeaking(false);
    await Task.Delay(50);
    Assert.NotNull(got);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter FullyQualifiedName~DualPartyTurnGateTests`

Expected: FAIL

- [ ] **Step 3: 实现 gate**

内部：`List<TalkLine> _buf`、三个 bool、`CancellationTokenSource? _quietCts`。任一说话状态变化时取消旧计时；若可冲刷则 `Task.Delay(dualSilence, token)` 后若仍满足则 `var snap = _buf; _buf = new(); TurnReady?.Invoke(snap)`。`Dispose` 取消计时。

- [ ] **Step 4: 测试通过**

Expected: PASS

---

### Task 4: 说话中不打断（协调器 + Runtime 接线）

当前：`RequestCoordinator.EnqueueAsync` 对麦会立刻 `_activeCts.Cancel()`（`RequestCoordinator.cs` 122–123）。`BotRuntime` 麦段注释写着 “always interrupts”。内录在 AI 说话时已 `_loopbackVadMuted`，但**使用者开口仍会截停 TTS**。弹幕 `ProcessTextAsync` 也会打断。

目标：

- `Speaking`（以及已开始 TTS 的 `Thinking`）期间，麦/内录/弹幕/PK 开场**只进 `DualPartyTurnGate`，不 `Enqueue` 新一代**。
- `MonitorViewModel.StopSpeaking` → 仍 `Interrupt()`，并 `gate` 清空或保留（清空更干净：截停=这轮作废）。
- 内录在 AI 说话时继续 mute（防自听），麦 ASR **可以**继续识别并 `AddLine`，但不调用 `ProcessSpeechAsync`。

**Files:**

- Modify: `AIVTuber.Core/Bot/RequestCoordinator.cs`（增加 `HoldNewRequests` 或让 `Enqueue` 在 hold 时返回 false 且不 cancel）
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs` 麦/内录 `SpeechDetected`、`OnAiStartSpeaking` / `OnAiStopSpeaking`
- Modify: `AIVTuber.Tests/RequestCoordinatorTests.cs`

**Interfaces:**

```csharp
public void SetHold(bool hold);
// hold==true：Enqueue 除 Manual 外一律 return false，且不得 CancelCurrent
```

`InputSource.Manual` = 截停后的人工触发如需要；截停走 `CancelCurrentAsync` 不走 Enqueue。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Hold_RejectsMic_WithoutCancellingActive()
{
    var coordinator = new RequestCoordinator();
    var cancelled = false;
    var first = coordinator.EnqueueAsync(InputSource.Danmaku, async (_, ct) =>
    {
        try { await Task.Delay(400, ct); }
        catch (OperationCanceledException) { cancelled = true; throw; }
    });
    coordinator.SetHold(true);
    var mic = await coordinator.EnqueueAsync(InputSource.Microphone, (_, _) => Task.CompletedTask);
    Assert.False(mic);
    await Task.Delay(50);
    Assert.False(cancelled);
    coordinator.SetHold(false);
    await first;
}
```

- [ ] **Step 2: 跑测试确认失败**

Expected: FAIL（当前麦会 cancel）

- [ ] **Step 3: 实现 `SetHold` + Runtime**

`BotRuntime`：

- 构造 `DualPartyTurnGate(TimeSpan.FromMilliseconds(_config.Audio.PostSpeechSilenceMs))`（复用现成「说完再等」时长，不再加说明文案）。
- `_vad.IsSpeaking` / `_loopbackVad.IsSpeaking` 变化时同步 gate（在 `Feed` 后读属性，或 VAD 增加 `SpeakingChanged`；若不想改 VAD，可在 `SpeechFrame` 置 true、`SpeechDetected` 置 false）。
- 麦/内录 ASR 成功后 `gate.AddLine(...)`，**删除**这段里对 `ProcessSpeechAsync` 的直接调用。
- `gate.TurnReady` → `IdentityPrompt.Format` → `_orchestrator.ProcessTextAsync`。
- `OnAiStartSpeaking` → `gate.SetAiSpeaking(true); coordinator.SetHold(true)`。
- `OnAiStopSpeaking` → `gate.SetAiSpeaking(false); coordinator.SetHold(false)`。
- `StopSpeaking`：`Interrupt()` + `gate.SetAiSpeaking(false)` + hold false。

弹幕选中、PK 模板开场也改为 `gate.AddLine(Danmaku/Opponent)`，不要再直接 `ProcessTextAsync`（PK 开场那条算「对方主播」身份的系统行，文本用现有 `PkTemplate` 替换后的句子）。

- [ ] **Step 4: 测试通过**

`dotnet test --filter FullyQualifiedName~RequestCoordinator`

Expected: 旧测试仍过（不 hold 时麦仍可抢非说话轮）；新测试过。

---

### Task 5: 三个身份 + 代码协议提示词

**Files:**

- Modify: `AIVTuber.Core/Config/AppConfig.cs`（新增 `IdentityConfig`，挂到 `AppConfig.Identity`）
- Create: `AIVTuber.Core/Bot/IdentityPrompt.cs`
- Create: `AIVTuber.Tests/IdentityPromptTests.cs`
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs` `BuildLlmSystemPrompt`
- Modify: `config.json.template` 加 `identity` 段（只要字段，不要解释性散文）

**Interfaces:**

```csharp
public sealed class IdentityConfig
{
    public string SelfName { get; set; } = "";          // 使用者，用户填
    public string SelfUid { get; set; } = "";           // 可选，用于记忆 subject
    public string OpponentName { get; set; } = "";      // 空 = 用 PK 抓到的 Username
    public string DanmakuLabel { get; set; } = "直播间弹幕";
}

internal static class IdentityPrompt
{
    public static string ProtocolAppendix(string selfName, string opponentName, string danmakuLabel);
    public static string FormatTurn(IReadOnlyList<TalkLine> lines);
    public static string ResolveOpponentName(string configured, PkOpponent? live);
    public static string ResolveSelfName(string configured);
}
```

`ResolveSelfName`：配置非空用配置，否则 `"使用者"`。  
`ResolveOpponentName`：配置非空优先，否则 `live?.Username`，再否则 `"对方主播"`。

`FormatTurn` 每行：

```
使用者（小明）：今天吃什么
对方主播（笑笑）：随便
直播间弹幕（路人甲）：666
```

`ProtocolAppendix` **写在代码里**，拼到 `BuildLlmSystemPrompt` 末尾（人设之后）。内容必须包含：

- 三个身份的当前名字
- 输入是键值行，谁在说话以行首身份为准
- 提到她且该接话：正常口语，可带 `[emotion:]`
- 提到她但不必出声：只输出一行 `（心里话）`，不要正文
- 与她无关、不必记：只输出 `【PASS】`
- 禁止用 `【PASS】` 和心里话混在同一行正文里（分类器仍按 Task 2 兜底）

不要把这段协议渲染进设置页。

默认 `Input.MicTemplate` / `LoopbackTemplate` 不再被 Runtime 使用（改走 `FormatTurn`）。保留字段以免旧配置反序列化失败，但 UI 可先藏掉或改成「高级」——若藏，不要加「已改走身份行」这类说明。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void FormatTurn_LabelsThreeIdentities()
{
    var text = IdentityPrompt.FormatTurn([
        new(TalkIdentity.Self, "小明", "在吗", "u-self"),
        new(TalkIdentity.Opponent, "笑笑", "在的", "u-opp"),
        new(TalkIdentity.Danmaku, "路人", "加油", "u-d"),
    ]);
    Assert.Contains("使用者（小明）：在吗", text);
    Assert.Contains("对方主播（笑笑）：在的", text);
    Assert.Contains("直播间弹幕（路人）：加油", text);
}

[Fact]
public void ResolveOpponent_PrefersLiveWhenConfigEmpty()
{
    Assert.Equal("笑笑", IdentityPrompt.ResolveOpponentName("", new PkOpponent { Username = "笑笑" }));
    Assert.Equal("手动名", IdentityPrompt.ResolveOpponentName("手动名", new PkOpponent { Username = "笑笑" }));
}

[Fact]
public void ProtocolAppendix_MentionsPassAndThought()
{
    var p = IdentityPrompt.ProtocolAppendix("小明", "笑笑", "直播间弹幕");
    Assert.Contains("【PASS】", p);
    Assert.Contains("（", p);
    Assert.Contains("小明", p);
    Assert.Contains("笑笑", p);
}
```

- [ ] **Step 2–4:** 红 → 实现 → 绿。`BuildLlmSystemPrompt` 末尾 `+ "\n\n" + ProtocolAppendix(...)`。`ConfigDiff`：改 `Identity.*` 走 `RebuildLlm`（轻量，只为刷新协议里的名字）。

---

### Task 6: 记忆按身份检索；PASS 不入库

**Files:**

- Modify: `AIVTuber.Core/Bot/ConversationManager.cs`
- Modify: `AIVTuber.Core/Memory/MemoryExtractor.cs`
- Modify: `AIVTuber.Core/Bot/BotOrchestrator.cs`（`RunStreamingPipelineAsync` 用 `ReplyClassifier`）
- Test: 扩 `AIVTuber.Tests` 里现有 conversation/memory 测试或新建 `ReplyPipelineTests.cs`

**Interfaces:**

`BuildMessages` 增加可选 `IReadOnlyList<string?> subjectUids`。`AppendRelevantFacts` 对每个 uid 再 `SearchAsync(..., subjectUid, topK: 3)`，输出改成：

```
【记忆·使用者】
- ...
【记忆·对方主播】
- ...
```

uid 来自本轮 `TalkLine.SubjectUid`：使用者 = `Identity.SelfUid`（可空则不做 subject 过滤、仍用语义检索）；对面 = `CurrentPkOpponent.Uid`；弹幕 = 该条 uid。

对面若有 uid，额外 `ListByOpponentAsync(uid, limit: 4)`，拼成：

```
【记忆·对方主播·往期PK】
- 对面：… / 你：…
```

无 uid 则跳过往期 PK，避免把别人的对局灌进来。`OnLoopbackTranscript` 不再只写 `_pkBuffer`：gate 冲刷时整段 `FormatTurn` 作为 user 历史，这样下一轮模型能看见对谈，而不只是当前 user 消息。

`AllowSpeak` / `WakeGate`：`TurnReady` → `ProcessTextAsync` 时 `ShouldSpeak` 对麦/内录/已缓冲弹幕恒为 true（模型用 PASS 决定沉默）。若仍要挡刷屏弹幕，过滤放在 `DanmakuSelector` 入 gate 之前，不要放在 orchestrator 入队之后。

`MemoryExtractor.OnTurnAsync`：

- 本轮 `ClassifiedReply.Kind == Pass` → **不要** `AddAssistantMessage`，**不要**增加 turnCount / 提取。
- `InnerThought` → `AddAssistantMessage(History)`，**允许**提取（心里话里可能有「他们提到我但在阴阳」）。
- `Speak` → 历史用 `History`（可含括号旁注），TTS 只用 `Spoken`。

`RunStreamingPipelineAsync` 在拼好 `spoken` 之后：

```csharp
var classified = ReplyClassifier.Classify(buffer.ToString());
if (classified.Kind == ReplyKind.Pass) { /* 不 Write sentenceChannel，不 AddAssistant */ return; }
if (classified.Kind == ReplyKind.InnerThought) { /* AddAssistant(History)，不 TTS */ return; }
// Speak: WriteAsync(classified.Spoken)
```

`ConversationManager.AddUserMessage` 在 `TurnReady` 时写入 `FormatTurn` 全文，这样历史里也能看懂对谈。

- [ ] **Step 1:** 测试 `Classify` 接入：用现有 fake LLM 返回 `【PASS】`，断言 TTS `StreamAsync` 次数为 0、history 无 assistant。`（观察）` → TTS 0 次、history 有 assistant。

- [ ] **Step 2–4:** 红 → 改 orchestrator / extractor → 绿。

---

### Task 7: 设置页三个身份字段

**Files:**

- Modify: `App/WebUi/wwwroot/index.html`（AI 引擎或直播分区：三个 input）
- Modify: `AIVTuber.Core/ViewModels/ConfigViewModel.cs` `BuildWebDraft` / `ApplyWebPatch`
- Modify: `App/Views/ConfigView.xaml`（遗留页同步三个框，不要加说明句）

标签只用：

- `使用者`
- `对方主播`（留空则用 PK 抓到的名字）
- `直播间弹幕`（栏目标题，默认「直播间弹幕」）

不要 hint/tagline。密钥类「留空不修改」与这三项无关。

- [ ] **Step 1:** `BuildWebDraft` 带 `identity.selfName` 等；补一个 ViewModel 单测读 draft。
- [ ] **Step 2:** HTML + patch。
- [ ] **Step 3:** `dotnet build App/App.csproj`；`dotnet test --filter FullyQualifiedName~ConfigViewModel`。

---

### Task 8: 端到端接线与回归

**Files:** 只接线，不再加功能。

核对清单：

| 需求 | 落点 |
|---|---|
| 1 说话不被打断 | Task 4 `SetHold` + gate 不在 AI 说话时 flush |
| 2 双方都静音才截一段 | Task 3 `DualPartyTurnGate` |
| 3 三个身份键值、可填、对面来自 PK | Task 1 + Task 5 + Task 7 |
| 4 模型看见对谈、是否点名 | `FormatTurn` 多行 + 协议附录 |
| 5 `（心里话）` 不 TTS、可入库 | Task 2 + Task 6 |
| 6 `【PASS】` 不 TTS 不入库 | Task 2 + Task 6 |
| PK 仍拿不到对面 | Task 1（uid 可选 + 短长号 + 重编 exe） |

回归：

```
dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj
cd danmaku-bridge && go test ./...
```

Expected: 全绿。再手工：正常模式说一句 → AI 说完之前你再说话，TTS 不得被截；PK 开打监控应出现对面名；对谈里点名但不需要回时日志 `[LLM输入]` 后应看到心里话或 PASS，扬声器无声。

---

## Spec coverage（自检）

1. 不打断 — Task 4  
2. 双静音区间 — Task 3、Task 4 接线  
3. 三个身份键值、可填、对面/弹幕来源 — Task 5、7；对面实时值 Task 1  
4. 理解对谈、是否提到她 — Task 5 协议 + FormatTurn  
5. 全角心里话 — Task 2、6  
6. 【PASS】 — Task 2、6  
7. PK 拿不到对面 — Task 1（这是身份生效的前置）

无 TBD。类型名前后一致：`ReplyKind` / `TalkLine` / `DualPartyTurnGate` / `IdentityPrompt` / `IdentityConfig`。
