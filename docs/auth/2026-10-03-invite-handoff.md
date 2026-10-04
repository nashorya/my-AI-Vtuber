# 邀请码注册接续记录（2026-10-03）

接续 Claude 会话 `bf43d2cd-1a45-429f-9430-7c50b133ee31`，分支 `feat/invite-registration`，基线 `ecac7a2`。原有七个实现任务已提交；本轮修复原审查遗留问题。本轮代码修复已提交为 `38219fe`，服务端已升级，后续状态见下文。

## 本轮改动

- 注册来源的预算检查、邀请码预检、错误计数在同一锁内完成。测试强制 16 个请求同时阻塞于数据库/入口锁：修复前 16 次全部通过预算，修复后只有 5 次返回错误邀请码，其余 11 次限流。
- 登录与注册使用独立失败表；错误邀请码、已占用账号名在密码计算前返回。注册密码哈希最多同时执行 2 个，锁外执行；数据库事务仍复查邀请码和账号唯一性。
- 注册失败来源最多 4096 个，定期回收过期窗口；容量满时新来源收到 429。格式错误与账号名占用不计入邀请码错误预算。
- 注册响应丢失时提示先使用刚才的账号密码登录；注册限流/缺失信息使用对应文案；成功后清空已用邀请码。
- 修正文档中邀请码只打印一次、逐账号版本限制能完整停用共享包的错误描述。

## 验证证据

- 新增并发预算及键冲突测试先失败再通过；客户端三种错误提示测试先失败再通过。
- 使用现有 xUnit 2.9.3 的进程内 discovery/executor 执行 **190 项相关测试，0 失败、0 跳过**。涵盖服务端、额度、管理命令、客户端许可、AccountViewModel、档案、打包器及运行时鉴权/额度门控。
- Windows WPF Release 编译成功，0 错误、2 个现有警告；没有 Windows 实机视觉验收。
- Linux x64 框架依赖发布成功；核验包内 DLL、依赖及 ELF x86-64 SQLite 库。未在 Linux 上执行此发布包。
- 独立只读代码审查未发现阻止提交的重要问题。
- `git diff --check` 通过。

权限开放前，本会话沙箱不允许套接字：普通 `dotnet test` 的 VSTest 通信端口报 `Permission denied`；改用进程内 xUnit 完成上述相关测试。完整测试尝试出现本地网络、资源路径和平台相关失败后中止，**不能声称全套通过**；失败名称和实际输出保存在 `full-tests-interrupted.log`。HTTP AuthEndToEndTests 单独尝试未完成，已中止。仍需在正常环境执行完整测试和真实 HTTP 验收。

## 交付文件

`artifacts/invite-registration-release/`：

- `auth-server-linux-x64.tgz`：服务端发布包（通过 `/opt/dotnet/dotnet AIVTuber.AuthServer.dll` 启动）。
- `review-fixes.patch`：本轮七个源文件/测试/文档的补丁，不含原有 `.claude` 或其他未跟踪文件。
- `SHA256SUMS`：包和补丁的 SHA-256。
- `focused-tests.log`、`windows-build.log`、`publish.log`、`full-tests-interrupted.log`：验证证据。

用户随后开放权限，已完成正式测试、提交和服务器升级。PR #28 经 GitHub 核验是已合并的界面与额度改版，并非邀请码注册 PR；本分支需要新建 PR。

## 后续发布与验收参考

1. 审阅本轮工作区差异，仅提交本轮文件及本记录，保留原有 `.claude` 等用户文件；为邀请码分支新建 PR。按原计划运行完整测试及 Windows 质量门禁。
2. 上传前执行 `shasum -a 256 -c SHA256SUMS`，上传本次 tgz，不要误用原有 `artifacts/auth-server-linux-x64/`。
3. 按 `deploy-tencent-server.md` 升级：先核对线上 systemd、nginx 状态，备份数据库；另将 `/opt/aivtuber-auth` 完整打包备份。停服务后解压新包、修正文件属主，再启动。
4. 检查服务日志、HTTPS 注册/登录接口和迁移后的 `invites` 表。创建专用验收档案的邀请码，完成注册→登录→心跳→同码重用失败→禁用验收账号；勿使用主播正式邀请码测试。
5. 若升级失败，停止服务并恢复旧程序目录后启动；本次数据库变化仅新增表，通常无需回滚数据库。不要覆盖已经产生新注册数据的数据库。
6. 在 Windows 实机验证注册表单、密码确认错误、成功登录、长期有效显示；生成并验证共用安装包。服务端 tgz 不等于 Windows 主播安装包。

## 明确保留的限制

- 逐账号 `set-min-revision` 不是全局包退役策略，旧包仍可能用未消费邀请码注册；没有新增全局最低版本策略。
- 旧登录限流表自身的并发/容量问题属于既有范围，本轮仅隔离并修复注册路径。
- 不将本地编译、190 项相关测试或静态审查等同于实际服务器上线或完整安装包交付。

## 权限开放后的执行结果

- 正式 `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -p:PlatformTarget=AnyCPU`：1077 项，1062 通过、13 跳过、2 失败。鉴权与 StreamerConsole 相关 196 项均通过，包含 6 项真实 HTTP 端到端测试。
- 两个既有失败：`DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`（预期取消异常，实际快速连接拒绝）；`StreamerConfigTests.StreamerDraft_ContainsNoManagedServiceFields`（macOS 进程名 `iCloudDriveFileProvider` 命中测试中宽泛的 `provider` 子串检查）。未把它们算作通过。
- 线上程序升级成功：`aivtuber-auth`、`nginx` 均 active，邀请码表存在。线上 DLL SHA-256：`168721bed09cf51baa020ff0fffadd282e9d90d509e38ecb0c202c37aa40627b`，与发布包一致。
- 备份位置：`/var/backups/aivtuber-auth/releases/invite-20261003T080951Z/`，含 `auth-before.db` 和 `program-before.tgz`。升级失败回滚保护只恢复程序，不覆盖新注册数据；本次未触发回滚。
- 正式测试 TRX、部署输出保存在交付目录。
- 公网 HTTPS 验收于 2026-10-03 16:11（北京时间）完成：注册 200、同码重用 403、登录 200、心跳 200、退出 204、已注销会话 401、停用账号 403；长期有效日期及默认 3600 秒额度正确。专用验收账号已停用，未使用正式主播邀请码。详见 `public-smoke.json`。
- 尚待 Windows 实机 UI、完整共用主播安装包交付及 Windows CI 验收；不将已上线的鉴权服务等同于完整客户端正式交付。
