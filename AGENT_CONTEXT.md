# AIVTuber 项目背景与 Agent 交接说明

> 面向准备继续分析或实现本项目的 Agent。
> 功能基线：`main@b83adf4`，2026-07-10。
> 任务清单与优先级以 [`TODO.md`](TODO.md) 为准；本文负责解释背景、现状、已定决策和工作边界。

## 1. 项目一句话说明

AIVTuber 是一个 Windows 桌面 AI 虚拟主播后端：接收麦克风、系统内录或直播弹幕，经过 ASR、LLM、TTS 流水线生成回复，同时驱动 VTube Studio 模型、OBS 字幕、音频输出和本地长期记忆。

仓库地址：<https://github.com/nashorya/my-AI-Vtuber>

项目作者就是当前用户。可以直接从产品长期维护角度提出架构调整，但不要把实验性方案误当成已确认需求。

## 2. 当前最重要的产品目标

### 2.1 让 Avatar 不再僵硬

当前模型本身已经制作了对应 Motion/Expression，但原实现主要只有：

- 音频 RMS 驱动一个嘴型参数；
- LLM 情绪标签触发 VTS 热键；
- 缺少可靠的动作选择、时间轴、连续头部姿态、视线、表情生命周期和自然 Idle。

用户期望 AI 能更细致地控制模型，例如：

- 点头、摇头、侧头；
- 情绪表情和姿态；
- 说话、倾听、思考、静默时不同的自然微动作；
- 已有 `.motion3.json` 动作能由 LLM 在合适的语义和时间点触发。

### 2.2 重做当前较丑且拥挤的 UI

目标不是更换整个桌面框架，而是在现有 WPF 技术栈上建立现代 Fluent 风格、稳定布局、明确状态和适合直播期间操作的交互。

### 2.3 先形成可审阅 TODO，再逐项实现

用户明确要求先把问题、方案和验收拆进 TODO。此前 Action 功能被提前做成了一个最小 POC，用户要求 **保留、不回退**，但这不代表 POC 已达到正式质量。

后续工作应先对应到 `TODO.md` 的任务 ID，再做小范围实现、测试和验证。不要因为 POC 已存在就跳过其中的 P0 门禁。

## 3. 当前主流程

```text
麦克风 / 系统内录 / B站弹幕 / 手动输入
                  ↓
          ASR 或弹幕选择器
                  ↓
 ConversationManager + 部分 Memory（facts 链路待修）
                  ↓
             流式 LLM
                  ↓
       分句 / 情绪与动作意图
                  ↓
             流式 TTS
                  ↓
             AudioPlayer
        ↙           ↓            ↘
   VTS 口型/动作   OBS 字幕    虚拟音频设备
```

当前 `BotRuntime` 是总 composition root，负责创建、连接、热更新和释放绝大多数模块；`BotOrchestrator` 负责单轮 ASR -> LLM -> TTS 编排。两者职责都偏重，生命周期和并发问题已经列入 TODO。

注意：对话历史和部分观众数据已经存在，但长期事实记忆尚未完整注入正常回复上下文，不能把 Memory 描述成已全部接通；见 `P1-01`。

## 4. 技术栈与已定选型

| 领域 | 当前选择 | 决策 |
| --- | --- | --- |
| 运行时 | .NET 10 | 保留，但需要 `global.json` 和固定依赖版本 |
| 桌面 UI | WPF + WPF-UI | 保留，不迁移 WinUI 3/Avalonia/MAUI |
| 音频 | NAudio + WebRtcVadSharp | 保留，修复原生 DLL 交付和生命周期 |
| 数据 | Microsoft.Data.Sqlite | 保留，立即修复高危传递依赖和文件锁 |
| 本地推理 | ONNX Runtime | 保留，用于向量记忆等能力 |
| WebSocket | `ClientWebSocket` | VTS/自定义协议继续使用；OBS 可另评估成熟 v5 SDK |
| Avatar 后端 | VTube Studio Plugin API | 第一阶段唯一主路线 |
| 直接 Cubism | 暂缓 | 只有 VTS 明确无法满足渲染级需求时再评估 |
| Python | ASR/弹幕 sidecar | 能力保留，但不能依赖用户随意安装的系统 Python |

