# VTS 连续控制实验

分支：`codex/vts-continuous-control`；起点：`origin/main@81953b1`。默认关闭，不替代现有像素形象或旧 VTS 热键模式。

## 默认体验：复用已有面捕映射

1. 在 Windows 打开 VTube Studio，加载能正常面捕的模型（先试自带模型），打开 Allow Plugin API access。
2. 确认 `config.json` 中 `avatar.backend` 为 `vts`（默认）或 `both`；修改后重启应用。在“角色与动作”检查 VTS 主机/端口并保存。
3. 保持“复用模型已有面捕映射”勾选，勾选“VTS 连续控制实验”并保存；首次在 VTS 允许 AIVTuber 插件，连接失败可点“重新连接 / 授权”。
4. 开始聊天。**无需创建 AIVTuber 输入、填写范围、逐项绑定或确认 profile**。可随时回中停止，或恢复已保存的控制。

程序发送标准 tracking 输入，复用模型已有的 IN→OUT 设置。面板“可发送”仅表示 VTS 返回了该输入，**不表示已确认模型绑定或视觉效果**。API 无法读出完整映射关系，因此某部位未绑定时可能不动，特殊绑定也可能产生不同动作。

| 语义 | 内置输入 |
| --- | --- |
| 头部左右、上下、歪头 | FaceAngleX / Y / Z |
| 目光 XY | EyeLeftX / Y 和 EyeRightX / Y（同向发送） |
| 左右眼开合 | EyeOpenLeft / Right |
| 左右眉高 | BrowLeftY / RightY；同时发送两侧平均值到 Brows，部分模型会联动 |
| 嘴形 | MouthSmile |
| 音频口型 | MouthOpen、VoiceVolume、VoiceVolumePlusMouthOpen（仅发送实际存在的输入） |

仅发现于 `defaultParameters` 且范围有效的输入会被使用。头部目标幅度上限取 12 度与输入可用范围的较小者，目光/眉高/嘴形使用克制幅度，眼睛和口型保持完整开合范围。不把 Live2D 输出范围误用于 tracking 输入。默认模式不会单独驱动身体、眉形或呼吸；身体跟随、呼吸、头发和衣服物理沿用模型原设置。现有模型若把身体绑定到头部输入，身体也可能跟随。

持续注入期间覆盖同名真实面捕输入，停止后交还原驱动。不会改写 VTS 配置，不自动清除表情或禁用物理/自动眨眼。VTS 自带自动眨眼仍可能与本地眨眼叠加；模型的动画、表情、物理也可能覆盖输入效果。人工热键/表情会暂停 AI，检查后手动恢复。模型或配置变化先停止，刷新后点击恢复即可按新模型内置输入重新开始，不复用旧动作。断线重连只恢复待机。

## 高级：自定义映射

需要独立眉形、身体等专项适配时，取消“复用模型已有面捕映射”并保存，展开的高级表保留旧方案。旧的自定义 profile 不会被删除；导入 profile 不会切换模式。

1. 刷新模型与通道，选择目标并校准最小/中立/最大和方向，再创建输入参数。
2. 在 VTS 设置 IN 为 `AIVTuber…`，OUT 为目标参数。**IN/OUT 使用相同校准范围**；保留旧口型名 `AIVTuberMouthOpen`。
3. 修改 VTS 配置后刷新，从中立、低强度试动开始，确认眼、嘴、头和身体视觉正确；点击确认后保存。输入创建和数值响应不代表已正确绑定。
4. 高级模式仅开放已验证通道；配置变化/模型切换使旧验证失效。对应通道关闭重复驱动，保留头发和衣服物理。

## 回复与动作

同一次 LLM 请求输出 JSON：

```json
{"reply":"围裙也算工作服呀。","avatar":{"targets":{"headRoll":0.2,"gazeX":-0.3},"transitionMs":400,"holdMs":1500}}
```

`reply` 保留原有正文、`（心里话）`、`【PASS】` 和情绪标签协议。动作数据不进入 TTS、字幕、对话或记忆；记忆提取器不使用此 JSON 格式。坏外层 JSON 丢弃整轮，合法正文中的坏动作单独丢弃。每轮最多一个目标，512 token 预算，不增加额外 LLM 请求。

公开发言：通过原有提交检查后，在播放器首次读取这一轮 PCM 时启动。该时刻不等于声卡最终出声，不提供音素级同步。心里话：提交时动作、无 TTS；PASS：忽略附带动作，只维持自然待机。PK 双人静默和禁止普通语音打断的规则不变。

