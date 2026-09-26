# Rollout plan（分阶段启用顺序 · RT-07）

原则：一次只开一个开关；每步有明确验证方法与回滚开关；未完成真实对拍前不进入下一步的"宣布可用"。

## 0. 前置

- 全部新开关默认 legacy/off（`RealtimeConfigTests.Defaults_are_all_legacy_and_off` + 模板断言）。
- 结构性门禁 7 条全部有 fake 证据（benchmark-report.md）；全量测试通过后再开始灰度。

## 1. 阶段顺序

| 步 | 开关 | 内容 | 每步验证 | 回滚 |
|---|---|---|---|---|
| 1 | `tts.transport = "streaming"`（RT-01） | 只改 TTS 输出方式，LLM 整轮分类不动，隔离变量 | 真实 Key 下对同一台词记录 stream=false/true 的首音频与完整耗时；末包不重复、取消后旧块不播（fake 已锁）；听感/音色不变 | 改回 `"legacy"` |
| 2 | `llm.reply_protocol = "v2"`（RT-05） | 解除两层全文缓冲；decision/speech 分段 | 日志可见 `first_speech_segment < llm_done` 且 `tts_request < llm_done`；PASS/心里话/JSON 不进 TTS（fake 已锁）；字幕按播放边界结算 | 改回 `"legacy"`（旧协议完整保留） |
| 3 | `realtime.turn_manager_v2_enabled = true`（RT-04） | 分开听见/被邀请/可出声；去掉固定 350ms 合并 | 点名/接话/改口场景手测（turn-manager.md 测试清单）；每条决策带机器可读原因；不打断旧 PK 身份 | 改回 `false`（回 ConversationTurnGate） |
| 4 | `realtime.streaming_asr_enabled = true` + `asr.provider = "tencent_realtime"` 或 `"volcano_realtime"`（RT-02/03） | 音频进行中即送识别 | 未出现 SpeechDetected 前日志有 `asr_first_audio_sent` 与可见 partial（fake 已锁）；真实语料对比两家（同语料、单变量），按 provider-contracts.md 清单对拍 | provider 改回 aliyun/minimax + streaming_asr=false（UI 标明 legacy 整段识别） |
| 5 | `tts.transport = "bidi"` + `tts.bidi_host`（RT-06） | 双向会话与连接内取消屏障 | 先确认账号开放该接口；同会话多句、打断立即截断云端+本地、两轮同 socket 不串音（fake 已锁）；cancel 确认超时重建路径实测 | 改回 `"streaming"`（或 `"legacy"`）；`bidi_host` 留空即显式报错不静默降级 |
| 6 | `vision.enabled = true` + 选定窗口（VIS-01/02） | 窗口截图 + 豆包识图旁路观察 | 普通语音回合不等 VLM（fake 已锁）；画面变化不自动插话；按需识图有明确超时；预算（每小时上限）生效 | `vision.enabled = false`（无残留路径） |

全程可选：`realtime.trace_enabled = true`（诊断期开，稳定后关）；`speculative_generation_enabled`
保持 false，直至 P1 验收完成并接受废弃成本。

## 2. 回滚总开关

所有新行为都受独立开关控制且互不依赖隐式状态；将
`realtime.*` / `llm.reply_protocol` / `tts.transport` / `vision.enabled` 全部改回默认即恢复
RT-00 baseline 行为（既有套件即等价性证据）。配置有 `realtime.schema_version` 迁移，
老 config 自动补安全默认值，不覆盖用户显式值。

## 3. 成本风险（启用前登记，见计划 §2.4）

- 两路 ASR 常开会话：静音上行也计费 → 用 `idle_disconnect_ms` 省费，但重连冷启动计入指标。
- bidi 长连接：TTS 按已接受字符计费，取消后服务端未必退款；打断频繁时废弃成本上升。
- 视觉：每请求图片 token；用 `min_request_interval_ms`（3s 起步）与 `max_requests_per_hour` 控制。
- 试探生成（默认关）：废弃 tokens/音频计入成本。
- 每次真实试验按项目单独记账（两路 ASR、LLM 输入/输出/废弃、TTS 字符、视觉请求、重试），
  不用"每小时直播"粗算；新启用付费调用前需操作者给出 Key、配额与上限。

## 4. 30–60 分钟授权模拟直播（正式发行门禁，未执行）

双路输入、插话、背景音乐、切对手、暂停、设备热拔插、网络抖动、配置热更新、长静默、退出重启；
检查内存/队列增长、socket 泄漏、事件订阅倍增、孤儿任务、字幕串轮、motion 回调重复。
此项需要真实环境，当前未实测。
