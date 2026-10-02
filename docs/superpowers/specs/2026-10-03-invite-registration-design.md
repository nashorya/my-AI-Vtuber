# 邀请码注册与共用安装包 设计

日期：2026-10-03 · 状态：用户已在对话中逐段确认，授权自主实施

## 目标

主播不再由运营者手动逐个建账号。运营者批量生成一次性邀请码，所有主播使用同一个安装包，在登录页用「邀请码 + 自己起的账号名 + 密码」注册，注册成功即登录。

## 已确认的决定

| 项 | 决定 |
|---|---|
| 邀请码含义 | 一次性注册门票：不带有效期、不带时长；一个码只能注册一个账号 |
| 账号有效期 | 邀请码注册的账号**没有到期日**（长期有效）；每日时长沿用默认 60 分钟，可用 `set-daily` 单独调；仍可 `disable` |
| 注册填写项 | 邀请码、账号名（自己起）、密码（输两次） |
| 安装包 | 所有主播共用一个包（共用同一套厂商 Key，已接受其风险）；老的「每人一个专属包 + 手动建号」继续可用 |
| 不含 | 找回密码自助流程（仍由运营者 `set-password`）、邮箱/手机验证、邀请码过期时间 |

## 1. 服务端

### 数据

```sql
CREATE TABLE IF NOT EXISTS invites (
    code TEXT PRIMARY KEY,            -- 规范化：大写、无横线
    profile_id TEXT NOT NULL,         -- 绑定到哪批安装包
    note TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    used_by_account_id TEXT NULL,
    used_at TEXT NULL,
    revoked_at TEXT NULL
);
```

**无到期日**：账号表的 `valid_until` 保持非空；用常量 `AuthService.NoExpiry = 2100-01-01T00:00:00Z` 表示长期有效（取这个日期而不是 9999 年，避免客户端用单调时钟换算时溢出）。`valid_until >= NoExpiry` 一律视为长期有效。

### 邀请码格式

8 位，字符集 `ABCDEFGHJKMNPQRSTUVWXYZ23456789`（去掉 `0 O 1 I L`），显示为 `XXXX-XXXX`。输入时忽略大小写、横线和空格。生成时重复则重试。

### 注册接口

`POST /v1/auth/register`

```json
{ "invite_code": "K7M4-9QXD", "username": "alice", "password": "…", "profile_id": "shared-001",
  "app_version": "0.37.0", "credential_revision": 1 }
```

检查顺序：

1. 注册失败限流（按来源，沿用登录的 5 次 / 15 分钟）；超过 → `rate_limited`。
2. 必填项为空 → `bad_request`。
3. 账号名 3–20 位，只含字母、数字、下划线、横线 → 否则 `invalid_username`。
4. 密码至少 8 位 → 否则 `weak_password`。
5. 在**一个数据库事务内**：邀请码存在、未使用、未作废、绑定的 `profile_id` 与请求一致，否则 `invalid_invite`（「不存在 / 已用 / 已作废 / 档案不符」对外是同一个状态，不泄露哪些码存在）；插入账号（用户名唯一冲突 → `username_taken`，事务回滚，邀请码保持未用）；把邀请码标记已用并记录账号和时间。
5. 成功：建会话并返回与登录相同的回复（`status: ok`、会话 token、许可、每日额度），主播注册即登录。

只有 `invalid_invite` 计入限流失败次数。来源取值：连接地址是本机回环时取 `X-Real-IP`（反向代理会设置），否则取连接地址。

新增状态码（线上蛇形命名）：`invalid_invite`（HTTP 403）、`username_taken`（409）、`invalid_username`（400）、`weak_password`（400）。

### 管理命令

| 命令 | 作用 |
|---|---|
| `invite create --count N --profile P [--note T]` | 批量生成并**一次性打印**；`--count` 1–500 |
| `invite list` | 列出每个码的状态：未用 / 已用（账号名、时间）/ 已作废 |
| `invite revoke --code C` | 作废一个未使用的码；已用的码不可作废 |
| `create … --no-expiry` | 手动建号时可选「长期有效」（与 `--days` / `--valid-until` 三选一） |

## 2. 客户端

- `AuthCode` 增加 `InvalidInvite`、`UsernameTaken`、`InvalidUsername`、`WeakPassword`；`IAuthApi.RegisterAsync(AuthRegisterRequest, ct)`；`AuthApiClient` 调 `v1/auth/register`，回复解析与登录一致（含额度字段缺失即传输错误的规则）。
- `CloudLicense.RegisterAsync(inviteCode, username, password)`：成功后状态与登录完全一致。登录与注册共用同一段「拿到回复 → 建立许可」逻辑。
- 提示文案：
  - `invalid_invite`：邀请码不对或已经用过，请向发放者确认
  - `username_taken`：这个账号名已被占用，换一个试试
  - `invalid_username`：账号名需要 3 到 20 位，只能用字母、数字、下划线和横线
  - `weak_password`：密码至少需要 8 位
- `AccountViewModel`：新增 `IsRegistering`、`ToggleRegister()`、`RegisterAsync(invite, username, password, confirm)`；本地先校验两次密码一致、密码长度、账号名格式，不合格直接提示、不发请求。`ValidUntilText`：`AccountValidUntil >= 2100-01-01` 显示「长期有效」，否则「有效至 …」。
- 登录页（WPF `LoginView`）：登录表单下方文字链接「没有账号？用邀请码注册」，切换为注册表单（邀请码、账号名、密码、再输一次密码、「注册并登录」）；再点「已有账号？去登录」切回。沿用 `StreamerSkin.xaml` 的样式。

## 3. 共用安装包

- `DistributionProfile.Account` 变为可选：缺省时登录页不预填账号名；`Validate` 不再要求 `account`。
- 共用包的 `profile_id` 表示这批包的编号（如 `shared-001`），邀请码绑定它；老的专属包与专属账号不受影响。
- 打包器无需改动逻辑（包名用 `profile_id`；manifest 里 `account` 为空串）。
- 示例档案与文档补充共用包写法。

## 4. 部署

服务端新版本按 `docs/auth/deploy-tencent-server.md` 的升级步骤发布；数据库在启动时自动新增 `invites` 表。

## 5. 测试

- 服务端：邀请码规范化与格式；注册成功（建账号、标记已用、返回额度与 token）；一个码只能用一次；用户名冲突回滚且码仍可用；档案不符 / 已作废 / 不存在都返回同一状态；账号名与密码校验；`invalid_invite` 触发限流；来源取自 `X-Real-IP`；长期有效账号的许可不被到期日截短、额度正常。
- 管理命令：`invite create/list/revoke`、数量上限、`create --no-expiry`。
- 客户端：`RegisterAsync` 成功与各失败码的文案；本地校验不发请求；长期有效的显示；回复缺额度字段按传输错误；端到端（真实服务 + 真实客户端）注册后心跳与额度可用。
- 档案：缺 `account` 可通过校验。
- WPF 登录页只能编译验证，真机观感需在 Windows 上确认。
