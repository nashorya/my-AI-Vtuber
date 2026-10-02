# 第 A 批交付报告：简单账号鉴权 + 主播私有包（v0.36.0 候选）

批次：第 0 批复核 + 第 A 批（AUTH-01–09、DIST-01–06；R05 的最小停止缺口提前修）
分支：`feat/simple-auth-distribution`，draft PR nashorya/my-AI-Vtuber#26
起始 HEAD：`9c5756e`（main）；结束 HEAD：见 PR 最新提交
版本：`Directory.Build.props` → 0.36.0（未打 tag，未发 Release）

## 需求对照

| ID | 结论 | 修改 / 提交 | 回归测试 | 结果 |
|---|---|---|---|---|
| AUTH-01 | 已实现：CLI 建号、重置密码、启用/禁用、设置和延长到期、作废旧凭据修订；不存明文密码 | `AIVTuber.AuthServer/*` · 7a327e0 | `AuthServiceTests`、`AdminCliTests` | 通过 |
| AUTH-02 | 已实现：登录页显示账号、期限和错误原因；未登录可进设置/诊断，云端入口全部关闭 | `AccountViewModel`、`LoginView`、`MainWindow`、`App.xaml.cs` · 6efc523 | `AccountViewModelTests`、`RuntimeCloudGateTests.NotSignedIn_*` | 通过；WPF 界面**未在 Windows 上实际点过** |
| AUTH-03 | 已实现：服务端判定有效期；许可 ≤180s 且不超过账号截止时间；客户端用"服务端时长 + 本机单调计时"计算，改系统时间无效 | `AuthService.Granted`、`CloudLicense.ApplyLeaseLocked` · 7a327e0, a7a0095 | `Login_ReturnsLeaseCappedByAccountExpiry`、`WallClockRollback_DoesNotExtendLease`、`LeaseFollowsServerDurationNotLocalWallClock` | 通过 |
| AUTH-04 | 已实现：拒绝、到期、退出时先本地停声、清空排队、取消在途 ASR，再在后台等厂商收尾；旧回合不能在重新登录后提交 | `BotRuntime.OnCloudRevoked`、`BotOrchestrator.BeginInterrupt`、`RequestCoordinator.CancelCurrent` · b73a44c, 2ea4054 | `InterruptLocalStopTests`、`RuntimeCloudGateTests.Revocation_*`、`Relogin_DoesNotRevivePreviouslyStartedWork` | 通过；已做变异检查 |
| AUTH-05 | 已实现：首次和重启都必须在线登录；断网时只用未到期的许可；网络错误与账号拒绝提示分开 | `CloudLicense`、`AuthApiClient` · a7a0095, cf18a5e | `NetworkLoss_KeepsOldLeaseOnlyUntilItExpires`、`CloudLicenseLoopTests`、`UnreachableService_IsTransportFailureNotDenial` | 通过；循环测试已做变异检查 |
| AUTH-06 | 已实现：云端入口只读内存里的 `IsAllowed`/`Epoch`，续验在后台循环里做 | `ICloudAccess`、`BotRuntime` 门禁 · 2ea4054 | `RuntimeCloudGateTests`（门禁本身没有 HTTP 调用） | 通过 |
| AUTH-07 | 已实现：包里的 profile_id 由服务端核对；拿错包提示"profile 不匹配"；本机还会拒绝另一个 profile 的包 | `AuthService.Check`、`CredentialRevisionGuard` · 7a327e0, 6fdcdde | `Login_WithAnotherStreamersPackage_ReportsProfileMismatch`、`WrongPassword_And_WrongPackage_AreDistinguishable`、`RevisionGuard_IsPerProfile` | 通过 |
| AUTH-08 | 已实现：按账号限制登录失败次数；服务日志只有账号 ID、状态、profile、版本；鉴权客户端用独立 HttpClient，不设默认 Authorization | `AuthService`、`AuthServerApp`、`AuthApiClient` | `RepeatedFailures_AreRateLimited_UntilWindowPasses`、`Client_DoesNotCarryADefaultAuthorizationHeader`；真实进程冒烟时 grep 服务日志，没有密码和 token | 通过 |
| AUTH-09 | 已实现：分发模式拒绝本地 ASR、dots TTS 和本机 LLM；不启动 ASR sidecar，不加载 ONNX；记忆降级为字符串相似度，数据保留 | `DistributionProfile.Validate`、`BotRuntime.InitMemoryAsync/StartLocalAsrServerAsync` | `Profile_RejectsLocalInference` | 档案校验有测试；跳过 ONNX / sidecar 属于**静态修改，没有单独测试** |
| DIST-01 | 已实现：同一构建加仓库外档案；不建主播分支，不写源码常量 | `DistributionProfile`、打包器 | `TwoStreamers_FromSameBuild_HaveSameCoreHashesAndOwnKeys` | 通过 |
| DIST-02 | 已实现：档案记录 profile_id、account、credential_revision、credential_scope、provider 配置 | `docs/auth/profile.example.json`（假值） | `Profile_OverridesProviderSettingsAndKeys` | 通过 |
| DIST-03 | 已实现：打包器生成专属 zip、manifest 和 SHA-256；拒绝仓库内路径，不上传 | `tools/AIVTuber.Packager` · 84309be | `PackagerTests`（9 个） | 通过 |
| DIST-04 | 已实现：设置页/Web 界面只有 `apiKeySet`；manifest 和诊断只显示 Key 末四位；`config.json` 不保存托管 Key | `DistributionProfile.KeyHint/Describe/StripManagedSecrets` | `ConfigManager_DoesNotPersistManagedKeys_AndReloadUsesCurrentProfile`、`Stream_ProducesZipManifestAndChecksum_WithoutPrintingKeys` | 通过 |
| DIST-05 | 已实现：换 Key 只需发新档案（`credentials` 子命令）；本机拒绝回滚到更低修订号；服务端 `set-min-revision` 拒绝旧包 | `CredentialRevisionGuard`、`AuthService` | `RevisionGuard_RejectsRollbackToOlderCredentials`、`Login_WithRevokedCredentialRevision_IsRejected`、`CredentialsOnly_ProducesSmallUpdateWithoutProgramFiles` | 通过 |
| DIST-06 | 已实现：A、B 两个包的 Key 互不串；公开构建里如果混有个人文件或像 Key 的内容就拒绝打包 | 打包器 | `TwoStreamerPackages_CarryTheirOwnKeys`、`PublicBuildCarryingPersonalFiles_IsRefused`、`PublicBuildWithEmbeddedKey_IsRefused` | 通过 |
| R05（仅 main 的停止缺口） | 已修：`Interrupt()` 原来要等在途任务结束才停播放 | b73a44c | `InterruptLocalStopTests`（先 RED 后 GREEN） | 通过；R05 其余内容在 PR #25，留给第 C 批 |
| AUTH-TIME-01 | 未实现，界面不显示剩余小时 | — | — | 延期 |

