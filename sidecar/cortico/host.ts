/** JSON-lines host for unmodified Cortico L2–L4. stdout is protocol only. */
import { createInterface } from 'node:readline';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { createRequire } from 'node:module';
import { Performer, type AudioSink } from './upstream/orchestrator.ts';
import { Mixer } from './upstream/mixer.ts';
import type { IRFrame } from './upstream/mixer.ts';
import { VtsBackend } from './upstream/backend.ts';
import { VtsClient } from './upstream/vts-client.ts';
import { DeviceAudioSink } from './upstream/device-audio.ts';
import { loadPack, EXAMPLE_PACK_DIR, vocabTableRows } from './upstream/pack.ts';
import { loadProfiles, resolveProfile, DEFAULT_PROFILE } from './upstream/models/index.ts';
import { ScriptParser } from './upstream/parser.ts';
import { decodeWav, extractEnvelope, pcm16ToWav } from './upstream/tts.ts';
import { adapt } from './adapt.ts';

const send = (value: unknown) => process.stdout.write(JSON.stringify(value) + '\n');
const log = Object.fromEntries(['debug','info','warn','error'].map(level => [level,
 (message: string, fields?: unknown) => process.stderr.write(JSON.stringify({ level, message, fields })+'\n')])) as any;
console.log = (...args) => console.error(...args);
let seq = 0;
const replies = new Map<number, { resolve: (x: any) => void; reject: (e: Error) => void }>();
function ask(kind: string, value: object, signal?: AbortSignal): Promise<any> {
 return new Promise((resolveReply, reject) => {
  const id = ++seq;
  const abort = () => { replies.delete(id); reject(new Error('Cancelled')); };
  if (signal?.aborted) { abort(); return; }
  signal?.addEventListener('abort', abort, { once: true });
  replies.set(id, {
   resolve: x => { signal?.removeEventListener('abort', abort); resolveReply(x); },
   reject: e => { signal?.removeEventListener('abort', abort); reject(e); },
  });
  send({ kind, id, ...value });
 });
}
/**
 * Upstream preempt drops queued content, but a head gesture that already started runs to its
 * end (up to ~1.5 s). A stop must stop the visible action too, so the host fades running
 * gesture/prosody pulses out over a short window and drops pulses scheduled for later. Only
 * the cue objects handed to the mixer are touched; no upstream curve or algorithm changes.
 */
class StoppableMixer extends Mixer {
 private fading = new Map<{ intensity: number }, { from: number; at: number }>();
 static readonly FADE_MS = 200;
 stopGestures(now: number): void {
  const self = this as unknown as { pulses: Array<{ startTs: number; intensity: number }>; prosody: Array<{ startTs: number; intensity: number }> };
  for (const key of ['pulses', 'prosody'] as const) {
   self[key] = self[key].filter(cue => cue.startTs <= now);
   for (const cue of self[key]) if (!this.fading.has(cue)) this.fading.set(cue, { from: cue.intensity, at: now });
  }
 }
 override frame(now: number): IRFrame {
  if (this.fading.size > 0) {
   const self = this as unknown as { pulses: Array<{ intensity: number }>; prosody: Array<{ intensity: number }> };
   for (const [cue, fade] of this.fading) {
    const k = 1 - (now - fade.at) / StoppableMixer.FADE_MS;
    if (k <= 0) {
     self.pulses = self.pulses.filter(c => c !== cue);
     self.prosody = self.prosody.filter(c => c !== cue);
     this.fading.delete(cue);
    } else cue.intensity = fade.from * k;
   }
  }
  return super.frame(now);
 }
}
let performer: Performer | undefined;
let mixer: StoppableMixer | undefined;
let vts: VtsClient | undefined;
let backend: VtsBackend | undefined;
/**
 * One app turn. A turn is fed incrementally: each approved speech segment becomes its own
 * upstream round (append semantics), so the first segment is synthesized and performed while
 * the app's LLM is still producing the next one, and no piece ever spans two segments.
 */