### UI 组件方向

继续使用 WPF-UI 的 Fluent 控件：

- `NavigationView`、`TabView`、`CardExpander`；
- `InfoBar`、`Snackbar`、`ProgressRing`；
- `ContentDialog`、`SymbolIcon`；
- `ComboBox`、`NumberBox`、`ToggleSwitch`、`AutoSuggestBox`；
- 数据型页面使用 `DataGrid`，不要用大量 Card 模拟表格。

不要同时引入 MaterialDesign、MahApps、HandyControl 等第二套全局主题。`CommunityToolkit.Mvvm` 可以作为 MVVM 辅助库评估，但不是为了改变视觉风格。

## 5. 仓库结构

| 路径 | 职责 |
| --- | --- |
| `AIVTuber.Core/Runtime/BotRuntime.cs` | 模块创建、连接、热更新、事件转发和总生命周期 |
| `AIVTuber.Core/Bot/BotOrchestrator.cs` | 单轮 ASR/LLM/TTS 编排、打断、VTS 事件桥接 |
| `AIVTuber.Core/Pipeline/` | LLM、ASR、TTS provider 与协议实现 |
| `AIVTuber.Core/Audio/` | 麦克风、回环、VAD、播放和虚拟麦克风 |
| `AIVTuber.Core/Vts/` | VTube Studio WebSocket 客户端、协议和 DTO |
| `AIVTuber.Core/Obs/` | OBS WebSocket 字幕客户端 |
| `AIVTuber.Core/LiveStream/` | B站弹幕入口与选择器 |
| `AIVTuber.Core/Memory/` | SQLite、事实/观众仓库、向量检索和记忆提取 |
| `AIVTuber.Core/Config/` | `AppConfig`、JSON 加载保存和兼容逻辑 |
| `AIVTuber.Core/ViewModels/` | 设置、监控和记忆页面 ViewModel |
| `App/` | WPF 应用、主窗口和三个主要页面 |
| `AIVTuber.Tests/` | xUnit 测试 |
| `asr_server.py` | 本地 ASR 服务原型，目前尚未达到可发布 sidecar 标准 |
| `danmaku_bridge.py` | B站弹幕 Python 桥 |
| `TODO.md` | 经代码审查整理的正式工作清单 |

当前 UI 主要包含：

- `MonitorView`：直播运行状态、对话和控制；
- `ConfigView`：LLM/ASR/TTS/音频/VTS/OBS/B站等配置；
- `MemoryPlaceholderView`：事实记忆和观众档案。

## 6. Avatar 技术决策

### 6.1 为什么继续使用 VTube Studio

当前僵硬不是 VTS 的能力上限，而是项目还没有动作导演层。VTS API 已能提供：

- 自定义 tracking parameter 创建与批量注入；
- `set`、`add`、`weight` 混合；
- Expression 激活/停用；
- 触发已经在 VTS 中配置为 `TriggerAnimation` 的 Motion hotkey；
- 模型、参数、Expression、Hotkey 能力查询；
- 模型移动、物理等控制。

直接接入 Cubism Native/Unity 会额外承担渲染、透明窗口、D3D/OpenGL、物理混合、DPI、设备丢失和 Live2D Publication License 等成本。除非产品明确要求“不安装 VTS、应用内直接渲染或 Drawable 级控制”，否则不启动该路线。

### 6.2 LLM 负责语义，不负责逐帧参数

目标协议应类似：

```json
{
  "speech": "才不是这样呢。",
  "emotion": "annoyed",
  "gestures": [
    {
      "type": "head_shake",
      "at_ms": 250,
      "duration_ms": 900,
      "intensity": 0.45
    }
  ]
}
```

LLM 只选择经过白名单裁剪的语义动作。它不应该知道：

- VTS hotkey ID；
- `.motion3.json`/`.exp3.json` 文件路径；
- Live2D 参数 ID；
- 完整 `model3.json` 内容；
- 每一帧应该注入的数值。

