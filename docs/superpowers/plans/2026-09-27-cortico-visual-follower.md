# Cortico 视觉跟随层 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 声音只由 App 的 TTS 和 `AudioPlayer` 产生；Cortico sidecar 决定每一片的开口时机，并跟随 App 的实际播放驱动皮套；Cortico 故障时 App 自行播完本轮；回复管线合并为唯一一条 v2 管线。

**Architecture:** host 给上游 `Performer` 注入代理 TTS / 音频接口（`AppAudioBridge`），通过 JSON-lines 协议向 App 请求合成（`synth`）和播放（`play`），App 回报 PCM 与播放事件。App 侧 `BotOrchestrator` 只保留一条 v2 管线，把批准的片段交给节拍器 `ISpeechPacer`：`ImmediatePacer`（普通模式/兜底）或 `CorticoPacer`（Cortico 定节拍）。上游 `sidecar/cortico/upstream/` 一个字节不改。

**Tech Stack:** C# / .NET 10（Core `net10.0`，WPF App `net10.0-windows`，xUnit + `SkippableFact`），Node 22 + TypeScript（`tsx`，`node:test`），VTube Studio WebSocket API。

**Spec:** `docs/superpowers/specs/2026-09-27-cortico-visual-follower-design.md`

## Global Constraints

- `sidecar/cortico/upstream/` 不改任何字节；`tests/host.test.ts` 的哈希校验测试必须一直通过。
- 只保留 v2 回复协议；`llm.reply_protocol` 读到任何非 `v2` 值都按 `v2` 运行并记一次诊断日志。
- 皮套参数控制方唯一：Cortico 启用时，旧 VTS 控制器和连续控制不初始化，兜底期间也不启用。
- 字幕和历史只记录真正交给播放器的片段；被拒绝、失败或未播的片段不写入。
- Cortico 首片切片门槛保持上游的 40 字，不在接入层提前切。
- 兜底触发条件：host 进程退出或断开；心跳超过 2000ms 未到；有片已就绪、没有片在播，且距上次进展超过 `maxHoldMs + 2000ms`。
- 切皮套时正在播的片照常播完；本轮其余片走普通模式；兜底期间嘴不动。
- 用户可见提示用中文，不直接显示原始异常或堆栈。
- 提交信息末尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。

## Review Focus

1. **TTS 分块是奇数字节**：MiniMax 等厂商的分块不保证按 PCM16 的两字节对齐。host 必须跨块保留多出的那个字节，否则包络会错位、嘴型抖动（Task 1 测试 `odd-length chunks`）。
2. **合成还没完就收到 `play`**：已到的音频立即播，后续边合成边播，不能等合成完才开始（Task 5 测试 `Play_BeforeSynthesisFinishes_PlaysWhatArrivedAndStreamsTheRest`）。
3. **兜底发生在一片正在播放的时候**：兜底的播放器调用会先 `Stop()` 当前播放，所以兜底必须等当前片播完再开始（Task 6 测试 `Fallback_WhilePlaying_WaitsForTheCurrentPiece`）。
4. **host 在 `aborted` 之后仍发来 `play`，或在兜底之后仍发来 `play`**：这两种都必须忽略，同一段文字不能说两遍（Task 6 测试 `PlayAfterFallback_IsIgnored`）。
5. **连续几个 `【动作】` 块**：多个合法的长停顿首尾相连，不能被看门狗误判为卡住。每一拍触发时 host 都会发 `cue` 重置计时（Task 2 测试 `each fired beat sends cue`，Task 6 测试 `Cue_ResetsTheHoldTimer`）。

---

## 文件结构

| 文件 | 职责 |
|---|---|
| Create `sidecar/cortico/app-audio.ts` | `AppAudioBridge`：把 App 回报转成上游 `PerformerTts.synthStream` 与 `AudioSink.beginStream` 契约 |
| Create `sidecar/cortico/timing.ts` | `maxHoldMs(pack)`：上游最长合法等待 |
| Modify `sidecar/cortico/host.ts` | 使用 bridge；删除 Node 出声、`authorize`/`tts` 回调、`prepare`；发送 `cue`、`maxHoldMs`、`aborted` |
| Modify `sidecar/cortico/tests/harness.ts`、`host.test.ts`、`stream.test.ts` | 测试替身扮演 App：应答 `synth`/`play` |
| Create `sidecar/cortico/tests/app-audio.test.ts` | bridge 单元测试 |
| Modify `sidecar/cortico/package.json`、`Setup-Cortico.ps1` | 去掉 `audify` |
| Modify `AIVTuber.Core/Cortico/ICorticoPerformance.cs` | 新接口：`ICorticoPerformance`、`ICorticoAudioHandler`、`ICorticoStage` |
| Modify `AIVTuber.Core/Cortico/CorticoProcess.cs` | 新协议；`IsAlive`、`MaxHoldMs`；去掉 TTS 依赖和 `AudioDevice` |
| Create `AIVTuber.Core/Cortico/CorticoScript.cs` | 本地去标记、规范化（和上游解析规则一致） |
| Create `AIVTuber.Core/Bot/Pacing/SpeechPacing.cs` | `SpeechItem`、`SpeechTurnPorts`、`ISpeechPacer` |
| Create `AIVTuber.Core/Bot/Pacing/PieceAudio.cs` | 一片的流式合成缓冲 |
| Create `AIVTuber.Core/Bot/Pacing/ImmediatePacer.cs` | 普通模式节拍器 |
| Create `AIVTuber.Core/Bot/Pacing/CorticoPacer.cs` | Cortico 节拍器 + 兜底 + 看门狗 |
| Modify `AIVTuber.Core/Bot/BotOrchestrator.cs` | 唯一 v2 管线 `RunReplyAsync`；删除 legacy、`RunCorticoAsync`、无用入口与事件 |
| Modify `AIVTuber.Core/Cortico/CorticoReplyAdapter.cs` | 删除 `FromV2`、`FromLegacy`、`LegacyScriptReader`，只留 `Sanitize` |
| Modify `AIVTuber.Core/Cortico/CorticoPrompt.cs`、`AIVTuber.Core/Bot/IdentityPrompt.cs` | 删除 legacy 分支 |
| Modify `AIVTuber.Core/Config/AppConfig.cs`、`config.json.template` | 协议固定为 v2 |
| Modify `AIVTuber.Core/Runtime/BotRuntime.cs` | 新的 `CorticoProcess.StartAsync` 签名、提示词调用、诊断日志、删除无用订阅 |
| Delete | `Avatar/CompositeAvatarController.cs`、`Avatar/LayeredBreathFollow.cs`、`App/ConfigWizard.cs`、`App/PageService.cs`、`MicLevelToWidthConverter` |
| Tests | `AIVTuber.Tests/Pacing/*`、`AIVTuber.Tests/Cortico/CorticoFakes.cs`、`CorticoOrchestratorTests.cs`、`CorticoProcessTests.cs`、`RuntimeCorticoAcceptanceTests.cs`，以及移植到 v2 的编排器测试 |

**命令约定**（macOS arm64 上测试项目需覆盖平台）：

- C#：`dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~<名字>"`
- Node：在 `sidecar/cortico` 目录下运行 `npm test`、`npm run typecheck`
- 已知的平台差异失败：`DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows` 只在 macOS 上失败，不属于本计划范围。

---

### Task 1: host 改用 App 提供的音频（AppAudioBridge）

**Files:**
- Create: `sidecar/cortico/app-audio.ts`
- Create: `sidecar/cortico/tests/app-audio.test.ts`
- Modify: `sidecar/cortico/host.ts`
- Modify: `sidecar/cortico/tests/harness.ts`
- Modify: `sidecar/cortico/tests/host.test.ts`（第 21 行起的用例）、`sidecar/cortico/tests/stream.test.ts`
- Modify: `sidecar/cortico/package.json`、`sidecar/cortico/Setup-Cortico.ps1`

**Interfaces:**
- Produces（host → App 消息）：`{kind:'synth',requestId,pieceId,text}`、`{kind:'cancelSynth',requestId,pieceId}`、`{kind:'play',requestId,pieceId}`、`{kind:'stop',requestId,pieceId}`
- Consumes（App → host 消息）：`{kind:'pcm',requestId,pieceId,sampleRate,data(base64 PCM16LE)}`、`{kind:'synthEnd',requestId,pieceId}`、`{kind:'synthError',requestId,pieceId,message}`、`{kind:'started'|'ended'|'stopped',requestId,pieceId}`
- 删除的旧消息：`tts`、`authorize`、`reply`，以及命令 `prepare`
- `pieceId` 在 host 内单调递增，`requestId` 等于 `perform` 命令的 id

- [ ] **Step 1: 写 bridge 的失败测试**

`sidecar/cortico/tests/app-audio.test.ts`：

```ts
import test from 'node:test';
import assert from 'node:assert/strict';
import { AppAudioBridge } from '../app-audio.ts';

const pcm = (samples: number[]) => { const b = Buffer.alloc(samples.length * 2); samples.forEach((s, i) => b.writeInt16LE(s, i * 2)); return b; };
const setup = () => {
 const sent: any[] = []; let now = 1000;
 const bridge = new AppAudioBridge(m => sent.push(m), () => now);
 bridge.beginTurn(7);
 return { sent, bridge, tick: (ms: number) => { now += ms; } };
};

test('synthStream asks the app, builds the envelope from app PCM and resolves on synthEnd', async () => {
 const { sent, bridge } = setup();
 const chunks: Uint8Array[] = []; let began = 0;
 const piece = bridge.tts.synthStream!('你好', { begin: () => { began++; }, pcm: c => chunks.push(c) }, new AbortController().signal);
 assert.deepEqual(sent[0], { kind: 'synth', requestId: 7, pieceId: 1, text: '你好' });
 bridge.onMessage({ kind: 'pcm', requestId: 7, pieceId: 1, sampleRate: 16000, data: pcm(Array(320).fill(3000)).toString('base64') });
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 });
 const result = await piece;
 assert.equal(began, 1);
 assert.equal(chunks.length, 1);
 assert.equal(Math.round(result.durationMs), 20);
 assert.ok(result.envelope.at(5) > 0, 'envelope follows app audio');
});

test('odd-length chunks are re-aligned to PCM16 before reaching the envelope', async () => {
 const { bridge } = setup();
 const got: Uint8Array[] = [];
 const piece = bridge.tts.synthStream!('嗯', { pcm: c => got.push(c) }, new AbortController().signal);
 const whole = pcm([1000, 2000, 3000]);
 bridge.onMessage({ kind: 'pcm', requestId: 7, pieceId: 1, sampleRate: 16000, data: whole.subarray(0, 3).toString('base64') });
 bridge.onMessage({ kind: 'pcm', requestId: 7, pieceId: 1, sampleRate: 16000, data: whole.subarray(3).toString('base64') });
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 });
 await piece;
 assert.ok(got.every(c => c.length % 2 === 0));
 assert.equal(got.reduce((n, c) => n + c.length, 0), 6);
});

test('beginStream sends play and its session follows started / ended', async () => {
 const { sent, bridge, tick } = setup();
 const piece = bridge.tts.synthStream!('你好', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 }); await piece;
 const session = bridge.audio.beginStream(16000, '你好');
 assert.deepEqual(sent.at(-1), { kind: 'play', requestId: 7, pieceId: 1 });
 tick(30); bridge.onMessage({ kind: 'started', requestId: 7, pieceId: 1 });
 assert.equal(await session.started, 1030);
 tick(500); bridge.onMessage({ kind: 'ended', requestId: 7, pieceId: 1 });
 assert.equal(await session.ended, 1530);
});

test('stopped before started resolves both, so upstream never waits forever', async () => {
 const { bridge } = setup();
 const piece = bridge.tts.synthStream!('你好', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 }); await piece;
 const session = bridge.audio.beginStream(16000, '你好');
 bridge.onMessage({ kind: 'stopped', requestId: 7, pieceId: 1 });
 await session.started; await session.ended;
});

test('abort sends cancelSynth; session abort sends stop; stale turn reports are ignored', async () => {
 const { sent, bridge } = setup();
 const ac = new AbortController();
 const piece = bridge.tts.synthStream!('取消我', { pcm() {} }, ac.signal);
 ac.abort();
 await assert.rejects(piece);
 assert.deepEqual(sent.at(-1), { kind: 'cancelSynth', requestId: 7, pieceId: 1 });
 const second = bridge.tts.synthStream!('第二片', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthEnd', requestId: 6, pieceId: 2 }); // older turn: ignored
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 2 }); await second;
 bridge.audio.beginStream(16000, '第二片').abort();
 assert.deepEqual(sent.at(-1), { kind: 'stop', requestId: 7, pieceId: 2 });
});

test('synthError rejects so upstream skips the piece', async () => {
 const { bridge } = setup();
 const piece = bridge.tts.synthStream!('坏片', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthError', requestId: 7, pieceId: 1, message: 'boom' });
 await assert.rejects(piece, /boom/);
});
```

- [ ] **Step 2: 运行确认失败**

Run: `cd sidecar/cortico && npx tsx --test tests/app-audio.test.ts`
Expected: FAIL，报错 `Cannot find module '../app-audio.ts'`

- [ ] **Step 3: 实现 `sidecar/cortico/app-audio.ts`**