interface Performance {
 id: number; aborted: boolean; controller: AbortController; failure?: string;
 ended: boolean; rounds: number; finish: () => void; finished: Promise<void>;
}
let active: Performance | undefined;
let device: DeviceAudioSink | undefined;
let statusTimer: ReturnType<typeof setInterval> | undefined;
let initialized = false;
let shutdown = false;
let pack = loadPack(EXAMPLE_PACK_DIR);
/** True while the loaded model's profile is being (re)resolved; nothing new may start. */
let syncing = false;
let syncDone: Promise<void> = Promise.resolve();
/** Bumped on every model sync; expression pulses opened for an older model never touch the new one. */
let modelGeneration = 0;
/** Stop the real performance: queued rounds, audio, pending synthesis, and held states. */
const interrupt = async () => {
 const victim = active;
 if (victim) { victim.aborted = true; victim.controller.abort(); victim.finish(); }
 const cut = performer?.preempt({ boundaryWindowMs: 0 }); // drops queued rounds synchronously
 // Held emotion/pose/gaze states would otherwise outlive the stopped turn until they time out,
 // and a gesture already under way would play to its end.
 performer?.beginExternalTimeline();
 mixer?.stopGestures(Date.now());
 await cut;
 await performer?.whenIdle();
 if (active === victim) active = undefined;
};
const owned = (requestId: unknown): Performance => {
 const current = active;
 if (!current || current.id !== requestId || current.aborted) throw new Error('Stale performance');
 return current;
};
const feedRound = (current: Performance, script: unknown) => {
 if (typeof script !== 'string') throw new Error('script must be a string');
 if (current.ended) throw new Error('Performance already ended');
 if (!script.trim()) return;
 const round = performer!.beginRound(); round.feed(script); round.end();
 current.rounds++;
};
async function initialize(config: any) {
 if (initialized) throw new Error('Already initialized');
 if (config.audioDevice !== 'none') createRequire(import.meta.url)('audify');
 pack = loadPack(config.packDir || EXAMPLE_PACK_DIR);
 let profile = DEFAULT_PROFILE;
 const tokenPath = resolve(config.tokenPath);
 vts = new VtsClient({ url: config.vtsUrl,
  pluginName: 'AIVTuber Cortico Preview', pluginDeveloper: 'AIVTuber',
  authToken: existsSync(tokenPath) ? readFileSync(tokenPath, 'utf8').trim() : '',
  onToken: token => writeFileSync(tokenPath, token, { mode: 0o600 }),
  log: (event, message, fields) => log.info(message, { event, ...fields }),
 });
 const client = vts;
 // Expression pulses are the one VTS write upstream schedules on a timer (fx off after its
 // duration). Route them through a guard so a pulse from the previous model cannot switch an
 // expression of the newly loaded one, and nothing is switched on while the profile resolves.
 const expressionOwner = new Map<string, number>();
 const guarded = new Proxy(client, { get(target, prop) {
  if (prop === 'setExpression') return async (file: string, on: boolean, fade?: number) => {
   if (on) {
    if (syncing) { log.info('model sync in progress; expression skipped', { file }); return; }
    expressionOwner.set(file, modelGeneration);
    return target.setExpression(file, on, fade);
   }
   const owner = expressionOwner.get(file);
   if (owner !== undefined && owner !== modelGeneration) { log.info('stale expression pulse from previous model dropped', { file }); return; }
   expressionOwner.delete(file);
   return target.setExpression(file, on, fade);
  };
  const value = Reflect.get(target, prop, target);
  return typeof value === 'function' ? value.bind(target) : value;
 } }) as VtsClient;
 let syncGeneration = 0;
 const sync = async () => {
  const generation = ++syncGeneration;
  modelGeneration++;
  syncing = true;
  let done!: () => void;
  syncDone = new Promise<void>(r => done = r);
  backend?.beginParameterSync();
  try {
   const model = await client.currentModel();
   const registry = loadProfiles(config.live2dDir || '', { paramIds: pack.paramIds, fxIds: pack.fxIds });
   const configured = config.modelProfile || 'auto';
   const modelName = model?.name || '';
   const selected = resolveProfile(registry, configured, modelName);
   const names = await client.inputParameterNames();
   if (generation !== syncGeneration) return;
   const adaptation = adapt({ resolution: selected, configured, modelName, known: names,
    packParams: pack.paramIds, live2dDir: config.live2dDir || '' });
   profile = adaptation.profile;
   backend?.setKnownParameters(names);
   // A command that was already in flight when VTS switched rigs can land on the new model;
   // start every model from its own resting expressions (keeping only its outfit files).
   try { await client.clearActiveExpressions({ keepFiles: [...profile.keepExpressions] }); }
   catch (e) { log.warn('clearing leftover expressions failed', { error: String(e) }); }
   send({ kind: 'status', connected: true, model: modelName, profile: profile.id, mode: adaptation.mode,
    modelFile: adaptation.modelFile, driven: adaptation.driven, skipped: adaptation.skipped,
    warnings: [...registry.errors.map(x => x.message), ...(selected.source?.warnings || []), ...adaptation.warnings] });
  } catch (e) {
   if (generation === syncGeneration) backend?.setKnownParameters(new Set());
   throw e;
  } finally {
   if (generation === syncGeneration) syncing = false;
   done();
  }
 };
 backend = new VtsBackend(guarded, { profile: () => profile, resyncKnown: async () => {
  try { await sync(); } catch (e) { backend?.setKnownParameters(new Set()); log.error(String(e)); }
 },
  onError: error => log.warn(error.message) });
 client.onModelLoaded(() => {
  // A different rig: old rounds, held states and mappings must not reach it.
  syncing = true; backend?.beginParameterSync();
  void interrupt().then(sync).catch(e => log.error(String(e)));
 });
 const audioLog = { ...log, warn: (message: string, fields?: unknown) => {
  log.warn(message, fields);
  if (active && config.audioDevice !== 'none' && (message.includes('静音时间线') || message.includes('没有声音')))
   active.failure = message;
 }, error: (message: string, fields?: unknown) => {
  log.error(message, fields);
  if (active && config.audioDevice !== 'none') active.failure = message;
 }};
 const sink = new DeviceAudioSink(audioLog, { device: () => config.audioDevice || '', secondary: () => 'off' });
 device = sink;
 const audio: AudioSink = {
  async play(piece) {
   const current = active;
   if (!current || current.aborted) throw new Error('Cancelled');
   // Last gate before the audience hears anything: the app decides per piece, with its text,
   // whether this turn may still speak (people resumed talking, stop, pause, sign-out).
   const permission = await ask('authorize', { requestId: current.id, text: piece.text }, current.controller.signal);
   if (!permission.allowed || active !== current || current.aborted) {
    current.failure = 'Turn superseded';
    // Upstream would otherwise move on to the next beat and keep acting without a voice.
    if (active === current) void interrupt();
    throw new Error(current.failure);
   }
   const playback = await sink.play(piece);
   if (current.failure) { sink.stop(0); throw new Error(current.failure); }
   send({ kind: 'started', requestId: current.id });
   return playback;
  },
  beginStream: (...args) => sink.beginStream(...args),
  stop: fade => sink.stop(fade),
  cut: (...args) => sink.cut(...args),
 };
 mixer = new StoppableMixer({ pack: () => pack, idleBlinks: () => profile.idleBlinks });
 performer = new Performer({ pack: () => pack, mixer, backend, audio, log,
  tts: { async synth(text, signal) {
   const current = active;
   if (!current || current.aborted) throw new Error('No active turn');
   try {
    const result = await ask('tts', { text, requestId: current.id }, current.controller.signal);
    if (current.aborted || signal?.aborted) throw new Error('Cancelled');
    const pcm = Buffer.from(result.pcm, 'base64');
    if (pcm.length === 0 || pcm.length % 2 || !Number.isInteger(result.sampleRate) || result.sampleRate < 8000)
     throw new Error('Invalid PCM16 mono audio');
    const wav = pcm16ToWav([pcm], result.sampleRate);
    const decoded = decodeWav(wav);
    return { text, wav, durationMs: decoded.samples.length / decoded.sampleRate * 1000,
      envelope: extractEnvelope(decoded) };
   } catch (e) { current.failure = String(e); throw e; }
  } },
  streamEnabled: () => false, alignEnabled: () => false,
  trace: (area, message, opts) => { log.debug(message, { area, ...opts });
   if (opts?.level === 'error' && active) active.failure = message;
  },
 });
 backend.beginParameterSync();
 await client.connect(); await sync();
 performer.start(); initialized = true;
 statusTimer = setInterval(() => send({ kind: 'status', connected: client.connected }), 1000);
 statusTimer.unref();
 // Grammar and vocabulary only. The reply envelope (legacy script or v2 events), PASS and
 // thought rules are composed by the app so the prompt always matches the parser in use.
 return { grammar: '【演出台本语法】\n'
  + '<动作>随说随做；【动作】暂停说话做动作。支持逗号分隔组合，只能使用下表里的词。\n'
  + vocabTableRows(pack)
  + '\n不要使用旧的 [action:]、[emotion:]、[pose:] 标签，不要输出 TTS 专属方括号语气标签，不要写 VTS 参数名。' };
}
async function handle(message: any) {
 if (message.kind === 'reply') {
  const item = replies.get(message.id); replies.delete(message.id);
  if (message.error) item?.reject(new Error(message.error)); else item?.resolve(message);
  return;
 }
 const { id, command } = message;
 try {
  let value: any = {};
  if (command === 'init') value = await initialize(message.config);
  else if (command === 'interrupt') {
   // A fenced interrupt targets performances up to that id; one that started later (the next
   // turn, after a slow stop) is left alone.
   const fence = typeof message.fence === 'number' ? message.fence : undefined;
   if (fence === undefined || !active || active.id <= fence) await interrupt();
  }
  else if (command === 'prepare') {
   if (!initialized) throw new Error('Not initialized');
   const pieces: string[] = [];
   const parser = new ScriptParser({ onBeat() {}, onSpeech(_index, piece) { pieces.push(piece.text); }, onEnd() {} }, pack);
   parser.feed(message.script); parser.end(); value = { spoken: pieces.join('') };
  } else if (command === 'perform') {
   if (!initialized || !performer) throw new Error('Not initialized');
   if (active) throw new Error('An earlier performance is still active');
   while (syncing) await syncDone; // never start on a model whose mapping is still being resolved
   if (active) throw new Error('An earlier performance is still active');
   let finish!: () => void;
   const finished = new Promise<void>(r => finish = r);
   const current: Performance = { id, aborted: false, controller: new AbortController(), ended: false,
    rounds: 0, finish, finished };
   active = current;
   try {
    if (message.script !== undefined) { feedRound(current, message.script); current.ended = true; finish(); }
    else send({ kind: 'ready', requestId: id });
    await finished;
    await performer.whenIdle();
    if (current.aborted) throw new Error('Cancelled');
    if (current.failure) throw new Error(current.failure);
    value = { rounds: current.rounds };
   } finally { if (active === current) active = undefined; }
  } else if (command === 'feed') {
   feedRound(owned(message.requestId), message.script);
  } else if (command === 'end') {
   const current = owned(message.requestId);
   current.ended = true; current.finish();
  } else throw new Error('Unknown command');
  send({ kind: 'result', id, ...value });
 } catch (error) { send({ kind: 'result', id, error: error instanceof Error ? error.message : String(error) }); }
}
const input = createInterface({ input: process.stdin });
input.on('line', line => { try { void handle(JSON.parse(line)); } catch (e) { log.error(String(e)); } });
async function close() {
 if (shutdown) return; shutdown = true;
 await interrupt(); performer?.stop(); device?.close();
 if (statusTimer) clearInterval(statusTimer);
 await vts?.close(); process.exit(0);
}
input.on('close', () => { void close(); });
process.on('SIGTERM', () => { void close(); });