计划 3.4 的验收场景对照：

| 场景 | 覆盖 |
|---|---|
| 未登录 / 密码错误 / 到期 / 账号与 profile 不符 → 不启动云端任务 | `RuntimeCloudGateTests.NotSignedIn_*`；`CloudLicenseTests.Login_Denied_*`；端到端 HTTP |
| 登录、后台续验、续期不换包，语音中没有逐轮鉴权等待 | `Heartbeat_ExtendsLeaseWithoutNewEpoch`、`Extending_Validity_TakesEffectOnNextHeartbeat_WithoutNewLogin`、`CloudLicenseLoopTests` |
| 断网直到许可失效、离线重启、时间回拨 | `NetworkLoss_*`、`CloudLicenseLoopTests`、`WallClockRollback_*`；离线重启：客户端不持久化授权（静态复核），启动只能走 `LoginAsync` |
| TTS 播放中禁用或退出 → 本地停止不依赖取消 ACK，旧回包没有新输出 | `Revocation_DuringSpeech_StopsLocallyWithoutWaitingForTheProvider`（TTS 故意不响应取消） |
| 退出后旧 heartbeat 才成功返回 | `HeartbeatSucceedingAfterLogout_DoesNotRestoreAccess`、`LoginResponseArrivingAfterLogout_IsDiscarded` |
| A/B 专属包及公共升级 | `TwoStreamers_*`、`PublicBuildCarryingPersonalFiles_IsRefused`、`RevisionGuard_*` |
| 分发模式启动与登录不加载本地推理 | 档案校验有测试；runtime 跳过 ONNX 和 sidecar 仅静态复核 |

## 验证层级

