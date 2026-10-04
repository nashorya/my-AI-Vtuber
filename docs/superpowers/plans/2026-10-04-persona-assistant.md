# 人设助手 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 主播页「帮我写人设」：检查描述 → 追问缺的关键信息 → 生成简短人设 + AI 名字/别名 + 称呼 → 预览 → 写入草稿。

**Architecture:** Core 新增 `AIVTuber.Core/Persona/`（`PersonaAssistant` 两步调用主模型、`PersonaValidator` 程序校验）。`StreamerConsoleController` 新增两个命令，把结果以 `personaAssistant` 消息推回页面。页面只改草稿；「保存人设」在助手填过称呼/别名时一并提交 `live.selfName` / `live.wakeKeywords`。

**Tech Stack:** .NET 10、xUnit、WebView2 页面（原生 JS/CSS）。

**Spec:** `docs/superpowers/specs/2026-10-04-persona-assistant-design.md`

## Global Constraints

- 人设只写：身份一句（名字、与主播关系、正在陪播）、性格 ≤4 个关键词、行为方式 ≤2 条、例子 ≤2 个；例子行固定带「只体现语气，不要照搬原句」。
- 人设绝不写：何时说话/静默、心里话、字数、输出格式、「必须」「严禁」类规则。
- 必问：AI 名字、对主播的称呼（字段已填或描述里已写明则不问）；风格问题仅在描述几乎无性格信息时问；最多 3 个问题，每个 2–4 个建议答案。
- 跳过名字 → 生成器取名并标为补写；跳过称呼 → 用「主播」。
- 校验失败重试一次；仍失败提示「没生成好，请改改描述再试」，不改任何字段。
- 超时 15 秒，提示「AI 没有及时回应，请稍后再试」。
- 云端未授权时按钮不可用。
- 「用这个」只改草稿，不自动保存。
- 测试工程在 arm64 Mac 上运行：`dotnet build AIVTuber.Tests -p:PlatformTarget=AnyCPU` 后 `dotnet test AIVTuber.Tests --no-build -p:PlatformTarget=AnyCPU --filter ...`。基线已知失败 2 个（`StreamerDraft_ContainsNoManagedServiceFields`、`GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`），与本计划无关。

## Review Focus

1. 主播粘贴一大段（>2000 字）人设 → 不调用模型，提示「描述太长了，请精简到 2000 字以内」。（Task 2）
2. 模型把 JSON 包在 ```json 代码块里或前后带说明文字 → 照常解析。（Task 2）
3. 名字/别名里含逗号、空白或重复（「小咪，咪咪」「小咪」）→ 别名去空白、按逗号拆开、去重，名字排第一。（Task 2）
4. 生成进行中主播又点一次 / 退出登录 → 第二次点击被忽略；退出登录后返回的结果不推给页面。（Task 3）
5. 主播点了「用这个」又点「撤销修改」→ 人设恢复，助手带来的称呼/别名也一并丢弃，不会在下次保存时被偷偷提交。（Task 4）

---

### Task 1: `PersonaValidator` 与结果类型

**Files:**
- Create: `AIVTuber.Core/Persona/PersonaTypes.cs`
- Create: `AIVTuber.Core/Persona/PersonaValidator.cs`
- Test: `AIVTuber.Tests/Persona/PersonaValidatorTests.cs`

**Interfaces:**
- Produces (`PersonaTypes.cs`, namespace `AIVTuber.Core.Persona`, all `internal`):
  - `record PersonaDraftInput(string Description, IReadOnlyList<string> ExistingAiNames, string ExistingStreamerTitle)`
  - `record PersonaQuestion(string Id, string Text, IReadOnlyList<string> Suggestions)`；Id ∈ `"aiName" | "streamerTitle" | "style"`
  - `record PersonaAnswer(string QuestionId, string? Text)`（null = 跳过）
  - `record PersonaCheckResult(IReadOnlyList<PersonaQuestion> Questions, string? FoundAiName, string? FoundStreamerTitle)`
  - `record PersonaComposeResult(string Persona, IReadOnlyList<string> AiNames, string StreamerTitle, IReadOnlyList<string> Supplemented)`
  - `sealed class PersonaAssistantException(string userMessage) : Exception(userMessage)`
- Produces: `static class PersonaValidator { static string? Validate(PersonaComposeResult r); }` — null = 合格，否则返回中文原因（用于重试提示）。

人设正文行格式（生成器提示词与校验共用）：`行为方式：a；b`（全角分号分隔），例子为 `例子（只体现语气，不要照搬原句）：` 之后以 `- ` 开头的行。

- [ ] **Step 1: 写失败测试**

```csharp
const string Good = "你是小咪，老大的猫娘搭档，正在陪老大直播。\n性格：傲娇、爱吐槽。\n行为方式：被夸会嘴硬；看不惯就直说。\n例子（只体现语气，不要照搬原句）：\n- 老大夸你今天很乖 → “哼，本喵一直都很乖好吗。”";
static PersonaComposeResult R(string persona, string title = "老大") => new(persona, ["小咪"], title, []);