```ts
/**
 * App-owned audio for the unmodified upstream Performer. The app synthesizes and plays every
 * piece; this bridge turns its reports into the PerformerTts.synthStream / AudioSink.beginStream
 * contracts, so upstream pacing, lip-sync, accents and anchors run on the app's real timeline.
 */
import type { AudioSink, AudioStreamSession, PerformerTts } from './upstream/orchestrator.ts';
import { StreamingEnvelope, type TtsPiece, type TtsStreamSink } from './upstream/tts.ts';

type Send = (message: object) => void;

interface Unit {
 pieceId: number; text: string; sink: TtsStreamSink;
 sampleRate: number; envelope: StreamingEnvelope | null; bytes: number; carry: number | null;
 synthDone: boolean; failed: boolean; played: boolean;
 resolve: (piece: TtsPiece) => void; reject: (error: Error) => void;
 started?: (t: number) => void; ended?: (t: number) => void;
}

const toFloat = (pcm: Uint8Array) => {
 const view = new DataView(pcm.buffer, pcm.byteOffset, pcm.byteLength);
 const out = new Float32Array(pcm.byteLength / 2);
 for (let i = 0; i < out.length; i++) out[i] = view.getInt16(i * 2, true) / 32768;
 return out;
};

export class AppAudioBridge {
 private seq = 0;
 private requestId = 0;
 private readonly units = new Map<number, Unit>();
 private readonly order: number[] = [];
 /** Set while the host is being cut for a model switch: the app keeps its current piece. */
 suppressStop = false;

 constructor(private readonly send: Send, private readonly now: () => number = Date.now) {}

 /** A new app turn: reports for earlier turns are ignored from now on. */
 beginTurn(requestId: number): void {
  this.reset();
  this.requestId = requestId;
  this.suppressStop = false;
 }

 /** Releases every waiter (upstream must never hang on a piece the app will not report). */
 reset(): void {
  const t = this.now();
  for (const u of this.units.values()) {
   if (!u.synthDone) { u.synthDone = true; u.reject(new Error('Cancelled')); }
   u.started?.(t); u.ended?.(t);
  }
  this.units.clear();
  this.order.length = 0;
 }

 readonly tts: PerformerTts = {
  synth: async () => { throw new Error('Whole-piece synthesis is not used: audio is app-owned'); },
  synthStream: (text, sink, signal) => new Promise<TtsPiece>((resolve, reject) => {
   if (signal.aborted) { reject(new Error('Cancelled')); return; }
   const pieceId = ++this.seq;
   const unit: Unit = { pieceId, text, sink, sampleRate: 0, envelope: null, bytes: 0, carry: null,
    synthDone: false, failed: false, played: false, resolve, reject };
   this.units.set(pieceId, unit);
   this.order.push(pieceId);
   signal.addEventListener('abort', () => {
    if (unit.synthDone) return;
    unit.synthDone = true; unit.failed = true;
    this.drop(pieceId);
    this.send({ kind: 'cancelSynth', requestId: this.requestId, pieceId });
    reject(new Error('Cancelled'));
   }, { once: true });
   this.send({ kind: 'synth', requestId: this.requestId, pieceId, text });
  }),
 };

 readonly audio: AudioSink = {
  play: async () => { throw new Error('Whole-piece playback is not used: audio is app-owned'); },
  beginStream: (_sampleRate, text) => this.beginStream(text),
  // The app owns its player; per-piece stops go through the session's abort.
  stop: () => {},
 };

 private beginStream(text: string): AudioStreamSession {
  let started!: (t: number) => void; let ended!: (t: number) => void;
  const session: AudioStreamSession = {
   started: new Promise<number>(r => { started = r; }),
   ended: new Promise<number>(r => { ended = r; }),
   push() {}, end() {}, abort() {},
  };
  // Upstream plays segments in synthesis order and skips failed ones.
  const pieceId = this.order.find(id => { const u = this.units.get(id); return !!u && !u.played && !u.failed && u.text === text; });
  if (pieceId === undefined) { const t = this.now(); started(t); ended(t); return session; }
  const unit = this.units.get(pieceId)!;
  unit.played = true; unit.started = started; unit.ended = ended;
  session.abort = () => {
   if (!this.suppressStop) this.send({ kind: 'stop', requestId: this.requestId, pieceId });
   const t = this.now(); started(t); ended(t);
   this.drop(pieceId);
  };
  this.send({ kind: 'play', requestId: this.requestId, pieceId });
  return session;
 }

 /** App → host report. Reports for another turn or an unknown piece are ignored. */
 onMessage(m: any): void {
  if (m.requestId !== this.requestId) return;
  const u = this.units.get(m.pieceId);
  if (!u) return;
  switch (m.kind) {
   case 'pcm': {
    let bytes: Uint8Array = Buffer.from(String(m.data), 'base64');
    if (u.carry !== null) { const merged = new Uint8Array(bytes.length + 1); merged[0] = u.carry; merged.set(bytes, 1); bytes = merged; u.carry = null; }
    if (bytes.length % 2) { u.carry = bytes[bytes.length - 1]; bytes = bytes.subarray(0, bytes.length - 1); }
    if (!u.envelope) {
     u.sampleRate = Number(m.sampleRate) || 24000;
     u.envelope = new StreamingEnvelope(u.sampleRate);
     u.sink.begin?.({ sampleRate: u.sampleRate, envelope: u.envelope });
    }
    if (bytes.length === 0) return;
    u.envelope.append(toFloat(bytes));
    u.bytes += bytes.length;
    u.sink.pcm(bytes);
    return;
   }
   case 'synthEnd': {
    if (u.synthDone) return;
    if (!u.envelope) { u.sampleRate = 24000; u.envelope = new StreamingEnvelope(u.sampleRate); }
    u.envelope.finish();
    u.synthDone = true;
    u.resolve({ text: u.text, wav: new Uint8Array(0), envelope: u.envelope,
     durationMs: u.bytes / 2 / u.sampleRate * 1000 });
    return;
   }
   case 'synthError':
    if (u.synthDone) return;
    u.synthDone = true; u.failed = true;
    this.drop(u.pieceId);
    u.reject(new Error(String(m.message || 'TTS failed')));
    return;
   case 'started': u.started?.(this.now()); return;
   case 'ended': case 'stopped': {
    const t = this.now(); u.started?.(t); u.ended?.(t);
    this.drop(u.pieceId);
    return;
   }
  }
 }

 private drop(pieceId: number): void {
  this.units.delete(pieceId);
  const at = this.order.indexOf(pieceId);
  if (at >= 0) this.order.splice(at, 1);
 }
}
```

- [ ] **Step 4: 运行 bridge 测试确认通过**

Run: `cd sidecar/cortico && npx tsx --test tests/app-audio.test.ts`
Expected: 6 个用例全部 PASS

- [ ] **Step 5: 把 `host.ts` 接到 bridge 上**

对 `sidecar/cortico/host.ts` 做以下修改（其它部分保持不变）：

1. 删除 import：`DeviceAudioSink`、`AudioSink` 类型、`decodeWav, extractEnvelope, pcm16ToWav`；新增 `import { AppAudioBridge } from './app-audio.ts';`。
2. 删除 `seq`、`replies`、`ask()` 三个定义，以及 `let device` 和 `createRequire` 的导入与调用（`if (config.audioDevice !== 'none') createRequire(...)('audify')`）。
3. 在 `let active` 附近新增：`const bridge = new AppAudioBridge(send);`
4. 在 `interrupt` 里，`await performer?.whenIdle();` 之后加一行 `bridge.reset();`。
5. 删除 `initialize()` 中的 `audioLog`、`sink`、`device = sink`，以及整个 `const audio: AudioSink = {...}` 块。把 `performer = new Performer({...})` 替换为：

```ts
 performer = new Performer({ pack: () => pack, mixer, backend, log,
  audio: bridge.audio, tts: bridge.tts,
  // App TTS streams; upstream forced alignment (a VoxCPM server feature) is not available.
  streamEnabled: () => true, alignEnabled: () => false,
  trace: (area, message, opts) => { log.debug(message, { area, ...opts });
   if (opts?.level === 'error' && active) active.failure = message;
  },
 });
```

6. 在 `handle()` 的开头，把 `if (message.kind === 'reply') {...}` 块替换为：

```ts
 if (['pcm', 'synthEnd', 'synthError', 'started', 'ended', 'stopped'].includes(message.kind)) {
  bridge.onMessage(message);
  return;
 }
```

7. 在 `perform` 分支里，`active = current;` 之后加一行 `bridge.beginTurn(id);`。
8. 删除整个 `else if (command === 'prepare') {...}` 分支，以及 `ScriptParser` 的 import。
9. `close()` 中删除 `device?.close();`。

- [ ] **Step 6: 修改测试 harness，让测试替身扮演 App**

在 `sidecar/cortico/tests/harness.ts` 的 `Host` 类里：

- 删除字段 `authorized`、`allow`；保留 `ttsTexts`、`holdTts`、`onTts`、`starts`；新增：

```ts
 readonly played: string[] = [];
 readonly controls: any[] = [];
 /** Return false to refuse a play (the app's own gate): the host gets 'stopped'. */
 allowPlay: (text: string) => boolean = () => true;
 playMs = 400;
 private texts = new Map<number, string>();
```

- 把 stdout 处理里的 `authorize` 和 `tts` 两个分支替换为：

```ts
   if (m.kind === 'synth') {
    this.ttsTexts.push(m.text); this.texts.set(m.pieceId, m.text); this.onTts?.(m.text);
    if (!this.holdTts) {
     this.send({ kind: 'pcm', requestId: m.requestId, pieceId: m.pieceId, sampleRate: 16000, data: tone(this.playMs) });
     this.send({ kind: 'synthEnd', requestId: m.requestId, pieceId: m.pieceId });
    }
   }
   if (m.kind === 'play') {
    const text = this.texts.get(m.pieceId) ?? '';
    if (!this.allowPlay(text)) { this.send({ kind: 'stopped', requestId: m.requestId, pieceId: m.pieceId }); return; }
    this.played.push(text); this.starts++;
    this.send({ kind: 'started', requestId: m.requestId, pieceId: m.pieceId });
    setTimeout(() => this.send({ kind: 'ended', requestId: m.requestId, pieceId: m.pieceId }), this.playMs);
   }
   if (['cancelSynth', 'stop', 'cue', 'aborted'].includes(m.kind)) this.controls.push(m);
```

- 删除 `if (m.kind === 'started') this.starts++;` 这一行（host 不再发 `started`）。
- `init()` 中删除 `audioDevice: 'none'`。

- [ ] **Step 7: 更新现有 host 测试的断言**

在 `tests/stream.test.ts` 和 `tests/host.test.ts` 中：

- `host.authorized` 改为 `host.played`（它记录的就是真正开播的干净文本）。
- 旧用例 `a denied piece stops the rest of that turn...`（stream.test.ts 第 31 行起）改为使用 `host.allowPlay = t => t !== '被拒绝的一句。'`，断言：被拒绝的那句不在 `host.played` 里，而且 `perf.result` 在 5 秒内完成。App 此后会发 `interrupt`；这里测试替身手动发送：`host.command('interrupt', { fence: perf.id })`。
- 在 `host.test.ts` 第 21 行的用例中，删除针对 `prepare` 命令的断言。
- 在 `stream.test.ts` 开头的用例里追加一条断言：`play` 一定在对应的 `synth` 之后（用 `host.ttsTexts.indexOf(t) >= 0` 检查 `host.played` 的每一项）。

- [ ] **Step 8: 去掉 `audify` 依赖**

```bash
cd sidecar/cortico && npm uninstall audify
```

在 `Setup-Cortico.ps1` 中删除第 14–15 行对 audify 的检查（把 `npm ci` 失败时的提示改为 `'npm ci failed.'`，并删除 `& $node -e "require('audify')"` 那一行）。

- [ ] **Step 9: 跑完整 sidecar 测试和类型检查**

Run: `cd sidecar/cortico && npm test && npm run typecheck`
Expected: 全部 PASS（包括 `vendored upstream files exactly match the pinned source hashes`）

- [ ] **Step 10: Commit**

```bash
git add sidecar/cortico
git commit -m "Cortico host: app-owned audio bridge; the sidecar never plays or synthesizes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: host 的存活与切皮套信号（cue、maxHoldMs、aborted）

**Files:**
- Create: `sidecar/cortico/timing.ts`
- Modify: `sidecar/cortico/host.ts`
- Test: `sidecar/cortico/tests/stream.test.ts`，新增 `sidecar/cortico/tests/timing.test.ts`

**Interfaces:**
- Consumes：Task 1 的 `AppAudioBridge.suppressStop`
- Produces（host → App）：`{kind:'cue',requestId}`（每一拍真正触发时发送）；`{kind:'aborted',requestId,reason:'model-changed'}`；每条 `status` 都带 `maxHoldMs:number`

- [ ] **Step 1: 写失败测试**

`sidecar/cortico/tests/timing.test.ts`：

```ts
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { maxHoldMs, UPSTREAM_TIMING } from '../timing.ts';
import { loadPack, EXAMPLE_PACK_DIR } from '../upstream/pack.ts';

test('mirrored timing constants equal the vendored upstream ones', () => {
 const src = readFileSync(new URL('../upstream/orchestrator.ts', import.meta.url), 'utf8');
 for (const [name, value] of Object.entries(UPSTREAM_TIMING))
  assert.match(src, new RegExp(`const ${name} = ${String(value).replace('.', '\\.')};`), name);
});

test('maxHoldMs covers the longest gesture block of the pack', () => {
 const pack = loadPack(EXAMPLE_PACK_DIR);
 const longest = Math.max(...Object.values(pack.pulse).map(c => c.durationMs));
 assert.ok(maxHoldMs(pack) >= 300 + longest);
 assert.ok(maxHoldMs(pack) >= 1200 * 1.1);
});
```

在 `stream.test.ts` 末尾追加：

```ts
test('each fired beat sends cue; status carries maxHoldMs', { timeout: 30000 }, async () => {
 await withHost(async (_vts, host) => {
  const perf = await host.begin();
  await perf.feed('【点头】你好。【摇头】再见。');
  await perf.end(); await perf.result;
  assert.ok(host.controls.filter(c => c.kind === 'cue' && c.requestId === perf.id).length >= 2);
  await until(() => host.statuses.some(s => typeof s.maxHoldMs === 'number' && s.maxHoldMs > 0));
 });
});

test('a model switch mid-piece sends aborted and no stop for the playing piece', { timeout: 30000 }, async () => {
 await withHost(async (vts, host) => {
  host.playMs = 1500;
  const perf = await host.begin();
  await perf.feed('这一句会被切皮套打断。');
  await until(() => host.played.length === 1);
  vts.loadModel({ name: 'Other', inputs: PACK_PARAMS });
  await until(() => host.controls.some(c => c.kind === 'aborted' && c.requestId === perf.id));
  assert.ok(!host.controls.some(c => c.kind === 'stop' && c.requestId === perf.id));
 });
});
```

为此要让 `harness.ts` 的 `statuses` 记录所有 `status`：把 `if (m.kind === 'status' && m.profile) this.statuses.push(m);` 改为 `if (m.kind === 'status') this.statuses.push(m);`；原来依赖 `statuses[0]` 取 profile 的用例，改为 `host.statuses.filter(s => s.profile)`。

- [ ] **Step 2: 运行确认失败**

Run: `cd sidecar/cortico && npx tsx --test tests/timing.test.ts tests/stream.test.ts`
Expected: FAIL（`timing.ts` 不存在；收不到 `cue` 和 `aborted`）

- [ ] **Step 3: 实现 `sidecar/cortico/timing.ts`**

```ts
/**
 * The longest time upstream may legitimately hold a ready piece before asking to play it.
 * Upstream keeps these constants module-private; tests/timing.test.ts pins the mirror to the
 * vendored source so a re-vendor that changes them fails loudly.
 */
import type { PerformancePack } from './upstream/pack.ts';

export const UPSTREAM_TIMING = {
 GAP_CAP_MS: 1200,
 GAP_JITTER: 0.1,
 SAME_BEAT_GESTURE_DELAY_MS: 300,
} as const;

/** One beat: the boundary pause (capped, with jitter) or a blocking gesture, whichever is longer. */
export function maxHoldMs(pack: PerformancePack): number {
 const longestPulse = Math.max(0, ...Object.values(pack.pulse).map(clip => clip.durationMs));
 const gap = UPSTREAM_TIMING.GAP_CAP_MS * (1 + UPSTREAM_TIMING.GAP_JITTER);
 return Math.ceil(Math.max(gap, UPSTREAM_TIMING.SAME_BEAT_GESTURE_DELAY_MS + longestPulse));
}
```

- [ ] **Step 4: 修改 `host.ts`**

1. `import { maxHoldMs } from './timing.ts';`
2. 在 `new Performer({...})` 的参数里加 `onCue: () => { if (active) send({ kind: 'cue', requestId: active.id }); },`
3. `sync()` 里的第一条 `send({ kind: 'status', connected: true, ... })` 加字段 `maxHoldMs: maxHoldMs(pack),`；周期心跳改为 `send({ kind: 'status', connected: client.connected, maxHoldMs: maxHoldMs(pack) })`。
4. 把 `client.onModelLoaded(...)` 的回调改为：

```ts
 client.onModelLoaded(() => {
  // A different rig: old rounds, held states and mappings must not reach it. The app keeps the
  // piece it is already playing and finishes the turn voice-only, so no stop for that piece.
  if (active) { send({ kind: 'aborted', requestId: active.id, reason: 'model-changed' }); bridge.suppressStop = true; }
  syncing = true; backend?.beginParameterSync();
  void interrupt().then(sync).catch(e => log.error(String(e)));
 });
