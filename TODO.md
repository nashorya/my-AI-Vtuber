# AIVTuber 工程 TODO

> 审查基线：`main@21b3c061` 加本次保留的 Action POC，2026-07-10。优先处理 P0，再扩展动作系统和 UI。
> 每项完成时必须同时提交自动化测试或可复现的验收记录，不能只以“本机能运行”作为完成标准。

## 技术选型决策

### 结论

- **保留 `.NET 10 + WPF`。** 产品依赖 Windows 音频设备、WASAPI、进程回环、VTube Studio 和 OBS；迁移 WinUI 3、Avalonia 或 MAUI 会重写 UI，却不能消除 Windows 专属核心。
- **保留 WPF-UI，但固定版本并统一使用。** 当前 `App/App.csproj` 使用 `3.*`，本次还原实际得到 3.1.1；先完成 3.x -> 4.3.0 的兼容性验证，再锁定精确版本。不要同时引入 MaterialDesign、MahApps、HandyControl 等第二套全局主题。
- **保留 NAudio、SQLite 和 ONNX Runtime。** 它们符合本地 Windows 音频、单用户数据和本地推理场景；问题主要在版本、安全、生命周期和可测试性，而不是库本身。
- **保留定制协议所需的 `ClientWebSocket`。** 删除未使用的 `Websocket.Client`；OBS 客户端可评估成熟 v5 SDK，但 VTS 和云端 ASR/TTS 不必为了统一而强行换库。
- **保留 Python 能力，但替换“依赖用户系统 Python”的交付方式。** ASR 和弹幕桥应成为有锁定依赖、健康协议和版本信息的自包含 sidecar。
- **可引入 `CommunityToolkit.Mvvm`。** 它不是视觉组件库，用于统一 `ObservableObject`、`RelayCommand`、`AsyncRelayCommand` 和表单验证，减少 code-behind 与 `async void`。
- **把 Neuro-sama 当作表现目标，不当作技术选型证据。** 其具体动作编排后端没有足够公开实现可供复刻；自然度应通过模型 rig、预制 Motion/Expression、本地行为导演和语音时间轴共同实现。
- **ZerolanLiveRobot 可借鉴渲染与 AI 流水线解耦，但不能照搬动作层。** 其公开实现主要仍是 RMS 口型、眨眼、呼吸和基础 LookAt，说明直接换 Cubism/live2d-py 不会自动解决僵硬。

### 现代 UI 组件路线

首选现有 WPF-UI 的 Fluent 控件完成改版：

| 场景 | 推荐控件 |
| --- | --- |
| 页面与分区导航 | `NavigationView`、`TabView`、`CardExpander` |
| 状态与反馈 | `InfoBar`、`Snackbar`、`ProgressRing` |
| 危险操作与确认 | `ContentDialog`、带 `SymbolIcon` 的危险按钮 |
| 配置输入 | `ComboBox`、`NumberBox`、`ToggleSwitch`、`AutoSuggestBox` |
| 事实和观众数据 | `DataGrid`，不要用重复 Card 模拟表格 |
| 运行控制 | 图标按钮、Tooltip、固定尺寸 CommandBar/工具栏 |

仅在产品目标发生变化时重新评估框架：

- 需要 macOS/Linux，且已经替换 Windows 专属音频后端：评估 Avalonia。
- 明确采用 MSIX/Store，并愿意完整重写 UI：评估 WinUI 3。
- 明确要 Material 视觉语言：用 MaterialDesignInXaml **替换** WPF-UI，而不是混用。
- 需要工业级 DataGrid、Docking 或图表：再评估 DevExpress/Telerik/Syncfusion；当前三页工具不需要。

## 当前质量基线

- [ ] 更新此节的数据，直到它可以作为发布门禁自动生成。
- `dotnet build AIVTuber.slnx -c Release`：成功，但有 4 个 `NU1903` 高危漏洞警告。
- `dotnet publish App/App.csproj -c Release -r win-x64`：成功，但产物并不包含完整本地 ASR 运行环境。
- 当前测试：151 个测试中通过 134 个；补入 `WebRtcVad.dll` 后通过 137 个，剩余 14 个失败为 SQLite teardown 文件锁。
- 覆盖率：行 32.44%，分支 20.08%；`BotOrchestrator` 为 0%。

## P0：发布阻塞

### [ ] P0-01 修复 SQLite 高危依赖漏洞

**证据**

- `AIVTuber.Core/AIVTuber.Core.csproj` 使用 `Microsoft.Data.Sqlite 9.0.6`。
- 还原出的 `SQLitePCLRaw.lib.e_sqlite3 2.1.10` 命中 `CVE-2025-6965`，构建产生 `NU1903`。

**实施**

- 将 `Microsoft.Data.Sqlite` 升级到与 .NET 10 兼容的最新 10.0.x 补丁版本。
- 检查所有传递依赖，不以 `NoWarn` 隐藏漏洞。
- 对现有 `memory.db` 做升级前后读写和 schema 兼容回归。

**验收**

- `dotnet restore`、Release build、测试和 publish 全部通过。
- `dotnet package list --vulnerable --include-transitive` 不再报告高危漏洞。
- 旧数据库可直接打开，事实和观众数据不丢失。

### [ ] P0-02 修复配置对象引用别名和二次保存失效

**证据**

- `ConfigViewModel.SaveAsync` 将同一个 `Working` 实例传给保存和热应用。
- `BotRuntime.ApplyConfigAsync` 执行 `_config = newConfig`；第一次保存后，设置页继续编辑的就是运行时持有的对象，第二次 `ConfigDiff.Compute` 可能比较同一对象而返回 `None`。

**实施**

- 明确配置所有权：UI 草稿、已持久化快照、运行时快照必须是不同实例。
- `BotRuntime` 只接收深拷贝或不可变配置快照；应用成功后再原子替换当前快照。
- 应用失败时保留旧运行时配置，并在 UI 明确显示“已保存但未应用”。
- 为配置分配 revision ID，保留 last-known-good；候选配置先校验/测试，再原子持久化并尝试应用，失败时 active revision 不前移且可一键回滚。

