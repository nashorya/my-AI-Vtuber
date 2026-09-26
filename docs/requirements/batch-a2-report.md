# 第 A2 批交付报告：面向主播的分发界面（v0.36.0-rc.2 候选）

批次：A2（A2-1 → A2-2 → A2-3 → A2-4），依据 `PR26_REVIEW_STREAMER_UX_AND_OPUS_FIX_PLAN.md`
起始 HEAD：`49f1d04`（PR nashorya/my-AI-Vtuber#26 `feat/simple-auth-distribution` 的最新提交，审查固定提交同一个）
分支：`claude/new-session-i4wnxx`，直接建立在 PR #26 之上（未合并 main，也未包含 PR #25）
版本：`Directory.Build.props` → `0.36.0-rc.2`（未打 tag，未发 Release，未上传任何私有包）

## 结论

分发版现在有独立的主播控制台（陪播台 / 我的 AI / 直播设置 / 账号与帮助），前端和后端都没有厂商、Key、地址、模型、本地模型入口；审查里的 U01–U08 和 V01–V03 都有代码修改和离线测试。**Windows 实际界面、真实声卡路由、真实厂商调用都没有做**，下面逐项标明。

## 需求对照

| 问题/需求 | 原因 | 修改路径 | 提交 | 测试（真实结果） | 人工实测 | 未完成项 |
|---|---|---|---|---|---|---|
| U01 分发模式进入开发设置 | 分发版沿用完整开发控制台 | 新 `App/WebUi/wwwroot/streamer.{html,js,css}`；`StreamerConsoleController`（Core，唯一可用命令表）；`ConfigViewModel.BuildStreamerDraft/ApplyStreamerPatch`（白名单字段）；`WebConsoleHost` 分发版加载主播页并只走控制器；`MainWindow` 按 `DistributionMode` 选择 | f5a7bb5, 19f116d | `DistributionUi_ContainsNoManagedProviderFields`、`StreamerPage_SendsOnlyCommandsTheControllerOffers`、`DeveloperCommands_AreRefusedOnTheStreamerPage`（5 种构造的旧命令）、`DistributionPatch_RejectsManagedServiceChanges`（5 种）、`StreamerDraft_ContainsNoManagedServiceFields`、`Settings_AndState_NeverCarrySecrets` —— 通过 | 未在 Windows 上打开 | 公开版开发控制台保持原样；分发版不再提供记忆页（未列入四页信息架构） |
| U02 音色保存被档案覆盖 | `ApplyTo` 每次强写 `voice_id` | `DistributionProfile`：`voice_id` 改为默认音色，新增 `providers.tts.voices` 目录与 `ResolveVoice`；`ConfigManager.LastLoadNotice`、`BotRuntime.LastApplyNotice`；`ConfigViewModel.SaveAsync` 生效后用运行时实际配置重置草稿 | f5a7bb5 | `SelectedVoice_PersistsAndMatchesEffectiveTts`（A→B→保存→运行时为 B→只改人设仍 B→重启仍 B）、`CredentialUpdate_KeepsStillOfferedVoice_AndExplainsWhenItIsGone`、`UnknownVoiceChoice_IsRejected_NotReplacedWithSomethingElse` —— 通过；变异检查：恢复强写后 2 个失败 | 无 | — |
| U03 托管 Key 与用户地址拼接 | 档案省略 `base_url` 时沿用 config.json 地址 | `DistributionProfile.ResolveLlmRoute`：地址/模型只来自档案或内置厂商预设，解析不出就报配置错误；ASR/TTS 的 model/app_id/group_id 一律按档案写（缺省=厂商默认） | f5a7bb5 | `MissingManagedEndpoint_DoesNotInheritUserEndpoint`（含直接向运行时提交篡改配置）、`CustomProviderWithoutEndpoint_IsAConfigurationError_NotASilentMerge`、`ManagedAsrAndTtsModels_DoNotInheritConfigJson` —— 通过；变异检查：恢复条件覆盖后失败 | 无 | — |
| U04 WPF 登录页与 WebView2 同区叠加 | 标准 WebView2 是 HwndHost（airspace） | `MainWindow.RefreshAccount`：登录页显示时折叠 WebView 宿主，反之亦然；`LoginView.FocusFirstField`；状态栏去掉专属包编号与版本 | 19f116d | 只有编译验证（`dotnet build App/App.csproj -p:EnableWindowsTargeting=true` 0 错误） | **未验证**：冷启动/登录/退出/到期/重新登录、100/125/150% 缩放、Tab/回车 | 需 Windows 手工走一遍；另需确认 WebView2 在初始折叠状态下能正常初始化 |
| U05 云端 ASR 被报未连接 | 首页状态只看本地 sidecar | `BotRuntime.CurrentAsrHealth`（未检测/可用/正在识别/暂时不可用/已暂停），由真实识别结果驱动；本地 sidecar 只影响本地模式 | f5a7bb5 | `CloudAsrHealth_NotDerivedFromLocalSidecar`、`CloudAsrFailure_IsUnavailable_AndRecoversOnNextSuccess`、`InFlightRecognition_IsReportedAsRecognizing`、`SignedOut_ReportsPaused_NotBroken` —— 通过 | 无 | — |
| U06 原始异常进界面 | 多处直接显示 `ex.Message`/`ex.ToString()` | `Diagnostics/UserFacingError.cs`（错误对象 + 唯一映射 + 诊断日志）、`DiagnosticRedactor`；`App.ShowFatalError`/`LoadConfigSafe`、`WebConsoleHost` 消息异常、`WebConsoleView` 初始化失败、`CloudLicense` 登录网络错误、设置应用失败、试听失败都改成「问题 + 动作 + 诊断编号」；同一对话框不叠弹 | f5a7bb5, 19f116d | `UserErrors_DoNotContainRawPayloadsOrSecrets`（超时/401/403/429/设备/取消/未知）、`Redactor_RemovesSecrets`（7 类）、`PipelineErrors_AreShownMappedWithDiagnosticId`、`Diagnostics_AreRedacted`、`SavingFailed_IsNotReportedAsApplied` —— 通过 | 未在 Windows 上看对话框 | 只有 401 判定为「凭据不可用」，403/429 保持「未知」+ 诊断编号（未做各厂商错误码识别）；公开版开发控制台的运行时事件列表仍显示原始文本 |
| U07 账号到期被说成网络问题 | 到期检查只有一种文案 | `CloudLicense`：按服务端「账号剩余时长」在单调时钟上记下截止点；`LicenseStopReason`（AccountExpired / VerificationLost / Denied / SignedOut）；主播页按原因给不同动作 | f5a7bb5 | `ExpiringAccount_IsNotNetworkFailure`（30 s 账号、60 s 心跳）、`ExpiringAccount_DetectionUsesMonotonicClockNotWallClock`、`ServerSaysExpiredOnHeartbeat_IsReportedAsAccountEnded`、`AccountEnded_IsShownAsRenewal_NotNetwork`，以及原有 `NetworkLoss_*`、`CloudLicenseLoopTests` —— 通过 | 无 | — |
| U08 人设藏在技术设置里 | 3 行文本框在折叠的「AI 引擎」里 | 「我的 AI」一级页：14 行编辑框、未保存/保存中/已生效/应用失败、撤销、插入示例（只追加不覆盖）、恢复默认（需确认）；首页「编辑人设」「更换音色」一步直达；走原 `SystemPrompt` 保存链 | 19f116d | `PersonaSave_ReachesTheRuntimePromptSource`（保存后运行时构造给下一个 LlmClient 的系统提示以新人设开头；`Identity.SelfName` 不变）、`PersonaEditor_IsAFirstLevelPage_WithALargeEditor` —— 通过；无头 Chromium 脚本点「编辑人设」后焦点在编辑框、连点两次保存时旧回包被忽略 | 未在 WebView2 里操作 | 试聊未做（计划允许） |
| 陪播台三个动作 | 原来只有「截停」 | `BotRuntime.SetCompanionPaused`：暂停后不送识别、不开回合、不说话，设备电平照常；「停止当前发言」沿用原中断；「关闭麦克风监听」只作用于麦克风 | f5a7bb5 | `PausedCompanion_SendsNothingToAsr_AndSaysPaused`、`PauseAndResume_ControlTheRuntime_NotTheAccount` —— 通过 | 无 | — |
| V01 音色目录 | — | 目录来自私有档案（运营核对过的音色）；MiniMax `POST /v1/get_voice`（voice_type=all，Bearer，与 TTS 同域名）只标记可用/不可用，不加入账号里的其它音色；失败保留当前选择 | 9498dbc | `VoiceList_MarksCatalogEntriesFromVendor_AndNeverAddsVendorOnlyVoices`（假 HTTP，核对请求地址/头/体）、`VoiceListFailure_PreservesCurrentSelection`、`VoiceList_NotSignedIn_DoesNotCallVendor` —— 通过 | **未调用真实 MiniMax** | 其它厂商只显示目录、不在线核对 |
| V02 试听≠应用 | — | `VoicePreviewService`：固定短句直接走陪播同一个 TTS 适配器（不经 LLM），每次冻结 TTS 配置；试听不写配置；「应用这个音色」才保存 | 9498dbc | `VoicePreview_DoesNotApplyCandidate`（断言厂商实际收到的 voice 参数为 B、正式音色仍为 A）、`VoicePreview_UnknownChoice_IsNotSubstituted` —— 通过 | 未出声实测 | — |
| V03 取消与路由 | — | 每次试听独立 request id / 取消源 / 短命客户端；新试听、停止、退出、到期都在本地立即停，迟到音频丢弃；未登录拒绝；AI 思考或说话时拒绝而不打断；播放用单独的 `AudioPlayer`（监听设备），不接虚拟麦、字幕、记忆、正式事件 | 9498dbc | `VoicePreview_LateA_DoesNotOverrideB`、`VoicePreview_Revoked_StopsLocally`（退出/停止两种，厂商故意不响应取消）、`VoicePreview_NeedsSignIn`、`VoicePreview_WhileCompanionSpeaks_IsRefusedInsteadOfInterrupting`、`VoicePreview_VendorFailure_IsMappedWithoutRawText` —— 通过；变异检查：去掉迟到音频守卫后 3 个失败 | **未验证**：真实声卡、直播软件是否采集到 | 界面已写明「直播软件采集桌面音频时观众仍可能听到」 |
| V04 缓存 | — | 未实现（计划为可选） | — | — | — | 每次试听都真实合成一次 |
| 应用时机 | 应用会重建管线、可能截断正在说的句子 | 主播页在 AI 思考/说话时拒绝保存并提示「等这一句说完」 | 8890715 | `SaveWhileSpeaking_WaitsForTheTurnBoundary` —— 通过 | 无 | 公开版开发控制台保存时仍可能打断当前句（原有行为） |
| 迟到的 ASR 结果 | 厂商不响应取消时，旧许可下的识别可能在重新登录后到达 | `BotRuntime.RecognizeSegmentAsync`：记下发出时的许可代次，结果返回时代次不同或已暂停就丢弃 | f5a7bb5 | `AsrResultArrivingAfterRelogin_IsNotTreatedAsNewInput` —— 通过 | 无 | — |

