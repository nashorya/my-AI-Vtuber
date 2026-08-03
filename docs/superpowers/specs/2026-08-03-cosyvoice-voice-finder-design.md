# CosyVoice 音色查找与配置脚本设计

## 目标

新增一个 Python 命令行脚本，查询当前阿里云百炼账号下可调用的 CosyVoice 自定义音色，优先展示 Asia/Shanghai 时区当天 00:00（含）至 12:00（不含）创建的音色，并让用户交互选择一个音色安全写入 AI VTuber 的运行配置。

脚本只处理 CosyVoice / Qwen-Audio-TTS 共用的 `voice-enrollment` 音色管理接口，不查询 Qwen3-TTS 的 `qwen-voice-enrollment` 音色，也不查询 MiniMax 音色。

## 用户界面

主入口为 `scripts/find_cosyvoice_voices.py`。典型调用：

```powershell
python scripts/find_cosyvoice_voices.py
```

脚本按创建时间从新到旧打印候选项，包括序号、`voice_id`、创建时间、状态和绑定的 `target_model`。若今天上午存在状态为 `OK` 的音色，只展示这些优先候选；否则明确提示未找到今早音色，并展示所有状态为 `OK` 的自定义音色作为回退。

用户输入序号后，脚本展示将要写入的配置值并要求确认。取消、空候选或输入无效时不修改任何文件。命令行参数至少支持：

- `--config PATH`：覆盖自动定位到的配置文件路径。
- `--region {beijing,singapore}`：选择服务地域，默认 `beijing`。
- `--workspace-id ID`：使用 workspace 专属域名；也可通过 `DASHSCOPE_WORKSPACE_ID` 提供。
- `--prefix PREFIX`：在服务端按音色名前缀过滤。
- `--all`：不启用“今早优先”，直接展示全部可调用音色。

## 配置与端点

API Key 仅从 `DASHSCOPE_API_KEY` 环境变量读取，不接受命令行明文参数，也不打印到日志。缺失时脚本以非零状态退出并给出设置方法。

若存在 workspace ID，使用官方推荐的专属端点：

- 北京：`https://{workspace_id}.cn-beijing.maas.aliyuncs.com/api/v1/services/audio/tts/customization`
- 新加坡：`https://{workspace_id}.ap-southeast-1.maas.aliyuncs.com/api/v1/services/audio/tts/customization`

若未提供 workspace ID，则使用官方仍支持的旧端点：北京为 `https://dashscope.aliyuncs.com/api/v1/services/audio/tts/customization`，新加坡为 `https://dashscope-intl.aliyuncs.com/api/v1/services/audio/tts/customization`。

默认配置路径按以下顺序选择第一个已存在的文件：

1. 当前工作目录下的 `config.json`。
2. 仓库内 `App/bin/Debug/net10.0-windows/win-x64/config.json`。

若两者都不存在，脚本要求通过 `--config` 指定现有配置文件，不创建猜测位置的新文件。模板和 `artifacts` 下的文件永远不会被自动选择。

## 数据流

1. 使用 `model=voice-enrollment`、`action=list_voice` 分页查询音色。
2. 对列表中的每个音色使用 `action=query_voice` 查询详情，以获得权威的 `target_model`。
3. 仅将列表状态和详情状态均为 `OK`，且具有非空 `voice_id` 和 `target_model` 的音色视为可配置候选。
4. 将无时区的 `gmt_create` 按所选地域的当地时间解释；北京默认使用 Asia/Shanghai。当天上午窗口为 `[00:00, 12:00)`。
5. 优先展示上午候选；没有时回退到全部可用候选。所有候选按创建时间倒序排列。
6. 用户选择并确认后，保留配置文件中的全部未知字段，只更新：
   - `tts.provider` 为 `aliyun`
   - `tts.voice_id` 为选中音色 ID
   - `tts.model` 为详情接口返回的绑定模型
7. 在同目录创建 `config.json.bak` 备份，将新 JSON 写到临时文件后原子替换原配置。

列表分页以固定页大小查询，从第 0 页开始；当返回数量小于页大小或为空时结束。设置合理的最大页数并检测重复结果，避免异常服务响应导致死循环。

## 代码结构

脚本内部保持以下可独立测试的边界：

- 端点解析：由地域和可选 workspace ID 生成 URL。
- API 客户端：负责 JSON POST、分页列表和详情查询。
- 音色标准化与筛选：统一接口字段、判断可用状态并执行上午筛选。
- 配置定位与更新：解析、备份并原子更新 JSON。
- CLI：负责参数、表格展示、交互选择、确认和退出码。

网络实现使用 Python 标准库，避免为一个维护脚本增加第三方依赖。

## 错误处理与安全

- HTTP、鉴权、限流、超时、非 JSON 响应和服务端错误均输出简洁错误信息并返回非零退出码。
- API Key、Authorization Header 和完整服务端响应不写入日志。
- 单个音色详情查询失败时记录该音色不可验证并跳过；若所有详情都失败，则整体失败且不写配置。
- 配置缺少 `tts` 对象时创建该对象；若根节点或 `tts` 不是 JSON 对象，则拒绝修改。
- 在完成备份和临时文件写入前不覆盖原配置；失败时清理临时文件并保留原文件。
- 所有写操作必须发生在用户明确选择并确认之后。

## 测试与验收

使用 Python `unittest` 和模拟 HTTP 响应，不依赖真实网络。测试覆盖：

- 北京、新加坡以及 workspace/旧域名端点选择。
- 多页查询、空页终止和重复页保护。
- `OK`/`UNDEPLOYED` 状态过滤与详情模型获取。
- Asia/Shanghai 当日上午边界（00:00 包含、12:00 排除）及无上午候选时的回退。
- 创建时间倒序和最新候选默认项。
- 配置未知字段保留、三项 TTS 字段成对更新、备份创建和失败时不破坏原文件。
- 缺少 Key、无候选、无效输入、用户取消、API/JSON 错误的退出行为。

实施完成后先运行单元测试和 Python 语法检查，再使用当前环境已有的 `DASHSCOPE_API_KEY` 执行一次只读查询。真实配置只有在交互选择并确认后才会更新；验收标准是写入后的 `voice_id` 与 `model` 来自同一音色详情，并且备份可恢复原配置。

## 非目标

- 不创建、更新或删除远端音色。
- 不枚举系统预置音色。
- 不试听或调用 TTS 合成，以免产生不必要调用或费用。
- 不修改 C# 客户端、配置界面或应用启动流程。
