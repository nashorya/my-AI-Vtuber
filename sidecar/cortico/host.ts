/** JSON-lines host for unmodified Cortico L2–L4. stdout is protocol only. */
import { createInterface } from 'node:readline';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { createRequire } from 'node:module';
import { Performer, type AudioSink } from './upstream/orchestrator.ts';
import { Mixer } from './upstream/mixer.ts';
import { VtsBackend } from './upstream/backend.ts';
import { VtsClient } from './upstream/vts-client.ts';
import { DeviceAudioSink } from './upstream/device-audio.ts';
import { loadPack, EXAMPLE_PACK_DIR, vocabTableRows } from './upstream/pack.ts';
import { loadProfiles, resolveProfile, DEFAULT_PROFILE } from './upstream/models/index.ts';
import { ScriptParser } from './upstream/parser.ts';
import { decodeWav, extractEnvelope, pcm16ToWav } from './upstream/tts.ts';

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
let performer: Performer | undefined;
let vts: VtsClient | undefined;
let backend: VtsBackend | undefined;
let active: { id: number; aborted: boolean; controller: AbortController; failure?: string } | undefined;
let device: DeviceAudioSink | undefined;
let statusTimer: ReturnType<typeof setInterval> | undefined;
let initialized = false;
let shutdown = false;
let pack = loadPack(EXAMPLE_PACK_DIR);
const interrupt = async () => {
 if (active) { active.aborted = true; active.controller.abort(); }
 await performer?.preempt({ boundaryWindowMs: 0 });
 await performer?.whenIdle();
 active = undefined;
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
 let syncGeneration = 0;
 const sync = async () => {
  const generation = ++syncGeneration;
  backend?.beginParameterSync();
  try {
   const model = await client.currentModel();
   const registry = loadProfiles(config.live2dDir || '', { paramIds: pack.paramIds, fxIds: pack.fxIds });
   const selected = resolveProfile(registry, config.modelProfile || 'auto', model?.name || '');
   const names = await client.inputParameterNames();
   if (generation !== syncGeneration) return;
   if (selected.how === 'missing') throw new Error('Selected model profile does not exist');
   if (selected.how === 'configured' && selected.profile.vtsModelName !== model?.name)
    throw new Error('Selected profile does not match the loaded VTS model');
   profile = selected.profile;
   backend?.setKnownParameters(names);
   send({ kind: 'status', connected: true, model: model?.name, profile: profile.id,
    warnings: [...registry.errors.map(x => x.message), ...(selected.source?.warnings || []),
     ...(selected.how === 'fallback' ? ['Uncalibrated default profile; calibrate before visual acceptance'] : [])] });
  } catch (e) {
   if (generation === syncGeneration) backend?.setKnownParameters(new Set());
   throw e;
  }
 };
 backend = new VtsBackend(client, { profile: () => profile, resyncKnown: async () => {
  try { await sync(); } catch (e) { backend?.setKnownParameters(new Set()); log.error(String(e)); }
 },
  onError: error => log.warn(error.message) });
 client.onModelLoaded(() => { backend?.beginParameterSync(); void interrupt().then(sync).catch(e => log.error(String(e))); });
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
   const permission = await ask('authorize', { requestId: current.id }, current.controller.signal);
   if (!permission.allowed || active !== current || current.aborted) {
    current.failure = 'Turn superseded'; throw new Error(current.failure);
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
 const mixer = new Mixer({ pack: () => pack, idleBlinks: () => profile.idleBlinks });
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
 await client.clearActiveExpressions({ keepFiles: [...profile.keepExpressions] });
 performer.start(); initialized = true;
 statusTimer = setInterval(() => send({ kind: 'status', connected: client.connected }), 1000);
 statusTimer.unref();
 return { prompt: '发言时使用以下 Cortico 演出台本语法。直接输出台本，不调用工具，不输出参数。\n'
  + '<动作>随说随做；【动作】暂停说话做动作。支持逗号分隔组合。\n'
  + vocabTableRows(pack) + '\n不接话仍只输出【PASS】，心里话仍使用全角括号。不要使用旧的 [action:]、[emotion:]、[pose:] 标签。不要输出 TTS 专属方括号语气标签。' };
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
  else if (command === 'interrupt') await interrupt();
  else if (command === 'prepare') {
   if (!initialized) throw new Error('Not initialized');
   const pieces: string[] = [];
   const parser = new ScriptParser({ onBeat() {}, onSpeech(_index, piece) { pieces.push(piece.text); }, onEnd() {} }, pack);
   parser.feed(message.script); parser.end(); value = { spoken: pieces.join('') };
  } else if (command === 'perform') {
   if (!initialized || !performer) throw new Error('Not initialized');
   if (active) throw new Error('An earlier performance is still active');
   const current = { id, aborted: false, controller: new AbortController(), failure: undefined as string | undefined };
   active = current;
   try {
    const round = performer.beginRound(); round.feed(message.script); round.end();
    await performer.whenIdle();
    if (current.aborted) throw new Error('Cancelled');
    if (current.failure) throw new Error(current.failure);
   } finally { if (active === current) active = undefined; }
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
