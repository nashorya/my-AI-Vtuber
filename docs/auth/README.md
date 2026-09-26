# 账号鉴权与主播私有包（v0.36.0）

对应需求：AUTH-01–09、DIST-01–06（`REQUIREMENTS_AUTH_BUGFIX_PLUGINS_V1.md`）。

## 它做什么，不做什么

- 自有鉴权服务只回答一件事：这个账号现在能不能继续用云端。它不接收、也不转发音频、文本、图片或 TTS。
- 主播客户端拿到许可后直连厂商。对话链路上只读内存里的许可状态，不会每句话都去请求鉴权服务。
- 私有包里带着该主播的厂商 Key。**账号控制的是正常客户端的使用资格，挡不住有人从安装包里取出 Key、改客户端后直连厂商。** 禁用账号不等于撤销 Key；要彻底停掉某个 Key，必须在厂商后台撤销或轮换。
- 首版只做"有效到某日"。按陪播小时扣时长的 `AUTH-TIME-01` 没有实现，界面也不显示剩余小时。

## 组成

| 部分 | 位置 | 说明 |
|---|---|---|
| 鉴权服务 | `AIVTuber.AuthServer/` | ASP.NET Core Minimal API + SQLite，三个接口：login / heartbeat / logout |
| 管理 CLI | 同一程序的 `admin` 子命令 | 在服务器本机操作数据库，不进主播安装包 |
| 客户端 | `AIVTuber.Core/Auth/` | `CloudLicense`（许可与续验）、`DistributionProfile`（私有档案）、`CredentialRevisionGuard` |
| 打包器 | `tools/AIVTuber.Packager/` | 用一份公开构建加仓库外的私有档案，生成主播专属包 |

## 部署鉴权服务

需要 .NET 10 运行时，外面加一层 HTTPS 反向代理（Caddy、nginx 都可以）。服务本身只监听内网或本机端口。

```bash
dotnet publish AIVTuber.AuthServer -c Release -o /opt/aivtuber-auth
```

```bash
Auth__DatabasePath=/var/lib/aivtuber-auth/auth.db /opt/aivtuber-auth/AIVTuber.AuthServer --urls http://127.0.0.1:5080
```

可调配置（环境变量 `Auth__<名字>`，或 `appsettings.json` 的 `Auth` 节）：

| 名字 | 默认 | 含义 |
|---|---|---|
| `DatabasePath` | `auth.db` | SQLite 文件。里面只存密码哈希（ASP.NET Core `PasswordHasher`）和会话 token 的 SHA-256 摘要 |
| `LeaseSeconds` | 180 | 单次许可最长多久，永远不超过账号截止时间 |
| `HeartbeatSeconds` | 60 | 建议客户端多久续验一次 |
| `MaxFailedLogins` / `FailedLoginWindowMinutes` | 5 / 15 | 同一账号连续输错多少次后暂时锁定 |

60/180 秒是需求稿的初始建议值，没有经过线上验证，可以按需调整。

服务日志只记录账号 ID、状态、profile 和版本号，不记录密码、token 或请求体。

## 管理账号

所有命令都在服务器上执行。密码从标准输入读（`--password-stdin`），不传参数就自动生成一个只打印一次的密码，都不会进 shell 历史。

```bash
AIVTuber.AuthServer admin --db auth.db create --username alice --profile streamer-017 --days 30 --password-stdin
```

| 命令 | 作用 |
|---|---|
| `create --username U --profile P (--days N \| --valid-until ISO) [--note T] [--password-stdin]` | 建账号，绑定到一个 profile |
| `extend --username U (--days N \| --valid-until ISO)` | 续期。`--days` 从"当前截止时间和现在较晚的那个"往后加。客户端下次心跳生效，不用换包 |
| `disable` / `enable --username U` | 禁用时同时注销该账号所有会话，客户端下次心跳（约 60 秒内）停止 |
| `set-password --username U [--password-stdin]` | 重置密码，同时注销所有会话 |
| `set-min-revision --username U --revision N` | 拒绝凭据修订号低于 N 的旧包（换 Key 后用） |
| `list` | 列出账号状态，不输出哈希 |

## 生成主播专属包

私有档案（格式见 `docs/auth/profile.example.json`，里面都是假值）**必须放在仓库外**，生成的包也要输出到仓库外。打包器发现路径在仓库内会直接拒绝。

1. 在 Windows 上生成公开构建（不带 Key）：`publish.sh`，或者 `dotnet publish App/App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`。
2. 生成专属包：

```bash
dotnet run --project tools/AIVTuber.Packager -- stream --profile ~/private/streamer-017.json --app-dir App/bin/Release/net10.0-windows/win-x64/publish --out ~/private/out
```

输出 `AIVTuber-v0.36.0-streamer-017-c2.zip` 和对应的 `.sha256`。包里除了公开程序，还有 `distribution/profile.json` 和 `distribution-manifest.json`。manifest 记录版本、source_commit、profile、凭据修订号、核心文件哈希，Key 只显示末四位。