**验收**

- 新增回归测试：同一设置页连续保存两次不同值，两次均产生正确 diff 并热生效。
- Action 专项回归：连续两次修改 `ActionMap` 后，运行时映射与自动生成的 LLM system prompt 都使用第二次保存的值。
- 新增回归测试：应用失败不会让运行时快照与实际模块状态不一致。

### [ ] P0-03 补齐 ASR/TTS 热更新差异检测

**证据**

- `ConfigDiff.Compute` 没有比较 `Asr.Model`、`Asr.LocalAsrUrl`、`Asr.PythonPath`。
- `ConfigDiff.Compute` 没有比较 `Tts.Model`、`Tts.GroupId`、`Tts.Speed`。

**实施**

- 为 `AppConfig` 每个运行时字段定义明确的 `RuntimeChange` 行为。
- 本地 ASR 地址、Python/sidecar 路径变化时，同时重建客户端并重启受管进程。
- TTS 模型、GroupId、语速变化后重建或原位更新对应 provider。

**验收**

- 参数化测试覆盖 `AppConfig` 的每个属性，断言期望的 diff 标志。
- UI 保存后下一轮请求使用新参数，无需重启应用。

### [ ] P0-04 修复请求取消的代际竞争

**证据**

- `BotOrchestrator.ProcessSpeechAsync`、`ProcessTextAsync` 先 `Interrupt()`，随后各自创建 `_currentCts`。
- 旧请求的 `finally` 仍会把 `_isProcessing` 设为 `false` 并发出停止事件，可能覆盖新请求状态；多个 `async EventHandler` 也无法被统一等待和监督。

**实施**

- 将麦克风、内录、弹幕和手动输入统一写入有界 `Channel<InputEnvelope>`。
- 由单一 coordinator 决定优先级、取消、背压和当前 generation。
- 只有当前 generation 可以更新状态、播放音频、写 OBS/VTS 或发出完成事件。
- 将关键 `async (_, ...) =>` 事件处理替换为可等待的 Task/命令边界。
- 将当前 `_ = TriggerMappedHotkeyAsync(...)` 纳入 generation 管理；动作有序、可取消、异常可观察，不能脱离回复生命周期后台补触发。

**验收**

- 任意时刻最多一个 ASR/LLM/TTS 回复流水线进入输出阶段。
- 压力测试覆盖连续麦克风打断、弹幕抢占、内录并发和停止按钮。
- 被取消的旧请求不能清除新请求状态，也不能继续播放、写字幕或触发动作。
- 断线或新回复开始前排队的旧动作全部丢弃；VTS 失败能关联到对应回复和动作，而不是成为未观察异常。

### [ ] P0-05 修复热更新后的事件订阅累积

**证据**

- 每次 `InitPipeline` 都创建新的 `BotOrchestrator`。
- `BotOrchestrator` 构造时订阅共享 `AudioPlayer.RmsUpdated`、`PlaybackFinished` 和 LLM 事件，但 `Dispose` 没有解除这些订阅。

**实施**

- 保存具名 handler，并在 `Dispose`/重建前对称解除订阅。
- 为所有运行时组件建立统一 `StartAsync` / `StopAsync` / `IAsyncDisposable` 生命周期契约。
- 重建期间串行化 Apply，避免旧、新组件同时存活。

**验收**

- 连续热应用 20 次后，每个音频事件只触发一次 VTS/状态更新。
- 连续 Rewire 20 次后，单个 `[action:...]` 标签也只产生一次 VTS hotkey 请求。
- 已释放的 orchestrator 可被 GC，不再被 `AudioPlayer` 或 LLM 事件引用。

### [ ] P0-06 让本地 ASR 成为可发布、可诊断的 sidecar

**证据**

- `asr_server.py` 硬编码作者机器的模型路径和 `device_map="cuda"`。
- `App/App.csproj` 发布 `danmaku_bridge.py`，但没有发布 `asr_server.py`。
- `/health` 在模型仍为 `loading` 时也返回 HTTP 200；`LocalAsrClient.PingAsync` 只检查状态码，因此会误报可用。

**实施**

- 模型路径、设备、端口均配置化；启动时探测 CPU/DirectML/CUDA，并给出可操作错误。
- `/health` 返回结构化的 `loading/ready/failed`，客户端必须验证 `ready`。
- 固定 Python 与包版本，优先打包独立 sidecar/embedded Python，不依赖用户全局环境。
- 使用绝对工作目录、版本握手、模型清单/哈希和可取消的 readiness 超时。

**验收**

- 在没有作者缓存目录、没有系统 Python、没有 CUDA 的干净 Windows VM 上能启动或给出明确的受支持降级提示。
- 发布包包含启动本地 ASR 所需的脚本/可执行文件和依赖清单。
- 模型加载中不会显示“本地 ASR 在线”，加载失败不会只等待 60 秒后给笼统错误。

### [ ] P0-07 建立当前 Action POC 的正确性门禁

**证据**

- 当前动作标签由流式正则在 token 缓冲区中识别，动作随检测事件立即 fire-and-forget 触发，尚无端到端 exactly-once 证明。
- 文本标签兼容路径同时影响流式分句、TTS、字幕、历史与 VTS，任何边界遗漏都可能造成标签被朗读、显示、重复触发或在取消后迟到触发。

**实施**

- 用录制的 SSE 分块覆盖跨 token、同 chunk 多标签、标签紧邻标点、截断/畸形标签、重复标签和流中断。
- 限制动作别名的字符集、长度和数量；未知或恶意动作只记录一次结构化降级，不进入任意 VTS ID/文件路径。
- 将解析结果先写入不可变回复计划，再由当前 generation 消费；兼容标签层不得直接控制 VTS。
- 增加 feature flag/兼容开关，使 POC 可在不回退代码的情况下禁用并恢复纯文本回复。

**验收**

