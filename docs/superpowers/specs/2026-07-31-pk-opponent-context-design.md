# PK 对手信息注入 LLM 上下文 — 设计文档

- 日期：2026-07-31
- 状态：待评审
- 范围：B站直播 PK 开始时，获取对方主播的 uid / 昵称 / 粉丝数，注入 LLM 上下文
- 对应 TODO：`LIVE-01`

## 1. 概述

当前 B站链路只处理 `DANMU_MSG`，AI 主播对"正在和谁 PK"完全无感知。本设计让 bridge 监听 PK 事件，
拉取对方主播的基础信息，经由现有的本地 HTTP 通道推给 C# 后端，填充模板后作为一次文本输入喂给 LLM，
使 AI 主播能在 PK 开场时自然地点评对手。

### 目标

- PK 开始（或倒计时阶段）时拿到对方主播的 uid、昵称、粉丝数。
- 以可配置模板注入 LLM，触发一次开场发言。
- 默认关闭，不改变现有部署的行为。

### 非目标（本期不做）

- 不拉取对方舰队榜 / 高能榜，因此**不做串门识别**。
- 不做 PK 战况播报（票数、连胜、结算）。
- 不持久化 PK 记录到 SQLite。
- 不新增 UI 面板。

## 2. 数据来源与延迟

对手 room_id 来自弹幕长连接推送的 PK cmd。粉丝数挂在**用户**维度而非房间维度，
因此必须先把 room_id 换成 uid：

```
PK payload → room_id
   ↓  GET room/v1/Room/room_init?id=<room_id>
uid
   ↓  GET live_user/v1/Master/info?uid=<uid>
uname + follower_num
```

两跳串行，无法并发。实测 5 次（冷连接，macOS 家用网络）：

| # | room_init | Master/info | 合计 |
|---|---|---|---|
| 1 | 114ms | 452ms | 565ms |
| 2 | 341ms | 675ms | 1015ms |
| 3 | 86ms | 338ms | 423ms |
| 4 | 86ms | 140ms | 226ms |
| 5 | 92ms | 372ms | 464ms |

中位数约 460ms。`room_init` 稳定，抖动主要来自 `Master/info`。

`xlive/web-room/v1/index/getInfoByRoom`（一次拿全）现已风控，返回 `-352`，补 Referer 无效，故不采用。

### 监听哪些 cmd

| cmd | 用途 |
|---|---|
| `PK_BATTLE_PRE` / `PK_BATTLE_PRE_NEW` | 匹配成功、倒计时阶段推送，比 START 早数秒。命中即可在正式开打前备好数据 |
| `PK_BATTLE_START` / `PK_BATTLE_START_NEW` | 正式开打。作为 PRE 未命中时的兜底 |
| `PK_BATTLE_END` / `PK_END` / `PK_BATTLE_CRIT` / `PK_BATTLE_SETTLE_NEW` | 任一到达即清空当前对手状态 |

**PRE 的字段结构未经实测**（需真实开一场 PK 验证）。因此解析按"尽力而为"实现：
在 payload 中递归搜索 room_id 候选，取不到就静默跳过，退化为 START 触发，不得抛异常。

### 对手 room_id 判定

PK payload 同时含发起方与匹配方两个 room_id。用自己的配置房间号比对，剩下那个即对手：

```
if init_info.room_id == 本房间: 对手 = match_info.room_id
else:                           对手 = init_info.room_id
```

两个都不等于本房间号时（异常）取 `init_info.room_id`；两个都等于或均缺失时放弃本次。

### 去重

`PRE` 与 `START` 及各自的 `_NEW` 变体会对同一场 PK 重复推送。以对手 room_id 为键、
**60 秒**窗口去重，避免同一场 PK 多次触发 LLM 发言。窗口取 60s 而非上游参考实现的 10s，
因为本设计同时监听 PRE 和 START，两者间隔可达数秒至数十秒。

## 3. 架构

```
bridge (Python)                              C# (Core)
  PK cmd handler
    ↓ 解析对手 room_id
    ↓ 60s 去重
    ↓ room_init → Master/info
  POST /pk/  ──────────────────────►  BilibiliDanmakuClient
  {uid, uname, follower, room_id}       ↓ OnPkStarted 事件
                                      BotRuntime
                                        ↓ 填充 Input.PkTemplate
                                      BotOrchestrator.ProcessTextAsync
```

### 为什么在 Python 侧发起 HTTP

