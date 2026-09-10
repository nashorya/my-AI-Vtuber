# VTS 连续控制实验

分支：`codex/vts-continuous-control`；起点：`origin/main@81953b1`。默认关闭，不替代现有像素形象或旧 VTS 热键模式。

## 体验步骤

1. 在 Windows 打开 VTube Studio，加载一只自带 Live2D 模型，打开 Allow Plugin API access。
2. 确认应用配置 `config.json` 中 `avatar.backend` 为 `vts`（默认）或 `both`；修改后重启应用。设置页检查 VTS 主机/端口并保存。
3. 展开“角色与动作”，点击“重新连接 / 授权”；首次在 VTS 允许 AIVTuber 插件。
4. 点击“刷新模型与通道”。候选来自当前模型参数名称，并不意味着已绑定。
5. 为要使用的通道选择目标、校准最小/中立/最大和方向，再“创建输入参数”。
6. 在 VTS 手动设置 IN 为表中的 `AIVTuber…` 输入，OUT 为目标参数。**IN/OUT 使用相同校准最小/最大值**。输入值已由本程序换算到该范围，不要再设置成固定 -1～1。旧口型输入仍为 `AIVTuberMouthOpen`。
7. 修改 VTS 配置后刷新通道。确认中立脸正常；由低强度开始试动眼、嘴、头和身体，方向不对时勾选反向并重新试动。先完成试动，再点击“确认试动正确”；数值响应不代表视觉正确。
8. 勾选实验开关并保存。只有已确认且范围有效的通道供 AI 使用。缺少通道不会自动创建模型变形。
9. 任意时刻可“回中并停止控制”。恢复使用已保存的 profile；试动会暂停聊天动作，需保存或手动恢复。

头/视线正方向为画面右、上；倾斜为顺时针。正眉形/嘴形表示更开心，以每个模型实际制作方式校准。

关闭重复驱动通道的摄像头/手机/鼠标面捕和自动眨眼，保留头发、衣服原有物理。不要把物理输出直接当成主动控制通道。VTS 最终参数还受动画、表情、物理优先级影响。本实验不会清除人工表情；人工热键/表情事件会暂停控制，检查后手动恢复。

## 回复与动作

同一次 LLM 请求输出 JSON：

```json
{"reply":"围裙也算工作服呀。","avatar":{"targets":{"headRoll":0.2,"gazeX":-0.3},"transitionMs":400,"holdMs":1500}}
```

`reply` 保留原有正文、`（心里话）`、`【PASS】` 和情绪标签协议。动作数据不进入 TTS、字幕、对话或记忆；记忆提取器不使用此 JSON 格式。坏外层 JSON 丢弃整轮，合法正文中的坏动作单独丢弃。每轮最多一个目标，512 token 预算，不增加额外 LLM 请求。

公开发言：通过原有提交检查后，在播放器首次读取这一轮 PCM 时启动。该时刻不等于声卡最终出声，不提供音素级同步。心里话：提交时动作、无 TTS；PASS：忽略附带动作，只维持自然待机。PK 双人静默和禁止普通语音打断的规则不变。

本地 30 Hz 插值与批量注入，网络拥堵仅保留最新帧；音频独占口型，眨眼乘以眼睑目标。结束、取消、停播时回到基础姿态；正常停止控制短暂回中后停止注入。连接丢失时由 VTS 约一秒输入超时释放，无法保证断线时平滑回中。

## 配置与连接

配置文件：`vts.continuous_control.enabled` 和 `profiles`，以 VTS modelID 索引。每个 profile 保存通道目标、范围、中立值、反向及验证状态。模型切换/配置变更会使旧 profile 失效，需要刷新并重新确认。配置草稿与控制器使用独立快照。

面板可导入/导出通道 JSON，不包含授权 token 或聊天 API key。导入保留校准候选，清除验证状态，不自动开启实验；在当前机器重新试动确认后保存。

授权 token 单独存储于用户 LocalApplicationData/AIVTuber/vts，按主机/端口区分，不进入配置导出或日志。断线自动尝试 1/2/5 秒三次，授权拒绝停止重试。连接恢复仅恢复有效模型的待机，不重放旧回复动作。

日志前缀 `[Avatar/VTS]` 记录状态、轮次、模型、接受的通道和停止时的帧汇总。出现输入创建失败、APIError 或超时不会报告成功。

## 实施计划对应

本分支完成 AVATAR-01～05、09～12 的实验切片：能力查询与校准、类型化意图、逐帧控制、播放时钟接入、按模型配置、连接治理和所有权。原 TODO 大项保持未完成；复杂 Motion 时间轴、尾巴/手势专项 rig、音素口型、Cubism 后端不在本轮。

## 验证记录

自动测试覆盖协议分类/隔离、插值/口型/眨眼、慢发送、授权/分片/APIError、映射失效、人工接管与重建订阅。Windows quality gate 负责完整 Release 构建、测试和打包。

2026-09-10 本地验证：macOS ARM64 以 AnyCPU 构建测试项目，相关回归 137/137 通过。Windows WPF 项目跨平台 Release 编译通过（0 警告、0 错误）。Web 设置页在浏览器用 16 通道样例检查布局，未出现脚本错误；这不等于 Windows WebView2/VTS 端到端验收。

本地测试命令（仅开发机覆盖运行架构，产品仍为 Windows x64）：

```sh
dotnet build AIVTuber.Tests/AIVTuber.Tests.csproj --no-restore -t:Rebuild -p:PlatformTarget=AnyCPU
dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --no-build --no-restore -p:PlatformTarget=AnyCPU -p:VSTestPlatform=ARM64 --filter 'FullyQualifiedName~Continuous|FullyQualifiedName~RequestCoordinator|FullyQualifiedName~BotOrchestrator|FullyQualifiedName~ReplyClassifier|FullyQualifiedName~ConfigDiff|FullyQualifiedName~ConfigViewModel|FullyQualifiedName~DualParty'
```

官方协议依据：[Plugin API](https://github.com/DenchiSoft/VTubeStudio)、[事件协议](https://github.com/DenchiSoft/VTubeStudio/blob/master/Events/README.md)。稳定版不支持 `ExpressionToggledEvent` 时，每 500 ms 查询表情状态补足人工接管检测。

真实 VTS：**待 Windows 实机验收**。当前开发机没有运行中的 VTS，不能据此声称动作自然度通过。实机需记录自带模型名称、通道列表，完成中立脸/眼/嘴/头/身体试动、聊天动作、心里话/PASS、人工接管、切模型和 VTS 重启，最后运行 30 分钟检查抖动与积压。