```

- [ ] **Step 5: 运行测试确认通过**

Run: `cd sidecar/cortico && npm test && npm run typecheck`
Expected: 全部 PASS

- [ ] **Step 6: Commit**

```bash
git add sidecar/cortico
git commit -m "Cortico host: beat cues, max legitimate hold, aborted on model switch

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: App 侧 Cortico IPC 协议

**Files:**
- Modify: `AIVTuber.Core/Cortico/ICorticoPerformance.cs`（整体替换）
- Modify: `AIVTuber.Core/Cortico/CorticoProcess.cs`
- Modify: `AIVTuber.Tests/CorticoProcessTests.cs`（整体替换）
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs:638`（`StartAsync` 调用）、`AIVTuber.Tests/Cortico/RuntimeCorticoAcceptanceTests.cs:136`（`StartAsync` 调用，暂时只改签名）

**Interfaces:**
- Consumes：Task 1、2 的消息
- Produces：

```csharp
public interface ICorticoPerformance
{
    string ScriptGrammar { get; }
    int MaxHoldMs { get; }
    bool IsAlive { get; }
    Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct);
    Task InterruptAsync(CancellationToken ct);
}
public interface ICorticoAudioHandler
{
    void Synth(long pieceId, string text);
    void CancelSynth(long pieceId);
    void Play(long pieceId);
    void Stop(long pieceId);
    void Cue();
    void Aborted(string reason);
}
public interface ICorticoStage : IAsyncDisposable
{
    Task FeedAsync(string script, CancellationToken ct);
    Task CompleteAsync(CancellationToken ct);
    Task PcmAsync(long pieceId, int sampleRate, byte[] pcm);
    Task SynthEndAsync(long pieceId);
    Task SynthErrorAsync(long pieceId, string message);
    Task StartedAsync(long pieceId);
    Task EndedAsync(long pieceId);
    Task StoppedAsync(long pieceId);
}
```

`CorticoProcess.StartAsync(CorticoOptions options, string baseDir, VtsConfig vts, Action<string> diagnostic, CancellationToken ct)`

- [ ] **Step 1: 写失败测试（替换 `AIVTuber.Tests/CorticoProcessTests.cs`）**

```csharp
using System.Diagnostics;
using AIVTuber.Core.Config;
using AIVTuber.Core.Cortico;

namespace AIVTuber.Tests;

public sealed class CorticoProcessTests
{
    /// <summary>Plays the app's side of the protocol: tone PCM for every synth, started/ended for every play.</summary>
    private sealed class ToneApp : ICorticoAudioHandler
    {
        public ICorticoStage Stage = null!;
        public readonly List<string> Synth = [], Played = [], Log = [];
        private readonly Dictionary<long, string> _texts = [];
        public bool HoldSynth;
        public readonly TaskCompletionSource SynthHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void ICorticoAudioHandler.Synth(long pieceId, string text)
        {
            lock (Log) { Synth.Add(text); _texts[pieceId] = text; Log.Add($"synth:{pieceId}"); }
            if (HoldSynth) { SynthHeld.TrySetResult(); return; }
            _ = Task.Run(async () =>
            {
                var pcm = new byte[6400];
                for (var i = 0; i < pcm.Length / 2; i++)
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(3000 * Math.Sin(i / 8.0)));
                await Stage.PcmAsync(pieceId, 16000, pcm);
                await Stage.SynthEndAsync(pieceId);
            });
        }
        void ICorticoAudioHandler.CancelSynth(long pieceId) { lock (Log) Log.Add($"cancelSynth:{pieceId}"); }
        void ICorticoAudioHandler.Play(long pieceId)
        {
            lock (Log) { Played.Add(_texts[pieceId]); Log.Add($"play:{pieceId}"); }
            _ = Task.Run(async () => { await Stage.StartedAsync(pieceId); await Task.Delay(200); await Stage.EndedAsync(pieceId); });
        }
        void ICorticoAudioHandler.Stop(long pieceId) { lock (Log) Log.Add($"stop:{pieceId}"); }
        void ICorticoAudioHandler.Cue() { lock (Log) Log.Add("cue"); }
        void ICorticoAudioHandler.Aborted(string reason) { lock (Log) Log.Add($"aborted:{reason}"); }
    }

    [SkippableFact]
    public async Task RealNodeHost_PacesAppAudio_AndCancelsPendingSynthesis()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "sidecar/cortico/host.ts")))
            directory = directory.Parent;
        Skip.If(directory is null, "Source checkout is required");
        var sidecar = Path.Combine(directory!.FullName, "sidecar/cortico");
        Skip.IfNot(Directory.Exists(Path.Combine(sidecar, "node_modules/tsx")), "Run npm ci in sidecar/cortico first");
        var temp = Path.Combine(Path.GetTempPath(), "cortico-ipc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo("node") { WorkingDirectory = sidecar, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--import"); start.ArgumentList.Add("tsx"); start.ArgumentList.Add("tests/fake-vts.ts");
        using var server = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var port = int.Parse((await server.StandardOutput.ReadLineAsync(deadline.Token))!);
            await using var bridge = await CorticoProcess.StartAsync(new CorticoOptions { Enabled = true, SidecarPath = sidecar },
                temp, new VtsConfig { Host = "127.0.0.1", Port = port }, _ => { }, deadline.Token);
            Assert.Contains("点头", bridge.ScriptGrammar);
            Assert.True(bridge.IsAlive);

            var app = new ToneApp();
            await using (var stage = await bridge.BeginAsync(app, deadline.Token))
            {
                app.Stage = stage;
                await stage.FeedAsync("<微笑>你好【点头】再见。", deadline.Token);
                await stage.CompleteAsync(deadline.Token);
            }
            Assert.Equal(["你好", "再见。"], app.Played);
            Assert.True(bridge.MaxHoldMs > 1000);
            lock (app.Log)
                foreach (var played in app.Log.Where(l => l.StartsWith("play:")))
                    Assert.True(app.Log.IndexOf("synth:" + played[5..]) < app.Log.IndexOf(played));

            var held = new ToneApp { HoldSynth = true };
            using var cancel = new CancellationTokenSource();
            var stage2 = await bridge.BeginAsync(held, deadline.Token);
            held.Stage = stage2;
            await stage2.FeedAsync("这次取消", deadline.Token);
            await held.SynthHeld.Task.WaitAsync(deadline.Token);
            await stage2.DisposeAsync(); // unfinished: interrupts the host
            await TestWait.Until(() => { lock (held.Log) return held.Log.Any(l => l.StartsWith("cancelSynth:")); });

            var again = new ToneApp();
            await using (var stage3 = await bridge.BeginAsync(again, deadline.Token))
            {
                again.Stage = stage3;
                await stage3.FeedAsync("再试一次", deadline.Token);
                await stage3.CompleteAsync(deadline.Token);
            }
            Assert.Equal(["再试一次"], again.Played);
        }
        finally
        {
            server.StandardInput.Close();
            try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { server.Kill(true); }
            Directory.Delete(temp, true);
        }
    }
}

internal static class TestWait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("condition not met before timeout");
            await Task.Delay(20);
        }
    }
}
```

- [ ] **Step 2: 运行确认编译失败**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~CorticoProcessTests"`
Expected: 编译失败（`ICorticoAudioHandler` 不存在，`StartAsync` 参数个数不对）

- [ ] **Step 3: 替换 `ICorticoPerformance.cs`**

```csharp
namespace AIVTuber.Core.Cortico;

/// <summary>
/// The Cortico sidecar as a visual follower. It decides when each piece may start (upstream
/// pacing: boundary pauses, blocking gestures) and drives the rig from the app's real playback;
/// it never synthesizes or plays audio itself.
/// </summary>
public interface ICorticoPerformance
{
    /// <summary>Cortico script grammar and vocabulary for the system prompt.</summary>
    string ScriptGrammar { get; }

    /// <summary>Longest time the host may legitimately hold a ready piece (one beat), in ms.</summary>
    int MaxHoldMs { get; }

    /// <summary>False once the sidecar exited or its 1 s heartbeat lapsed for more than 2 s.</summary>
    bool IsAlive { get; }

    /// <summary>Starts one reply. Host requests for this reply arrive on <paramref name="handler"/>.</summary>
    Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct);

    /// <summary>Stops every performance begun before this call: queued pieces, held states, gestures.</summary>
    Task InterruptAsync(CancellationToken ct);
}

/// <summary>Host → app requests for one reply. Called on the IPC reader; must not block.</summary>
public interface ICorticoAudioHandler
{
    /// <summary>Start synthesizing this clean text and report PCM for it.</summary>
    void Synth(long pieceId, string text);
    /// <summary>Upstream no longer wants this piece: stop its synthesis and drop its audio.</summary>
    void CancelSynth(long pieceId);
    /// <summary>Upstream's moment to speak this piece.</summary>
    void Play(long pieceId);
    /// <summary>Upstream cut this piece while it was playing.</summary>
    void Stop(long pieceId);
    /// <summary>A beat fired (liveness for the hold watchdog).</summary>
    void Cue();
    /// <summary>The rig side ended this reply (model switch): finish the current piece, the rest voice-only.</summary>
    void Aborted(string reason);
}

/// <summary>One reply being performed. Dispose always; disposing an unfinished stage interrupts it.</summary>
public interface ICorticoStage : IAsyncDisposable
{
    /// <summary>Performs one approved script segment (appended after the earlier ones).</summary>
    Task FeedAsync(string script, CancellationToken ct);
    /// <summary>No more segments: waits until everything fed has been performed.</summary>
    Task CompleteAsync(CancellationToken ct);
    Task PcmAsync(long pieceId, int sampleRate, byte[] pcm);
    Task SynthEndAsync(long pieceId);
    Task SynthErrorAsync(long pieceId, string message);
    /// <summary>The piece's first audio actually reached the sound card.</summary>
    Task StartedAsync(long pieceId);
    Task EndedAsync(long pieceId);
    Task StoppedAsync(long pieceId);
}
```

- [ ] **Step 4: 修改 `CorticoProcess.cs`**

1. `CorticoOptions`：删除 `AudioDevice` 属性。
2. 删除字段 `_callbacks`、`_callbackSequence`、`_tts`、`_ttsConfig`，删除 `Turn` record 及 `using AIVTuber.Core.Pipeline;`；`_turns` 的类型改为 `ConcurrentDictionary<long, ICorticoAudioHandler>`。新增字段：

```csharp
    private long _lastStatusAt = Environment.TickCount64;
    public int MaxHoldMs { get; private set; } = 5000;
    public bool IsAlive => !_lifetime.IsCancellationRequested &&
        Environment.TickCount64 - Interlocked.Read(ref _lastStatusAt) <= 2000;
```

3. 构造函数改为 `private CorticoProcess(Process process) => _process = process;`；`StartAsync` 的签名按上面 Interfaces 修改，函数体里 `new CorticoProcess(process)`，并从 `init` 的 config 中删除 `options.AudioDevice`。收到 `init` 结果后加一行 `Interlocked.Exchange(ref self._lastStatusAt, Environment.TickCount64);`。
4. 删除 `PrepareAsync`。`BeginAsync` 改为：

```csharp
    public async Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _sequence);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready[id] = ready;
        _turns[id] = handler;
        var stage = new Stage(this, id, timeout);
        stage.Result = CommandAsync(new { command = "perform" }, timeout.Token, id);
        try
        {
            var first = await Task.WhenAny(ready.Task, stage.Result).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (first == stage.Result) await stage.Result.ConfigureAwait(false);
            return stage;
        }
        catch
        {
            await stage.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _ready.TryRemove(id, out _); }
    }
```

5. 在 `Stage` 里加入回报方法（发送失败时不抛异常：sidecar 已退出的情况由 `IsAlive` 负责判断）：

```csharp
        public Task PcmAsync(long pieceId, int sampleRate, byte[] pcm) =>
            owner.ReportAsync(new { kind = "pcm", requestId = id, pieceId, sampleRate, data = Convert.ToBase64String(pcm) });
        public Task SynthEndAsync(long pieceId) => owner.ReportAsync(new { kind = "synthEnd", requestId = id, pieceId });
        public Task SynthErrorAsync(long pieceId, string message) => owner.ReportAsync(new { kind = "synthError", requestId = id, pieceId, message });
        public Task StartedAsync(long pieceId) => owner.ReportAsync(new { kind = "started", requestId = id, pieceId });
        public Task EndedAsync(long pieceId) => owner.ReportAsync(new { kind = "ended", requestId = id, pieceId });
        public Task StoppedAsync(long pieceId) => owner.ReportAsync(new { kind = "stopped", requestId = id, pieceId });
```

并在 `CorticoProcess` 中加：

```csharp
    private async Task ReportAsync(object message)
    {
        if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested) return;
        try { await SendAsync(message, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { Diagnostic?.Invoke($"report dropped: {ex.Message}"); }
    }
```

6. `ReadAsync` 中：`status` 分支改为

```csharp
                else if (kind == "status")
                {
                    IsConnected = message.GetProperty("connected").GetBoolean();
                    if (message.TryGetProperty("maxHoldMs", out var hold) && hold.TryGetInt32(out var holdMs) && holdMs > 0)
                        MaxHoldMs = holdMs;
                    Interlocked.Exchange(ref _lastStatusAt, Environment.TickCount64);
                    Diagnostic?.Invoke(line);
                }
```

并把 `kind is "tts" or "authorize" or "started"` 分支替换为：

```csharp
                else if (kind is "synth" or "cancelSynth" or "play" or "stop" or "cue" or "aborted")
                    Dispatch(kind, message);
```

7. 删除 `HandleCallbackAsync`，改为：

```csharp
    private void Dispatch(string kind, JsonElement message)
    {
        if (!_turns.TryGetValue(message.GetProperty("requestId").GetInt64(), out var handler)) return;
        var pieceId = message.TryGetProperty("pieceId", out var piece) ? piece.GetInt64() : 0;
        try
        {
            switch (kind)
            {
                case "synth": handler.Synth(pieceId, message.GetProperty("text").GetString() ?? ""); break;
                case "cancelSynth": handler.CancelSynth(pieceId); break;
                case "play": handler.Play(pieceId); break;
                case "stop": handler.Stop(pieceId); break;
                case "cue": handler.Cue(); break;
                case "aborted": handler.Aborted(message.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""); break;
            }
        }
        catch (Exception ex) { Diagnostic?.Invoke($"{kind} handler failed: {ex.Message}"); }
    }
```

8. `DisposeAsync` 中删除 `await Task.WhenAll(_callbacks.Values)`。
9. 删除 `CorticoPerformanceExtensions`（已在 Step 3 的整体替换中去掉）。
10. 把 `BotRuntime.cs:638` 改为 `CorticoProcess.StartAsync(_corticoOptions, _baseDir, _config.Vts, message => ..., _cts.Token)`（删除 `() => _tts, () => _config.Tts` 两个参数）。`RuntimeCorticoAcceptanceTests.cs:136` 同样删除这两个参数和 `AudioDevice = "none"`。