本地 `AvatarMotionDirector` 负责以约 20-30 Hz 混合：

- `Idle`：呼吸、眨眼、微眼动、身体轻摆；
- `Speech`：嘴型、韵律和说话微动作；
- `Emotion`：Expression、眉眼、姿态；
- `Gesture`：点头、摇头、侧头和预制 Motion。

简单连续动作优先使用本地参数曲线；复杂动作只能通过 VTS 中已经配置好的 Motion hotkey 触发，应用不按本地 `.motion3.json` 路径直接要求 VTS 播放；Expression 使用 VTS 的真实状态/激活 API。

### 6.3 `model3.json` 的正确用途

在用户显式选择或配置本地模型目录后，未来可以选择读取 `model3.json` 及其引用的 Motion/Expression，用于：

- 预填动作候选；
- 检查缺失文件；
- 辅助作者建立语义映射；
- 生成裁剪后的本地能力清单。

VTS API 不会替应用自动提供本地 `model3.json` 文件。即使用户导入了本地模型描述，运行时仍应以 VTS 当前模型和 API 查询结果为真相。不要把完整模型 JSON 连同 VTS 内部参数直接发给 LLM。

映射最终应按 VTS `modelID` 保存，避免切换模型后继续触发旧 hotkey。

## 7. Neuro-sama 与 ZerolanLiveRobot 的参考边界

### Neuro-sama

可以把其自然度、反应时机和动作克制程度作为产品表现参考，但公开信息不足以可靠判断其真实动画控制架构。不能据此声称“必须直嵌 Cubism”或复制不存在的公开实现。

更合理的工程结论是：高质量 rig、预制 Motion/Expression、语音时间轴和本地行为控制器共同决定自然度。

### ZerolanLiveRobot

公开代码使用 `live2d-py + PyQt5/OpenGL`，另有 Cubism Unity 展示端，但公开动作能力仍主要是：

- RMS 口型；
- 自动眨眼；
- 自动呼吸；
- 基础 LookAt。

它值得借鉴的是 AI/语音流水线与 Avatar renderer 解耦，而不是当前动作编排实现。它也证明“换成底层 Live2D 库”本身不会自动让角色自然。

## 8. 当前已保留的 Action POC

提交 `b83adf4` 已加入一个最小动作触发 POC，用户要求保留：

- `VtsConfig.ActionMap`：语义动作名 -> VTS hotkey ID；
- LLM 文本兼容标签：`[action:动作名]`；
- `ILlmClient/LlmClient.OnActionDetected`；
- `BotOrchestrator` 白名单查找并触发 VTS hotkey；
- 设置页动作映射行；
- 从 VTS 导入 `TriggerAnimation` 热键；
- VTS Hotkey DTO 增加 `type`/`file`；
- 配置模板、ConfigDiff 和相关测试。

主要修改区域：

- `AIVTuber.Core/Bot/BotOrchestrator.cs`
- `AIVTuber.Core/Config/AppConfig.cs`
- `AIVTuber.Core/Pipeline/ILlmClient.cs`
- `AIVTuber.Core/Pipeline/LlmClient.cs`
- `AIVTuber.Core/Runtime/BotRuntime.cs`
- `AIVTuber.Core/Runtime/ConfigDiff.cs`
- `AIVTuber.Core/ViewModels/ConfigViewModel.cs`
- `AIVTuber.Core/Vts/VtsModels.cs`
- `App/Views/ConfigView.xaml`
- `App/Views/ConfigView.xaml.cs`
- `config.json.template`
- 对应测试文件

这个 POC 只打通了“LLM 标签 -> 白名单映射 -> VTS hotkey 请求”的最小代码路径。当前测试还没有通过 authenticated fake/live VTS 端到端证明真实动作、exactly-once 和断线行为；这些属于 `P0-07`。

当前 POC 也不包含 `model3.json` 解析、模型能力发现、真实音频时间轴或 `AvatarMotionDirector`。它还不是最终 Avatar 架构，不应继续在正则标签和全局可变状态上堆功能。

## 9. 必须优先知道的已确认问题

