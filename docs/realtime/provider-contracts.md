# Provider contracts（厂商契约登记 · RT-07 汇总）

对应计划 §2「官方文档接入表与验证门槛」、§2.4。本文件汇总 RT-01~RT-06 / VIS-02 引入的全部厂商接口：
**全部未实测（无真实 Key / 无真实账号 / 无声卡）**。已实现的语义均以官方公开文档为准，行为证据全部来自
fake transport 契约测试；接入真实账号前必须逐项对拍，不得把 fake 测试结果当厂商时延/正确性。

| 厂商/能力 | 端点（代码中使用） | 官方文档 | 状态 |
|---|---|---|---|
| 腾讯实时 ASR | `wss://asr.cloud.tencent.com/asr/v2/{appid}` + 文档签名查询参数 | https://cloud.tencent.com/document/product/1093/48982 | 按文档实现，未实测 |
| 火山/豆包双向 ASR | `wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async` | https://docs.volcengine.com/docs/DoubaoVoice/bidirectional-streaming-automatic-speech-recognition-websocket | 按文档实现，未实测 |
| MiniMax HTTP TTS（RT-01 流式） | 账号区域对应 HTTPS 主机 + `/v1/t2a_v2`（`stream=true`，SSE） | https://platform.minimax.io/docs/api-reference/speech-t2a-http | 按文档实现，未实测 |
| MiniMax 双向 TTS（RT-06） | 账号区域对应 WSS 主机 + `/ws/v1/t2a_v2_bidi` | https://platform.minimax.cn/docs/api-reference/speech-t2a-websocket-bidi | 按文档实现，未实测；`tts.bidi_host` 默认空，未配置时显式报错 |
| 豆包方舟视觉（VIS-02） | Chat Completions 多模态形态，`vision.base_url`（默认 `https://ark.cn-beijing.volces.com/api/v3`） | https://docs.volcengine.com/docs/ark/image-understanding | 按文档实现，未实测 |
| Fish TTS（既有保留） | 现有 HTTP 路径（保留为回归基线） | https://docs.fish.audio/features/realtime-streaming | 既有实现，本轮未改 |

## 1. 已按文档实现的语义（fake 测试锁定的客户端契约）

### 腾讯实时 ASR（`AIVTuber.Core/RealtimeAsr/Tencent/`）
- 客户端计算签名（SecretId/SecretKey 不进 URL 日志），`slice_type` 1=可变文本 / 2=稳态文本，按会话+句序号去重；
  约 200ms 发包粒度（`realtime.send_packet_ms`，可配）；结束消息结束当次识别任务。
- 测试：`AIVTuber.Tests/RealtimeAsr/TencentRealtimeAsrSessionTests.cs`。

### 火山双向 ASR（`AIVTuber.Core/RealtimeAsr/Volcano/`）
- 双向 WSS `bigmodel_async` 入口（不是 `bigmodel_nostream`）；AppId/Token/ResourceId 成套配置；
  二进制头、压缩、序号、最终包均有 fixture；二遍识别/diarization 默认关闭。
- 测试：`AIVTuber.Tests/RealtimeAsr/VolcanoRealtimeAsrSessionTests.cs`。

### MiniMax HTTP TTS 流式（RT-01，`MiniMaxHttpStreamingTtsClient.cs`）
- 请求 `stream=true`，锁定 voice_id / sample_rate / model；响应按完整 SSE event 解析（跨块 UTF-8、
  keepalive、null data、error final）；hex 音频按声明的 PCM 格式增量产出，不等全文。
- 测试：`AIVTuber.Tests/MiniMaxHttpStreamingTtsClientTests.cs`。

### MiniMax 双向 TTS（RT-06，`MiniMaxBidiTtsClient.cs` + `TtsSessionCoordinator.cs`）
- `task_start` 建任务（音色/task 配置固定）→ `task_continue` 投递已批准文本 → 回合尾部 `task_flush`
  → 打断 `task_cancel` 并等待确认（取消屏障，超时重建连接 epoch）→ 正常结束 `task_finish`；
  flush 确认 ≠ 已播放；背压上限暂停文本、控制消息越过积压。
- 测试：`AIVTuber.Tests/RealtimeTts/MiniMaxBidiTtsClientTests.cs`。

### 豆包视觉（VIS-02，`DoubaoVisionClient.cs`）
- 固定 Chat Completions 的 `messages/content` + `image_url` 形态（不与 Responses 的 `input` 混用）；
  模型名由账号配置（空=拒绝，不猜"最新最大"）；输出为受控 `VisionObservation`（第4.4节结构），
  画面文字只作被观察文本，无命令权限。
- 测试：`AIVTuber.Tests/VisionObservationTests.cs`。

## 2. 实施时需真实核对的字段清单（各任务"未验证事项"汇总）

接入真实账号/Key 前，逐项与所选端点的真实响应对拍（不猜默认值）：

**MiniMax HTTP TTS（RT-01）**
- 流式 PCM 支持与目标采样率：`audio_setting` 完整 schema、流式末包结构、聚合音频语义。
- `is_final` 末包是否可能携带新音频 / 汇总重播（fixture 假设两种都可能出现）。
- 音频字符串编码格式（hex 后的真实格式，不得把 MP3 当 PCM）。

**MiniMax 双向 TTS（RT-06）**
- 账号/区域是否开放 `/ws/v1/t2a_v2_bidi` 及音频规格；官方 WSS 主机名（计划未猜 CN/国际域名）。
- `task_cancel` 确认的真实时序与交错回包形态；`task_flush` 确认字段名与语义。
- 空闲保活消息语义（`bidi_keep_alive_interval_ms` 默认 0=off，验证前不开）。

**腾讯实时 ASR（RT-03）**
- 引擎权限与 `engine_model_type` 匹配、签名有效期与时钟偏差、热词表 ID 能力。
- 断句参数、会话期限、两路并发上限、空闲断线行为（`voice_id` 去重）。

**火山双向 ASR（RT-03）**
- 新旧鉴权方式与 ResourceId 成套性；`definite` 字段与二遍识别组合下的真实最终结果语义
  （火山快路径若无句级 final，仅以客户端断点快照作候选，不冒充厂商 final——当前实现已区分
  `TranscriptFinalKind.ClientEndpointSnapshot`）。
- 压缩模式、序号/事件二进制头的真实字节序。

**豆包视觉（VIS-02）**
- 所选视觉模型名、区域/Key/端点成套性；图像 token 计费单位与实际扣费。
- 输出 schema 拒绝越权字段的真实错误形态。

**通用**
- 各厂商 P50/P95 时延、断流率、并发计费与长时稳定性：全部未测（见 benchmark-report.md）。
- 主播网络/声卡实机指标：未测。