- [ ] **Step 5: 暂时屏蔽依赖旧接口的测试，让本任务可以单独编译**

旧的 `FakeCortico`（`AIVTuber.Tests/Cortico/CorticoFakes.cs`）和 `CorticoOrchestratorTests.cs` 会在 Task 5 重写；`BotOrchestrator.RunCorticoAsync` 会在 Task 7 删除。本任务内做最小改动以保证能编译：

- `BotOrchestrator.RunCorticoAsync`：函数体替换为 `throw new NotSupportedException("Cortico 管线在 Task 7 中迁移到 CorticoPacer");`，然后删除其中已无用的局部函数。
- `CorticoOrchestratorTests.cs`：给类加 `[Trait("Pending", "Task5")]`，并把每个测试标成 `[Fact(Skip = "rewritten in Task 5")]` / `[Theory(Skip = "rewritten in Task 5")]`。
- `CorticoFakes.cs`：`FakeCortico` 改为实现新接口，所有成员的方法体暂时写成 `throw new NotImplementedException();`（Task 5 会整体替换）。
- `RuntimeCorticoAcceptanceTests`：每个测试加 `Skip = "rewired in Task 8"`。

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~CorticoProcessTests"`
Expected: PASS（本机没有执行过 `npm ci` 时为 Skipped；先在 `sidecar/cortico` 目录执行 `npm ci`）

- [ ] **Step 7: Commit**

```bash
git add AIVTuber.Core AIVTuber.Tests
git commit -m "Cortico IPC: host requests synth/play, app reports audio; heartbeat liveness and hold budget

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 节拍器基础件与 ImmediatePacer

**Files:**
- Create: `AIVTuber.Core/Cortico/CorticoScript.cs`
- Create: `AIVTuber.Core/Bot/Pacing/SpeechPacing.cs`
- Create: `AIVTuber.Core/Bot/Pacing/PieceAudio.cs`
- Create: `AIVTuber.Core/Bot/Pacing/ImmediatePacer.cs`
- Test: `AIVTuber.Tests/Pacing/PacingTestKit.cs`、`AIVTuber.Tests/Pacing/ImmediatePacerTests.cs`、`AIVTuber.Tests/Pacing/CorticoScriptTests.cs`

**Interfaces:**
- Produces：

```csharp
internal static class CorticoScript { public static string Clean(string script); public static string Normalize(string text); }
internal readonly record struct SpeechItem(string Text, string? Emotion = null, PieceAudio? Audio = null);
internal sealed record SpeechTurnPorts(
    Func<string, string?, CancellationToken, IAsyncEnumerable<byte[]>> Synthesize,
    Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> Play,
    Func<bool> CanSpeak, Action<string> Commit, Action OnFirstPcm, Action<string> Warn, int SampleRate);
internal interface ISpeechPacer : IAsyncDisposable
{ Task SubmitAsync(SpeechItem item, CancellationToken ct); Task CompleteAsync(CancellationToken ct); }
internal sealed class PieceAudio
{
    public static PieceAudio Start(string text, Func<CancellationToken, IAsyncEnumerable<byte[]>> source,
        Func<byte[], Task> forward, Action firstChunk, Func<Exception?, Task> finished, CancellationToken turn);
    public string Text { get; }
    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken ct);
    public void Cancel();
}
internal sealed class ImmediatePacer(SpeechTurnPorts ports, CancellationToken turn, Task? after = null) : ISpeechPacer
{ public void Enqueue(SpeechItem item); }
```

- [ ] **Step 1: 写测试工具与失败测试**

`AIVTuber.Tests/Pacing/PacingTestKit.cs`：

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Bot.Pacing;

namespace AIVTuber.Tests.Pacing;

/// <summary>Ports whose "audio" is the UTF-8 of the spoken text, so what was played is readable.</summary>
internal sealed class PacingTestKit
{
    public readonly List<string> Synthesized = [], Committed = [], Warnings = [];
    public readonly List<string> Played = [];
    public int FirstPcm;
    public bool Speakable = true;
    public Func<string, Task>? BeforeSynthChunk;
    public TaskCompletionSource? HoldPlayback;

    public SpeechTurnPorts Ports => new(Synth, Play, () => Speakable,
        t => { lock (Committed) Committed.Add(t); }, () => Interlocked.Increment(ref FirstPcm),
        w => { lock (Warnings) Warnings.Add(w); }, 24000);

    private async IAsyncEnumerable<byte[]> Synth(string text, string? emotion, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (Synthesized) Synthesized.Add(text);
        if (BeforeSynthChunk is not null) await BeforeSynthChunk(text);
        ct.ThrowIfCancellationRequested();
        yield return Encoding.UTF8.GetBytes(text);
    }

    private async Task Play(IAsyncEnumerable<byte[]> chunks, CancellationToken ct, Action? first)
    {
        var bytes = new List<byte>();
        var started = false;
        try
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (!started) { started = true; first?.Invoke(); }
                bytes.AddRange(chunk);
            }
            if (HoldPlayback is not null) await HoldPlayback.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) { }
        finally { if (bytes.Count > 0) lock (Played) Played.Add(Encoding.UTF8.GetString(bytes.ToArray())); }
    }
}
```

`AIVTuber.Tests/Pacing/ImmediatePacerTests.cs`：

```csharp
using AIVTuber.Core.Bot.Pacing;

namespace AIVTuber.Tests.Pacing;

public sealed class ImmediatePacerTests
{
    [Fact]
    public async Task Segments_AreSynthesizedCommittedAndPlayedInOrder_InOnePlayback()
    {
        var kit = new PacingTestKit();
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await pacer.SubmitAsync(new SpeechItem("再见。"), default);
        await pacer.CompleteAsync(default);
        Assert.Equal(["你好。", "再见。"], kit.Committed);
        Assert.Equal(["你好。再见。"], kit.Played);
        Assert.Equal(1, kit.FirstPcm);
    }

    [Fact]
    public async Task NotSpeakableAnymore_BeforeAudio_NothingIsCommittedOrPlayed()
    {
        var kit = new PacingTestKit();
        kit.BeforeSynthChunk = _ => { kit.Speakable = false; return Task.CompletedTask; };
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await pacer.CompleteAsync(default);
        Assert.Empty(kit.Committed);
        Assert.Empty(kit.Played);
    }

    [Fact]
    public async Task ProvidedAudio_IsPlayedWithoutSynthesizingAgain()
    {
        var kit = new PacingTestKit();
        var audio = PieceAudio.Start("已合成。", ct => One("已合成。"), _ => Task.CompletedTask, () => { }, _ => Task.CompletedTask, default);
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None);
        pacer.Enqueue(new SpeechItem("已合成。", Audio: audio));
        await pacer.CompleteAsync(default);
        Assert.Empty(kit.Synthesized);
        Assert.Equal(["已合成。"], kit.Played);
    }

    [Fact]
    public async Task After_DelaysThePlaybackUntilThatTaskEnds()
    {
        var kit = new PacingTestKit();
        var gate = new TaskCompletionSource();
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None, after: gate.Task);
        await pacer.SubmitAsync(new SpeechItem("后说。"), default);
        await Task.Delay(100);
        Assert.Empty(kit.Played);
        gate.SetResult();
        await pacer.CompleteAsync(default);
        Assert.Equal(["后说。"], kit.Played);
    }

    private static async IAsyncEnumerable<byte[]> One(string text)
    {
        await Task.Yield();
        yield return System.Text.Encoding.UTF8.GetBytes(text);
    }
}
```

`AIVTuber.Tests/Pacing/CorticoScriptTests.cs`：

```csharp
using AIVTuber.Core.Cortico;

namespace AIVTuber.Tests.Pacing;

public sealed class CorticoScriptTests
{
    [Theory]
    [InlineData("<微笑>你好【点头】再见。", "你好 再见。")]
    [InlineData("【点头】", "")]
    [InlineData("  hello   <开心> world ", "hello world")]
    public void Clean_RemovesMarkupAndNormalizesWhitespace(string script, string clean) =>
        Assert.Equal(clean, CorticoScript.Clean(script));
}
```

- [ ] **Step 2: 运行确认编译失败**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~Pacing"`
Expected: 编译失败（类型不存在）

- [ ] **Step 3: 实现 `CorticoScript.cs`**

```csharp
using System.Text.RegularExpressions;

namespace AIVTuber.Core.Cortico;

/// <summary>Local mirror of the upstream parser's text rules: 【…】 and &lt;…&gt; are markup, not speech.
/// Square voice tags never reach here (<see cref="CorticoReplyAdapter.Sanitize"/> strips them).</summary>
internal static class CorticoScript
{
    private static readonly Regex Markup = new(@"【[^】]*】|<[^>\r\n]{0,32}>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string script) => Normalize(Markup.Replace(script, " "));

    public static string Normalize(string text) => Spaces.Replace(text, " ").Trim();
}
```

- [ ] **Step 4: 实现 `SpeechPacing.cs`**

```csharp
namespace AIVTuber.Core.Bot.Pacing;

/// <summary>One approved segment. Immediate: clean speech text. Cortico: sanitized script.
/// <paramref name="Audio"/> is audio already being synthesized for exactly this text.</summary>
internal readonly record struct SpeechItem(string Text, string? Emotion = null, PieceAudio? Audio = null);

/// <summary>What a pacer needs from the turn.</summary>
/// <param name="Synthesize">TTS for one text (voice and emotion already resolved by the caller).</param>
/// <param name="Play">The app player; the callback fires when the first PCM reaches the device.</param>
/// <param name="CanSpeak">The last gate before a piece becomes public (people resumed, stop, sign-out).</param>
/// <param name="Commit">Captions and history for a piece handed to the player.</param>
/// <param name="OnFirstPcm">First audible audio of the turn (staged avatar motion).</param>
/// <param name="Warn">A user-visible, already-localized warning for this turn.</param>
internal sealed record SpeechTurnPorts(
    Func<string, string?, CancellationToken, IAsyncEnumerable<byte[]>> Synthesize,
    Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> Play,
    Func<bool> CanSpeak,
    Action<string> Commit,
    Action OnFirstPcm,
    Action<string> Warn,
    int SampleRate);

/// <summary>Decides when approved segments become audible. One instance per reply.</summary>
internal interface ISpeechPacer : IAsyncDisposable
{
    Task SubmitAsync(SpeechItem item, CancellationToken ct);
    /// <summary>No more segments; returns when everything submitted has played or the turn stopped.</summary>
    Task CompleteAsync(CancellationToken ct);
}
```

- [ ] **Step 5: 实现 `PieceAudio.cs`**

```csharp
using System.Threading.Channels;

namespace AIVTuber.Core.Bot.Pacing;

/// <summary>Streaming synthesis of one piece: chunks are buffered for playback (read once) and
/// forwarded as they arrive. Playback may start before synthesis ends.</summary>
internal sealed class PieceAudio
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts;

    private PieceAudio(string text, CancellationToken turn)
    {
        Text = text;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(turn);
    }

    public string Text { get; }

    public static PieceAudio Start(string text, Func<CancellationToken, IAsyncEnumerable<byte[]>> source,
        Func<byte[], Task> forward, Action firstChunk, Func<Exception?, Task> finished, CancellationToken turn)
    {
        var audio = new PieceAudio(text, turn);
        _ = Task.Run(() => audio.PumpAsync(source, forward, firstChunk, finished));
        return audio;
    }

    private async Task PumpAsync(Func<CancellationToken, IAsyncEnumerable<byte[]>> source,
        Func<byte[], Task> forward, Action firstChunk, Func<Exception?, Task> finished)
    {
        Exception? failure = null;
        var first = true;
        try
        {
            await foreach (var chunk in source(_cts.Token).WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                if (chunk.Length == 0) continue;
                _chunks.Writer.TryWrite(chunk);
                if (first) { first = false; firstChunk(); }
                await forward(chunk).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { _chunks.Writer.TryComplete(); }
        if (_cts.IsCancellationRequested) return; // cancelled pieces report nothing
        try { await finished(failure).ConfigureAwait(false); } catch { /* the watchdog owns host loss */ }
    }

    public IAsyncEnumerable<byte[]> ReadAllAsync(CancellationToken ct) => _chunks.Reader.ReadAllAsync(ct);

    public void Cancel() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
}
```

- [ ] **Step 6: 实现 `ImmediatePacer.cs`**

```csharp
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot.Pacing;

/// <summary>
/// Plain pacing (the tested pre-Cortico behaviour): one player call for the whole reply, each
/// segment synthesized and played as soon as its audio arrives, gated and committed at its first
/// chunk. Also the voice-only fallback of <see cref="CorticoPacer"/>; <paramref name="after"/> lets
/// that fallback wait for a piece still playing (the player stops whatever plays when it starts).
/// </summary>
internal sealed class ImmediatePacer(SpeechTurnPorts ports, CancellationToken turn, Task? after = null) : ISpeechPacer
{
    private readonly Channel<SpeechItem> _items = Channel.CreateUnbounded<SpeechItem>();
    private readonly object _sync = new();
    private Task? _playback;

    public Task SubmitAsync(SpeechItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Enqueue(item);
        return Task.CompletedTask;
    }

    /// <summary>Synchronous submit, for handing over a queue from another pacer.</summary>
    public void Enqueue(SpeechItem item)
    {
        lock (_sync) _playback ??= Task.Run(RunAsync);
        if (!_items.Writer.TryWrite(item)) item.Audio?.Cancel();
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        _items.Writer.TryComplete();
        Task? playback;
        lock (_sync) playback = _playback;
        if (playback is not null) await playback.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        if (after is not null)
        {
            try { await after.ConfigureAwait(false); } catch { /* its own owner reported it */ }
        }
        await ports.Play(Chunks(turn), turn, ports.OnFirstPcm).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<byte[]> Chunks([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _items.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (!LlmClient.IsSpeakableText(item.Text)) { item.Audio?.Cancel(); continue; }
            var first = true;
            var audio = item.Audio?.ReadAllAsync(ct) ?? ports.Synthesize(item.Text, item.Emotion, ct);
            await foreach (var chunk in audio.WithCancellation(ct).ConfigureAwait(false))
            {
                if (first)
                {
                    first = false;
                    // Recheck after synthesis: people may have resumed while TTS was on the network.
                    if (!ports.CanSpeak()) { Drain(); yield break; }
                    ports.Commit(item.Text);
                }
                yield return chunk;
            }
        }
    }

    private void Drain()
    {
        _items.Writer.TryComplete();
        while (_items.Reader.TryRead(out var rest)) rest.Audio?.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        Drain();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 7: 运行测试确认通过**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~Pacing"`
Expected: 全部 PASS

- [ ] **Step 8: Commit**