- 每个合法动作意图最多执行一次，非法/未知动作执行零次。
- 控制标签不会进入 TTS、字幕、对话历史、记忆提取或最终 UI 文本。
- 取消、断线、模型切换和热更新期间不存在迟到动作；失败可追踪且不会中断后续语音。

## P1：产品正确性、稳定性与安全

### [ ] P1-01 修复对话上下文和长期记忆链路

**证据**

- `ConversationManager.BuildMemoryContext` 只读取观众档案，注入的 `_factRepo` 从未使用。
- 弹幕调用 `ProcessTextAsync` 前没有写入对话历史；回复会进入历史，但对应用户消息不会。
- `ConversationManager.BuildMessages` 已加入 system prompt，`LlmClient.BuildMessages` 又加入一次，导致每轮重复发送。

**实施**

- 对当前输入做 embedding/检索，将有界、去重后的相关事实加入动态 memory system message。
- 将麦克风、内录、弹幕统一为带来源/用户 ID 的 conversation turn，并成对提交 user/assistant 历史。
- 只保留一个 system prompt 组装入口，静态 prompt 位于可缓存前缀。

**验收**

- 集成测试证明已保存事实能影响后续相关回复，且无关事实不会全部塞入上下文。
- 请求 payload 中静态 system prompt 恰好出现一次。
- 弹幕问答在后续轮次中保持成对历史，并受 token 上限约束。

### [ ] P1-02 修复 SQLite 生命周期和文件锁

**证据**

- `MemoryDb` 与多个 repository 共享长寿命 connection。
- 修正 VAD DLL 路径后，仍有 14 个测试因 SQLite teardown 文件锁失败。

**实施**

- 改为每操作短连接/连接工厂，启用池化、WAL 和 `busy_timeout`。
- 串行化 schema migration 与必要写入；所有 command/reader/transaction 均及时释放。
- 测试使用每测试独立数据库目录，并等待所有后台任务结束后清理。

**验收**

- 测试连续运行 10 次无文件锁或 flaky 失败。
- 并发读写、关闭应用、热切换数据库路径均不丢数据。

### [ ] P1-03 让“虚拟麦克风”行为与文案一致

**证据**

- `VirtualMicMixer` 支持 `WriteMic` 和 `WriteTts`，配置注释也声明混合真人麦克风与 TTS。
- `BotRuntime.StartAudio` 明确没有把真人麦克风写入 mixer，实际只输出 TTS。

**实施**

- 二选一并明确产品语义：真正混合 mic + TTS，或将功能重命名为“AI 语音输出到虚拟设备”。
- 增加独立增益、静音、防反馈和设备断开处理。

**验收**

- 录制虚拟设备输出，验证所选语义下的声道、音量、静音和无回授行为。
- UI、配置字段、README 与实际音频路由一致。

### [ ] P1-04 完整实现 OBS WebSocket 生命周期

**证据**

- `BotRuntime.ObsConnected` 只判断 `_obs is not null`，不能代表认证连接成功。
- `ObsClient` 握手后没有持续 receive loop、请求响应关联、发送锁、心跳或重连；请求失败也无法反馈到调用方。

**实施**

- 使用真实连接状态机：Disabled / Connecting / Connected / Reconnecting / Failed。
- 补齐单发送者、receive loop、requestId correlation、超时、关闭检测和有界重连。
- 评估 `OBSWebsocketDotNet` v5；若保留手写实现，上述协议能力必须有测试。

**验收**

- 密码错误、OBS 重启、网络中断、组件名错误都显示准确状态和恢复动作。
- 并发字幕更新不会触发 WebSocket 多写竞争，失败请求可追踪。

### [ ] P1-05 默认保护日志与本地入口

**证据**

- `DebugLog.Enabled` 默认为 `true`，完整 ASR/LLM 文本被追加到 `audio_debug.log`，没有轮转或保留期限。
- 弹幕 `HttpListener` 没有 token、Content-Type/方法校验、请求大小和并发限制。

**实施**

- 默认关闭内容日志；区分诊断元数据和私人文本，敏感字段统一脱敏。
- 使用滚动文件、大小/天数上限和 UI 中明确的诊断开关。
- 弹幕入口只接受 POST + JSON，增加启动时随机 token、最大 body、超时和并发上限。

**验收**

- 默认运行不会把对话、Cookie、API key 或 OBS 密码写入日志。
- 超大请求、错误方法、错误 token 和畸形 JSON 被快速拒绝且不影响主流水线。

### [ ] P1-06 拆分 `BotRuntime` 的生命周期职责

**证据**

- `BotRuntime` 同时管理数据库、模型、音频、provider、OBS/VTS、Python 进程、热更新和 UI 事件，重建顺序难以验证。
- 多个客户端自行 `new HttpClient`，代理、超时、测试 handler 和遥测策略分散。

**实施**

- 使用 Generic Host/DI 作为 composition root，按边界拆为 coordinator、audio session、integration supervisor、memory service。
- HTTP provider 改为 typed `HttpClient`；只对幂等请求采用有界重试，流式请求不盲目重试。
- 为各边界注入 fake transport/device，避免单元测试依赖真实硬件和网络。

**验收**

- 每个服务能独立启动、停止、失败和测试；应用退出时无后台任务或进程残留。
- `BotOrchestrator`/coordinator 的关键并发路径有自动化测试，行覆盖率不再为 0%。

### [ ] P1-07 安全持久化配置、凭据和配置迁移

**证据**

- `ConfigManager.Save` 直接 `File.WriteAllText` 覆盖完整配置；进程崩溃或磁盘写入中断可能留下半写 JSON。
- LLM/ASR/TTS API key、B站 Cookie 和 OBS 密码与普通配置一起明文保存。
- 当前只有单个 `use_loopback` 兼容分支，没有统一 `schema_version`、顺序迁移、备份或未来版本拒绝策略。

**实施**