- **静态复核**：确认厂商客户端（DashScope/Qwen 连接池、MiniMax WS 等）都是用到时才建连，未登录时 runtime 虽已启动也不会连厂商；日志和导出不输出 Key；Web 配置页只拿到 `apiKeySet`。
- **离线组件测试（macOS arm64）**：`dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU` → 通过 695、失败 1、跳过 14，共 710。唯一失败是基线里就有的 `DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`（macOS 对 `localhost:1` 立即拒绝连接，Windows 不会）。本批新增 86 个测试。
- **生产入口 fake 集成**：`RuntimeCloudGateTests` 从 `AcceptTalkLine` 和生产的回合门订阅进入，用的是真实 `BotRuntime` 和 `BotOrchestrator`，只替换厂商客户端、播放设备和账号状态。`AuthEndToEndTests` 让真实客户端和真实服务端在 loopback 上走 HTTP。
- **变异检查**：去掉 runtime 门禁后有 3 个门禁测试失败；去掉吊销时的停声，`Revocation_DuringSpeech_*` 失败；去掉续验循环的每秒到期检查，`CloudLicenseLoopTests` 失败。
- **真实进程冒烟（macOS）**：发布服务端 → 管理员 CLI 建号 → curl 登录（200，许可 180s）→ 心跳 200 → 禁用 → 心跳 403 `disabled` → 密码错误 401。服务日志里 grep 不到密码和 token。
- **Windows CI**（draft PR 触发 `Windows quality gate`，Release 构建含 WPF App 和全量测试）：
  - 第一轮（服务端 + 停止修复）通过；
  - 第三轮 707 通过、1 失败，失败的是 `VtsTrackingTests.ModeSwitchReplacesWriterAndDisablingStopsInjection`：400ms 真实时间窗口内要求 5–18 次注入，实际 0 次。这是本批没改过的 VTS 连续控制测试，属于时序抖动；本批新增的真实时钟测试可能加重了并行负载。
  - 最新一轮结果见 PR。
- **WPF 编译**：macOS 上 `dotnet build App/App.csproj -c Release -p:EnableWindowsTargeting=true` 成功，Windows CI 也能编过。
- **真实厂商**：未测。仓库里只有假 Key，本批没有调用任何厂商接口。
- **Windows 实际播放 / 界面操作**：未执行。登录页、状态栏、禁用时停声的真实声卡表现都需要在 Windows 上手动验证。

## 剩余阻断与已知限制

- **没有部署任何鉴权服务，也没有建真实账号。** 需要运营者按 `docs/auth/README.md` 部署（HTTPS 反向代理 + SQLite），然后用 CLI 建号、打包。
- 打包 Key 的边界：账号只控制正常客户端的使用资格，挡不住从包里提取 Key；禁用账号不等于撤销 Key。
- 登录失败频控按账号计数：知道用户名的人可以故意输错，把该账号锁 15 分钟。首版接受这个取舍。
- 档案校验允许 `http://` 的 auth_server（方便本地测试）；生产环境应该用 HTTPS。
- B 站 cookie（sessdata/bili_jct/buvid3）本来就会明文推给本地 WebView 配置页。这是现有行为，不是厂商 Key，本批没有改。
- `BotRuntime.DisposeAsync` 仍然会等在途任务收尾；遇到永不响应的厂商时，退出会卡住。这是现有行为，第 C 批处理。
- 吊销时换掉的 `CancellationTokenSource` 没有 Dispose，每次吊销会泄漏一个很小的对象，可以接受。

## 配置、数据迁移与回滚

- 公开构建没有档案，行为不变：`UnrestrictedCloudAccess`，`config.json` 照常保存 Key。
- 私有包：厂商配置以 `distribution/profile.json` 为准；`config.json` 不再保存这些 Key，其余配置和 `memory.db` 不动。
- 回滚：直接回到 `main@9c5756e` 的公开构建即可，不需要迁移数据。私有包回滚会被修订号守卫拦下，这是预期行为。
- 代码回滚：`git revert` 本分支的提交，不要用 `reset --hard`。

## 可以发布什么，不能宣称什么

- 可以作为 **v0.36.0-rc** 候选：鉴权服务、客户端门禁、私有打包，前提是先在 Windows 上把"登录 → 说话 → 禁用 → 停声"走一遍。
- 不能宣称：已在真实厂商上联调、已在 Windows 实际播放中验证、Key 无法从包里提取、按小时计费（AUTH-TIME-01）。
- 实时化路径（PR #25）不在本批，也没有合入。

## 下一批

第 B 批：在 PR #25 分支（或其修复分支）上修 R01/R02/R03/R04/R08，并补官方协议 fixture。等你确认后再开始。
