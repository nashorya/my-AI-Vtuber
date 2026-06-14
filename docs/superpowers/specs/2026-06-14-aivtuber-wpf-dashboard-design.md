# AIVTuber 可视化仪表盘 — 设计文档

- 日期：2026-06-14
- 状态：待评审
- 范围：为现有 AIVTuber .NET 后端增加桌面可视化界面

## 1. 概述

当前 AIVTuber 是纯控制台 .NET 10 程序，流水线为
麦克风 → VAD → ASR → LLM → TTS → 播放 → VTS，外加 B站弹幕、OBS 字幕、SQLite 长期记忆。
本设计为它增加一个 **WPF 桌面仪表盘**，用于实时监控、配置编辑、记忆数据管理。

### 目标
- 实时监控流水线状态、对话、情绪、弹幕队列、延迟。
- 图形化编辑配置，**保存即生效**（无需重启进程）。
- 查看/搜索/编辑/删除长期记忆（事实 + 观众档案）。
- 打包为**单 exe**，可直接分发。

### 非目标（本期不做）
- 直播中的运行控制（启停/打断/静音按钮）。
- LLM tool calling / web search 的**实现**（独立特性，单独 spec）；本期仅在 UI 预留位。
- 跨平台（仅 Windows）。

## 2. 技术选型

| 决策 | 选择 | 理由 |
|------|------|------|
| UI 技术 | WPF（`net10.0-windows`, `UseWPF`, `WinExe`） | 全程 C#，复用现有 `PublishSingleFile + SelfContained + win-x64`，单 exe 分发；数据绑定适合实时面板 |
| UI 架构 | 轻量 MVVM（ViewModel + `INotifyPropertyChanged`） | 属性变更自动刷新界面，不引入 Prism 等重框架 |
| 配置生效 | 保存即热生效 | 轻量项秒生效；重型项就地重连约 1-2s；改动从下一次互动起作用 |

## 3. 架构

三层，自上而下：

```
WPF 窗口 (App, 新)         三个标签页 ViewModel ← 数据绑定 → View
        │
BotRuntime (Core, 新)      持有所有模块 / 暴露状态与事件 / ApplyConfigAsync
        │
现有 Core 模块 (不动)       Audio / Pipeline / Vts / Obs / LiveStream / Memory
```

### 3.1 BotRuntime（核心新增，位于 Core）
把现在散落在 `App/Program.cs` 静态字段 + 一次性 `Init*` 方法里的装配逻辑，收拢成一个可重建的运行时对象。

- **职责**：创建/持有所有模块；暴露状态（当前阶段、各连接状态、延迟）与转发事件；提供 `StartAsync` / `StopAsync` / `ApplyConfigAsync(AppConfig newConfig)`。
- **接口（UI 只依赖它）**：
  - 事件：`UserTranscript`、`SentenceReady`、`EmotionDetected`、`StateChanged`（阶段/连接/延迟）。
  - 方法：`StartAsync()`、`ApplyConfigAsync(newConfig)`、`StopAsync()`。
  - 属性：`CurrentConfig`、`PipelineState`、`ConnectionStates`、`DanmakuSelector`（供监控页读队列）。
- **依赖**：现有 Core 模块。**不依赖 WPF**，因此可单元测试，且控制台模式（如保留）也能复用。

### 3.2 WPF App（替换现有控制台 App 项目）
- `App.xaml` + `MainWindow.xaml`（TabControl：监控 / 配置 / 记忆）。
- `Program.cs` 现有逻辑迁移：装配进 `BotRuntime`，启动流程进 WPF App 启动事件。
- 首次运行（无 key）时默认打开「配置」页，替代现有 `ConfigWizard`。

### 3.3 现有 Core 模块
不改动接口。orchestrator 的 `OnUserTranscript / OnSentenceReady / OnAiStartSpeaking / OnAiStopSpeaking / OnEmotionDetected` 直接接入 `BotRuntime` 并转发给 ViewModel。
唯一新增：`FactRepository.DeleteAsync(id)`（记忆页删除用，现仅有 Insert/Update）。

## 4. 标签页设计

### 4.1 监控页（MonitorViewModel，只读）
- **状态灯**：当前阶段（在听/在想/在说）+ 各连接状态（VTS/OBS/B站）。
- **流水线阶段条**：麦克风→VAD→ASR→LLM→TTS→播放，当前步高亮。状态枚举**预留** `WebSearching` 一态（tool calling 用）。
- **实时对话**：用户转写 + AI 流式回复（打字机）。来自 `UserTranscript` / `SentenceReady`。
- **当前情绪**：来自 `EmotionDetected`。
- **弹幕队列**：读 `DanmakuSelector`，老粉带星标，按优先级排序，将被 TTL 淘汰的置灰。
- **延迟**：ASR、首句延迟（需 `BotRuntime` 埋点统计）。