- 为配置增加 `schema_version` 和可测试的逐版本迁移；遇到更高未知版本时只读打开并提示，不静默覆盖。
- 普通设置与秘密分离；Windows 首选 DPAPI/Credential Manager，配置中只保存秘密引用，诊断导出默认脱敏。
- 首次加载旧明文配置时完成一次性秘密迁移，之后普通配置、备份和 revision 历史都不再写回明文。
- 使用同目录临时文件、flush、原子 replace 和 `.bak`；损坏时提供验证、备份恢复和“重置但保留旧文件”。
- 明确用户数据目录、便携模式和文件权限，发布升级不能覆盖用户配置或 `memory.db`。

**验收**

- 在保存的任意阶段强制终止进程，重启后只能读到完整旧版本或完整新版本。
- 代表性旧配置可无损迁移，包含现有 `emotion_map`/`action_map`；迁移重复执行保持幂等。
- config、模板、日志、诊断包和崩溃报告均不出现明文凭据。

## P1：UI/UX 重做

### [ ] UX-01 建立应用级设计 token 和主题

**证据**

- `App/App.xaml` 只有暗色主题和控件资源，没有统一字号、间距、圆角、表面和语义色。
- 页面散落硬编码 FontSize、Margin 和功能色。

**实施**

- 新增 `App/Resources/`，定义 4/8px 间距体系、12/14/16/20 字级、固定控件高度、图标尺寸和不超过 8px 的圆角。
- 定义 Success / Warning / Error / Info 语义色以及不同表面层级；页面禁止直接写功能色 hex。
- 跟随系统亮/暗主题，并允许手动切换。

**验收**

- 三个页面全部使用 token；亮色和暗色均完成对比度检查。
- 760x520、980x680、1440x900 三种窗口截图无文字裁切、控件重叠或不可达操作。

### [ ] UX-02 将设置页从超长表单改为分类设置

**证据**

- `ConfigView.xaml` 约 377 行，将 LLM、音频、TTS、ASR、VTS、OBS、B站等全部纵向铺开。
- 内容固定 `MaxWidth=640`；保存按钮位于页面最底部，宽屏浪费空间且长滚动后才能保存。

**实施**

- 将“设置”移到 `NavigationView.FooterMenuItems`。
- 分为：快速设置、AI 引擎、语音、直播集成、角色与动作、高级；使用左侧分区导航或 `TabView`。
- 使用固定可见的保存栏，提供未保存、保存中、已应用、应用失败和放弃修改状态。
- 切换分区不能丢失草稿；离开页面时提示未保存修改。

**验收**

- 常用配置在两层导航内可达，无需滚过无关 provider 字段。
- `Ctrl+S` 保存、`Esc` 放弃/关闭；保存状态不会被底部小字替代。

### [ ] UX-03 使用语义控件、渐进披露和就地校验

**实施**

- Provider 使用 `ComboBox`，布尔值使用 `ToggleSwitch`，端口/索引/语速/VAD 使用带范围的 `NumberBox`。
- 只显示当前 provider 和已启用集成的字段；高级字段用 `CardExpander`。
- 密钥和 Cookie 使用可显隐的密码输入，默认遮罩。
- 错误显示在对应字段附近，并阻止无效配置保存；不要只在底部显示原始异常。

**验收**

- 所有数字字段都有范围、单位和边界测试。
- 切换 provider/开关不会清空已有草稿；无效字段可通过键盘直接定位。

### [ ] UX-04 补齐配置覆盖面和连接测试

**证据**

- 当前 UI 未完整覆盖 `ObsConfig` 和 `MemoryConfig`，部分功能仍需手改 JSON。

**实施**

- 每个 `AppConfig` 字段必须有 UI，或明确归入“高级 JSON”并解释用途。
- 为 LLM、ASR、TTS、VTS、OBS、B站增加“使用当前草稿测试连接”。
- 测试状态使用 `ProgressRing` + `InfoBar`，给出成功、失败原因和可执行恢复动作。

**验收**

- 用户无需编辑 JSON 即可完成正常配置。
- 连接测试不持久化草稿、不污染正式客户端，并能取消和防止重复触发。

### [ ] UX-05 将监控页改为稳定的直播运行台

**证据**

- `MonitorView.xaml` 顶部单行同时塞入流水线、四个连接、ASR 重启、两个电平条、打断和静音，窄窗无法合理换行。
- 动态出现的“打断”按钮会推动其他控件；绿/灰圆点无法区分关闭、未配置、连接中和失败。

**实施**

- 顶部固定主控制：运行状态、静音、打断；按钮位置不随状态变化。
- 第二行显示 VTS/OBS/ASR/弹幕的具名状态，图标、文字和颜色共同传义。
- 主体左侧为对话/事件时间线，右侧为弹幕队列和音频电平；原始诊断放到可展开区域。
- 小于约 1000px 时右栏移到下方或 Tab，导航自动收窄。

**验收**

- 状态覆盖 Disabled / Connecting / Online / Degraded / Failed，并与真实服务状态一致。
- 760px 宽不裁切；状态变化不会引起主操作位置跳动。

### [ ] UX-06 将对话、弹幕和诊断改为可滚动运营流

**实施**

- 消息显示来源、用户、时间和处理状态；保留有界历史。
- 默认自动滚到最新，用户向上滚动后暂停跟随，并提供“回到最新”。
- 弹幕使用虚拟化列表并独立滚动；长用户名和长文本不会撑坏布局。
- 错误使用可关闭/重试的 `InfoBar`，短暂成功使用 `Snackbar`。

**验收**

- 10,000 条模拟事件不会导致列表无限增长或明显卡顿。
- 用户查看旧消息时不会被新事件强制拉回底部。

### [ ] UX-07 重做记忆页

**证据**

- 当前通过按钮切换事实/观众，但没有明确选中状态，搜索文案也不会随上下文变化。
- ListBox + Card 模拟表格，不能排序；删除缺少确认/撤销；搜索每次输入立即刷新且没有取消。

**实施**

- 将 `MemoryPlaceholderView` 改名为 `MemoryView`，使用 `TabView` 区分事实记忆和观众档案。
- 主体使用 `DataGrid`，支持排序、选择、空状态和加载状态；详情编辑放在侧栏/对话框。
- 搜索 debounce 250-300ms，取消或丢弃过期结果。
- 删除使用危险样式 `SymbolIcon` 按钮，并提供 `ContentDialog` 确认或 `Snackbar` 撤销。
- 移除 `async void + Task.Delay(200)` 的刷新流程。