bridge 已持有 WS 长连接和 asyncio 事件循环，PK cmd 只能在这里解析。把两跳 HTTP 也放在
bridge 内，C# 侧收到的就是一个完整、可直接使用的事件，无需感知 B站 API 的存在——
与现有 `DANMU_MSG` 的处理方式一致。

### 为什么不走 DanmakuSelector

`DanmakuSelector` 的职责是"从积压弹幕中按间隔挑一条"，带 `SelectionIntervalSec`(默认 8s) 节流和排队。
PK 开场白是单次、时效性强的事件，压进该队列会被无意义地延迟甚至丢弃。因此直连
`ProcessTextAsync`，与 `OnDanmakuSelected` 处理器并列。

## 4. 组件改动

### 4.1 `danmaku_bridge.py`

- 新增模块级共享 `httpx.AsyncClient`，替代每条消息新建客户端（顺带修复 `DANMU_MSG` 路径的握手开销）。
- 新增 `_extract_opponent_room_id(payload, self_room_id)`：纯函数，无 IO，可单测。
- 新增 `_fetch_opponent(room_id)`：两跳 HTTP，任一失败返回 `None`。
- 注册 PRE / START / END 三组 handler。
- PK 处理全程包裹异常捕获：**PK 链路任何失败都不得影响弹幕主链路**。

### 4.2 `BilibiliDanmakuClient.cs`

- `HttpListener` 增加 `/pk/` 前缀；`ProcessRequestAsync` 按 `AbsolutePath` 分流到弹幕 / PK 两个处理分支。
- 新增 `public event EventHandler<PkOpponent>? OnPkStarted`。
- `StartPythonProcess` 显式设置子进程 `no_proxy=*` / `NO_PROXY=*`：
  `UseShellExecute=false` 时子进程继承父进程全部环境变量，若宿主终端配置了代理，
  B站 API 会绕行境外并劣化到秒级（实测 0.10s → 3.69s）。

### 4.3 `PkOpponent.cs`（新文件）

```csharp
public sealed class PkOpponent
{
    public string Uid { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public long FollowerCount { get; init; }
    public int RoomId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
```

### 4.4 `AppConfig.cs`

- `BilibiliConfig.PkNotice`：`bool`，默认 `false`。
- `InputConfig.PkTemplate`：`string`，默认 `"（PK 开始了，对手是 {uname}，有 {follower} 个粉丝）"`。
  占位符 `{uname}` / `{follower}` / `{uid}` / `{roomid}`。
- `PkNotice` 变更需要重启 bridge（heavy），并入 `ConfigDiff` 现有的 `RestartDanmaku` 类别。

### 4.5 `BotRuntime.cs`

在 `_danmaku.OnDanmaku` 订阅处相邻位置订阅 `OnPkStarted`，填充模板后调用 `ProcessTextAsync`。

## 5. 错误处理

| 场景 | 行为 |
|---|---|
| PK payload 解析失败 | 记录 debug 日志，跳过 |
| 两个 room_id 都取不到 | 跳过，不发 POST |
| `room_init` / `Master/info` 失败或超时(3s) | 跳过，不发 POST，不重试 |
| 重复 cmd | 60s 窗口内静默丢弃 |
| C# 端点收到畸形 JSON | 返回 200，不触发事件（与现有弹幕分支一致） |

原则：PK 是锦上添花的功能，任何异常都只能导致"这次没播报"，绝不能影响弹幕主链路或让 bridge 退出。

## 6. 测试

Python（新增 `AIVTuber.Tests` 之外的 pytest，或以现有方式）：

- `_extract_opponent_room_id`：init 是自己 / match 是自己 / 都不是自己 / 字段缺失 / 空 payload。
- 去重窗口：同一 room_id 连续两次只放行一次；超窗后放行。

C#（`AIVTuber.Tests`）：

- `/pk/` 端点收到合法 JSON 时触发 `OnPkStarted`，字段映射正确。
- 畸形 JSON / 缺字段时不触发事件且返回 200。
- 弹幕与 PK 两条路径互不串扰。

两跳 HTTP 属外部依赖，不进测试。

## 7. 验收

- 配置 `PkNotice=false`（默认）时，行为与当前完全一致，bridge 不注册 PK handler。
- 开启后真实开一场 PK：AI 主播在开场时说出一句包含对手昵称的发言，且**只说一次**。
- bridge 日志能看到实际收到的 PK cmd 序列，可据此确认 PRE 是否早于 START 命中。
- 断网或 B站 API 失败时，弹幕回复功能不受影响，bridge 不退出。