| 优先级 | 问题 | 影响 | TODO |
| --- | --- | --- | --- |
| P0 | SQLite 传递依赖存在高危漏洞 | 构建产生 `NU1903`，阻塞发布 | `P0-01` |
| P0 | 配置对象引用别名 | 第二次保存 `ActionMap` 等配置可能没有 diff、不能热生效 | `P0-02` |
| P0 | 请求 generation/取消竞争 | 旧回复可能覆盖新状态或继续输出/触发动作 | `P0-04` |
| P0 | Orchestrator 事件订阅累积 | 多次保存后可确认 RMS/CloseMouth handler 倍增并泄漏实例 | `P0-05` |
| P0 | Action POC 缺少 exactly-once 门禁 | 标签可能重复、迟到、跨输出边界泄漏 | `P0-07` |
| P1 | 动作按 LLM token 到达时触发 | 没有和真实 TTS/AudioPlayer 时间轴对齐 | `AVATAR-09` |
| P1 | VTS 生命周期不完整 | token 不持久化、无可靠重连、pending/receive loop 管理不足 | `AVATAR-11` |
| P1 | 动作映射是全局字典 | 切换 VTS 模型后可能使用旧 hotkey ID | `AVATAR-10` |
| P1 | 参数没有明确所有权 | AI、摄像头 tracking、VTS 自动行为和 physics 可能互相抢值 | `AVATAR-12` |
| P1 | 配置和秘密明文、非原子保存 | 崩溃可能损坏 JSON，API key/Cookie/密码落盘 | `P1-07` |

不要只修“当前看得见的动作没触发”。如果跳过配置快照、generation、事件退订和测试门禁，动作功能会在第二次保存、打断或长时间直播后出现更难诊断的问题。

## 10. UI 当前问题与目标形态

当前设置页把多个 provider 和集成长表单纵向铺开；监控页顶部状态和按钮过密；记忆页使用 Card 模拟数据表。主要问题包括：

- 缺少设计 token、亮暗主题和一致的间距/字号；
- 状态只靠颜色或底部小字表达；
- 保存按钮不稳定、表单过长、无就地校验；
- 宽屏浪费空间、窄屏裁切；
- 对话/弹幕/诊断列表缺少虚拟化和合理自动滚动；
- 动作映射没有独立的现代编辑/预览工作台。

目标页面：

1. **直播运行台**：固定主操作、真实连接状态、对话时间线、弹幕队列和音频电平；
2. **分类设置**：快速设置、AI、语音、直播集成、角色与动作、高级；
3. **角色与动作编辑器**：能力发现、按模型 profile、搜索过滤、预览、校验、急停和 AI 意图诊断；
4. **记忆管理**：`TabView + DataGrid`，支持搜索、排序、编辑、删除确认/撤销；
5. **首次启动向导**：无需手改 JSON 或打开终端即可完成最小可运行配置。

## 11. 构建与测试基线

### 已验证

- `dotnet build AIVTuber.slnx -c Debug --no-restore`：成功，0 errors；
- Action/配置/VTS 相关测试：42/42 通过；
- `git diff --check`：通过；
- 当前已知构建警告：`SQLitePCLRaw.lib.e_sqlite3 2.1.10` 高危漏洞 `NU1903`。

### 全量测试现状

- 当前共 151 个测试，默认路径通过 134 个、失败 17 个；
- 其中 3 个失败与缺少 `WebRtcVad.dll` 有关；
- 将 DLL 放入测试输出后通过 137 个；
- 剩余 14 个失败为既有 SQLite teardown 文件锁问题。

不要把这些既有失败描述成新 Action POC 引入的回归，但也不要在完成相关 TODO 前声称全量测试通过。

### 常用命令

```powershell
dotnet build AIVTuber.slnx -c Debug
dotnet test AIVTuber.Tests\AIVTuber.Tests.csproj -c Debug
dotnet test AIVTuber.Tests\AIVTuber.Tests.csproj -c Debug --no-build --filter "FullyQualifiedName~ConfigDiffTests|FullyQualifiedName~ConfigManagerTests|FullyQualifiedName~ConfigViewModelTests|FullyQualifiedName~PipelineTests|FullyQualifiedName~Vts"
git diff --check
```