```bash
git add AIVTuber.Core/Bot/Pacing AIVTuber.Core/Cortico/CorticoScript.cs AIVTuber.Tests/Pacing
git commit -m "Pacing: speech pacer contract, streaming piece audio, immediate pacer

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: CorticoPacer 正常路径（由 Cortico 决定开口）

**Files:**
- Create: `AIVTuber.Core/Bot/Pacing/CorticoPacer.cs`
- Modify: `AIVTuber.Tests/Cortico/CorticoFakes.cs`（整体替换 `FakeCortico`；`ChunkedLlm` 保留，并加上声道参数）
- Test: `AIVTuber.Tests/Pacing/CorticoPacerTests.cs`

**Interfaces:**
- Consumes：Task 3 的接口；Task 4 的 `SpeechTurnPorts`、`PieceAudio`、`ImmediatePacer`、`CorticoScript`
- Produces：`internal sealed class CorticoPacer(ICorticoPerformance cortico, SpeechTurnPorts ports, CancellationToken turn, Func<long>? clock = null, TimeSpan? watchInterval = null) : ISpeechPacer, ICorticoAudioHandler`（`watchInterval` 传 `Timeout.InfiniteTimeSpan` 时不启动看门狗，供测试直接调用 `CheckStall`），另有 `internal string? CheckStall()`（Task 6 使用）

- [ ] **Step 1: 替换 `FakeCortico`**

在 `AIVTuber.Tests/Cortico/CorticoFakes.cs` 中，把 `FakeCortico` 整体替换为下面的实现。`ChunkedLlm` 的构造函数加可选参数 `IEnumerable<string>? channels = null`，`StreamEventsAsync` 里改为 `new ReplyProtocolV2Parser(channels ?? [])`（C# 主构造函数不支持在 `params` 之后再加参数，所以改为普通构造函数 `public ChunkedLlm(string protocol, string[] chunks, IEnumerable<string>? channels = null)`，再加一个便利重载 `public ChunkedLlm(string protocol, params string[] chunks) : this(protocol, chunks, null) {}`）。

```csharp
/// <summary>
/// Emulates the host side: each fed segment becomes one piece (clean text per Cortico grammar),
/// synthesized on request, played when upstream would ask. It proves routing, pacing hand-off and
/// the fallback rules — not rendering, real timing or real audio.
/// </summary>
internal sealed class FakeCortico : ICorticoPerformance
{
    public string ScriptGrammar => "【演出台本语法】<动作>随说随做；【动作】暂停说话做动作。点头 摇头 微笑";
    public int MaxHoldMs { get; set; } = 300;
    public bool IsAlive { get; set; } = true;
    /// <summary>When set, pieces are synthesized but never asked to play.</summary>
    public bool HoldPlay;
    public readonly List<string> Feeds = [], Log = [];
    public int Begins, Interrupts;
    public Stage? Current;

    public static string Clean(string script) => CorticoScript.Clean(script);

    public Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct)
    {
        Interlocked.Increment(ref Begins);
        Current = new Stage(this, handler);
        return Task.FromResult<ICorticoStage>(Current);
    }

    public Task InterruptAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Interrupts);
        Current?.Cut();
        return Task.CompletedTask;
    }

    internal sealed class Stage(FakeCortico owner, ICorticoAudioHandler handler) : ICorticoStage
    {
        private long _next;
        private readonly Dictionary<long, (TaskCompletionSource Ready, TaskCompletionSource Done)> _pieces = [];
        private readonly List<Task> _runs = [];
        private readonly CancellationTokenSource _cut = new();
        public ICorticoAudioHandler Handler => handler;

        private void Note(string line) { lock (owner.Log) owner.Log.Add(line); }
        public void Cut() => _cut.Cancel();

        public Task FeedAsync(string script, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (owner.Feeds) owner.Feeds.Add(script);
            var text = Clean(script);
            if (text.Length == 0) return Task.CompletedTask;
            var id = Interlocked.Increment(ref _next);
            var entry = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                         new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (_pieces) _pieces[id] = entry;
            Task previous;
            lock (_runs) previous = _runs.Count > 0 ? _runs[^1] : Task.CompletedTask;
            var run = Task.Run(async () =>
            {
                if (_cut.IsCancellationRequested) return;  // a cut host requests nothing more
                handler.Synth(id, text);
                await entry.Item1.Task.WaitAsync(_cut.Token);
                await previous;                       // upstream plays pieces one after another
                // A holding host keeps the performance open until it is interrupted.
                if (owner.HoldPlay) { await Task.Delay(Timeout.Infinite, _cut.Token); return; }
                if (_cut.IsCancellationRequested) return;
                handler.Cue();
                handler.Play(id);
                await entry.Item2.Task.WaitAsync(_cut.Token);
            });
            lock (_runs) _runs.Add(run);
            return Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken ct)
        {
            Task[] runs;
            lock (_runs) runs = [.. _runs];
            await Task.WhenAll(runs).WaitAsync(ct);
            if (_cut.IsCancellationRequested) throw new InvalidOperationException("Cancelled");
        }

        private void Ready(long id) { lock (_pieces) if (_pieces.TryGetValue(id, out var p)) p.Ready.TrySetResult(); }
        private void Done(long id) { lock (_pieces) if (_pieces.TryGetValue(id, out var p)) { p.Ready.TrySetResult(); p.Done.TrySetResult(); } }

        public Task PcmAsync(long pieceId, int sampleRate, byte[] pcm) { Note($"pcm:{pieceId}"); Ready(pieceId); return Task.CompletedTask; }
        public Task SynthEndAsync(long pieceId) { Note($"synthEnd:{pieceId}"); Ready(pieceId); return Task.CompletedTask; }
        public Task SynthErrorAsync(long pieceId, string message) { Note($"synthError:{pieceId}"); Done(pieceId); return Task.CompletedTask; }
        public Task StartedAsync(long pieceId) { Note($"started:{pieceId}"); return Task.CompletedTask; }
        public Task EndedAsync(long pieceId) { Note($"ended:{pieceId}"); Done(pieceId); return Task.CompletedTask; }
        public Task StoppedAsync(long pieceId) { Note($"stopped:{pieceId}"); Done(pieceId); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _cut.Cancel(); return ValueTask.CompletedTask; }
    }
}
```

- [ ] **Step 2: 写失败测试 `AIVTuber.Tests/Pacing/CorticoPacerTests.cs`**

```csharp
using AIVTuber.Core.Bot.Pacing;
using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests.Pacing;

public sealed class CorticoPacerTests
{
    [Fact]
    public async Task EachPiece_IsSynthesizedOnce_PlayedWhenCorticoAsks_AndCommittedWithCleanText()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("<微笑>你好。"), default);
        await pacer.SubmitAsync(new SpeechItem("【点头】再见。"), default);
        await pacer.CompleteAsync(default);
        Assert.Equal(["你好。", "再见。"], kit.Synthesized);
        Assert.Equal(["你好。", "再见。"], kit.Played);
        Assert.Equal(["你好。", "再见。"], kit.Committed);
        Assert.Equal(["<微笑>你好。", "【点头】再见。"], cortico.Feeds);
        lock (cortico.Log)
        {
            Assert.Contains("started:1", cortico.Log);
            Assert.True(cortico.Log.IndexOf("started:1") < cortico.Log.IndexOf("ended:1"));
        }
    }

    [Fact]
    public async Task NothingIsCommittedOrPlayed_BeforeCorticoAsksToPlay()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        await Task.Delay(100);
        Assert.Empty(kit.Committed);
        Assert.Empty(kit.Played);
    }

    [Fact]
    public async Task Play_WhenTheTurnMayNoLongerSpeak_ReportsStopped_AndEndsTheTurn()
    {
        var kit = new PacingTestKit { Speakable = false };
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(kit.Committed);
        Assert.Empty(kit.Played);
        lock (cortico.Log) Assert.Contains("stopped:1", cortico.Log);
    }

    [Fact]
    public async Task Play_BeforeSynthesisFinishes_PlaysWhatArrivedAndStreamsTheRest()
    {
        var kit = new PacingTestKit();
        var release = new TaskCompletionSource();
        var cortico = new FakeCortico();
        var ports = kit.Ports with { Synthesize = (text, _, ct) => TwoChunks(text, release.Task, ct) };
        await using var pacer = new CorticoPacer(cortico, ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("前半后半"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("started:1"); });
        release.SetResult();
        await pacer.CompleteAsync(default);
        Assert.Equal(["前半后半"], kit.Played);
    }

    [Fact]
    public async Task ActionOnlySegment_WaitsForTheNextSpokenOne()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("【点头】"), default);
        Assert.Equal(0, cortico.Begins);
        await pacer.SubmitAsync(new SpeechItem("好的。"), default);
        await pacer.CompleteAsync(default);
        Assert.Equal(["【点头】好的。"], cortico.Feeds);
    }

    private static async IAsyncEnumerable<byte[]> TwoChunks(string text, Task release,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return System.Text.Encoding.UTF8.GetBytes(text[..2]);
        await release.WaitAsync(ct);
        yield return System.Text.Encoding.UTF8.GetBytes(text[2..]);
    }
}
```

- [ ] **Step 3: 运行确认编译失败**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~CorticoPacerTests"`
Expected: 编译失败（`CorticoPacer` 不存在）

- [ ] **Step 4: 实现 `CorticoPacer.cs`（完整文件，兜底部分一并写入，Task 6 负责测试它）**

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using AIVTuber.Core.Cortico;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot.Pacing;

/// <summary>
/// Cortico decides when each piece may start; the app synthesizes and plays it. Host requests
/// arrive through <see cref="ICorticoAudioHandler"/> on the IPC reader and never block it. When the
/// sidecar dies, its heartbeat lapses, it holds a ready piece past its longest legitimate beat plus
/// 2 s, or it aborts the reply (model switch), the rest of the reply plays voice-only through an
/// <see cref="ImmediatePacer"/> after the piece that is currently audible.
/// </summary>
internal sealed class CorticoPacer : ISpeechPacer, ICorticoAudioHandler
{
    internal const int HoldGraceMs = 2000;

    private sealed class Piece(long id, string text)
    {
        public long Id { get; } = id;
        public string Text { get; } = text;
        public PieceAudio? Audio;
        public bool Ready, Played;
        public CancellationTokenSource? PlayCts;
        public Task? Playback;
    }

    private readonly ICorticoPerformance _cortico;
    private readonly SpeechTurnPorts _ports;
    private readonly CancellationToken _turn;
    private readonly Func<long> _clock;
    private readonly TimeSpan _watchInterval;
    private readonly CancellationTokenSource _life = new();
    private readonly object _sync = new();
    private readonly SortedDictionary<long, Piece> _pieces = new();
    private readonly List<string> _segments = [];
    private int _segmentCursor, _charCursor;
    private readonly StringBuilder _pendingActions = new();
    private readonly TaskCompletionSource _fellBack = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _denied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ICorticoStage? _stage;
    private ImmediatePacer? _fallback;
    private Piece? _playing;
    private bool _fedSpeech;
    private long _lastProgress;
    private Task _watchdog = Task.CompletedTask;

    public CorticoPacer(ICorticoPerformance cortico, SpeechTurnPorts ports, CancellationToken turn,
        Func<long>? clock = null, TimeSpan? watchInterval = null)
    {
        _cortico = cortico;
        _ports = ports;
        _turn = turn;
        _clock = clock ?? (() => Environment.TickCount64);
        _watchInterval = watchInterval ?? TimeSpan.FromMilliseconds(250);
        _lastProgress = _clock();
    }

    // ── segments from the reply ────────────────────────────────────────────────────

    public async Task SubmitAsync(SpeechItem item, CancellationToken ct)
    {
        ImmediatePacer? fallback;
        lock (_sync) fallback = _fallback;
        var clean = CorticoScript.Clean(item.Text);
        if (fallback is not null) { fallback.Enqueue(new SpeechItem(clean)); return; }
        if (!LlmClient.IsSpeakableText(clean))
        {
            // Actions alone never start a turn: without a voice there is no gate. Keep them.
            _pendingActions.Append(item.Text);
            return;
        }
        if (_stage is null)
        {
            try
            {
                _stage = await _cortico.BeginAsync(this, ct).ConfigureAwait(false);
                Touch();
                if (_watchInterval != Timeout.InfiniteTimeSpan) _watchdog = Task.Run(WatchAsync);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                FallBack($"皮套未能开演（{ex.Message}）");
                lock (_sync) _fallback!.Enqueue(new SpeechItem(clean));
                return;
            }
        }
        var script = _pendingActions + item.Text;
        _pendingActions.Clear();
        lock (_sync) _segments.Add(clean);
        try
        {
            await _stage.FeedAsync(script, ct).ConfigureAwait(false);
            _fedSpeech = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            FallBack($"皮套未接收台词（{ex.Message}）");
        }
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_stage is not null && _pendingActions.Length > 0 && _fedSpeech && FallbackOrNull() is null && _ports.CanSpeak())
        {
            try { await _stage.FeedAsync(_pendingActions.ToString(), ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* trailing actions only */ }
        }
        if (_stage is not null)
        {
            var complete = _stage.CompleteAsync(ct);
            var first = await Task.WhenAny(complete, _fellBack.Task, _denied.Task).ConfigureAwait(false);
            if (first == complete)
            {
                try { await complete.ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested && !_denied.Task.IsCompleted)
                {
                    if (!_fellBack.Task.IsCompleted) FallBack($"皮套演出中断（{ex.Message}）");
                }
            }
        }
        if (_denied.Task.IsCompleted) return;
        var fallback = FallbackOrNull();
        if (fallback is not null) await fallback.CompleteAsync(ct).ConfigureAwait(false);
        Task? last;
        lock (_sync) last = _playing?.Playback;
        if (last is not null) await last.WaitAsync(ct).ConfigureAwait(false);
    }

    // ── host requests ──────────────────────────────────────────────────────────────

    void ICorticoAudioHandler.Synth(long pieceId, string text)
    {
        lock (_sync)
        {
            if (_fallback is not null || _turn.IsCancellationRequested || _pieces.ContainsKey(pieceId)) return;
            var piece = new Piece(pieceId, text);
            _pieces[pieceId] = piece;
            AdvanceCursor(text);
            var stage = _stage!;
            piece.Audio = PieceAudio.Start(text,
                ct => _ports.Synthesize(text, null, ct),
                chunk => stage.PcmAsync(pieceId, _ports.SampleRate, chunk),
                () => { lock (_sync) { piece.Ready = true; Touch(); } },
                failure => failure is null ? stage.SynthEndAsync(pieceId) : stage.SynthErrorAsync(pieceId, failure.Message),
                _turn);
        }
    }

    void ICorticoAudioHandler.CancelSynth(long pieceId)
    {
        lock (_sync)
        {
            if (_fallback is not null || !_pieces.Remove(pieceId, out var piece) || piece.Played) return;
            piece.Audio?.Cancel();
        }
    }

    void ICorticoAudioHandler.Play(long pieceId)
    {
        Piece? piece;
        lock (_sync)
        {
            if (_fallback is not null || _denied.Task.IsCompleted || !_pieces.TryGetValue(pieceId, out piece) || piece.Played) return;
            piece.Played = true;
        }
        if (!_ports.CanSpeak())
        {
            piece.Audio?.Cancel();
            lock (_sync) _pieces.Remove(pieceId);
            _ = _stage!.StoppedAsync(pieceId);
            _denied.TrySetResult();
            return;
        }
        StartPlayback(piece);
    }

    void ICorticoAudioHandler.Stop(long pieceId)
    {
        lock (_sync)
            if (_pieces.TryGetValue(pieceId, out var piece)) piece.PlayCts?.Cancel();
    }