[Fact] Accepts_AShortPersona()                      => Assert.Null(PersonaValidator.Validate(R(Good)));
[Theory]
[InlineData("你是小咪，正在陪老大直播。\n不想说话时只输出【PASS】")]
[InlineData("你是小咪，正在陪老大直播。\n心里话用全角括号包裹")]
[InlineData("你是小咪，正在陪老大直播。\n回答控制在30字以内")]
[InlineData("你是小咪，正在陪老大直播。\n你必须先判断是否被叫到")]
Rejects_WorkflowRules(string persona)               => Assert.NotNull(PersonaValidator.Validate(R(persona)));
[Fact] IgnoresForbiddenWords_InsideExampleQuotes()  // “你必须给我道歉！” 在例子台词里 → 合格
[Fact] Rejects_MoreThanTwoBehaviours()              // 行为方式：a；b；c
[Fact] Rejects_MoreThanTwoExamples()                // 三行 "- "
[Fact] Rejects_WhenNameMissingFromPersona()         // AiNames = ["小咪"]，正文写「你是小猫」
[Fact] Rejects_WhenGivenTitleMissingFromPersona()   // title "老大"，正文只写「主播」
[Fact] Accepts_EmptyTitle()                         // title ""，不检查称呼
```

- [ ] **Step 2: 运行，确认失败**（类型不存在，编译失败）

Run: `dotnet build AIVTuber.Tests -p:PlatformTarget=AnyCPU`
Expected: FAIL，`PersonaValidator` 未定义。

- [ ] **Step 3: 实现类型与 `PersonaValidator.Validate`**

禁用词：`PASS`、`【`、`】`、`括号`、`心里话`、`字以内`、`输出`、`必须`、`严禁`。检查前先去掉 `“…”` 与 `"…"` 中的内容。行为数 = `行为方式：` 行按 `；` 拆分后的非空项数；例子数 = 以 `- ` 开头的行数。

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test AIVTuber.Tests --no-build -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~PersonaValidatorTests"`（先 build）
Expected: PASS

- [ ] **Step 5: Commit** `Persona: validator keeps workflow rules out of generated personas`

---

### Task 2: `PersonaAssistant`（检查 + 生成）

**Files:**
- Create: `AIVTuber.Core/Persona/PersonaAssistant.cs`
- Create: `AIVTuber.Core/Persona/PersonaPrompts.cs`
- Modify: `AIVTuber.Core/Pipeline/LlmClient.cs`（构造函数新增可选参数 `int? maxTokens = null`，legacy `StreamAsync` 用 `maxTokens ?? (allowedChannels is null ? 256 : 512)`）
- Test: `AIVTuber.Tests/Persona/PersonaAssistantTests.cs`

**Interfaces:**
- Consumes: Task 1 全部类型；`MemoryExtractor.ExtractJson(string)`（internal static，复用）。
- Produces:
  - `internal sealed class PersonaAssistant(Func<ILlmClient> createLlm)`
  - `Task<PersonaCheckResult> CheckAsync(PersonaDraftInput input, CancellationToken ct)`
  - `Task<PersonaComposeResult> ComposeAsync(PersonaDraftInput input, PersonaCheckResult check, IReadOnlyList<PersonaAnswer> answers, CancellationToken ct)`
  - `public const int MaxDescriptionChars = 2000;`
  - 失败一律抛 `PersonaAssistantException`（携带给主播看的中文）。
  - `PersonaPrompts.Check`、`PersonaPrompts.Compose`（string 常量）。

调用方式：每次调用 `createLlm()` 得到客户端（用后若 `IDisposable` 则释放），`StreamAsync([system 提示词], userInput, ct)` 收集全部 token 后 `ExtractJson` → 反序列化。

