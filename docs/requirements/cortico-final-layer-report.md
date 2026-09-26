# Cortico 作为唯一演出层：兼容修复记录

起始 HEAD：`1a9ea25`（`claude/new-session-i4wnxx`，PR nashorya/my-AI-Vtuber#27）。
范围：本机两项发现 + Cortico 最终要求一至四。**没有**用"v2 时绕过 Cortico、走普通播放管线"作为修复。上游 `sidecar/cortico/upstream/` 未改一个字节（哈希校验测试通过）。未改厂商 API、人设和音色选择，未重新设计 motion，未新增插件框架。

## 本机发现核对（针对 1a9ea25 的实际代码）

| 发现 | 结论 | 证据 |
|---|---|---|
| 1. `WebConsoleHost.Push*` 在切 UI 线程前读 `CoreWebView2`；后台通知的异常反向污染 ASR 成功状态 | **成立**，且是 A2 引入的：`RecognizeTrackedAsync` 在 try 里触发 `AsrHealthChanged`，界面处理函数抛出的异常被 catch 当成识别失败，状态记为 Unavailable，转写被丢弃 | 新测试 `ThrowingUiSubscriber_DoesNotTurnASuccessfulRecognitionIntoAFailure` 在去掉隔离时失败 |
| 2. `RunCorticoAsync` 调用 legacy `_llm.StreamAsync`，v2 时抛异常 | **成立**。另外发现同一路径的三处不一致：历史里注入的接话规则要求输出 JSON（`{"respond":…}`），Cortico 提示词却要求直接输出台本，解析器按纯文本分类（JSON 会被当台词念）；v2 提示词写"speech 里不要出现控制标记"，与 Cortico 台本语法冲突；整轮缓冲后才开演 | 把 v2 分支改回 legacy 调用时，7 个测试失败（含 Runtime 入口验收） |

## 修改

**一、界面异常隔离**
- `WebConsoleHost`：所有 `Push*` 及 `Post` 改为先投递到 UI 线程，在 UI 线程上检查 `CoreWebView2` 并执行，异常只记日志（`OnUi`）。
- `BotRuntime`：`AsrHealthChanged`、`CompanionPausedChanged`、`CloudAccessRevoked`、`TurnStatusChanged` 经 `Notify` 逐个订阅者隔离；`RecognizeTrackedAsync` 的 try 只包识别调用本身，结果确定后才发状态通知。

**二、回复协议兼容，Cortico 保留为唯一演出层**
- `CorticoReplyAdapter`：legacy（逐句、括号感知的增量读取）与 v2（decision/speech/end 事件）都转成 Cortico 台本片段。PASS / 心里话从不进入 Cortico；协议包装（JSON）、`[emotion:]` 等旧标签、TTS 方括号语气词从不进入台本或朗读正文；v2 的 control 行在 Cortico 模式下不执行（动作写在台本标记里）。
- 提示词与解析一致：`CorticoPrompt.For(协议, 语法)`、`IdentityPrompt.InvitationPolicyFor(协议, cortico)`、`ReplyProtocolV2.Prompt(通道, scriptMarkup)`；Cortico 时 LlmClient 不提供 avatar 通道。host 只返回台本语法与词表，外层格式由 App 按协议拼装。
- 流式开演：host 新增 `perform`（无 script）→ `ready` → `feed`（每个批准片段一个上游 round）→ `end`。第一句在模型还在生成时就开始合成和演出，不再等整轮。
- 逐片授权与提交：每片出声前带上该片干净文本询问 App；App 此时才把这段写入历史和字幕，被打断的回复只记已出声的部分。被拒绝时 host 立即中断本轮其余内容（上游原本会继续做无声动作）。只有动作、没有台词的片段不会单独开演。

**三、演出职责**
- Cortico 启用时：播放和 VTS 参数只由 sidecar 负责；App 播放器、旧 VTS 控制器不初始化；编排器 TTS 不被调用（测试里用"一调用就抛"的替身确认）。
- 停止 / 暂停 / 账号失效 / 切场：编排器中断时同时向 Cortico 发 `interrupt`（带栅栏，只作用于发出时已存在的演出，不会误伤下一轮）；host 丢弃排队内容、把持续表情/姿态/注视复位、正在进行的动作 200ms 淡出。退出时 sidecar 关闭流程同样中断。
- 重连形象 / Cortico 进程更换：先中断在演内容；LLM 客户端按当前 Cortico 实例重建，提示词不会停留在旧后端。Cortico 启动失败时本次运行**临时降级**为普通 VTS 演出，提示词、解析和播放器一起切换，不存在半连接状态；这不是 Cortico 兼容完成。