    void ICorticoAudioHandler.Cue() { lock (_sync) Touch(); }

    void ICorticoAudioHandler.Aborted(string reason) => FallBack("皮套切换，本轮其余内容仅语音");

    // ── playback ───────────────────────────────────────────────────────────────────

    private void StartPlayback(Piece piece)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_turn);
        var stage = _stage!;
        lock (_sync)
        {
            piece.PlayCts = cts;
            _playing = piece;
            piece.Playback = Task.Run(async () =>
            {
                var completed = false;
                try
                {
                    await _ports.Play(Committed(piece, cts.Token), cts.Token, () =>
                    {
                        _ports.OnFirstPcm();
                        _ = stage.StartedAsync(piece.Id);
                    }).ConfigureAwait(false);
                    completed = !cts.IsCancellationRequested;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ports.Warn($"[Cortico] 这一句播放失败：{ex.Message}");
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_playing == piece) _playing = null;
                        _pieces.Remove(piece.Id);
                        Touch();
                    }
                    _ = completed ? stage.EndedAsync(piece.Id) : stage.StoppedAsync(piece.Id);
                    cts.Dispose();
                }
            });
        }
    }

    /// <summary>Captions and history for this piece when its first chunk is handed to the player.</summary>
    private async IAsyncEnumerable<byte[]> Committed(Piece piece, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var first = true;
        await foreach (var chunk in piece.Audio!.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (first) { first = false; _ports.Commit(CorticoScript.Normalize(piece.Text)); }
            yield return chunk;
        }
    }

    // ── fallback ───────────────────────────────────────────────────────────────────

    private ImmediatePacer? FallbackOrNull() { lock (_sync) return _fallback; }

    private void Touch() => _lastProgress = _clock();

    /// <summary>Why the reply should go voice-only now, or null. Internal for tests.</summary>
    internal string? CheckStall()
    {
        if (!_cortico.IsAlive) return "皮套进程无响应";
        lock (_sync)
        {
            if (_fallback is not null || _playing is not null) return null;
            if (!_pieces.Values.Any(p => p.Ready && !p.Played)) return null;
            return _clock() - _lastProgress > _cortico.MaxHoldMs + HoldGraceMs ? "皮套超时未开口" : null;
        }
    }

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(_watchInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false))
                if (CheckStall() is { } reason) { FallBack(reason); return; }
        }
        catch (OperationCanceledException) { }
    }

    private void FallBack(string reason)
    {
        ImmediatePacer fallback;
        List<Piece> replay;
        string remaining;
        lock (_sync)
        {
            if (_fallback is not null) return;
            replay = _pieces.Values.Where(p => !p.Played).OrderBy(p => p.Id).ToList();
            foreach (var p in replay) _pieces.Remove(p.Id);
            remaining = RemainingText();
            // The piece that is audible now finishes first: the player stops whatever plays when it starts.
            fallback = _fallback = new ImmediatePacer(_ports, _turn, after: _playing?.Playback);
        }
        _ports.Warn($"[Cortico] 皮套异常，本轮仅语音：{reason}");
        foreach (var p in replay) fallback.Enqueue(new SpeechItem(CorticoScript.Normalize(p.Text), Audio: p.Audio));
        if (LlmClient.IsSpeakableText(remaining)) fallback.Enqueue(new SpeechItem(remaining));
        _fellBack.TrySetResult();
        _ = Task.Run(async () =>
        {
            try { await _cortico.InterruptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* the sidecar may be gone */ }
        });
    }

    /// <summary>Moves past the text of a requested piece within the fed segments.</summary>
    private void AdvanceCursor(string pieceText)
    {
        var key = CorticoScript.Normalize(pieceText);
        if (key.Length == 0) return;
        for (var i = _segmentCursor; i < _segments.Count; i++)
        {
            var from = i == _segmentCursor ? _charCursor : 0;
            var at = _segments[i].IndexOf(key, from, StringComparison.Ordinal);
            if (at < 0) continue;
            _segmentCursor = i;
            _charCursor = at + key.Length;
            return;
        }
    }

    /// <summary>Fed text that no piece was requested for yet.</summary>
    private string RemainingText()
    {
        var sb = new StringBuilder();
        for (var i = _segmentCursor; i < _segments.Count; i++)
        {
            var text = _segments[i];
            sb.Append(i == _segmentCursor ? text[Math.Min(_charCursor, text.Length)..] : text).Append(' ');
        }
        return CorticoScript.Normalize(sb.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        try { await _watchdog.ConfigureAwait(false); } catch { }
        lock (_sync)
            foreach (var piece in _pieces.Values)
            {
                if (!piece.Played) piece.Audio?.Cancel();
                piece.PlayCts?.Cancel();
            }
        if (_stage is not null) await _stage.DisposeAsync().ConfigureAwait(false);
        var fallback = FallbackOrNull();
        if (fallback is not null) await fallback.DisposeAsync().ConfigureAwait(false);
        _life.Dispose();
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~CorticoPacerTests|FullyQualifiedName~ImmediatePacerTests"`
Expected: 全部 PASS

- [ ] **Step 6: Commit**

```bash
git add AIVTuber.Core/Bot/Pacing/CorticoPacer.cs AIVTuber.Tests/Pacing AIVTuber.Tests/Cortico/CorticoFakes.cs
git commit -m "Pacing: Cortico decides when to speak, the app plays; commits only what reaches the player

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: CorticoPacer 的兜底、中止与看门狗

**Files:**
- Test: `AIVTuber.Tests/Pacing/CorticoPacerFallbackTests.cs`
- Modify（仅当测试暴露问题时）：`AIVTuber.Core/Bot/Pacing/CorticoPacer.cs`

**Interfaces:**
- Consumes：Task 5 的 `CorticoPacer.CheckStall()`、构造参数 `clock`，以及 `FakeCortico.Current.Handler`

- [ ] **Step 1: 写测试**

```csharp
using AIVTuber.Core.Bot.Pacing;
using AIVTuber.Core.Cortico;
using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests.Pacing;

public sealed class CorticoPacerFallbackTests
{
    [Fact]
    public async Task HeartbeatLost_TheRestPlaysVoiceOnly_InOrder_WithAWarning()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("第一句。"), default);
        await pacer.SubmitAsync(new SpeechItem("第二句。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:2"); });
        cortico.IsAlive = false;
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["第一句。第二句。"], kit.Played);
        Assert.Equal(["第一句。", "第二句。"], kit.Committed);
        Assert.Equal(2, kit.Synthesized.Count);           // already synthesized audio is reused
        Assert.Contains(kit.Warnings, w => w.Contains("皮套异常"));
        Assert.True(cortico.Interrupts >= 1);
    }

    [Fact]
    public async Task HoldBeyondBudget_IsAStall_ButCueResetsTheTimer()
    {
        var kit = new PacingTestKit();
        long now = 0;
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 1000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None, clock: () => now,
            watchInterval: Timeout.InfiniteTimeSpan);
        await pacer.SubmitAsync(new SpeechItem("等一下。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        now += 2500;
        Assert.Null(pacer.CheckStall());                  // 1000 + 2000 not exceeded
        ((ICorticoAudioHandler)pacer).Cue();              // a legitimate beat fired
        now += 2900;
        Assert.Null(pacer.CheckStall());
        now += 200;
        Assert.Equal("皮套超时未开口", pacer.CheckStall());
    }

    [Fact]
    public async Task Aborted_WhilePlaying_FinishesTheCurrentPieceThenTheRestVoiceOnly()
    {
        var kit = new PacingTestKit { HoldPlayback = new TaskCompletionSource() };
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("正在说的这句。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("started:1"); });
        await pacer.SubmitAsync(new SpeechItem("后面这句。"), default);
        ((ICorticoAudioHandler)pacer).Aborted("model-changed");
        await Task.Delay(100);
        Assert.Equal(["正在说的这句。"], kit.Committed);   // nothing new starts while the current piece plays
        var hold = kit.HoldPlayback!; kit.HoldPlayback = null; hold.SetResult();
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["正在说的这句。", "后面这句。"], kit.Played);
    }

    [Fact]
    public async Task PlayAfterFallback_IsIgnored_NoTextIsSpokenTwice()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("只说一次。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        ((ICorticoAudioHandler)pacer).Aborted("model-changed");
        ((ICorticoAudioHandler)pacer).Play(1);
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["只说一次。"], kit.Played);
        Assert.Equal(["只说一次。"], kit.Committed);
    }

    [Fact]
    public async Task SidecarDeadBeforeTheFirstSegment_TheWholeReplyIsVoiceOnly()
    {
        var kit = new PacingTestKit();
        var cortico = new ThrowingCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("<微笑>你好。"), default);
        await pacer.SubmitAsync(new SpeechItem("再见。"), default);
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["你好。再见。"], kit.Played);
    }

    [Fact]
    public async Task SegmentsNotYetRequested_AreSpokenAfterTheRequestedOnes()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("甲。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        // The host received the second segment but has not asked to synthesize it yet.
        cortico.Current!.Cut();
        await pacer.SubmitAsync(new SpeechItem("乙。"), default);
        ((ICorticoAudioHandler)pacer).Aborted("model-changed");
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["甲。乙。"], kit.Played);
    }

    private sealed class ThrowingCortico : ICorticoPerformance
    {
        public string ScriptGrammar => "";
        public int MaxHoldMs => 1000;
        public bool IsAlive => false;
        public Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct) =>
            throw new IOException("Cortico sidecar exited");
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
```

说明：`SegmentsNotYetRequested...` 里，`Cut()` 之后假 host 新开的运行在第一行就退出，所以不会为"乙。"发 `synth`。这样兜底时"乙。"只存在于剩余文本里，正好检验 `RemainingText`。

- [ ] **Step 2: 运行测试**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~CorticoPacerFallbackTests"`
Expected: 全部 PASS。某个用例失败时，先用 superpowers:systematic-debugging 找到根因，再修改 `CorticoPacer.cs`，不要改测试的期望。

- [ ] **Step 3: Commit**

```bash
git add AIVTuber.Tests/Pacing AIVTuber.Core/Bot/Pacing
git commit -m "Pacing: voice-only fallback on sidecar loss, stall or model switch; current piece finishes first

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: 编排器合并为唯一 v2 管线

**Files:**
- Modify: `AIVTuber.Core/Bot/BotOrchestrator.cs`
- Modify: `AIVTuber.Core/Cortico/CorticoReplyAdapter.cs`
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs`（第 1196、1315 行删除 `requireStructuredReply: true`；删除第 1007–1025 行中 `OnUserTranscript`、`OnUserEmotionDetected`、`OnLoopbackTranscript` 的订阅；对应的 `BotRuntime` 事件如果已没有其它触发点，也一并删除，并用 `grep -rn` 确认 App 里没有订阅者）
- Test: `AIVTuber.Tests/CorticoOrchestratorTests.cs`（重写）、`BotOrchestratorReplyTests.cs`、`BotOrchestratorGenerationTests.cs`、`BotOrchestratorWakeGateTests.cs`、`BotOrchestratorLifecycleTests.cs`、`ContinuousReplyPipelineTests.cs`、`InterruptLocalStopTests.cs`、`ReplyProtocolV2Tests.cs`、`VisionObservationTests.cs`、`Auth/RuntimeCloudGateTests.cs`、`RealtimeTts/MiniMaxBidiTtsClientTests.cs`

**Interfaces:**
- Consumes：Task 4–6 的节拍器
- Produces：
  - `BotOrchestrator` 内部构造函数最后几个参数变为 `(…, VtsClient? vts, VtsConfig? vtsConfig, Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> play, Action stopPlayback, Func<string, CancellationToken, Task>? triggerHotkeyAsync, IReadOnlyDictionary<string,string>? ttsEmotionMap = null)`
  - `ProcessTextAsync(string text, List<Message> history, string? wakeProbe = null, bool bypassWake = false, Func<bool>? canCommit = null)`
  - `ConfigureContinuousControl(IAvatarMotionSink motion, Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task>? play = null)`
  - LLM 必须实现 `IReplyProtocolStream`，否则在构造时抛出 `InvalidOperationException("LLM 客户端必须支持回复协议 v2")`

- [ ] **Step 1: 删除 BotOrchestrator 中的 legacy 和无用成员**

删除以下成员及其所有引用：

- 字段：`_avatarPlans`、`_avatarPlanHandler`、`_playWithStart`、`_playChunksAsync`、`_sentenceReadyHandler`、`_emotionDetectedHandler`、`_actionDetectedHandler`、`_poseDetectedHandler`、`_currentEmotion`、`_deferLlmEvents`、`_deferredEmotions`、`_deferredActions`、`_deferredPoses`、`_eventContext`
- 方法：`CurrentEventContext`、`ApplyStagedControls`、`FlushDeferredControls`、`ClearDeferredControls`、`ProcessSpeechAsync`、`ProcessLoopbackSpeechAsync`、`ProcessSpeechStreamingAsync`、`ProcessLoopbackSpeechStreamingAsync`、`AnnotateWithUserEmotion`、`RunStreamingPipelineAsync`、`RunStreamingPipelineV2Async`、`RunCorticoAsync`、`PeekPendingEmotion`、`CommitClassifiedReply`（Step 3 会给出新的替代）
- 事件：`OnSentenceReady` 保留（它是字幕出口，由 `PublishSpokenSentence` 触发）；删除 `OnUserTranscript`、`OnUserEmotionDetected`、`OnLoopbackTranscript`
- 类型：`ReplySegmentState`、`ReplySegment`；`ReplyTurnV2` 按 Step 3 替换为 `ReplyTurn`
- 构造函数里对 `_llm.On*` 事件的订阅；`Dispose` 里对应的退订
- `BeginInterrupt` 里的 `_currentEmotion = null; _deferLlmEvents = false; ClearDeferredControls();`

新增字段 `private Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> _play;`。构造函数：公开构造函数传 `player.PlayChunksAsync`（它有 `(chunks, ct, Action? firstPcmRead)` 重载，方法组可以直接转换）；内部构造函数用 `play` 参数赋值。另外加：

```csharp
        if (llm is not IReplyProtocolStream) throw new InvalidOperationException("LLM 客户端必须支持回复协议 v2");
```

`ConfigureContinuousControl` 改为：

```csharp
    public void ConfigureContinuousControl(IAvatarMotionSink motion,
        Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task>? play = null)
    {
        _motion = motion;
        if (play is not null) _play = play;
    }
```

- [ ] **Step 2: 用新的 `ProcessTextAsync` 替换旧实现**

```csharp
    /// <summary>Process text directly (a talk turn, danmaku, a dual-silence turn).</summary>
    /// <param name="wakeProbe">Text used for wake matching; defaults to <paramref name="text"/>.</param>
    /// <param name="bypassWake">When true, the model decides PASS / thought / speak.</param>
    public Task ProcessTextAsync(string text, List<Message> history, string? wakeProbe = null,
        bool bypassWake = false, Func<bool>? canCommit = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        return _coordinator.EnqueueAsync(InputSource.Danmaku, async (envelope, ct) =>
        {
            var pipelineStarted = false;
            try
            {
                if (!bypassWake && !AllowSpeak(wakeProbe ?? text)) return;
                pipelineStarted = true;
                await RunReplyAsync(history, text, envelope, ct, canCommit).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportCurrentError(envelope.Generation, $"[Pipeline] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!pipelineStarted && IsCurrent(envelope, ct, allowCancellation: true))
                    OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
            }
        });
    }
```

