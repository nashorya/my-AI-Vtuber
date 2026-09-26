import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { mkdtempSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { WebSocketServer } from 'ws';
import { loadPack, EXAMPLE_PACK_DIR } from '../upstream/pack.ts';
import { writeModel } from './harness.ts';

const root = new URL('../', import.meta.url);
test('vendored upstream files exactly match the pinned source hashes', () => {
 const manifest = JSON.parse(readFileSync(new URL('UPSTREAM.json', root), 'utf8'));
 for (const [path, hash] of Object.entries(manifest.files))
  assert.equal(createHash('sha256').update(readFileSync(new URL('upstream/'+path, root))).digest('hex'), hash, path);
});

test('real host + original Performer: clean speech, VTS frames, authorization and cancellation', { timeout: 20000 }, async () => {
 const temp = mkdtempSync(join(tmpdir(), 'cortico-host-'));
 // The fake model's file wires every pack input, so the conservative adaptation may drive them.
 writeModel(temp, 'Fake', loadPack(EXAMPLE_PACK_DIR).paramIds);
 const server = new WebSocketServer({ port: 0 }); await once(server, 'listening');
 const frames: any[] = [];
 const names = loadPack(EXAMPLE_PACK_DIR).paramIds;
 server.on('connection', ws => ws.on('message', raw => {
  const m = JSON.parse(raw.toString()); let data: any = {};
  if (m.messageType === 'AuthenticationTokenRequest') data = { authenticationToken: 'test-token' };
  if (m.messageType === 'AuthenticationRequest') data = { authenticated: true };
  if (m.messageType === 'CurrentModelRequest') data = { modelLoaded: true, modelName: 'Fake', modelID: 'fake' };
  if (m.messageType === 'InputParameterListRequest') data = { defaultParameters: names.map(name => ({ name })) };
  if (m.messageType === 'ExpressionStateRequest') data = { expressions: [] };
  if (m.messageType === 'InjectParameterDataRequest') frames.push(m.data);
  ws.send(JSON.stringify({ apiName: 'VTubeStudioPublicAPI', apiVersion: '1.0', requestID: m.requestID,
   messageType: m.messageType.replace('Request','Response'), data }));
 }));
 const child = spawn(process.execPath, ['--import','tsx','host.ts'], { cwd: root, stdio: ['pipe','pipe','pipe'] });
 let errors = ''; child.stderr.on('data', x => errors += x);
 const pending = new Map<number, (m: any) => void>(); let seq = 0;
 const spoken: string[] = []; let allowed = true; let starts = 0; let holdTts = false;
 let sawTts: (()=>void) | undefined;
 const send = (m: any) => child.stdin.write(JSON.stringify(m)+'\n');
 const command = (name: string, rest: any = {}) => new Promise<any>(resolve => {
  const id = ++seq; pending.set(id, resolve); send({ id, command: name, ...rest });
 });
 createInterface({ input: child.stdout }).on('line', line => {
  const m = JSON.parse(line);
  if (m.kind === 'result') { pending.get(m.id)?.(m); pending.delete(m.id); }
  if (m.kind === 'started') starts++;
  if (m.kind === 'authorize') send({ kind: 'reply', id: m.id, allowed });
  if (m.kind === 'tts') {
   spoken.push(m.text); sawTts?.();
   if (!holdTts) {
    const pcm = Buffer.alloc(16000 * 2 / 5);
    for (let i=0;i<pcm.length/2;i++) pcm.writeInt16LE(Math.round(3000*Math.sin(i/8)), i*2);
    send({ kind: 'reply', id: m.id, pcm: pcm.toString('base64'), sampleRate: 16000 });
   }
  }
 });
 try {
  const init = await command('init', { config: { vtsUrl: `ws://127.0.0.1:${(server.address() as any).port}`,
   tokenPath: join(temp,'token'), audioDevice: 'none', modelProfile: 'auto', live2dDir: temp } });
  assert.equal(init.error, undefined, errors); assert.match(init.grammar, /点头/);
  const script = '<微笑>你好【点头】再见';
  assert.equal((await command('prepare', { script })).spoken, '你好再见');
  const result = await command('perform', { script });
  assert.equal(result.error, undefined, errors); assert.ok(starts > 0);
  assert.ok(spoken.every(x => !/[<>【】]/.test(x)));
  assert.ok(frames.some(f => f.parameterValues.some((p: any) => p.id === 'FaceAngleY' && Math.abs(p.value) > 5)));
  assert.ok(frames.some(f => f.mode === 'set' && f.parameterValues.some((p: any) => p.id === 'MouthOpen' && p.value > 0)));
  allowed = false; const before = starts;
  assert.ok((await command('perform', { script: '不应该播出' })).error);
  assert.equal(starts, before);
  allowed = true; holdTts = true;
  const waiting = new Promise<void>(r => sawTts = r);
  const cancelled = command('perform', { script: '取消这句话' }); await waiting;
  await command('interrupt'); assert.ok((await cancelled).error);
  holdTts = false;
  assert.equal((await command('perform', { script: '下一轮' })).error, undefined);
 } finally {
  child.stdin.end();
  await Promise.race([once(child,'exit'), new Promise(r => setTimeout(r, 1500))]);
  if (child.exitCode === null) child.kill('SIGKILL');
  for (const client of server.clients) client.terminate();
  await new Promise<void>(r => server.close(() => r())); rmSync(temp,{recursive:true,force:true});
 }
});