打包器会拒绝以下情况：
- 档案校验不过：缺字段、指向本地 ASR、自托管 TTS 或本机 LLM；
- 公开构建里混有 `config.json`、`distribution/`、`distribution-state.json`、`memory.db`；
- 公开构建的文本文件里出现档案里的 Key，或者长得像 `sk-…` 的 Key。

**打包器只在运营者本机运行，不接 CI，不上传任何地方。** 生成的 zip 请私下交给对应主播。

### 厂商配置与音色目录（A2）

- **对话（LLM）**：`base_url` 和 `model` 可以省略，但只在 `provider` 是有内置地址的厂商（`deepseek`、`gemini`）时才会用内置值补全；其它厂商省略 `base_url` 会被打包器和客户端直接拒绝，不会沿用主播电脑上 `config.json` 里的地址。
- **识别 / 语音的 `model`、`app_id`、`group_id`**：档案没写就用厂商默认值，同样不会沿用 `config.json`。
- **音色**：`providers.tts.voice_id` 是默认音色；`providers.tts.voices` 是主播可以在「我的 AI」里选的目录，每项需要 `id`（界面用的稳定编号）、`name`（显示名）、`voice_id`（厂商音色 ID），`description` 可选。写了目录就必须写默认音色。主播界面只显示名字和说明，不显示厂商音色 ID。
- 主播选的音色保存在本机 `config.json`；换凭据修订号时，只要新档案里还有这个音色就保留。新档案去掉了这个音色时，会改回默认音色并在界面提示，不会随便换成列表第一项。
- MiniMax 账号会在主播点「刷新列表」时用 `POST /v1/get_voice` 核对目录里的音色是否可用（只标记可用 / 不可用，不会把账号里的其它音色加进列表）。其它厂商暂时只显示目录，不做在线核对。

### 换 Key（不重编程序）

1. 在厂商后台生成新 Key，撤销旧 Key。
2. 档案里把 `credential_revision` 加 1，填入新 Key。
3. 生成凭据更新包：`dotnet run --project tools/AIVTuber.Packager -- credentials --profile ~/private/streamer-017.json --out ~/private/out`，把 zip 发给主播，解压覆盖到安装目录。
4. 需要让旧包立刻失效时，执行 `admin set-min-revision --username U --revision <新修订号>`。

客户端会记录本机用过的最高修订号（`distribution-state.json`）。拿更旧修订号的包回滚时会明确报错，不会悄悄用回已作废的 Key。拿别的主播的包运行也会报错。

## 客户端行为

- 私有包启动后先显示登录页。账号预填为档案里的 `account`。未登录时可以点"先看看设备和帮助"，但所有云端入口（ASR、对话、记忆提取、PK 整理、音色试听）都关着。
- 登录后显示主播控制台（陪播台 / 我的 AI / 直播设置 / 账号与帮助），没有厂商、Key、地址、模型和本地模型设置。公开构建仍是原来的开发者控制台。
- 每次启动都必须在线登录一次，磁盘上不保存"上次登录成功"。
- 后台约每 60 秒续验一次，失败时 10 秒后重试。
- 短暂断网时继续使用尚未到期的许可，最长 180 秒；到期后停止，并提示"暂时无法验证登录状态"。账号本身到期时提示"使用期限已结束，请联系发放者续期"，两种情况分开显示。
- 明确收到禁用、到期或会话注销时立即停止，断网宽限不适用于这些情况。
- 许可时长按服务器给的时长、用本机单调计时计算，改电脑日期不会延长许可。
- 失效或退出时，先在本地停声、清空待播和排队输入、取消在途 ASR，再在后台等厂商请求收尾。重新登录不会把失效前开始的回复继续播出来。
- 分发版不启动本地 ASR sidecar，也不加载本地 ONNX 向量模型。记忆检索改用字符串相似度，已有记忆数据保留不删。
- 厂商配置固定来自档案：`config.json` 里不保存这些 Key；主播界面只接受白名单里的设置字段，其它字段（厂商、Key、地址、模型）的修改请求会被整条拒绝，运行时也会再按档案整体覆盖。

## 升级与回滚

- 公开程序包不含 `config.json`、`distribution/`、`distribution-state.json` 和 `memory.db`，解压覆盖升级不会动个人配置、凭据和记忆。
- 回滚程序同样不会恢复旧 Key。旧 Key 只可能来自旧的档案文件，而修订号守卫会拒绝它。
- 升级前建议备份 `config.json`、`memory.db` 和 `distribution/`。

## 状态码

| status | HTTP | 客户端提示 |
|---|---|---|
| `ok` | 200 | — |
| `invalid_credentials` | 401 | 账号或密码错误 |
| `session_revoked` / `invalid_session` | 401 | 登录已被注销 / 已失效，请重新登录 |
| `disabled` | 403 | 账号已被停用，请联系运营 |
| `expired` | 403 | 账号已到期，请联系运营续期 |
| `profile_mismatch` | 403 | 这个安装包不属于这个账号 |
| `credential_revoked` | 403 | 安装包里的凭据版本已作废 |
| `rate_limited` | 429 | 登录失败次数过多，请稍后再试 |

网络不通、超时，或者反向代理返回非 JSON（比如 502 页面）时，客户端按"连不上鉴权服务"处理，不会当成账号被拒绝。