- [ ] **Step 3: 加入回合状态 `ReplyTurn` 和唯一管线 `RunReplyAsync`**

```csharp
    /// <summary>Per-reply state. Every request owns its own instance.</summary>
    private sealed class ReplyTurn
    {
        public ReplyDecisionMode Decision;
        public string Thought = "";
        public string? ProtocolError;
        public bool Interrupted;
        public bool PlaybackStarted;
        public bool SpokeAny;
        public AvatarIntent? StagedMotion;
        public bool MotionFlushed;
        private readonly List<string> _emotions = [];
        private readonly List<string> _controls = [];
        private int _applied;

        public void NoteEmotion(string emotion) { lock (_emotions) _emotions.Add(emotion); }
        public void NoteControl(string tag) { lock (_controls) _controls.Add(tag); }
        public string? LatestEmotion() { lock (_emotions) return _emotions.LastOrDefault(); }
        public IReadOnlyList<string> DrainUnappliedEmotions()
        {
            lock (_emotions) { var copy = _emotions.Skip(_applied).ToArray(); _applied = _emotions.Count; return copy; }
        }
        public IReadOnlyList<string> DrainControls()
        {
            lock (_controls) { var copy = _controls.ToArray(); _controls.Clear(); return copy; }
        }
    }

    /// <summary>
    /// The one reply pipeline (protocol v2). Approved segments go to a pacer: Cortico decides when
    /// each piece starts and the app plays it, or plain pacing plays as soon as audio arrives.
    /// Segments are released while the model is still writing; a segment is committed (captions,
    /// history) only when its audio is handed to the player, so an interrupted reply records only
    /// what was actually heard. PASS and thoughts are never spoken.
    /// </summary>
    private async Task RunReplyAsync(List<Message> history, string userInput, InputEnvelope envelope,
        CancellationToken ct, Func<bool>? canCommit)
    {
        var protocol = (IReplyProtocolStream)_llm;
        AIVTuber.Core.Diagnostics.DebugLog.Write($"[LLM输入] {userInput}");
        _coordinator.SetHold(true);
        var context = new RequestContext(envelope.Generation, ct);
        var avatarGeneration = Interlocked.Increment(ref _avatarSequence);
        var previousAvatar = Interlocked.Exchange(ref _activeAvatarGeneration, avatarGeneration);
        _motion?.Cancel(previousAvatar);
        _motion?.BeginTurn(avatarGeneration);
        using var cancelAvatar = ct.Register(() => _motion?.Cancel(avatarGeneration));
        var turn = new ReplyTurn();
        var cortico = Cortico is { IsAlive: true } live ? live : null;
        if (Cortico is not null && cortico is null)
            ReportCurrentError(envelope.Generation, "[Cortico] 皮套异常，本轮仅语音：皮套进程无响应");

        var ports = new Pacing.SpeechTurnPorts(
            Synthesize: (text, emotion, token) => _tts.StreamAsync(text, _ttsConfig.VoiceId, ResolveTtsEmotion(emotion), token),
            Play: _play,
            CanSpeak: () => IsCurrent(envelope, ct) && canCommit?.Invoke() != false,
            Commit: text => CommitSpokenSegment(context, turn, text),
            OnFirstPcm: () => FlushStagedMotion(envelope, ct, turn, avatarGeneration),
            Warn: message => ReportCurrentError(envelope.Generation, message),
            SampleRate: _player.SampleRate);

        var bidiTts = _tts as AIVTuber.Core.RealtimeTts.IBidiTtsController;
        if (bidiTts is not null && IsCurrent(envelope, ct))
            await bidiTts.BeginTurnAsync(ct).ConfigureAwait(false);
        Exception? pipelineEx = null;
        Pacing.ISpeechPacer pacer = cortico is not null
            ? new Pacing.CorticoPacer(cortico, ports, ct)
            : new Pacing.ImmediatePacer(ports, ct);
        try
        {
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmRequest);
            await foreach (var ev in protocol.StreamEventsAsync(history, userInput, ct).ConfigureAwait(false))
            {
                if (!IsCurrent(envelope, ct)) { turn.Interrupted = true; break; }
                if (!await HandleReplyEventAsync(ev, turn, pacer, cortico is not null, userInput, avatarGeneration, ct).ConfigureAwait(false))
                    break;
            }
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmDone);
            // Fail closed on protocol errors: segments already released still finish, nothing more starts.
            await pacer.CompleteAsync(ct).ConfigureAwait(false);
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.PlaybackEnd);
            await AwaitCommandsAsync(envelope.Generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            pipelineEx = ex;
            ReportCurrentError(envelope.Generation, $"[LLM/TTS] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await pacer.DisposeAsync().ConfigureAwait(false);
            bidiTts?.EndTurn();
            _coordinator.SetHold(false);
            if (turn.PlaybackStarted || ct.IsCancellationRequested || pipelineEx is not null)
            {
                _motion?.Cancel(avatarGeneration);
                _motion?.OnRms(0);
            }
            if (turn.PlaybackStarted && IsCurrent(envelope, ct, allowCancellation: true))
                OnAiStopSpeaking?.Invoke(this, EventArgs.Empty);
        }

        if (turn.ProtocolError is { } violation)
        {
            if (IsCurrent(envelope, ct, allowCancellation: true))
                ReportCurrentError(envelope.Generation, turn.SpokeAny
                    ? $"[LLM] 回复后半段不符合协议，已停止后续内容（已开始的部分照常说完）：{violation}"
                    : $"[LLM] 回复不符合协议，已丢弃：{violation}");
            return;
        }
        if (turn.Interrupted || pipelineEx is not null || !IsCurrent(envelope, ct) || canCommit?.Invoke() == false) return;
        if (turn.Decision == ReplyDecisionMode.Pass)
        {
            OnReplyCommitted?.Invoke(this, new ClassifiedReply(ReplyKind.Pass, "", "", []));
            AIVTuber.Core.Diagnostics.DebugLog.Write("[PASS] 本轮不接话");
        }
        else if (turn.Decision == ReplyDecisionMode.Thought)
        {
            OnReplyCommitted?.Invoke(this, new ClassifiedReply(ReplyKind.InnerThought, "", turn.Thought, []));
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[心里话] （{turn.Thought}）");
        }
    }

    /// <summary>Returns false when the reply must stop releasing segments.</summary>
    private async Task<bool> HandleReplyEventAsync(ReplyStreamEvent ev, ReplyTurn turn, Pacing.ISpeechPacer pacer,
        bool cortico, string userInput, long avatarGeneration, CancellationToken ct)
    {
        switch (ev.Kind)
        {
            case ReplyStreamEventKind.ProtocolError:
                turn.ProtocolError = ev.Error ?? "未知协议错误";
                return false;
            case ReplyStreamEventKind.Decision:
                turn.Decision = ev.Decision;
                turn.Thought = ev.Text;
                return true;
            case ReplyStreamEventKind.Control:
                if (cortico)
                {
                    // Cortico is the only rig writer; actions arrive as script markup instead.
                    AIVTuber.Core.Diagnostics.DebugLog.Write($"[Cortico] 忽略 v2 control 行（{ev.ControlKind}）：动作应写在台本标记里");
                    return true;
                }
                if (ev.ControlKind == "emotion") turn.NoteEmotion(ev.Text);
                else if (ev.Motion is { } intent)
                {
                    bool started;
                    lock (turn) started = turn.PlaybackStarted;
                    if (started) _motion?.Submit(avatarGeneration, intent);
                    else turn.StagedMotion ??= intent;
                }
                return true;
            case ReplyStreamEventKind.Speech:
                string text;
                if (cortico)
                {
                    var segment = CorticoReplyAdapter.Sanitize(ev.Text);
                    if (segment is null or { Kind: CorticoReplyKind.Pass or CorticoReplyKind.Thought }) return true;
                    if (segment.Value.Kind == CorticoReplyKind.Invalid) { turn.ProtocolError = segment.Value.Text; return false; }
                    text = segment.Value.Text;
                }
                else
                {
                    var classified = ReplyClassifier.Classify(ev.Text);
                    if (classified.Kind == ReplyKind.Invalid)
                    {
                        turn.ProtocolError = "speech 段未通过内容隔离校验（括号/标记不完整）";
                        return false;
                    }
                    if (classified.Kind != ReplyKind.Speak) return true;
                    foreach (var tag in classified.StagedControls)
                    {
                        if (tag.StartsWith("[emotion:", StringComparison.OrdinalIgnoreCase)) turn.NoteEmotion(tag[9..^1].Trim());
                        else turn.NoteControl(tag);
                    }
                    text = classified.Spoken;
                }
                turn.SpokeAny = true;
                await pacer.SubmitAsync(new Pacing.SpeechItem(text, turn.LatestEmotion()), ct).ConfigureAwait(false);
                return true;
            case ReplyStreamEventKind.End:
                if (turn.Decision == ReplyDecisionMode.Speak && turn.StagedMotion is null &&
                    AvatarReplyProtocol.InferRequestedMotion(userInput) is { } inferred)
                    turn.StagedMotion = inferred;
                return true;
            default:
                return true;
        }
    }

    private void CommitSpokenSegment(RequestContext context, ReplyTurn turn, string text)
    {
        foreach (var emotion in turn.DrainUnappliedEmotions()) ApplyEmotion(context, emotion);
        bool first;
        lock (turn) { first = !turn.PlaybackStarted; turn.PlaybackStarted = true; }
        if (first)
        {
            FlushTurnControls(context, turn);
            Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.LlmFirstSpeechSegment);
            OnFirstSentenceToTts?.Invoke(this, EventArgs.Empty);
            OnAiStartSpeaking?.Invoke(this, EventArgs.Empty);
        }
        OnReplyCommitted?.Invoke(this, new ClassifiedReply(ReplyKind.Speak, text, "", []));
        PublishSpokenSentence(context, text);
    }

    private void FlushStagedMotion(InputEnvelope envelope, CancellationToken ct, ReplyTurn turn, long avatarGeneration)
    {
        Trace?.Mark(AIVTuber.Core.Diagnostics.RealtimeTrace.Events.PlaybackFirst);
        if (!IsCurrent(envelope, ct)) return;
        lock (turn)
        {
            if (turn.StagedMotion is not { } intent || turn.MotionFlushed) return;
            turn.MotionFlushed = true;
            _motion?.Submit(avatarGeneration, intent);
        }
    }
```

同时把原 `FlushTurnControls(RequestContext, ReplyTurnV2)` 的参数类型改为 `ReplyTurn`，函数体不变。`ClassifiedReply`、`ReplyKind`、`ReplyClassifier` 都保持原样。

- [ ] **Step 4: 精简 `CorticoReplyAdapter.cs`**

删除 `FromV2`、`FromLegacy` 两个方法和整个 `LegacyScriptReader` 类；保留 `CorticoReplyKind`、`CorticoReplyEvent` 和 `Sanitize`；删除不再需要的 `using System.Runtime.CompilerServices;` 和 `using System.Text;`。

- [ ] **Step 5: 编译，按编译错误修正调用点**

Run: `dotnet build AIVTuber.slnx -c Release -p:PlatformTarget=AnyCPU`

要修的调用点包括：`BotRuntime.cs` 两处 `requireStructuredReply`、第 1007–1025 行的事件订阅、`ConfigureContinuousControl` 的调用。Core 和 App 都编译通过后，测试项目的错误留到 Step 6 处理。

- [ ] **Step 6: 把编排器测试迁移到 v2**

先在 `AIVTuber.Tests/Cortico/CorticoFakes.cs` 里加共用的 v2 片段构造工具和播放替身：

```csharp
internal static class V2
{
    public static string Speak => Line(new { v = 2, type = "decision", mode = "speak" });
    public static string Pass => Line(new { v = 2, type = "decision", mode = "pass" });
    public static string Thought(string text) => Line(new { v = 2, type = "decision", mode = "thought", thought = text });
    public static string End => Line(new { v = 2, type = "end" });
    public static string Seg(int seq, string text) => Line(new { v = 2, type = "speech", seq, text });
    public static string Emotion(string value) => Line(new { v = 2, type = "control", kind = "emotion", value });
    public static string Avatar(string channel, float value) =>
        Line(new { v = 2, type = "control", kind = "avatar", targets = new Dictionary<string, float> { [channel] = value } });
    public static ChunkedLlm Say(params string[] segments) =>
        new("v2", [Speak, .. segments.Select((s, i) => Seg(i, s)), End]);
    private static string Line(object o) => System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}

internal static class TestPlay
{
    /// <summary>Drains the audio, firing the first-PCM callback like the real player.</summary>
    public static Func<IAsyncEnumerable<byte[]>, CancellationToken, Action?, Task> Into(List<byte>? sink = null, Action<byte[]>? each = null) =>
        async (chunks, ct, first) =>
        {
            var started = false;
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (!started) { started = true; first?.Invoke(); }
                each?.Invoke(chunk);
                if (sink is not null) lock (sink) sink.AddRange(chunk);
            }
        };
}
```

`thought` 字段名以 `ReplyProtocolV2Parser` 读取 decision 的字段为准。先看 `AIVTuber.Core/Pipeline/ReplyProtocolV2.cs` 中 `case "decision"` 的实现，与之保持一致。

迁移规则（对每个测试文件逐条执行）：

1. `new BotOrchestrator(..., async (chunks, ct) => {...}, () => { }, ...)` 改为 `new BotOrchestrator(..., TestPlay.Into(...), () => { }, ...)`。原 lambda 里如果有计数（例如 `played++`），改用 `TestPlay.Into(each: _ => played++)`。
2. 只实现 `ILlmClient` 的假 LLM（`FixedLlm`、`PlannedLlm`、`ScriptedLlm` 等）改为 `V2.Say(...)` 或 `new ChunkedLlm("v2", [...])`，删除原来的假类。
3. 删除调用处的 `requireStructuredReply: true`。
4. `ConfigureContinuousControl(motion, PlayAndStart)` 中的 `PlayAndStart` 改为三参数签名 `(chunks, ct, first)`。

逐个测试的处理：