当前工作机额外安装的 SDK 路径为：

```text
C:\Users\wan.kangping\anything\.dotnet\dotnet.exe
```

这是本机信息，不应硬编码进项目。

## 12. 推荐的下一步顺序

如用户要求继续实现 Avatar，优先顺序不要从 UI 美化或 Cubism 重写开始：

1. `P0-02`：配置不可变快照、二次保存；
2. `P0-04`：generation、取消和受监督动作命令；
3. `P0-05`：事件订阅对称释放；
4. `P0-07`：Action POC exactly-once 与输出隔离测试；
5. `AVATAR-01`/`02`：`IAvatarBackend` 和按当前模型发现能力；
6. `AVATAR-03`/`09`：结构化意图、不可变 `UtterancePlan`、真实播放时钟；
7. `AVATAR-10`/`11`：按模型富绑定和可靠 VTS 通道；
8. `UX-01`/`UX-10`：设计 token 与最小可用动作编辑器；
9. `AVATAR-04` 至 `AVATAR-08`、`AVATAR-12` 至 `AVATAR-14`：动作导演、Idle、Expression、口型和参数混合；
10. 最后才重新评估 `AVATAR-15` 的 Cubism renderer。

完整顺序与验收仍以 `TODO.md` 为准。

## 13. 下一位 Agent 的工作约束

1. **先读 `TODO.md`，再改代码。** 明确本次对应的任务 ID 和验收条件。
2. **不要回退现有 Action POC。** 除非用户明确要求删除；可以在后续正式架构中迁移或包在兼容层内。
3. **不要把 TODO 当成已实现功能。** `AVATAR-*` 大部分仍是设计和工作项。
4. **不要把完整 `model3.json`、VTS hotkey ID、文件路径或参数 ID交给 LLM。** 只提供裁剪后的语义能力。
5. **不要让 LLM 逐帧控制模型。** 高频曲线、混合、限流、取消和回中必须在本地完成。
6. **不要先换 Cubism。** 先证明 VTS API 有明确且不可绕过的能力缺口。
7. **不要引入第二套 WPF 主题库。** 优先复用 WPF-UI 和现有组件。
8. **修复/重构前补回归测试。** 尤其是配置二次保存、事件订阅、generation、动作 exactly-once 和 VTS 重连。
9. **保持小 diff。** 优先修边界和生命周期，不要在同一提交顺带重写整个应用。
10. **验证后再声明完成。** 至少运行变更相关测试、Debug/Release build 中适用的一项，并记录未能运行的验证。
11. **秘密不得进入代码、模板、日志或诊断包。** 本地代理和凭据只属于运行环境。
12. **更新文档。** 完成 TODO 时同时更新任务状态、配置迁移说明和用户可见行为。

## 14. 当前工作机与网络提示

这些信息仅用于当前交接环境：

- 仓库目录：`C:\Users\wan.kangping\anything\my-AI-Vtuber`
- 当前分支：`main`
- GitHub remote：`origin = https://github.com/nashorya/my-AI-Vtuber.git`
- 本地 HTTP 代理：`http://127.0.0.1:7890`

使用代理示例：

```powershell
git -c http.proxy=http://127.0.0.1:7890 fetch origin main
```

不要把代理地址写入应用默认配置或提交为全局 Git 设置。

## 15. 参考资料

- 项目任务清单：[`TODO.md`](TODO.md)
- 旧实现计划：[`AIVTuber_Implementation_Plan.md`](AIVTuber_Implementation_Plan.md)
- 当前用户文档：[`README.txt`](README.txt)
- VTube Studio Plugin API：<https://github.com/DenchiSoft/VTubeStudio#api-details>
- ZerolanLiveRobot：<https://github.com/AkagawaTsurunaki/ZerolanLiveRobot>
- Cubism SDK License：<https://www.live2d.com/en/sdk/license/>

其中 `TODO.md` 是当前任务与验收的权威来源；旧实现计划和 README 存在已知漂移，只能作为历史背景，不能覆盖本文和 TODO 中的新决策。