**验收**

- 两个 Tab 拥有各自的搜索、过滤、操作和空状态。
- 快速输入、切 Tab、删除后刷新都不会显示过期结果或重复执行。

### [ ] UX-08 补齐可访问性和键盘交互

**实施**

- 所有图标按钮都有 Tooltip 和 `AutomationProperties.Name`。
- 连接状态不只依赖颜色；正文不小于 12px；焦点样式清晰。
- 设置合理 Tab 顺序、访问键和命令快捷键；耗时操作期间阻止重复提交。

**验收**

- 仅使用键盘可完成启动、静音、打断、保存配置、搜索和删除确认。
- Windows Narrator 能读出按钮用途、字段错误和连接状态。

### [ ] UX-09 重做首次启动与环境检查

**证据**

- 当前首次配置仍依赖字段密集的设置页/旧式向导，用户需要自行理解 LLM、ASR、TTS、音频设备、VTS、OBS 和 Python 的依赖关系。

**实施**

- 使用 WPF-UI 页面或 `ContentDialog` 建立可跳过、可返回的分步向导：AI、语音、音频、Avatar、直播集成、完成检查。
- 自动枚举音频设备，检查 VTS/OBS/sidecar，使用当前草稿做连接测试并给出具体恢复步骤。
- 提供“最小可运行”和“完整直播”两条路径；高级字段默认折叠，不要求首次启动一次配完所有集成。
- 完成后生成本机能力摘要，随时可从设置重新运行，不通过控制台提示输入秘密。

**验收**

- 干净 Windows 用户无需手改 JSON 或打开终端即可完成一轮文字回复和一轮语音播放。
- VTS 未安装、未授权、未加载模型或未映射参数时，向导能准确区分并给出下一步。

### [ ] UX-10 增加现代“角色与动作”编辑器

**实施**

- 使用 `DataGrid`/`TabView` 管理 Emotion、Gesture、Motion 和参数映射，支持搜索、类型过滤、排序、批量导入和空状态。
- 显示当前 VTS 模型、热键类型、目标文件、映射状态和最后验证时间；模型切换后用 `InfoBar` 标记陈旧映射。
- 每项支持别名校验、逐项预览、强度/时长/冷却/优先级/可中断设置以及恢复 neutral 策略。
- 提供“让 AI 试一次”诊断：展示 LLM 结构化意图、最终绑定、调度时间和 VTS 响应，不暴露内部 ID 给 LLM。
- 连接、查询和预览必须使用当前未保存草稿创建一次性 VTS session，不能继续操作 runtime 的旧 Host/Port；离开页面时完整释放。
- 测试台提供 stop-all、neutral 和 close-mouth 急停；预览不得写入正式动作队列、历史或 LLM 上下文。
- 危险批量覆盖使用 `ContentDialog`；保存/导入结果使用 `Snackbar`，长任务使用 `ProgressRing` 且可取消。

**验收**

- 作者能在一个页面完成发现模型能力、预览现有 Motion/Expression、建立语义别名、保存并验证触发。
- 大小写等价的重复别名、失效 hotkey、错误类型、旧模型映射和不可取消 Motion 都有就地提示，不能 last-wins 或静默保存为“可用”。

## P1：Avatar 动作与表现力

> 选型：第一阶段继续使用 VTube Studio Plugin API，不直接集成 Cubism SDK。
> 当前工作树保留了一个最小 POC：`action_map`、`[action:动作名]` 标签、TriggerAnimation 热键导入和白名单触发。它只证明“LLM 能选择并触发已有 VTS 动作”，不代表下面的动作系统已经完成。

**当前 POC 的发布阻塞**

- 必须先完成 `P0-02`：首次保存后 UI 草稿与 Runtime 配置发生引用别名，第二次修改 `ActionMap` 可能不产生 diff，LLM 提示词和动作映射不会刷新。
- 必须先完成 `P0-05`：`ActionMap` 变化会重建 `BotOrchestrator`，旧实例未解除 `AudioPlayer` 事件订阅，连续保存会让口型/闭嘴调用倍增并泄漏实例。
- 当前标签在 LLM token 到达时触发，尚未与真实 TTS 播放时间轴同步；快速多个动作的顺序、取消和冷却也未完成。
- 当前“导入动画热键”依赖最近一次查询结果；正式版本必须处理模型切换、热键变化和陈旧缓存。

### [ ] AVATAR-01 定义 Avatar 后端边界和能力模型

**实施**

- 定义 `IAvatarBackend`，将 LLM/语音流水线与 VTS/Cubism 渲染后端解耦。
- 第一实现为 `VtsAvatarBackend`；能力模型至少包含 Parameter、Expression、Hotkey、Motion 和模型标识。
- 不将完整 `model3.json` 塞给 LLM；连接 VTS 后生成经过裁剪的 `AvatarCapabilities`。

**验收**

- 上层动作导演不依赖 `VtsClient` 具体类型。
- 后端不支持某能力时返回结构化降级结果，而不是静默失败。
- 未来增加 Unity/Cubism renderer 时无需修改 LLM 动作协议。

### [ ] AVATAR-02 自动发现并缓存当前模型能力

**实施**

- 连接后查询当前模型、Input Parameters、Live2D Parameters、Expressions 和 Hotkeys。
- 区分 `TriggerAnimation`、`ToggleExpression` 等热键类型，并保留 `file`、名称、ID 和范围。
- 订阅模型加载/配置变化事件；模型切换后使旧缓存失效并重新发现。
- 设置页显示能力检查：头部 XYZ、身体、视线、眉眼、嘴型、Expression、Motion 是否可用。
- 对自建 tracking parameter 分别显示“参数存在 / 当前模型已映射 / 注入验证成功”，不能只用 WebSocket 已连接代替模型可用。

**验收**

