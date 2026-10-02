# Cortico 视觉跟随层：交付报告

起始 HEAD：`403471d`（分支 `claude/new-session-i4wnxx`）
Spec：`docs/superpowers/specs/2026-09-27-cortico-visual-follower-design.md`
Plan：`docs/superpowers/plans/2026-09-27-cortico-visual-follower.md`

## 提交范围

```
834d84a Spec: Cortico as visual follower, app-owned playback, v2-only pipeline
2876ea0 Plan: Cortico visual follower implementation
2fedc05 Cortico host: app-owned audio bridge; the sidecar never plays or synthesizes
783acec Fix round 1: AppAudioBridge.audio.stop actually halts playing pieces
0577d56 Pacing: speech pacer contract, streaming piece audio, immediate pacer
fbaaeaf Remove unreferenced code: composite avatar controller, breath follow, config wizard, page service, mic level converter
0f0128f Cortico host: beat cues, max legitimate hold, aborted on model switch
11059c6 Cortico IPC: host requests synth/play, app reports audio; heartbeat liveness and hold budget
5d198f7 Pacing: Cortico decides when to speak, the app plays; commits only what reaches the player
832c765 Pacing: voice-only fallback on sidecar loss, stall or model switch; current piece finishes first
811fb61 One reply pipeline: v2 only, paced by Cortico or immediately; drop the legacy path and unused entry points
8de5844 v2 is the only reply protocol; Cortico prompts and runtime wired to the visual follower
```

## 自动化结果（macOS arm64，2026-09-27）

| 项目 | 命令 | 结果 |
|---|---|---|
| sidecar 测试 | `cd sidecar/cortico && npm test` | 21 个，通过 21，失败 0 |
| sidecar 类型检查 | `npm run typecheck` | 无错误 |
| C# 测试 | `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU` | 981 个，通过 966，跳过 13，失败 2 |

两个失败都与本次改动无关，属于本机环境问题：
- `DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`：macOS 对 `localhost:1` 立即拒绝连接，而测试假设连接会一直挂起到超时。基线报告中已有记录。
- `StreamerConfigTests.StreamerDraft_ContainsNoManagedServiceFields`：主播草稿会列出本机正在运行的进程，其中 `iCloudDriveFileProvider` 含有子串 `provider`，只有 macOS 上会误报。

真实 Node host 参与的集成测试（`CorticoProcessTests`、`RuntimeCorticoAcceptanceTests` 的 3 个用例）连续运行 5 次，全部通过。

## 上游未改的证据

`sidecar/cortico/tests/host.test.ts` 的 `vendored upstream files exactly match the pinned source hashes` 通过。`tests/timing.test.ts` 的 `mirrored timing constants equal the vendored upstream ones` 通过，这个测试把 host 镜像的节拍常量与上游源码逐项比对。

## 行为变化摘要

- 声音只由 App 的 TTS 和 `AudioPlayer` 产生，监听、虚拟麦、OBS 字幕沿用原链路；Node host 不出声，也不调用 TTS。
- 启用 Cortico 时，由它决定每一片的开口时机（句间停顿、`【】` 动作做完再说）；口型、重音和 `<>` 锚点以 App 回报的开播时刻为零点。
- 兜底规则：host 退出，或心跳超过 2 秒未到，或某片已就绪却超过"上游最长合法等待 + 2 秒"仍未开口，本轮剩余内容改为只有声音，并提示"皮套异常，本轮仅语音"。切皮套时，正在播的那句照常播完，其余只有声音，提示"皮套切换"。
- 回复协议只保留 v2；配置里写着 `legacy` 时按 v2 运行，诊断日志记一行。

## Windows 实机清单（待填）

| 检查项 | 结果 | 观察 |
|---|---|---|
| 真实 VTS + MiniMax，Cortico 启用：首句出声延迟（`RealtimeTrace`），与 `403471d` 同一句话、同一配置对比 | 未测 | |
| 监听设备、虚拟麦（OBS 能采到）、OBS 字幕在 Cortico 模式下正常，且只有一份声音 | 未测 | |
| `【点头】你好`：先点头再开口，口型跟随声音 | 未测 | |
| 说话时点"停止"：声音、口型、动作 300ms 内停下，下一轮正常 | 未测 | |
| 说话时在 VTS 切换模型：当前句念完，其余只有声音并出现"皮套切换"提示；下一轮在新模型上正常开演 | 未测 | |
| 说话时在任务管理器结束 node 进程：本轮声音继续，出现"皮套异常，本轮仅语音"；之后的回合只有声音、不报错 | 未测 | |
| 旧 `config.json` 写着 `reply_protocol: "legacy"`：按 v2 运行，诊断日志有一行说明 | 未测 | |
| Windows CI 上 App 项目（WPF）编译通过 | 未测 | macOS 上只能用 grep 确认没有引用已删成员 |

## 已知限制

- 兜底时，还没送去合成的文本会作为一整段交给 TTS。
- Node 被判定卡住时，`Stage.DisposeAsync` 的中断如果失败，会结束 sidecar 进程；本次运行之后都只有声音，需要重连形象或重启才能恢复。
- 部分回复的合成中途失败时，已播出的部分照常播完，字幕记录的是整句文本（和原 v2 行为一致）。