**检查** — 模型只回答描述里已有的信息和建议，问题由程序决定：
- 模型返回 `{"aiName":string|null,"streamerTitle":string|null,"needsStyle":bool,"suggestions":{"aiName":[...],"streamerTitle":[...],"style":[...]}}`。
- 问题：`aiName`（「它叫什么名字？」）当 `ExistingAiNames` 为空且模型 `aiName` 为 null；`streamerTitle`（「它怎么称呼你？」）当 `ExistingStreamerTitle` 空白且模型 `streamerTitle` 为 null；`style`（「它说话更像哪种？」）当 `needsStyle`。建议答案取前 4 个，不足 2 个时补默认（名字：「小鱼」「团子」；称呼：「主播」「老大」；风格：「温柔」「爱吐槽」「元气」）。

**生成** — 名字取值顺序：回答 → `ExistingAiNames[0]` → `check.FoundAiName` → 模型自取（并记入 Supplemented）；称呼：回答 → `ExistingStreamerTitle` → `check.FoundStreamerTitle` → 「主播」。把描述、已定的名字和称呼、风格回答写进 user 消息。
- 模型返回 `{"persona":string,"aiNames":[...],"streamerTitle":string,"supplemented":[...]}`。
- 规范化：别名去空白、按 `,`/`，` 拆分、去重，已定名字排第一；称呼用已定值覆盖。
- `PersonaValidator.Validate` 不通过（或 JSON 无法解析）→ 重试一次，额外附 system 消息 `上次结果不合格：{原因}。请按要求重写。`；仍失败 → `PersonaAssistantException("没生成好，请改改描述再试")`。

提示词（`PersonaPrompts.cs`，定稿文案）：

```text
Check:
你在帮主播整理直播 AI 角色的人设。主播给的描述可能为空、很短，或是一段现成的人设。
只做三件事：
1. 描述里如果明确写了 AI 的名字，填 aiName，否则 null；明确写了 AI 怎么称呼主播，填 streamerTitle，否则 null。
2. needsStyle：描述几乎没有性格信息（空，或只有“可爱”这类一个泛泛的词）时为 true，否则 false。
3. 为名字、称呼、说话风格各给 3 个贴合描述的简短建议。
只返回 JSON：{"aiName":null,"streamerTitle":null,"needsStyle":false,"suggestions":{"aiName":[],"streamerTitle":[],"style":[]}}

Compose:
你为直播 AI 角色写人设。它是直播间里的一个角色，陪主播一起直播，不是主播的助手。人设写的是它如何认识自己。
简短克制，描写多了角色会死板。严格按这个形状，没有的部分整行省略：
你是{名字}，{一句身份，含与主播的关系}，正在陪{称呼}直播。
{主播原文中的具体设定，每条一句}
性格：{最多4个关键词，只用主播给的词或直接同义，用顿号分隔}
行为方式：{最多2条，每条一句，写性子怎么表现，用全角分号分隔}
例子（只体现语气，不要照搬原句）：
- {场景} → “{它会说的一句话}”
（例子最多2个）
主播没写行为或例子时，最多补1条行为、2个例子，并把补写的那一整行原文放进 supplemented。
不要写：什么时候说话或沉默、心里话、字数、输出格式、“必须”“严禁”之类的规则——这些由程序负责。
只返回 JSON：{"persona":"…","aiNames":["名字","别名"],"streamerTitle":"…","supplemented":[]}
```

- [ ] **Step 1: 写失败测试**（假 `ILlmClient`：按调用次序返回预置字符串，记录收到的 system/user 文本）