## 验证层级

- **静态复核**：逐一核对主播页、设置投影、状态推送里没有 Key、B 站 Cookie、厂商音色 ID、专属包编号；每个试听 TTS 客户端都是新建的，不与正式会话共用连接（DashScope/MiniMax/MiMo/Fish 客户端都没有静态连接池）。
- **离线测试（Linux x64，.NET SDK 10.0.112；仓库固定 10.0.300，本机临时改 global.json，未提交）**：
  `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj` → **通过 761、失败 1、跳过 14，共 776**。唯一失败是基线已有的 `DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`（本批开始前在同一环境先跑了基线：695/1/14，失败的是同一个用例）。本批新增 / 改写的相关测试 78 个（`Distribution/*` 与 3 个许可到期用例），另把 10 处旧断言改成新文案或新版本号（到期、拿错包、凭据作废、登录网络错误、断网到期、版本号）。
- **生产入口 + 假网络/假声卡**：配置用真实 `ConfigManager` → `ConfigViewModel` → `BotRuntime.ApplyConfigAsync`（只替换模块重建）；ASR 用真实 `BotRuntime` 麦克风段入口和 `BotOrchestrator`；试听用 `BotRuntime.CreateVoicePreview`；主播页命令用 `StreamerConsoleController` 接真实运行时、账号 VM 和 `CloudLicense`（假鉴权 API + 手动时钟）。
- **变异检查**：音色强写、BaseUrl 条件覆盖、试听迟到音频守卫三处，各自恢复旧代码后对应测试失败，改回后通过。
- **前端脚本**：`node --check` 通过；用无头 Chromium（Playwright，本地 file://，模拟 `chrome.webview`）喂入状态/设置，检查无脚本错误、首页快捷入口聚焦、旧保存回包与旧试听状态被忽略、直播设置只提交白名单字段、390 px 宽无横向滚动。这不是 WebView2，也不是 WPF。
- **WPF 编译**：Linux 上 `dotnet build App/App.csproj -p:EnableWindowsTargeting=true` 0 警告 0 错误。
- **Windows CI**：本分支没有开 PR，未触发 `Windows quality gate`。
- **Windows GUI 操作**：未做。
- **真实厂商合成 / 音色查询**：未做（没有真实 Key）。
- **实际监听 / 直播输出**：未做。

