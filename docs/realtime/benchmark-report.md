# Benchmark / gate report（RT-07 汇总）

**真实时延未测（无真实厂商 Key、无 Windows 声卡实机测量）。** 本文件只汇总结构性门禁（计划 §6.1）
的 fake 证据；P50/P95 目标（语音结束→第一实际可听内容中位数较基线降 ≥30%，争取 P50≤2s / P95≤3s）
见计划 §6.2，须在真实 Key + 实机环境下按 §6.3 模板测量后才可报告。

## 1. 结构性门禁 7 条 → 测试证据

每条均可离线复现（`dotnet test AIVTuber.slnx`），无任何厂商网络依赖。

| # | 门禁（计划 §6.1） | 测试文件 · 测试名 |
|---|---|---|
| 1 | 输入音频尚未结束时，ASR 已收到前缀且可发布 partial | `RealtimeAsr/RealtimeAsrPumpTests.cs · HalfUtterance_PartialFlowsBeforeAnySegmentEnd`（另：`RealtimeGateIntegrationTests · SwitchMatrix_AllNewSwitchesOn_CompletesFakeEndToEndTurn`） |
| 2 | LLM 尚未 EOF 时，第一段已批准文字进入 TTS | `ReplyProtocolV2Tests.cs · FirstSpeechSegment_ArrivesBeforeEof`、`FirstSegmentReachesTts_BeforeSecondSegmentGenerated`（另：`RealtimeGateIntegrationTests · SwitchMatrix_AllNewSwitchesOn…` 中 `Assert.False(llmDone)`） |
| 3 | TTS 响应尚未结束时，音频已可播放 | `MiniMaxHttpStreamingTtsClientTests.cs · FirstPcm_Arrives_BeforeResponseCompletes`；`RealtimeTts/MiniMaxBidiTtsClientTests.cs · Trace_RecordsTtsRequestEncodedPcmAndFlushAck` |
| 4 | 视觉挂起/失败不阻塞非视觉语音任务 | `VisionObservationTests.cs · Slow_fake_vlm_does_not_delay_normal_voice_turn`、`Slow_vlm_does_not_block_and_results_arrive_out_of_order_safely`（另：`RealtimeGateIntegrationTests · SwitchMatrix…` 挂起 VLM 下完成整回合） |
| 5 | 被取消/换场的旧 generation 不产生新公共输出 | `RealtimeGateIntegrationTests.cs · Gate5_CancelledGeneration_ProducesNoNewPublicOutput`（新增，集成级）；`ReplyProtocolV2Tests.cs · InterruptedAfterFirstSegment_HistoryDoesNotClaimSecond`、`ProtocolError_FailsClosed…`；`RealtimeTts/MiniMaxBidiTtsClientTests.cs · CancelAckLost_RebuildsEpoch_OldSocketLatePacketsNeverReachNewTurn`、`TaskFailed_FailsClosed_AndDoesNotReplayDeliveredText`；`BotOrchestratorReplyTests.cs · NewSpeechBeforePlayback_DiscardsReplyWithoutPublicEffects`；`TurnManagerV2Tests.cs · RetractionAfterDispatch_CancelsBeforeCommit` |
| 6 | FAIL/PASS/私有备注/未验证 JSON 永不进 TTS 与公共字幕 | `BotOrchestratorReplyTests.cs · Pass_DoesNotCallTtsOrStartSpeaking`、`InnerThought_DoesNotCallTts`、`StructuredDecision_ControlsAllPublicEffects`；`ReplyProtocolV2Tests.cs · PassDecision_NoTtsNoCaptions`、`ThoughtDecision_PrivateOnly`、`ParenthesisThoughts_AreStructurallyIsolatedPerSegment`、`UnbalancedParenthesisInSegment_FailsClosed` |
| 7 | 两物理音源在重连/并发/设备切换后不串标签；不把播放当对手说话 | `RealtimeAsr/RealtimeAsrPumpTests.cs · TwoSources_DoNotCross`、`LatePacketAfterReconnect_DoesNotCrossEpoch`；半双工防自听：baseline §2.5（loopback mute 保留，`BotOrchestratorReplyTests` 系列覆盖不抢播） |

## 2. 开关矩阵证据

- **全部新开关开启**（streaming_asr + turn_manager_v2 + reply_protocol v2 + tts.transport=streaming +
  vision.enabled，全部 fake transport）：`RealtimeGateIntegrationTests ·
  SwitchMatrix_AllNewSwitchesOn_CompletesFakeEndToEndTurn`——一条回合完整走完，且门禁 1/2/3/4 在
  同一回合内同时成立。
- **全部关闭（默认配置）与现行为一致**：既有全量套件在默认配置上运行即为证据
  （见第 4 节真实通过数；`RealtimeConfigTests · Defaults_are_all_legacy_and_off` +
  模板断言锁死默认值）。

## 3. 其他 fake 证据（摘要，详见各分文档）

- 有界性/断流：`SilenceForever_MemoryStaysBounded_NoSessionCreated`、`BufferOverflow_StopsRebuildsAndReportsBreak_NotSilentDropOldest`、`IdleDisconnect_ReconnectsWithPreroll_AndRecordsColdStart`。
- 厂商适配器协议 fixture：`TencentRealtimeAsrSessionTests`、`VolcanoRealtimeAsrSessionTests`、
  `MiniMaxHttpStreamingTtsClientTests`（任意网络切片解码一致等 15 例）、`MiniMaxBidiTtsClientTests`
  （取消屏障/迟包/背压等 13 例）。
- cloud-only：`CloudOnlyGateTests`（含新增 `New_realtime_modules_do_not_bypass_the_cloud_only_gate`）。
- 配置迁移：`RealtimeConfigTests · RT02_RT06_Vision_keys_round_trip_through_save_load`、
  `Template_lists_every_realtime_tts_asr_vision_key_and_stays_legacy_off`。

## 4. 构建与测试（本轮真实结果）

- `dotnet build AIVTuber.slnx`：0 错误。
- `dotnet test AIVTuber.slnx`：**777 通过 / 0 失败 / 1 跳过（总计 778，耗时 1m22s）**。
  本轮 RT-07 之前基线为 772 通过 / 1 跳过；本轮净增 5 例（2 集成门禁 + 2 配置契约 + 1 cloud-only 复核）。
- **未测项**：真实厂商时延/正确性/费用（无 Key）、真实声卡 playback_first（当前所有播放时间均为
  软件估计，未与实际出声混列）、30–60 分钟长跑、打断尾音实测。