**四、不同皮套**
- `sidecar/cortico/adapt.ts`：有专用档案（配置或按模型名匹配）时用它自己的映射、范围、方向、中性值和强度，模型文件里没有接线的输入加入 `unsupported`，该动作跳过，不让整轮失败；没有专用档案时用保守适配：只驱动 `.vtube.json` 显示已接线的输入，强度 0.6、不开表情特效、眼睑交还模型；连模型文件都找不到时只驱动口型。状态行列出 `driven` 与 `skipped`（带原因）。不会把别的皮套的档案套到当前模型上。
- 切换模型：中断当前轮；新映射确定前不开新轮、不开表情；丢弃旧模型遗留的表情关闭脉冲；清掉新模型上的残留表情。

## 验证

| 项目 | 层级 | 结果 |
|---|---|---|
| Runtime 入口 + Cortico + v2：批准文本进入 Cortico，一次出声，口型与动作匹配 | 真实 `BotRuntime`（`AcceptTalkLine` → v2 回合管理 → 编排器）+ 真实 `LlmClient`（仅 HTTP 为假）+ 真实 `CorticoProcess` + 真实 Node host 与上游 Performer/Mixer/Backend + 假 VTS；声卡为 none | 通过：每片只合成一次、只有干净文本、一次开口；RigFull 上 MouthOpen 有值，点头按该模型档案反向半幅；请求里是 Cortico+v2 提示词，没有 control 行和 JSON 规则 |
| 停止后旧动作与声音不继续，下一轮正常 | 同上 | 通过：停止时正在摇头，停后 300–1100ms 内无摇摆（修复后幅度 ≤5°；去掉修复实测约 43°）、无口型；下一轮正常开演 |
| 缺少参数的皮套、运行中切换皮套 | 同上（RigFull 有专用档案；RigLite 无档案、模型文件只接 4 个输入） | 通过：在演轮被切断且不报错；RigLite 保守适配，只驱动已接线输入，口型和摇头（有的轴）正常 |
| 协议兼容细节（PASS/心里话/JSON/旧标签/流式/仅动作/人声恢复/停止） | 真实编排器 + 假演出层，17 个用例，legacy 与 v2 各自覆盖 | 通过 |
| host 流式、栅栏、拒绝、专用/保守/无文件适配、切换模型、表情残留 | Node 测试：真实 host + 假 VTS（8 项） | 通过（连续多轮稳定） |
| 界面异常不影响 ASR、暂停、退出登录 | 真实 `BotRuntime` | 通过 |
| 全量 | `dotnet test` | 964 通过，1 失败（基线已有的 Linux 专属 `DashScopeConnectionPoolTests`，Windows CI 上通过），13 跳过 |
| App 构建 | `dotnet build App -p:EnableWindowsTargeting=true` | 0 警告 0 错误 |

失败注入（逐项改回，跑对应测试）：Cortico 路径改回 legacy 调用 → 7 个失败；v2 提示词不区分 Cortico → 验收失败；历史接话规则不区分 Cortico → 验收失败；关掉适配 → 切换皮套验收失败；host 停止时不复位/不淡出 → Node 停止测试与 Runtime 停止验收均失败；拒绝时不中断 → Node 测试失败；界面通知不隔离 → 2 个 Runtime 测试失败。编排器主动通知 Cortico 停止这一层去掉后，只有假演出层测试失败，真实验收仍通过：本轮取消时 stage 释放也会发出中断，两层互为冗余，如实记为多一层保护。

**真实模型：0 个。** 本环境没有 VTube Studio、没有 Live2D 模型、没有 Windows 声卡，也没有调用真实 LLM/TTS。以下均**未验证**：真实模型上的画面效果与跨皮套稳定性、真实声卡出声与口型对齐的观感、VTS 平滑设置对动作的影响、VTS 在切模型瞬间对在途请求的实际处理、真实厂商流式回复的分段节奏、长时间运行。上表"通过"只说明信号在真实代码路径里的走向和假 VTS 收到的参数，不等于视觉验收。

另外：CI（Windows）不安装 sidecar 的 npm 依赖，所以依赖 Node 的测试（Node host 测试、`CorticoProcessTests`、Runtime 验收）在 CI 上跳过，只在本地（Node 22，`npm ci`）跑过。

## 回滚

`git revert` 本批提交；或把 `cortico.json` 的 `enabled` 设为 `false` 重启，回到普通 VTS 路径（不影响已有配置、记忆和私有 Key）。