### 4.2 配置页（ConfigViewModel）
- 按 `config.json` 分组铺成表单：LLM / 音频 / TTS / VTS / OBS / B站 / 记忆。
- 每组标注生效方式徽标：「秒生效」或「保存后重连 ~1-2s」。
- **预留**（禁用占位）：「工具 / 联网搜索」区——开关 + key 输入框，标注"即将支持"，不接逻辑。
- 底部「保存并应用」：写盘 `config.json` + 调 `BotRuntime.ApplyConfigAsync`。
- 状态行显示上次保存时间与应用结果。

### 4.3 记忆页（MemoryViewModel）
- 子切换：**事实记忆** / **观众档案**。
- 事实记忆：列表 + 搜索（走 `FactRepository.SearchAsync`，复用向量/字符串检索）+ 编辑内容/重要度（`UpdateContentAsync` / `UpdateWeightAsync`）+ 删除（新增 `DeleteAsync`）。按「重要度 × 时近度」(`Score`) 排序。
- 观众档案：读 `ViewerRepository`，展示互动次数、是否老粉。

## 5. 配置热生效机制

`ApplyConfigAsync(newConfig)` 对比 `CurrentConfig` 与 `newConfig`，**按模块分区，只重建变更的部分**，并重新挂接 orchestrator 的事件订阅。

| 配置项 | 生效方式 | 处理 |
|--------|----------|------|
| llm.* (base_url/key/model/system_prompt) | 轻量·秒 | 重建 `LlmClient`（纯内存对象，无 I/O） |
| tts.* / asr.* | 轻量·秒 | 重建对应 client；`voice_id` 为每次调用读取 |
| vts.emotion_map / mouth_scale | 轻量·秒 | 就地更新 `VtsConfig` |
| bilibili.selection_interval / 弹幕 TTL·上限 | 轻量·秒 | 重建 `DanmakuSelector` |
| obs 组件名 / typewriter / memory.extract_every_n | 轻量·秒 | 就地更新 |
| audio.input_device_index / use_loopback | 重型·重连 | 停止并重启 `MicrophoneCapture` + `VadDetector` |
| vts.host/port | 重型·重连 | 重连 `VtsClient`（WebSocket） |
| obs.enable/host/port/password | 重型·重连 | 重连/断开 `ObsClient` |
| bilibili.enable/room_id/cookies | 重型·重连 | 重启 `BilibiliDanmakuClient`（Python 桥） |
| memory.database_path / embedding_model_path | 重型·重连 | 重开 DB / 重载 `EmbeddingEngine` |

原则：仅创建内存对象 = 轻量；打开设备/socket/进程/文件 = 重型。改动一律「下一次互动」生效，不打断正在进行的那句。

## 6. 数据流

- **后端 → 界面**：模块事件 → `BotRuntime` 转发 → ViewModel 属性更新（经 `Dispatcher` 切到 UI 线程）→ 数据绑定刷新。
- **界面 → 后端**：配置保存 → `ApplyConfigAsync`；记忆增删改 → `FactRepository` / `ViewerRepository`。

## 7. 错误处理
- `ApplyConfigAsync` 中单个模块重建失败：捕获、保留旧实例、在配置页状态行报错，不让整个 runtime 崩。
- 连接类模块（VTS/OBS/B站）重连失败：状态灯显示断开，可重试，不阻塞其余流水线。
- 记忆 DB 操作失败：在记忆页提示，不影响直播流水线。

## 8. 测试策略
- **可单测**：`BotRuntime` 的配置差异分类（给定旧/新 config，断言哪些模块该重建）；ViewModel 的纯逻辑（排序、搜索过滤、状态映射）；新增 `FactRepository.DeleteAsync`。
- **手动/冒烟**：WPF 视图渲染、数据绑定刷新、热生效端到端（改配置→观察重连）。
- 现有 Core 测试保持通过。

## 9. 打包
- App 改为 WPF：`<TargetFramework>net10.0-windows</TargetFramework>`、`<UseWPF>true</UseWPF>`、`<OutputType>WinExe</OutputType>`，保留现有 `PublishSingleFile / SelfContained / win-x64`。
- `dotnet publish -c Release` 产出单 exe。

## 10. 实现阶段（建议顺序）
1. **Phase 0 — BotRuntime 重构**：把 `Program.cs` 装配迁入 `BotRuntime`，控制台仍可跑，加配置差异分类 + 单测。
2. **Phase 1 — WPF 骨架 + 监控页**：App 转 WPF，三标签壳，接事件，监控页实时刷新。
3. **Phase 2 — 配置页 + 热生效**：表单 + `ApplyConfigAsync` 接线 + 生效徽标。
4. **Phase 3 — 记忆页**：事实/观众列表、搜索、编辑、删除（含 `DeleteAsync`）。

## 11. 风险 / 待定
- 延迟埋点需在 `BotRuntime` 增加计时，可能小幅触及 orchestrator。
- 麦克风/连接类热重连的瞬态边界（重连过程中来了新语音）需在实现时定细节。
- Python 弹幕桥重启的健壮性。