| 文件 · 测试 | 处理 |
|---|---|
| `BotOrchestratorReplyTests.StructuredDecision_ControlsAllPublicEffects` | 改为 Theory，三组输入：`V2.Say("我觉得可以。")` → 说；`new ChunkedLlm("v2", V2.Pass, V2.End)` → 不说；`new ChunkedLlm("v2", V2.Speak, "{\"v\":2,\"type\":\"speech\"")`（截断）→ 不说。断言保持原样 |
| `…StructuredDecision_KeepsSpeakableProse` | 删除（legacy 纯文本兼容） |
| `…NewTranscriptDuringSynthesis_DoesNotCancelInvitedReply` | LLM 改为 `V2.Say("我觉得可以。")` |
| `…InvitedAvatarPlan_SpeaksAndSubmitsHeadYaw` | LLM 改为 `new ChunkedLlm("v2", [V2.Speak, V2.Avatar("headYaw", .5f), V2.Seg(0, "好呀。"), V2.End], channels: ["headYaw"])` |
| `…HeadShakeAsk_InfersMotionWhenModelOmitsAvatar` | LLM 改为 `V2.Say("好呀。")`，输入仍是 "大肥鱼，摇摇头呗" |
| `…Pass_DoesNotCallTtsOrStartSpeaking` | LLM 改为 `new ChunkedLlm("v2", V2.Pass, V2.End)` |
| `…InnerThought_DoesNotCallTts` | LLM 改为 `new ChunkedLlm("v2", V2.Thought("先听着"), V2.End)` |
| `…MixedThoughtAndSpeak_CallsTtsWithSpokenOnly` | LLM 改为 `V2.Say("（又叫我）谁叫我？我在听。")`，断言不变；如果 `ReplyClassifier.Classify` 把这类混合段判为 `Invalid`，改为断言该段被拒绝（`tts.CallCount == 0`），并在测试名中注明 |
| `…NewSpeechBeforePlayback_*`、`…NewSpeechAfterPlaybackStarts_*` | LLM 改为 `V2.Say("你好")` |
| `BotOrchestratorGenerationTests`、`BotOrchestratorWakeGateTests`、`ContinuousReplyPipelineTests`、`InterruptLocalStopTests`、`VisionObservationTests`、`Auth/RuntimeCloudGateTests`、`RealtimeTts/MiniMaxBidiTtsClientTests` | 按规则 1–4 迁移；`ContinuousReplyPipelineTests.ReplyKindControlsSpeechAndAvatarIndependently` 的 InlineData 用上表的 v2 写法重写（说话 / pass / thought） |
| `ContinuousReplyPipelineTests.CancelledLateLlmEventCannotMove` | 删除（它测的是 legacy LLM 事件，这条路径已经不存在） |
| `BotOrchestratorLifecycleTests` 中 `Dispose_detaches_all_llm_handlers…`、`Disposed_orchestrator_no_longer_forwards_publisher_events`、`Rewire_twenty_times…`（LLM 部分） | 删除（编排器不再订阅 LLM 事件）；与播放器事件相关的用例保留，并按规则 1–4 迁移 |
| `ReplyProtocolV2Tests` | 只迁移构造函数（规则 1）；v2 行为断言不变 |
| `CorticoOrchestratorTests` | 删除全部 `Legacy_*` 用例；v2 用例改用 Task 5 的 `FakeCortico`，构造时 `play: TestPlay.Into(...)`、`tts: ToneTts`（返回文本的 UTF-8），断言：Captions 与 Committed 只包含开播片段的干净文本；PASS 和心里话不调用 `BeginAsync`；`Stop_ReachesThePerformanceLayer…` 断言 `Interrupts >= 1`，而且 `Interrupt()` 返回之前 `stopPlayback` 已被调用 |

- [ ] **Step 7: 跑全部测试**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU`
Expected: 除 `DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`（macOS 平台差异）和被标为 `Skip = "rewired in Task 8"` 的用例外，全部 PASS。

- [ ] **Step 8: Commit**

```bash
git add -A AIVTuber.Core AIVTuber.Tests
git commit -m "One reply pipeline: v2 only, paced by Cortico or immediately; drop the legacy path and unused entry points

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: 配置、提示词、运行时接线与集成验收

**Files:**
- Modify: `AIVTuber.Core/Config/AppConfig.cs:278`、`config.json.template:62`
- Modify: `AIVTuber.Core/Cortico/CorticoPrompt.cs`、`AIVTuber.Core/Bot/IdentityPrompt.cs:64-80`
- Modify: `AIVTuber.Core/Runtime/BotRuntime.cs`（第 924、1096、1266 行附近）
- Test: `AIVTuber.Tests/RealtimeConfigTests.cs`、`AIVTuber.Tests/IdentityPromptTests.cs`、`AIVTuber.Tests/Distribution/StreamerConfigTests.cs`、`AIVTuber.Tests/ListeningContextTests.cs`、`AIVTuber.Tests/Cortico/RuntimeCorticoAcceptanceTests.cs`

**Interfaces:**
- Produces：`LlmConfig.ReplyProtocol` 永远是 `"v2"`；`LlmConfig.RequestedReplyProtocol`（`[JsonIgnore]`，读到非 v2 值时记录原值）；`CorticoPrompt.For(string grammar)`；`IdentityPrompt.InvitationPolicyFor(bool cortico)`

- [ ] **Step 1: 写失败测试**

在 `RealtimeConfigTests.cs` 追加：

```csharp
    [Theory]
    [InlineData("legacy")]
    [InlineData("")]
    [InlineData("V2")]
    public void ReplyProtocol_IsAlwaysV2_AndRemembersALegacyRequest(string configured)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
            $"{{\"llm\":{{\"reply_protocol\":\"{configured}\"}}}}", ConfigManager.JsonOptions)!;
        Assert.Equal("v2", config.Llm.ReplyProtocol);
        Assert.Equal(configured.Equals("v2", StringComparison.OrdinalIgnoreCase) ? null : configured,
            config.Llm.RequestedReplyProtocol);
    }
```

（如果 `ConfigManager` 的序列化选项不叫 `JsonOptions`，就用该文件里实际的公开或 internal 名称；用 `grep -n "JsonSerializerOptions" AIVTuber.Core/Config/ConfigManager.cs` 查找。）

在 `RuntimeCorticoAcceptanceTests.cs` 中去掉 Task 3 加的 `Skip`，并把 Harness 改为新接线：

- `h.Orchestrator = new BotOrchestrator(new RuntimeCloudGateTests.CountingAsr(), h.Llm, h.Tts, h._player, new TtsConfig { SampleRate = 16000 }, null, null, h.Play, () => { }, triggerHotkeyAsync: null) { Cortico = h.Cortico };`，其中 `h.Play = TestPlay.Into(each: c => { lock (h.Audio) h.Audio.Add(c.Length); })`，`h.Tts` 就是现有的 `ToneTts`（不再是 `ThrowingTts`；删除 `ThrowingTts` 类）。
- 三个用例的断言调整：
  1. `V2Reply_FromTheRuntimeEntry…`：`h.Tts.Texts` 每片只出现一次；`h.Audio.Count > 0`（声音由 App 播放器输出）；`MouthOpen` 峰值 > 0；请求体里没有 control 行，也没有 JSON 回复规则（原有断言保留）。
  2. `StopDuringThePerformance…`：停止后 300ms 内没有新的 `MouthOpen` > 0.05，也没有新的 `play`（统计 `h.Audio.Count` 不再增长）；下一轮正常。
  3. `SwitchingToARig…`：切换后，当前句仍播放完（`h.Audio` 继续增长到该句结束）；`h.Errors` 包含"皮套切换，本轮其余内容仅语音"；下一轮开演时 RigLite 只驱动已接线的输入（原有断言保留）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU --filter "FullyQualifiedName~RealtimeConfigTests|FullyQualifiedName~RuntimeCorticoAcceptanceTests"`
Expected: FAIL（`RequestedReplyProtocol` 不存在；验收用例不符合新行为）

- [ ] **Step 3: 实现配置归一**

`AppConfig.cs` 第 278 行替换为：

```csharp
    private string? _requestedReplyProtocol;

    /// <summary>Always "v2": the legacy reply protocol was removed. A different configured value is
    /// kept in <see cref="RequestedReplyProtocol"/> for one diagnostic line at startup.</summary>
    public string ReplyProtocol
    {
        get => "v2";
        set => _requestedReplyProtocol = string.Equals(value?.Trim(), "v2", StringComparison.OrdinalIgnoreCase) ? null : value ?? "";
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? RequestedReplyProtocol => _requestedReplyProtocol;
```

`config.json.template` 第 62 行改为 `"reply_protocol": "v2"`。

在 `BotRuntime` 构造函数末尾（或 `StartAsync` 开头）加一行：

```csharp
        if (_config.Llm.RequestedReplyProtocol is { } requested)
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[配置] reply_protocol=\"{requested}\" 已不再支持，按 v2 运行");
```

- [ ] **Step 4: 删除提示词里的 legacy 分支**

`CorticoPrompt.cs`：删除 `LegacyEnvelope` 和 `IsV2`；`For` 改为 `public static string For(string grammar) => grammar + "\n" + V2Envelope;`；类注释中描述 legacy 的句子删掉。

`IdentityPrompt.cs`：

```csharp
    public static string InvitationPolicyFor(bool cortico = false) => cortico ? InvitationPolicyV2Cortico : InvitationPolicyV2;
```

删除 `InvitationPolicyCortico`。`InvitationPolicy` 常量保留，因为 `InvitationPolicyV2` 是从它派生的。

`BotRuntime.cs`：第 1096 行改为 `CorticoPrompt.For(_cortico.ScriptGrammar)`；第 1266 行改为 `IdentityPrompt.InvitationPolicyFor(cortico: _orchestrator?.Cortico is not null)`。第 924 行 `_config.Llm.ReplyProtocol` 保持不变（它现在总是 `"v2"`）。

更新 `IdentityPromptTests.cs`、`ListeningContextTests.cs`、`StreamerConfigTests.cs` 中针对 legacy 提示词和 `"legacy"` 配置的断言：legacy 提示词的用例删除；配置用例改为断言"读到 legacy 时按 v2 运行"。

- [ ] **Step 5: 跑全部测试**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU`
Expected: 除 macOS 平台差异那一个用例外，全部 PASS；`RuntimeCorticoAcceptanceTests` 在已执行 `npm ci` 的机器上 PASS。

- [ ] **Step 6: Commit**

```bash
git add -A AIVTuber.Core AIVTuber.Tests config.json.template
git commit -m "v2 is the only reply protocol; Cortico prompts and runtime wired to the visual follower

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: 删除无引用代码

**Files:**
- Delete: `AIVTuber.Core/Avatar/CompositeAvatarController.cs`、`AIVTuber.Core/Avatar/LayeredBreathFollow.cs`、`App/ConfigWizard.cs`、`App/PageService.cs`
- Modify: `App/Converters.cs`（删除 `MicLevelToWidthConverter`）
- Delete/Modify: 引用 `LayeredBreathFollow` 的测试

- [ ] **Step 1: 确认确实没有引用**

```bash
for t in CompositeAvatarController LayeredBreathFollow ConfigWizard PageService MicLevelToWidthConverter; do
  echo "$t: $(grep -rlw $t AIVTuber.Core App AIVTuber.Tests App.Tests --include='*.cs' --include='*.xaml' | grep -v '/obj/\|/bin/' | tr '\n' ' ')"
done
```

Expected：每个名字只出现在它自己的定义文件里；`LayeredBreathFollow` 另外出现在一个测试文件中。

- [ ] **Step 2: 删除**

删除上面四个文件；删除 `App/Converters.cs` 中的 `MicLevelToWidthConverter` 类；在引用 `LayeredBreathFollow` 的测试文件中删除对应用例（如果整个文件都只测它，就删除整个文件）。

- [ ] **Step 3: 构建并测试**

Run: `dotnet build AIVTuber.slnx -c Release -p:PlatformTarget=AnyCPU && dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU`
Expected: 构建成功；测试结果与 Task 8 相同（除去被删的用例）。在 macOS 上 `App` 项目（`net10.0-windows`）可能无法构建，此时只构建 Core 和 Tests，App 的构建以 Windows CI 结果为准。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Remove unreferenced code: composite avatar controller, breath follow, config wizard, page service, mic level converter

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: 全量验证与 Windows 实机清单

**Files:**
- Create: `docs/requirements/cortico-visual-follower-report.md`

- [ ] **Step 1: 全量自动化验证**

```bash
cd sidecar/cortico && npm ci && npm test && npm run typecheck && cd ../..
dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -c Release -p:PlatformTarget=AnyCPU
```

记录通过、失败、跳过的数量。唯一允许的失败是 `DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows`（macOS 平台差异）。

- [ ] **Step 2: 写交付报告**

`docs/requirements/cortico-visual-follower-report.md` 包含以下内容：

1. 起始 HEAD 和本次提交范围（`git log --oneline <起点>..HEAD`）。
2. 自动化结果：Step 1 的实际数字，逐项写明，不写"全部通过"之类的概括。
3. 上游未改的证据：哈希校验用例的名称和结果。
4. **Windows 实机清单**（逐项填"通过 / 未通过 / 未测"，并写观察到的现象）：
   - 真实 VTS + MiniMax，Cortico 启用：用 `RealtimeTrace` 记录首句出声延迟，与 `403471d` 同一句话、同一配置对比。
   - 监听设备、虚拟麦（OBS 能采到）、OBS 字幕在 Cortico 模式下正常，而且只有一份声音。
   - `【点头】你好` 这类台本：先点头，再开口；口型跟随声音。
   - 说话时点"停止"：声音、口型、动作都在 300ms 内停下，下一轮正常。
   - 说话时在 VTS 里切换模型：当前句念完，其余内容只有声音，界面出现"皮套切换，本轮其余内容仅语音"提示；下一轮在新模型上正常开演。
   - 说话时在任务管理器里结束 node 进程：本轮声音继续到结束，出现"皮套异常，本轮仅语音"提示；之后的回合只有声音，不报错。
   - 旧的 `config.json` 里写着 `reply_protocol: "legacy"`：启动后按 v2 运行，诊断日志里有一行说明。
5. 已知限制：兜底时，尚未送去合成的文本按一整段交给 TTS；Node 被判定卡住时，`Stage.DisposeAsync` 的中断失败会结束 sidecar 进程，本次运行之后都只有声音，需要重连形象或重启才会恢复。

- [ ] **Step 3: Commit**

```bash
git add docs/requirements/cortico-visual-follower-report.md
git commit -m "Report: Cortico visual follower delivery and Windows checklist

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Self-Review 记录

- **Spec 覆盖**：
  - §4 架构 → Task 4、5、7
  - §5 协议 → Task 1、2、3
  - §6.1 → Task 7（`BeginInterrupt` 沿用"先停播放器"）和 Task 5（`Stop`、`StoppedAsync`）
  - §6.2 → Task 2（`maxHoldMs`、`cue`）和 Task 6
  - §6.3 → Task 2（`aborted`、`suppressStop`）和 Task 6
  - §6.4 → Task 8 沿用现有逻辑，Cortico 启用时不初始化旧 VTS 控制器
  - §7 → Task 7、8、9
  - §8 测试 → 各任务测试和 Task 10
  - §9 风险 → Task 10 报告
- **偏离 spec 的地方**：
  1. 四个 `Process*Speech*` 入口在生产代码中没有调用方，所以直接删除，而不是 spec §7 写的"合并"。
  2. §5 中"`started` 时才写字幕和历史"细化为"片段的第一块音频交给播放器时写入"。原因是 `AudioPlayer` 的首块回调运行在 WaveOut 线程上，不适合在那里触发界面事件；`started` 回报本身仍然在真正出声时发送。
  3. host 内部错误不单独发 `aborted`，而是让 `perform` 以错误结束，由 `CorticoPacer.CompleteAsync` 进入兜底，效果和 spec 相同。
- **类型一致性**：`SpeechTurnPorts` 各字段的名称和顺序在 Task 4、5、7 中一致；`ICorticoStage` 的回报方法在 Task 3、5 和 `FakeCortico` 中一致；`CheckStall()` 返回的中文原因与 Task 6 的断言一致。
