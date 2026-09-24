# RT-01: MiniMax HTTP 流式 TTS

分支 `feat/rt01-minimax-streaming`。改造目标：只改 TTS 输出方式（HTTP 流式），旧 LLM 整轮分类保持不动，隔离变量。不做双向 WebSocket（RT-06 另行处理）。

## 改动面

| 文件 | 改动 | 原因 |
|---|---|---|
| `AIVTuber.Core/Pipeline/MiniMaxHttpStreamingTtsClient.cs`（新增） | `ITtsClient` 实现：t2a_v2 `stream=true`、`ResponseHeadersRead`、SSE 增量解析、hex/base64 音频解码、末包复读防护、PCM 16-bit 对齐、MP3 增量解码防御路径、`TtsStreamingTelemetry` 首包时序挂点 | RT-01 主体 |
| `AIVTuber.Core/Pipeline/TtsClient.cs` | minimax 分支按 `tts.transport` 分流：`streaming` → 新客户端；否则保持原 `stream=false` 整段实现（显式回退） | 配置开关 + 回退 |
| `AIVTuber.Core/Runtime/BotRuntime.cs` | `CreateTtsClient` minimax：`transport=streaming` → `MiniMaxHttpStreamingTtsClient`；默认（legacy）→ 原 `MiniMaxWsTtsClient`（当前实际行为，不变） | 让开关在运行时可达，默认零行为变化 |
| `AIVTuber.Core/Config/AppConfig.cs` | `TtsConfig.Transport`（默认 `"legacy"`） | 开关 |
| `AIVTuber.Core/Runtime/ConfigDiff.cs` + `AIVTuber.Tests/ConfigDiffContractTests.cs` | `Tts.Transport` 变更映射 `RebuildTts`（热重载生效） | 配置迁移契约 |
| `AIVTuber.Tests/MiniMaxHttpStreamingTtsClientTests.cs`（新增） | 18 个 fake-transport 测试 | 契约固定 |

## 开关与回滚

```jsonc
{ "tts": { "provider": "minimax", "transport": "streaming" } }  // 启用 HTTP 流式
// transport 缺省或 "legacy"：运行时保持原有 MiniMaxWsTtsClient 路径，完全回滚到改动前行为。
```

- `transport` 变更走现有配置热重载（`RebuildTts`），无需重启。
- 代码级回退：`TtsClient.MiniMaxSynthesizeAsync`（`stream=false` 整段 JSON）原样保留。

## 实现要点（对照 RT-01 验收）

- **请求**：显式 `stream=true`，`audio_setting.format="pcm"`、`sample_rate`/`voice_id`/`model` 全部取自现有配置并 pin 死（不悄悄变）。测试断言请求 JSON。
- **消费**：`HttpCompletionOption.ResponseHeadersRead`；流式路径绝不 `ReadAsStringAsync`。按 Content-Type 分派：`text/event-stream` → SSE；`application/json` → 旧整段解析（服务端忽略 stream 时的显式回退）；原始音频媒体类型 → 块读取。
- **SSE 解析**：只按完整行（LF）切分，UTF-8 多字节跨块安全（LF 不是多字节序列的一部分）；处理空行 dispatch、`:` keepalive、`event:`/多行 `data:`、`[DONE]`/`null`/空 data、流中断在行尾无换行的残余事件。曾发现并修复一个真实 bug：单字节读取时 CRLF 的 `\r` 残留导致空行被当作非空行、事件不 dispatch（测试 `ArbitrarySliceSizes_ProduceIdenticalAudio` 在 slice=1 时抓到）。
- **音频解码**：`data.audio` hex 解码（非法 hex 时回退 base64）；实际格式以 `data.audio_format` 为准，MP3 走 NAudio `Mp3Frame.LoadFromStream` + ACM 增量解码（不等待完整 MP3），采样率/声道不符时经连续线性重采样归一到播放层采样率。PCM 输出经 `PcmAligner` 保持 16-bit 对齐（奇数字节残留跨块拼接，flush 丢弃不成对字节）。
- **末包语义（真实契约未验证，防御策略）**：`is_final=true` 或带 `extra_info` 视为末事件；末事件若带新音频则照常输出（不丢 final）；若与已输出全部音频逐字节相同（聚合复读）则跳过（不复读）。两种形态均有测试。
- **取消**：`CancellationToken` 贯穿 send/读流；取消后不再产出块（测试验证）。
- **首音提前**：事件粒度即产出 PCM，测试证明首个 PCM 在响应体未读完时已到达消费者；`TtsStreamingTelemetry` 记录 `FirstEncodedAudioTimestamp`/`FirstPcmTimestamp` 供 RT-00 打点挂接。
- **播放侧**：沿用现有 `AudioPlayer.PlayChunksAsync`（同一发言设备保持打开、已做样本对齐），本分支不重复实现缓冲；SSE 事件音频本身即按音频时长分块。

## 验证状态

- `dotnet build AIVTuber.slnx`：通过（0 error）。
- `dotnet test AIVTuber.slnx`：**642 通过 / 0 失败 / 1 跳过**（含新增 18 个 RT-01 测试）；`dotnet test App.Tests`：1 通过。
- **真实 MiniMax 调用：未实测（无真实 Key）。** 流式 SSE 的实际事件字段（`data.audio` 是否 hex、`is_final`/`extra_info` 语义、末包是否聚合复读、`stream=true` 时 PCM 格式是否被接受）需拿到 Key 后按 `provider-contracts.md` 固化；与 `stream=false` 的首音频/整段耗时对比也未测。

## 剩余风险

1. 末包语义是防御性实现（hex/PCM 假设来自文档与现有非流式代码），真实端点若末包行为不同（如重复部分音频、或 audio_format 与请求不符），需要按真实 fixture 调整 `ParseSseAudioPayload`/复读判定。
2. MP3 增量解码 + 线性重采样路径完全未被真实数据验证（我们请求 PCM，正常情况不会走到）；NAudio ACM 仅 Windows。
3. HttpClient 超时设为 `Timeout.InfiniteTimeSpan`，依赖调用方 CancellationToken 终止慢速响应；若上游某些路径不传 token，长挂连接不会被中断。
4. 并发/时效边界：同一 client 实例并发发起多个 `StreamAsync` 时共享一个 HttpClient（连接复用安全），但 `TtsStreamingTelemetry` 时间戳是实例级的，会记录最早的一次——RT-00 打点接入时需按轮次隔离。
