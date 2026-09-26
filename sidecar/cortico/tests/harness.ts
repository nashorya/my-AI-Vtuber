/** Test harness: the real host.ts child process against a scriptable fake VTS. */
import { spawn, type ChildProcess } from 'node:child_process';
import { createInterface } from 'node:readline';
import { mkdtempSync, rmSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { once } from 'node:events';
import { WebSocketServer, type WebSocket } from 'ws';
import { loadPack, EXAMPLE_PACK_DIR } from '../upstream/pack.ts';

export const PACK_PARAMS = loadPack(EXAMPLE_PACK_DIR).paramIds;

export interface FakeModel { name: string; inputs: string[] }

export class FakeVts {
 readonly server = new WebSocketServer({ port: 0 });
 readonly frames: Array<{ model: string; mode: string; parameterValues: Array<{ id: string; value: number }> }> = [];
 readonly expressions: Array<{ model: string; file: string; active: boolean }> = [];
 model: FakeModel = { name: 'Fake', inputs: PACK_PARAMS };
 readonly active = new Map<string, Set<string>>();
 activeOn(model: string) { return [...(this.active.get(model) ?? [])]; }
 private sockets = new Set<WebSocket>();
 async start() {
  await once(this.server, 'listening');
  this.server.on('connection', ws => {
   this.sockets.add(ws);
   ws.on('message', raw => {
    const m = JSON.parse(raw.toString()); let data: any = {};
    if (m.messageType === 'AuthenticationTokenRequest') data = { authenticationToken: 'test-token' };
    if (m.messageType === 'AuthenticationRequest') data = { authenticated: true };
    if (m.messageType === 'CurrentModelRequest') data = { modelLoaded: true, modelName: this.model.name, modelID: this.model.name };
    if (m.messageType === 'InputParameterListRequest') data = { defaultParameters: this.model.inputs.map(name => ({ name })) };
    if (m.messageType === 'ExpressionStateRequest')
     data = { expressions: this.activeOn(this.model.name).map(file => ({ file, active: true })) };
    if (m.messageType === 'InjectParameterDataRequest') this.frames.push({ model: this.model.name, ...m.data });
    if (m.messageType === 'ExpressionActivationRequest') {
     this.expressions.push({ model: this.model.name, file: m.data.expressionFile, active: m.data.active });
     const set = this.active.get(this.model.name) ?? new Set<string>();
     if (m.data.active) set.add(m.data.expressionFile); else set.delete(m.data.expressionFile);
     this.active.set(this.model.name, set);
    }
    ws.send(JSON.stringify({ apiName: 'VTubeStudioPublicAPI', apiVersion: '1.0', requestID: m.requestID,
     messageType: m.messageType.replace('Request', 'Response'), data }));
   });
  });
  return this;
 }
 get url() { return `ws://127.0.0.1:${(this.server.address() as any).port}`; }
 loadModel(model: FakeModel) {
  this.model = model;
  for (const ws of this.sockets) ws.send(JSON.stringify({ apiName: 'VTubeStudioPublicAPI', apiVersion: '1.0',
   messageType: 'ModelLoadedEvent', data: { modelLoaded: true, modelName: model.name, modelID: model.name } }));
 }
 values(id: string, model?: string) {
  return this.frames.filter(f => !model || f.model === model)
   .flatMap(f => f.parameterValues.filter(p => p.id === id).map(p => p.value));
 }
 async close() {
  for (const ws of this.sockets) ws.terminate();
  await new Promise<void>(r => this.server.close(() => r()));
 }
}

/** Writes a VTS model folder: `<dir>/<name>/<name>.vtube.json` wiring the given inputs. */
export function writeModel(live2dDir: string, name: string, wiredInputs: string[], profile?: object) {
 const dir = join(live2dDir, name);
 mkdirSync(dir, { recursive: true });
 writeFileSync(join(dir, `${name}.vtube.json`), JSON.stringify({ Name: name,
  ParameterSettings: wiredInputs.map(input => ({ Input: input, OutputLive2D: 'Param' + input, Smoothing: 0 })) }));
 if (profile) writeFileSync(join(dir, 'cortico.profile.json'), JSON.stringify(profile));
}

export class Host {
 readonly child: ChildProcess;
 readonly temp = mkdtempSync(join(tmpdir(), 'cortico-host-'));
 readonly ttsTexts: string[] = [];
 readonly authorized: string[] = [];
 readonly statuses: any[] = [];
 starts = 0;
 allow: (text: string) => boolean = () => true;
 holdTts = false;
 onTts?: (text: string) => void;
 errors = '';
 private pending = new Map<number, (m: any) => void>();
 private ready = new Map<number, () => void>();
 private seq = 0;
 constructor() {
  this.child = spawn(process.execPath, ['--import', 'tsx', 'host.ts'],
   { cwd: new URL('../', import.meta.url), stdio: ['pipe', 'pipe', 'pipe'] });
  this.child.stderr!.on('data', x => this.errors += x);
  createInterface({ input: this.child.stdout! }).on('line', line => {
   const m = JSON.parse(line);
   if (m.kind === 'result') { this.pending.get(m.id)?.(m); this.pending.delete(m.id); }
   if (m.kind === 'ready') this.ready.get(m.requestId)?.();
   if (m.kind === 'status' && m.profile) this.statuses.push(m);
   if (m.kind === 'started') this.starts++;
   if (m.kind === 'authorize') {
    const ok = this.allow(m.text);
    if (ok) this.authorized.push(m.text);
    this.send({ kind: 'reply', id: m.id, allowed: ok });
   }
   if (m.kind === 'tts') {
    this.ttsTexts.push(m.text); this.onTts?.(m.text);
    if (!this.holdTts) this.send({ kind: 'reply', id: m.id, pcm: tone(400), sampleRate: 16000 });
   }
  });
 }
 send(m: any) { this.child.stdin!.write(JSON.stringify(m) + '\n'); }
 command(name: string, rest: any = {}): { id: number; result: Promise<any> } {
  const id = ++this.seq;
  const result = new Promise<any>(resolve => this.pending.set(id, resolve));
  this.send({ id, command: name, ...rest });
  return { id, result };
 }
 /** Starts a streamed performance and resolves once the host accepts feeds for it. */
 async begin() {
  const id = this.seq + 1;
  const ready = new Promise<void>(r => this.ready.set(id, r));
  const started = this.command('perform');
  await Promise.race([ready, started.result.then(m => { throw new Error(m.error || 'ended before ready'); })]);
  return {
   id: started.id, result: started.result,
   feed: (script: string) => this.command('feed', { requestId: started.id, script }).result,
   end: () => this.command('end', { requestId: started.id }).result,
  };
 }
 init(vtsUrl: string, extra: any = {}) {
  return this.command('init', { config: { vtsUrl, tokenPath: join(this.temp, 'token'), audioDevice: 'none',
   modelProfile: 'auto', ...extra } }).result;
 }
 async close() {
  this.child.stdin!.end();
  await Promise.race([once(this.child, 'exit'), new Promise(r => setTimeout(r, 1500))]);
  if (this.child.exitCode === null) this.child.kill('SIGKILL');
  rmSync(this.temp, { recursive: true, force: true });
 }
}

export function tone(ms: number) {
 const pcm = Buffer.alloc(16000 * 2 * ms / 1000);
 for (let i = 0; i < pcm.length / 2; i++) pcm.writeInt16LE(Math.round(3000 * Math.sin(i / 8)), i * 2);
 return pcm.toString('base64');
}

export const until = async (condition: () => boolean, ms = 5000) => {
 const deadline = Date.now() + ms;
 while (!condition()) {
  if (Date.now() > deadline) throw new Error('condition not met before timeout');
  await new Promise(r => setTimeout(r, 10));
 }
};
export const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