```csharp
[Fact] Check_AsksNameAndTitle_WhenFieldsEmptyAndDescriptionSilent()
    // 模型 aiName/streamerTitle 为 null，needsStyle=false → Questions.Select(q=>q.Id) == ["aiName","streamerTitle"]
[Fact] Check_SkipsQuestions_ForFilledFieldsOrNamesInDescription()
    // ExistingAiNames=["大肥鱼"]，模型 streamerTitle="老板" → Questions 为空，FoundStreamerTitle=="老板"
[Fact] Check_AsksStyle_OnlyWhenModelSaysNeeded()
[Fact] Check_PadsSuggestions_ToAtLeastTwo_AndCapsAtFour()
[Fact] Check_RejectsDescriptionsOverLimit_WithoutCallingModel()
    // 2001 字 → PersonaAssistantException，消息 "描述太长了，请精简到 2000 字以内"；模型调用次数 0
[Fact] Check_ParsesJsonInsideCodeFenceOrProse()
[Fact] Compose_PassesDescriptionAndSettledNamesToModel()
    // 描述含「讨厌香菜」→ user 文本包含「讨厌香菜」「小咪」「老大」
[Fact] Compose_SkippedTitle_FallsBackTo主播()
[Fact] Compose_NormalisesAliases()
    // 模型 aiNames ["咪咪, 小咪 ", "小咪"]，名字回答「小咪」→ ["小咪","咪咪"]
[Fact] Compose_RetriesOnceWithReason_ThenSucceeds()
    // 第 1 次含【PASS】，第 2 次合格 → 返回第 2 次；第 2 次请求含「上次结果不合格」
[Fact] Compose_FailsAfterSecondRejection()
    // 两次都不合格 → PersonaAssistantException("没生成好，请改改描述再试")
```

- [ ] **Step 2: 运行，确认失败**（编译失败）
- [ ] **Step 3: 实现 `PersonaPrompts`、`PersonaAssistant`、`LlmClient` 的 `maxTokens` 参数**（生成用 `maxTokens: 800`，检查用 400；由调用方的 `createLlm` 决定，见 Task 3）
- [ ] **Step 4: 运行测试，确认通过**；再跑 `LlmClient` 相关已有测试：`--filter "FullyQualifiedName~LlmClient"`，Expected: PASS
- [ ] **Step 5: Commit** `Persona: assistant asks only what is missing and composes a short persona`

---

### Task 3: 主播控制器命令

**Files:**
- Modify: `AIVTuber.Core/ViewModels/StreamerConsoleController.cs`
- Test: `AIVTuber.Tests/Distribution/StreamerConsoleTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `PersonaAssistant`、类型。
- Produces（页面协议）：
  - 页面 → 宿主：`personaCheck {requestId:number, description:string}`；`personaCompose {requestId, description, answers:[{questionId, text|null}]}`。
  - 宿主 → 页面：`{type:"personaAssistant", data:{requestId, stage, ...}}`，`stage` ∈
    - `"questions"`：`questions:[{id,text,suggestions:[]}]`（可能为空数组，页面随即发 compose）
    - `"preview"`：`persona, aiNames:[], streamerTitle, supplemented:[]`
    - `"error"`：`message`
  - 构造函数新增可选参数 `PersonaAssistant? personaAssistant = null`（放在最后）；为 null 时用
    `new PersonaAssistant(() => new LlmClient(cfg.Llm.BaseUrl, cfg.Llm.ApiKey, cfg.Llm.Model, systemPrompt: "", maxTokens: 800))`，`cfg = _runtime.CurrentConfig`。

行为：
- `Commands` 加入 `"personaCheck"`, `"personaCompose"`。
- 两个命令都不 await（`_ = RunPersonaAsync(...)`），避免阻塞消息循环。
- `!_runtime.CloudAllowed` → 直接回 `error`「登录后才能使用人设助手」。
- 同时只允许一个请求：进行中再收到请求 → 忽略（不回消息）。
- 每次请求 15 秒 `CancellationTokenSource`；超时 → `error`「AI 没有及时回应，请稍后再试」；`PersonaAssistantException` → 其消息；其他异常 → `UserErrorMapper.FromException(ex, ErrorArea.Model)` 的 `UserMessage`（无映射时「人设助手出错了，请稍后再试」）。
- 返回时若 `!_runtime.CloudAllowed`（期间退出登录）→ 丢弃，不推送。
- 输入名字/称呼：`_config.Working.Interaction.WakeKeywords`、`_config.Working.Identity.SelfName`。
- `personaCompose` 需要 `PersonaCheckResult`：宿主缓存最近一次 check 的描述文本与结果；compose 收到的描述与缓存相同则复用，否则先重新 `CheckAsync`。

- [ ] **Step 1: 写失败测试**（沿用 `StreamerConsoleTests` 现有夹具；注入用假 LLM 构造的 `PersonaAssistant`；收集 `_post` 的消息）

```csharp
[Fact] PersonaCheck_PostsQuestions()
[Fact] PersonaCompose_PostsPreview_AndDoesNotTouchTheDraft()
    // 之后 _config.Working.Llm.SystemPrompt / SelfName / WakeKeywords 不变