- 更换 VTS 模型后 2 秒内刷新能力，不会触发旧模型热键。
- 缺少映射时 UI 明确指出“模型未 rig”还是“VTS 未映射”。
- 能力清单有大小上限，不包含纹理、ArtMesh 或无关原始 JSON。

### [ ] AVATAR-03 用结构化动作协议替代自由文本舞台描述

**实施**

- 正式协议包含 `speech`、`emotion`、`gestures[]`、`at_ms`、`duration_ms`、`intensity`、`priority`。
- 首选 provider 支持的 structured output/tool call；文本标签只保留为兼容降级。
- 动作名必须来自服务端白名单；未知动作被拒绝并记录一次诊断，不直接访问任意热键。
- 解析与 TTS/字幕分离，控制标签永远不能被朗读或显示。
- 将解析结果构造成不可变 `UtterancePlan`/`SpeechSegment`；每段文本持有自己的 emotion/gestures，不再依赖全局 `_currentEmotion`。

**验收**

- 覆盖标签跨 token、标签与标点相邻、重复标签、未知动作、截断标签和恶意动作名测试。
- 一条动作意图最多执行一次；被取消的旧回复不能继续触发动作。
- LLM 不需要知道 hotkey ID、文件路径或 Live2D 参数 ID。

### [ ] AVATAR-04 实现 `AvatarMotionDirector` 和分层混合

**实施**

- 本地 20-30 Hz 调度 `Idle + Speech + Emotion + Gesture` 四类轨道，LLM 不生成逐帧数值。
- 支持 easing、blend-in/out、幅度限制、动作冷却、优先级、打断、取消和自然回中。
- 点头/摇头/侧头使用连续参数曲线；复杂大动作使用 `.motion3.json` 热键。
- 同参数冲突必须有确定仲裁规则；VTS `set/add/weight` 策略配置化。
- 按能力区分可取消参数手势与不可保证中断的 VTS Motion；不可取消 Motion 只能阻止未开始任务，或使用模型配置的 stop/neutral hotkey 收尾。

**验收**

- 摇头、点头、侧头结束后平滑回中，没有跳变或永久占用 tracking 参数。
- 打断回复后 100ms 内取消所有未开始动作；标记为 `CanCancel` 的当前动作平滑停止，不可取消 Motion 按绑定的收尾策略处理。
- 运行 1 小时无动作队列无限增长、WebSocket 洪泛或明显漂移。

### [ ] AVATAR-05 增加自然 Idle 行为

**实施**

- 呼吸、随机眨眼、微眼动、视线停留和轻微身体摆动由本地确定性行为生成。
- 使用带种子的随机与最小/最大间隔，避免固定节拍和高频抖动。
- 说话、思考、倾听和静默状态使用不同的 Idle profile。
- 检测并避免与 VTS 自带眨眼、呼吸、摄像头 tracking 和模型 physics 双重驱动同一参数。

**验收**

- 不调用 LLM 时模型仍有自然但克制的生命感。
- 眨眼、呼吸和视线不会与 Expression/Motion 抢同一参数造成闪烁。
- 可在设置中单独调整强度或关闭，并提供一键恢复默认值。

### [ ] AVATAR-06 完整管理 Expression 生命周期

**实施**

- 查询可用 `.exp3.json`，语义情绪映射到 Expression，而不是所有内容都用热键 Toggle。
- 记录当前 active expression；支持淡入、持续、淡出、互斥组和 neutral 恢复。
- 同一次情绪判断同时驱动 TTS style、Expression 和姿态，避免额外情绪 LLM 请求。
- 使用 VTS Expression 状态/激活接口同步真实状态；用户手动切换、模型重载和重连后重新对账，不把本地缓存当真相。

**验收**

- 每个临时表情都能可靠关闭；取消或断线后不会把模型永久留在哭/怒状态。
- 表情切换无明显闪跳，未知 Expression 自动降级为 neutral。

### [ ] AVATAR-07 将口型从线性 RMS 升级为平滑语音驱动

**实施**

- 第一阶段增加 noise gate、归一化、attack/release、动态范围压缩和静音归零。
- 支持 `MouthOpenY + MouthForm`；provider 有音素/时间戳时评估 viseme 映射。
- 口型时钟与实际播放器时钟对齐，不以 LLM token 到达时间驱动。

**验收**

- 静音时嘴巴闭合；爆音不会瞬间满开；句尾能自然收口。
- 不同 TTS 音量下观感一致，音画偏差可测且稳定。

### [ ] AVATAR-08 建立模型制作与 VTS 配置契约

**实施**

- 文档列出建议 rig：Head/Body XYZ、Eye XY、Eye/Brow、Mouth Open/Form、Breath。
- 约定 Expression 和 Motion 的语义名称、互斥关系、推荐时长与回中行为。
- 提供 VTS 映射向导和动作测试面板，允许作者逐项试听/试播后加入白名单。
- 可选地在本地读取 `model3.json` 及其引用的 `.motion3.json`/`.exp3.json` 预填候选项，但以 VTS 当前模型和 hotkey API 为运行时真相；原始模型 JSON 不发送给 LLM。

**验收**

- 新模型无需改代码，仅通过 VTS/应用映射即可完成基础动作接入。
- 测试面板能逐个触发动作、表情和参数，并显示真实 API 错误。

### [ ] AVATAR-09 建立语句计划与真实播放时间轴

**实施**

- 流式 LLM 只产出有序 `UtterancePlan`，TTS 队列保留每个 `SpeechSegment` 自己的情绪、动作和 generation。
- `at_ms` 相对实际 `AudioPlayer` 播放起点调度；考虑首包缓冲、分段 TTS、设备延迟、暂停、打断和丢帧。
- 没有音素/字级时间戳时使用句段播放时钟和可配置锚点降级，不以 token 到达时间触发。
- 保持首句流式低延迟：计划可增量封口并进入 TTS，不等待整段回复结束。

**验收**

- 连续两句使用不同情绪/动作时互不串线，排队句不会拿到后一段的全局情绪。
- fake playback clock 下动作时间误差可量化；取消后时间轴停止推进且不会补触发。
- 与当前 POC 相比首句开播延迟没有明显回退，并记录 p50/p95。

