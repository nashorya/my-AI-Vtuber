# 每日陪播时长额度 设计

日期：2026-10-01 · 状态：待用户审阅

## 目标

主播每天有固定的陪播时长（默认 60 分钟），用完当天停止陪播，北京时间每天 06:00 刷新。运营者可以给个别主播单独调整时长或临时加时。时长由云端记账，客户端只负责倒计时和收尾。

## 已确认的决定

| 项 | 决定 |
|---|---|
| 余额类型 | 时长，不按金额 |
| 计时口径 | 只有「陪播进行中」才扣；暂停不扣 |
| 刷新 | 北京时间（UTC+8）每天 06:00；06:00 前的时间算前一个额度日 |
| 用完时 | 当前这句说完再停（收尾暂停），最多收尾 30 秒 |
| 额度 | 全局默认 60 分钟；可按账号单独设置；可当日临时加时 |
| 记账方 | 云端（鉴权服务）记账，借现有 60 秒心跳上报，不加新请求 |
| 兼容 | 尚未分发，新字段直接必填，不兼容旧客户端 |
| 防作弊 | 不防客户端少报（已接受）；只防改系统时间和重复上报 |

## 不在本设计内

- 鉴权服务迁移到国内云函数（另写设计；本设计先在现有 `AIVTuber.AuthServer` 实现，迁移时整体带过去，并用同一套测试验收）。
- 邀请码自助注册与通用安装包（另写设计）。
- 按金额计费、付费档位、主播自助充值。

## 1. 云端数据

在 `AIVTuber.AuthServer` 的 SQLite 中新增：

```sql
-- 全局设置。daily_quota_seconds 缺省为 3600。
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- accounts 新增两列
ALTER TABLE accounts ADD COLUMN daily_quota_seconds INTEGER NULL;   -- NULL = 用全局默认
-- 当日临时加时：只对 bonus_day 那个额度日有效
ALTER TABLE accounts ADD COLUMN bonus_seconds INTEGER NOT NULL DEFAULT 0;
ALTER TABLE accounts ADD COLUMN bonus_day TEXT NULL;                 -- 额度日，yyyy-MM-dd

-- 用量账本：每账号每额度日一行
CREATE TABLE IF NOT EXISTS usage (
    account_id TEXT NOT NULL REFERENCES accounts(id),
    quota_day TEXT NOT NULL,                -- yyyy-MM-dd
    used_seconds INTEGER NOT NULL,
    PRIMARY KEY (account_id, quota_day)
);

-- sessions 新增：本会话已入账的累计陪播秒数
ALTER TABLE sessions ADD COLUMN reported_active_seconds INTEGER NOT NULL DEFAULT 0;
```

迁移按「列不存在才添加」执行，已有数据库可直接升级。

**额度日**：`quota_day = (服务器 UTC 时间 + 8 小时 − 6 小时) 的日期部分`。下次刷新时间 = 该额度日次日 06:00（UTC+8）。

**今日额度** = `daily_quota_seconds ?? 全局默认` + (`bonus_day == 今天额度日` ? `bonus_seconds` : 0)。

**今日剩余** = max(0, 今日额度 − 当日 `used_seconds`)。

## 2. 协议

登录与心跳请求和回复均为现有 JSON（snake_case）。

**心跳请求**新增必填字段：

```json
{ "profile_id": "streamer-017", "active_seconds": 1834 }
```

`active_seconds`：本次登录以来陪播处于进行中的累计秒数（单调不减）。登录请求不带此字段（新会话从 0 开始）。

**登录与心跳回复**（`status = ok` 时）新增必填字段：

```json
{
  "quota_seconds": 3600,
  "quota_remaining_seconds": 1766,
  "quota_resets_at": "2026-10-02T06:00:00+08:00"
}
```

额度用完时 `status` 仍为 `ok`，`quota_remaining_seconds = 0`。额度用完不是账号拒绝，不注销会话、不影响许可（lease）。

## 3. 云端扣账逻辑（心跳）

在现有心跳校验（会话、账号、profile、有效期）全部通过之后：

1. `delta = active_seconds − session.reported_active_seconds`。
2. `delta < 0`：视为异常上报，不扣、不更新，按 0 处理（同一会话累计值不会变小）。
3. `delta > 0`：
   - 上限：`delta` 不超过「距该会话上次成功心跳的实际经过秒数 + 30 秒」。断网补报通常在 180 秒宽限内，超出部分丢弃。
   - 在一个事务中：`usage[account, 今天额度日] += delta`；`session.reported_active_seconds = active_seconds`。
4. 计算并返回今日额度与剩余。

同一个心跳被重发时，累计值相同，`delta = 0`，不会重复扣。跨 06:00 断网补报的时间记入上报时所在的额度日（可接受的误差）。