[Fact] PersonaCommands_RefusedWhenCloudNotAllowed()
[Fact] PersonaCommand_IgnoredWhileAnotherIsRunning()
[Fact] PersonaCommand_TimesOutAfter15Seconds()   // 假 LLM 永不返回；用可注入的超时（internal 属性 PersonaTimeout）设为 50ms
[Fact] PersonaResult_DroppedWhenSignedOutMeanwhile()
```

- [ ] **Step 2: 运行，确认失败**
- [ ] **Step 3: 实现**（`internal TimeSpan PersonaTimeout { get; set; } = TimeSpan.FromSeconds(15);`）
- [ ] **Step 4: 运行 `StreamerConsoleTests` 全部，确认通过**
- [ ] **Step 5: Commit** `Streamer console: persona assistant commands`

---

### Task 4: 页面

**Files:**
- Modify: `App/WebUi/wwwroot/streamer.html`（人设卡片内：「帮我写人设」按钮 + `<div id="personaAssist" hidden>`：问题卡片区、预览区、按钮）
- Modify: `App/WebUi/wwwroot/streamer.js`
- Modify: `App/WebUi/wwwroot/streamer.css`

**Interfaces:**
- Consumes: Task 3 页面协议。

行为：
- 「帮我写人设」：取编辑框文本发 `personaCheck`；按钮显示「正在看…」并禁用，直到收到同 requestId 的结果或 error。
- `questions` 非空：每题显示标题、建议答案 chips（单选，可再点取消）、一个「自己填」输入框、「跳过」状态默认；「生成」发 `personaCompose`。`questions` 为空：直接发 `personaCompose`。
- `preview`：显示人设全文（`<pre>`），`supplemented` 中的行加「AI 补的」标记；下方「AI 名字/别名：…」「称呼你：…」；按钮「用这个」「重新生成」（同答案再发 compose）「取消」。
- 「用这个」：编辑框 = persona，触发 input（状态「未保存」）；记 `pendingIdentity = {selfName, wakeKeywords: aiNames.join(", ")}`；同步写入直播设置页 `#selfName`、`#wakeWords` 的显示值；面板显示「称呼和名字会随『保存人设』一起保存」。
- 「保存人设」：`pendingIdentity` 存在时 patch 为 `{persona:{systemPrompt}, live:{selfName, wakeKeywords}}`；保存成功后清空 `pendingIdentity`。
- 「撤销修改」「恢复默认」：清空 `pendingIdentity`，并用 `settings` 重新渲染直播设置页（`renderLive()`）。
- `error`：面板内一行红字，按钮恢复。
- 账号未登录（`state.account.signedIn === false`）：按钮禁用，title「登录后才能使用」。
- 样式沿用现有 `.card`/`.btn`/chip 风格变量，不新增颜色。

- [ ] **Step 1: 实现 HTML/CSS/JS**
- [ ] **Step 2: 浏览器预览验证**：用内置浏览器打开 `App/WebUi/wwwroot/streamer.html`（无 WebView 时 `post` 为空操作），在控制台用 `window.dispatchEvent`/直接调用 `onHostMessage` 等价路径注入 `settings`、`state`、`personaAssistant` 三种 stage 的消息；确认：问题卡片可选可填、预览补写标记、「用这个」后编辑框与直播设置页字段更新、「撤销修改」后恢复。需要时在 `streamer.js` 暴露 `window.__streamerTestHook = onHostMessage`（仅在 `!hasWebView` 时）。截图留证。
- [ ] **Step 3: Commit** `Streamer page: help me write a persona`

---

### Task 5: 真实模型验收与收尾

- [ ] **Step 1:** 若本机有可用的 DeepSeek 配置（`config.json` 的 `Llm`），写一个一次性脚本（放 scratchpad，不入库）用 `PersonaAssistant` 跑 6 条描述：空、「可爱」、「傲娇猫娘，爱吐槽，叫我老大」、一段较长自由描述、开发者版大肥鱼人设、「讨厌香菜，说话带东北口音的小狐狸」。结果整理给用户评审。没有可用配置则在 PR 中注明「待用户在 Windows 上验收」并附这 6 条描述。
- [ ] **Step 2:** 跑全量测试（基线 2 个已知失败外无新增失败）。
- [ ] **Step 3:** 推送分支，开 PR（描述含：功能、协议、测试结果、验收状态、Windows 质量门待跑）。