### [ ] AVATAR-10 将简单映射升级为按模型保存的富动作绑定

**实施**

- 将 `Dictionary<string,string>` 演进为版本化 `ActionBinding`：`alias`、`backend`、`kind`、`targetId`、`modelId`、`duration`、`cooldown`、`priority`、`interruptible`、`returnToNeutral`。
- 按 VTS `modelID` 和能力签名保存 profile；同名动作在不同模型可绑定不同 Motion、Expression 或参数曲线。
- 为现有 `emotion_map`/`action_map` 提供无损迁移和兼容读取，迁移后仍可回退到旧版只读展示。
- 运行时只接收验证后的不可变绑定快照；UI 草稿、持久化 profile 和调度器快照互不共享可变集合。

**验收**

- 切换模型不会触发另一模型的 hotkey；切回后恢复各自映射。
- 旧配置升级后现有动作仍可触发，未知字段不丢失，重复迁移不改变结果。

### [ ] AVATAR-11 强化 VTS 连接生命周期、鉴权和注入吞吐

**证据**

- 当前每次连接都重新申请 token，receive loop 未被持有/等待，断线时 pending request 不会统一失败，且没有自动重连状态机。
- 高频参数通过单值 fire-and-forget 请求发送；增加头、眼、身体和嘴型后容易产生陈旧帧与 WebSocket 洪泛。

**实施**

- 持久化并优先复用 VTS auth token；仅在失效/拒绝时重新授权，明确 WaitingForApproval 状态。
- 实现 Disabled / Connecting / WaitingForApproval / Connected / Reconnecting / Failed 状态机和有界退避；持有并监督 receive loop。
- 断线/Dispose 时取消并完成全部 pending request；APIError 转为带 requestID/messageType 的失败结果。
- 定义 typed `VtsApiException`；只有明确的 Parameter AlreadyExists 等幂等错误可以白名单忽略，授权拒绝、权限错误和失效 hotkey 必须返回调用方。
- 参数更新按帧合并为单个 `InjectParameterDataRequest`，限制 20-30 Hz，丢弃过时帧而不堆积；热键命令与参数帧使用有界优先队列。
- 订阅模型加载和配置变化事件，触发能力缓存与 profile 对账。

**验收**

- VTS 重启、拒绝授权、token 失效、模型切换和网络抖动均能恢复或给出明确失败状态。
- 30 分钟多参数驱动无并发 Send、无 pending 泄漏、无无界队列；实际注入频率和丢帧数可观测。

### [ ] AVATAR-12 定义 VTS 参数所有权和 tracking/physics 混合规则

**实施**

- 为头、身体、眼睛、眉毛、嘴型和呼吸定义 AIVTuber 自建 tracking parameters，并在 VTS 中映射到模型参数。
- 为每个参数声明 owner、模式（set/add/weight）、范围、平滑、超时和 neutral；同一帧冲突使用确定性仲裁。
- 明确与摄像头 tracking、鼠标/手机 tracking、VTS 自动眨眼/呼吸和 Live2D physics 的共存策略。
- 断线、暂停、动作结束和应用退出时释放参数所有权并平滑回中。

**验收**

- 开启摄像头 tracking 时 AI 手势不会永久夺走头部控制，也不会产生明显双重抖动。
- 任一轨道崩溃或停止后，所有被注入参数在限定时间内回到 neutral/交还 tracking。

### [ ] AVATAR-13 管理 Motion 的可取消能力与断线恢复

**实施**

- 对参数曲线、Expression、TriggerAnimation 热键分别标注 `CanCancel`、预计时长和收尾策略。
- 对不可取消 Motion 支持可选 stop/neutral hotkey、冷却和互斥组；没有 stop 能力时只阻止后续冲突动作。
- 重连或模型重载后查询真实 Expression/Hotkey 状态，清理本地 active 队列，不盲目重放断线前动作。
- 用户手动触发 VTS 热键时允许配置“尊重人工操作”窗口，暂时降低 AI 动作优先级。

**验收**

- 可取消动作能平滑停止；不可取消动作不会被错误报告为已停止，也不会与后续互斥动作叠加。
- 断线恢复不重播旧动作，人工表情/动作不会立即被 AI 抢回。

### [ ] AVATAR-14 增加本地动作策略与安全降级

**实施**

- LLM 只负责高层语义意图；说话微点头、句尾回中、倾听、思考和轻微视线变化由本地策略生成。
- 当 LLM 未输出动作、输出未知动作或 provider 不支持 structured output 时，根据 emotion、标点、语音韵律和当前状态选择安全微动作。
- 使用概率、冷却、近期动作记忆和强度预算避免每句都动、重复同一 Motion 或过度摇晃。
- 提供全局强度、动作频率、敏感动作禁用和“一键回中/紧急停止”。

**验收**

- 关闭 LLM 动作输出后角色仍有克制的说话/倾听表现，但不会随机触发大幅预制动作。
- 固定种子回放结果确定；一小时运行中动作频率、重复率和强度不超过配置预算。

### [ ] AVATAR-15 评估 Cubism renderer 第二后端（暂缓）

仅在明确要求“不安装 VTS、应用内渲染、Drawable/物理/合成级控制”时启动。

**进入条件**

- `VtsAvatarBackend + AvatarMotionDirector` 已完成且证明确有 VTS API 无法满足的需求。
- 已完成 Live2D Publication License/AI chatbot 产品许可书面确认。
- 已接受独立 Unity/Cubism renderer、透明渲染、动作/物理混合、DPI 和设备丢失的维护成本。

**验收**

- 与 VTS 后端使用同一 `AvatarIntent`/能力协议。
- 后端切换不改变 ASR/LLM/TTS 和直播业务逻辑。

## P2：工程治理与交付

### [ ] P2-01 固定 SDK 和全部包版本