## 需要在 Windows 上补的实测

1. 冷启动 → 登录页（无网页层遮挡、焦点在账号或密码框、回车能登录）→ 登录后出现主播控制台 → 退出 → 再登录；在 100% / 125% / 150% 缩放各走一遍。
2. 账号到期（服务端把账号截止设为 1 分钟后）与断网 3 分钟两种情况，首页提示应不同。
3. 「我的 AI」：改人设保存，下一句回答体现新人设；重启后仍在。
4. 音色：选 B 试听（只有耳机/扬声器出声，虚拟麦克风、OBS 字幕无内容）→ 应用 → 下一句是 B → 重启仍是 B；试听中快速切换 A/B、点停止、退出登录，都应立刻无声。
5. 直播软件若采集桌面音频，确认试听是否被录进去，并据此决定是否提示主播改用耳机。

## 已知限制

- 本分支基于 PR #26；PR #26 与当前 main（已合入 PR #25）存在冲突，本批没有处理合并。
- MiniMax 查询域名与 TTS WebSocket 一样固定为 `api.minimaxi.com`；海外区账号需要另行确认。
- `StreamerConsoleController` 的首页状态依赖 `MonitorViewModel` 的连接状态，OBS/形象「连接中」与「未连接」的区分沿用原有判断。
- 公开版（无档案）的开发控制台未改动界面，只把消息处理异常改为友好文案 + 诊断编号。

## 配置迁移与回滚

- 旧档案里的 `voice_id` 自动变成默认音色；没有 `voices` 目录时，主播只能用默认音色（与原行为一致，只是不再覆盖一个目录里存在的选择）。
- 省略 `base_url` 的 `custom` 厂商档案现在会被拒绝，需要运营补上地址；`deepseek` / `gemini` 不受影响。
- 回滚：`git revert` 本批 4 个提交即可；`config.json` 新增的只是主播选择的音色值，旧版本会忽略目录并按档案强写。