本地 30 Hz 插值与批量注入，网络拥堵仅保留最新帧；音频独占口型，眨眼乘以眼睑目标。结束、取消、停播时回到基础姿态；正常停止控制短暂回中后停止注入。连接丢失时由 VTS 约一秒输入超时释放，无法保证断线时平滑回中。

## 配置与连接

配置文件：`vts.continuous_control.enabled`（默认 false）、`use_built_in_tracking`（默认 true）和 `profiles`。profile 仅用于高级模式，以 VTS modelID 索引。每个 profile 保存通道目标、范围、中立值、反向及验证状态。高级模式下模型切换/配置变更会使旧 profile 失效，需要刷新并重新确认。配置草稿与控制器使用独立快照。

面板可导入/导出通道 JSON，不包含授权 token 或聊天 API key。导入保留校准候选，清除验证状态，不自动开启实验；在当前机器重新试动确认后保存。

授权 token 单独存储于用户 LocalApplicationData/AIVTuber/vts，按主机/端口区分，不进入配置导出或日志。断线自动尝试 1/2/5 秒三次，授权拒绝停止重试。连接恢复仅恢复有效模型的待机，不重放旧回复动作。

日志前缀 `[Avatar/VTS]` 记录状态、轮次、模型、接受的通道和停止时的帧汇总。出现输入创建失败、APIError 或超时不会报告成功。

## 实施计划对应

本分支完成 AVATAR-01～05、09～12 的实验切片：能力查询与校准、类型化意图、逐帧控制、播放时钟接入、按模型配置、连接治理和所有权。原 TODO 大项保持未完成；复杂 Motion 时间轴、尾巴/手势专项 rig、音素口型、Cubism 后端不在本轮。

## 验证记录

2026-09-12 默认接入调整：内置输入复用已实现，本地相关回归 146/146 通过；覆盖无 profile 启动、禁止创建自定义参数时仍可连接、输入白名单与范围、双眼/眉高/音频输入合成、20 次热应用、两种模式互换、人工接管、模型卸载和重连不重播。Windows 应用 Release 交叉编译通过；浏览器样例验证默认隐藏映射表、切换后可显示高级表。真实 VTS 视觉效果仍未验收。后续 Windows CI 结果以试用包附带 summary.json 为准。

自动测试覆盖协议分类/隔离、插值/口型/眨眼、慢发送、授权/分片/APIError、映射失效、人工接管与重建订阅。Windows quality gate 负责完整 Release 构建、测试和打包。

2026-09-10 本地验证：macOS ARM64 以 AnyCPU 构建测试项目，相关回归 138/138 通过。Windows WPF 项目跨平台 Release 编译通过（0 警告、0 错误）。Web 设置页在浏览器用 16 通道样例检查布局，未出现脚本错误；这不等于 Windows WebView2/VTS 端到端验收。

试用包附 `verification/summary.json` 和 `publish-manifest.json`，记录所构建的精确 commit、完整 Windows 测试计数、各阶段结果和文件 SHA256。构建脚本已恢复 Go 工具链校验，并将 `danmaku_bridge.exe` 列为必需交付文件，避免仅有主程序却遗漏弹幕桥。

本地测试命令（仅开发机覆盖运行架构，产品仍为 Windows x64）：

```sh
dotnet build AIVTuber.Tests/AIVTuber.Tests.csproj --no-restore -t:Rebuild -p:PlatformTarget=AnyCPU
dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --no-build --no-restore -p:PlatformTarget=AnyCPU -p:VSTestPlatform=ARM64 --filter 'FullyQualifiedName~Continuous|FullyQualifiedName~RequestCoordinator|FullyQualifiedName~BotOrchestrator|FullyQualifiedName~ReplyClassifier|FullyQualifiedName~ConfigDiff|FullyQualifiedName~ConfigViewModel|FullyQualifiedName~DualParty'
```

官方协议依据：[Plugin API](https://github.com/DenchiSoft/VTubeStudio)、[事件协议](https://github.com/DenchiSoft/VTubeStudio/blob/master/Events/README.md)。稳定版不支持 `ExpressionToggledEvent` 时，每 500 ms 查询表情状态补足人工接管检测。

真实 VTS：**待 Windows 实机验收**。当前开发机没有运行中的 VTS，不能据此声称动作自然度通过。实机需记录自带模型名称、通道列表，完成中立脸/眼/嘴/头/身体试动、聊天动作、心里话/PASS、人工接管、切模型和 VTS 重启，最后运行 30 分钟检查抖动与积压。