- 新增 `global.json` 固定 .NET 10 feature band。
- 使用精确版本或 `Directory.Packages.props` 集中管理；禁止 `WPF-UI 3.*` 这类浮动版本。
- 在完成 UI 回归后升级并固定 WPF-UI 4.3.0；评估 NAudio 2.3.0、WebRtcVadSharp 1.3.2 和新 ONNX Runtime。
- 删除未引用的 `Websocket.Client 3.0.15`。
- **验收：**干净机器与 CI 还原到相同依赖图，lock file 无意外变化。

### [ ] P2-02 修复并强化测试套件

- 将 WebRtcVad 原生 DLL 可靠复制到测试和发布目录，不依赖手工修正搜索路径。
- 修复 SQLite teardown 锁并消除 14 个失败；测试连续运行 10 次。
- 优先补 `ConfigDiff`、配置快照、coordinator 代际取消、热更新生命周期、OBS 状态机和 sidecar readiness 测试。
- **验收：**151/151 当前测试通过；新增关键路径测试稳定通过，无跳过的发布阻塞测试。

### [ ] P2-03 将 CI 变成 main/PR 发布门禁

**证据**

- `.github/workflows/build-windows.yml` 只监听 `feature/wpf-monitor`，不监听 `main` 或 pull request。
- CI 只 build/publish，不执行测试、格式检查或依赖漏洞审计。

**实施与验收**

- 在 `main` push 和 PR 上执行 restore、Release build、test、漏洞审计和 publish smoke test。
- artifact 启动冒烟必须验证原生 DLL、配置模板、Python sidecar/依赖清单均存在。
- 只有 tag 构建发布 release；普通分支构建不得覆盖固定 prerelease tag。

### [ ] P2-04 修复发布模型和文档漂移

**证据**

- `README.txt` 使用错误的 `AIVTuber.App.exe`，`publish.sh` 又提示 `App.exe`；实际 AssemblyName 是 `AIVTuber`。
- `publish.sh` 的 TFM 输出路径与 `net10.0-windows` 不一致。
- `PublishSingleFile` 与原生 DLL、Python、模型/分词器并不等于“单文件完整产品”。

**实施与验收**

- 统一产物名、路径和快速开始说明，发布脚本从 MSBuild 输出获取路径而非手写。
- 发布版本化目录/zip，或评估 WiX/MSIX；模型可首启下载，但必须校验版本与哈希。
- 在干净 Windows VM 按 README 从零完成首次启动、连接测试和一轮回复。

### [ ] P2-05 建立可控的结构化诊断

- 用日志级别、event id 和 operation/generation id 替代散落的字符串追加。
- 记录延迟、状态转换和错误类别，默认不记录原始对话与凭据。
- UI 提供一键导出脱敏诊断包，包含版本、配置摘要、依赖和最近错误。
- **验收：**一次失败回复可以关联 ASR -> LLM -> TTS -> Audio/OBS/VTS，全链路不泄露私人内容。

### [ ] P2-06 建立 Avatar 集成测试与确定性回放基座

- 实现 fake VTS WebSocket server，覆盖授权、APIError、乱序/超时响应、超过 8KB 的分片消息、慢消费、断线重连、模型切换和 hotkey/参数请求记录。
- 使用录制 SSE 分块、fake TTS、fake `AudioPlayer` clock 和可控随机源，端到端验证计划、顺序、取消、exactly-once 和回中。
- 为模型 profile、ActionBinding 迁移、能力缓存失效和参数仲裁建立契约测试。
- CI 不依赖真人 VTS、音频设备或网络即可运行 Avatar 主路径。
- **验收：**同一 trace 在本地和 CI 生成一致动作序列；重放 100 次无 flaky、无真实等待和无残留后台任务。

### [ ] P2-07 将“自然度”变成可观测、可回归的质量指标

- 记录 intent -> audio start -> action start 延迟、调度抖动、参数注入 Hz、队列长度、丢弃/降级/冲突次数和回中耗时。
- 建立固定文本、固定音频、固定模型 profile 的 deterministic replay；保存参数曲线/动作事件 golden trace。
- 对关键场景保留短录屏基线：静默、倾听、普通说话、兴奋、否定摇头、打断、断线恢复。
- 设定表现预算而非只看“能触发”：大动作频率、重复率、最大头部幅度、表情滞留时间和嘴型音画偏差。
- **验收：**动作系统改动能自动比较 trace，并由人工只审查有限录屏差异；性能/自然度退化有明确阈值。

## 推荐执行顺序

1. P0-01 至 P0-07：先恢复发布安全、运行时正确性和当前 Action POC 的可控性。
2. AVATAR-01 至 AVATAR-03、AVATAR-09 至 AVATAR-11：先建立后端边界、能力清单、回复计划、富绑定和可靠 VTS 通道。
3. UX-01 + UX-10：建立设计 token，并先交付作者真正需要的角色与动作编辑器。
4. AVATAR-04 至 AVATAR-08、AVATAR-12 至 AVATAR-14：完成自然动作、表情、口型、参数混合和本地降级策略。
5. UX-02 至 UX-09：重做设置页、直播运行台、记忆页、首次启动和可访问性。
6. P1-01 至 P1-07：并行收敛记忆、连接、隐私、配置与生命周期。
7. P2：把前述验收固化到 CI、发布包、fake VTS、确定性回放和文档；AVATAR-15 保持暂缓。

## 参考

- WPF-UI：https://github.com/lepoco/wpfui
- WPF-UI 4.3.0：https://www.nuget.org/packages/WPF-UI/4.3.0
- .NET 10 WPF 更新：https://learn.microsoft.com/dotnet/desktop/wpf/whats-new/net100
- CommunityToolkit.Mvvm：https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/
- Windows App SDK 自包含部署：https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps
- Avalonia：https://docs.avaloniaui.net/docs/welcome
- VTube Studio Plugin API：https://github.com/DenchiSoft/VTubeStudio#api-details
- ZerolanLiveRobot Live2D 实现：https://github.com/AkagawaTsurunaki/ZerolanLiveRobot/tree/main/services/live2d
- Cubism SDK License：https://www.live2d.com/en/sdk/license/