多个会话（多台电脑）各自上报，按账号合并进同一行 `usage`。

## 4. 客户端

### 4.1 额度状态（`CloudLicense`）

- 每次登录/心跳成功，记录 `quota_remaining_seconds` 和收到时的单调时间戳（与现有 lease 计算方式一致，不用系统时间）。
- 对外提供：今日额度、今日剩余（本地推算）、刷新时间、是否用完。
- 本地推算：剩余 = 云端剩余 − 自收到以来本地累计的陪播进行中秒数。暂停时不减。
- `active_seconds` 计数器：登录时归零；陪播进行中按单调时钟累加；每次心跳上报当前值。断网期间照常累加，恢复后的第一次心跳自然补报。

### 4.2 收尾暂停（`BotRuntime`）

新增「收尾暂停」，区别于现有的立即暂停 `SetCompanionPaused(true)`：

1. 本地剩余到 0 时触发：立即停止接收新识别结果、不开始新的回复轮次。
2. 正在播放的当前一句播完；同一回复中尚未开始播放的后续句子丢弃。
3. 播完（或超过 30 秒上限）后进入暂停状态，原因标记为「额度用完」。
4. 若主播在额度用完前自己点暂停，仍是现有的立即暂停。

额度用完期间：「继续陪播」不可用；登录状态、设备检查、人设与设置编辑照常。

额度刷新（心跳拿到大于 0 的剩余）后，「继续陪播」恢复可点，但**不自动继续**，需主播手动点击。

### 4.3 主播界面（`StreamerConsoleController` → `streamer.html/js/css`）

状态推送新增：`quotaRemainingSeconds`、`quotaSeconds`、`quotaResetsAt`、`quotaExhausted`。

**陪播台**：陪播状态旁显示额度标签。

| 剩余 | 显示 |
|---|---|
| > 10 分钟 | 「今日剩余 42 分钟」，淡紫色 |
| 1–10 分钟 | 「今日剩余 8 分钟，快用完了」，橙色 |
| < 1 分钟 | 「今日剩余不到 1 分钟」，橙色 |
| 0 | 「今日时长已用完 · 明早 6:00 恢复」；「继续陪播」按钮禁用 |

暂停时数字不变化。剩余首次降到 10 分钟以内时，在「需要处理的问题」区域提示一次。

**账号与帮助**：账号卡片增加「今日已用 18 分钟 / 共 60 分钟」「每天早上 6:00 刷新」。

所有文案为易懂中文，不显示秒级倒计时。

## 5. 管理命令

在 `AIVTuber.AuthServer admin` 中新增：

| 命令 | 作用 |
|---|---|
| `set-default-daily --minutes N` | 设置全局默认每日时长 |
| `set-daily --username U (--minutes N \| --default)` | 单独设置某账号每日时长 / 恢复默认 |
| `add-today --username U --minutes N` | 当日临时加时（累加），下一个 06:00 作废 |
| `usage --username U [--days 7]` | 最近 N 个额度日的用量 |

`list` 增加「今日已用 / 今日额度」列。修改即时生效，客户端下一次心跳（≤ 60 秒）获取新值。

## 6. 错误处理

- 心跳失败（网络或云端故障）：沿用现有 180 秒 lease 宽限；本地继续倒计时；宽限内本地剩余先到 0 也按收尾暂停处理。
- 多台电脑同时陪播：各端本地倒计时可能偏乐观，云端合并后下一次心跳纠正，最多多用约 1 个心跳间隔。
- 额度相关字段缺失或格式错误：客户端按「连不上鉴权服务」处理，不当成账号拒绝。

## 7. 测试

服务端（`AIVTuber.Tests`，使用可控 `TimeProvider`）：
- 额度日边界：05:59 与 06:00（UTC+8）分属不同额度日；刷新时间计算正确。
- 累计值扣账：正常递增、重复上报不重复扣、累计值变小不扣、`delta` 超过经过时间上限被截断。
- 多会话合并扣同一账号。
- 默认额度、单独额度、当日加时及其次日失效。
- 额度用完时 `status = ok`、剩余 0、会话不被注销。
- 管理命令的读写与 `list`/`usage` 输出。

客户端：
- `CloudLicense` 本地推算：仅陪播进行中递减；暂停不减；以单调时钟计算，不受系统时间影响。
- 断网期间累计、恢复后补报。
- 收尾暂停：当前句播完后暂停；后续句被丢弃；30 秒上限；主播手动暂停仍为立即暂停；刷新后不自动继续。
- `StreamerConsoleController` 推送额度字段；`resumeCompanion` 在额度用完时被拒绝。

界面：在本地静态预览中检查四种额度状态的显示；Windows 上真机验收收尾暂停和刷新恢复。
