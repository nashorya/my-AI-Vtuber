import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { FakeVts, Host, PACK_PARAMS, writeModel, until, sleep } from './harness.ts';

const exampleProfile = JSON.parse(readFileSync(new URL('../upstream/models/examples/cortico.profile.json', import.meta.url), 'utf8'));
const StoppableMixerFadeMs = 200;
const peak = (values: number[]) => values.reduce((m, v) => Math.max(m, Math.abs(v)), 0);

async function withHost(fn: (vts: FakeVts, host: Host, live2d: string) => Promise<void>, setup?: (live2d: string, vts: FakeVts) => void) {
 const vts = await new FakeVts().start();
 const live2d = mkdtempSync(join(tmpdir(), 'cortico-live2d-'));
 setup?.(live2d, vts);
 const host = new Host();
 try {
  const init = await host.init(vts.url, { live2dDir: live2d });
  assert.equal(init.error, undefined, host.errors);
  await fn(vts, host, live2d);
 } finally {
  await host.close(); await vts.close(); rmSync(live2d, { recursive: true, force: true });
 }
}

test('streamed segments start performing before the turn is ended; only clean text is spoken', { timeout: 30000 }, async () => {
 await withHost(async (vts, host) => {
  const perf = await host.begin();
  await perf.feed('<微笑>你好呀。');
  // The first segment is synthesized and authorized while the app has not ended the turn.
  await until(() => host.authorized.length === 1);
  assert.equal(host.authorized[0], '你好呀。');
  await perf.feed('【点头】今天也要加油。');
  assert.equal((await perf.end()).error, undefined);
  const result = await perf.result;
  assert.equal(result.error, undefined, host.errors);
  assert.equal(result.rounds, 2);
  assert.deepEqual(host.authorized, ['你好呀。', '今天也要加油。']);
  assert.ok(host.ttsTexts.every(t => !/[<>【】]/.test(t)));
  assert.ok(peak(vts.values('MouthOpen')) > 0, 'mouth follows the audio');
  assert.ok(peak(vts.values('FaceAngleY')) > 3, 'nod reaches the model');
 }, (live2d) => writeModel(live2d, 'Fake', PACK_PARAMS));
});

test('interrupt stops the real performance; late feeds for it are rejected; the next turn works', { timeout: 30000 }, async () => {
 await withHost(async (vts, host) => {
  host.holdTts = true;
  const perf = await host.begin();
  await perf.feed('【摇头】这一句会被打断。');
  await until(() => host.ttsTexts.length === 1);
  await host.command('interrupt').result;
  assert.ok((await perf.result).error, 'stopped turn reports cancellation');
  assert.ok((await perf.feed('迟到的一段。')).error, 'late segment is not performed');
  await sleep(StoppableMixerFadeMs + 100); // a gesture under way fades out instead of snapping
  const framesAfterStop = vts.frames.length;
  await sleep(600);
  // Idle life may settle slowly (a few degrees); the shake swings ±20° and more.
  const yaw = vts.frames.slice(framesAfterStop).flatMap(f => f.parameterValues).filter(p => p.id === 'FaceAngleX').map(p => p.value);
  const span = yaw.length ? Math.max(...yaw) - Math.min(...yaw) : 0;
  assert.ok(span < 12 && peak(yaw) < 10, `no head shake continues after stop (span ${span}, peak ${peak(yaw)})`);
  assert.equal(host.starts, 0, 'no audio started for the stopped turn');
  host.holdTts = false;
  const next = await host.begin();
  await next.feed('下一轮。'); await next.end();
  assert.equal((await next.result).error, undefined, host.errors);
  assert.deepEqual(host.authorized, ['下一轮。']);
 }, (live2d) => writeModel(live2d, 'Fake', PACK_PARAMS));
});

test('a denied piece stops the rest of that turn instead of acting without a voice', { timeout: 30000 }, async () => {
 await withHost(async (vts, host) => {
  host.allow = () => false;
  const perf = await host.begin();
  await perf.feed('第一句。'); await perf.feed('【用力点头】第二句。'); await perf.end();
  assert.ok((await perf.result).error);
  assert.equal(host.starts, 0);
  assert.ok(!host.ttsTexts.includes('第二句。') || peak(vts.values('FaceAngleY')) < 5);
 }, (live2d) => writeModel(live2d, 'Fake', PACK_PARAMS));
});

test('each rig uses its own mapping; a rig without a profile gets a conservative, verified subset; switching rigs cuts the old turn', { timeout: 40000 }, async () => {
 const rigA = { ...exampleProfile, id: 'RigA', label: 'Rig A', vtsModelName: 'RigA',
  wiring: { ...exampleProfile.wiring, FaceAngleY: { invert: true, scale: 0.5 } },
  fx: { fx_sweat: { file: 'Sweat.exp3.json', durationMs: 3000 } }, keepExpressions: [] };
 const liteInputs = ['MouthOpen', 'FaceAngleX', 'EyeOpenLeft', 'EyeOpenRight']; // no FaceAngleY/Z, no brows, no cheeks
 await withHost(async (vts, host) => {
  const a = host.statuses.at(-1);
  assert.equal(a.profile, 'RigA'); assert.equal(a.mode, 'dedicated');

  // Rig A: dedicated inverted, half-strength nod.
  let perf = await host.begin();
  await perf.feed('【用力点头】好的。'); await perf.end();
  assert.equal((await perf.result).error, undefined, host.errors);
  const nodA = vts.values('FaceAngleY', 'RigA');
  assert.ok(nodA.length > 0);
  // The pack's nod goes negative (down, peak −26°×1.35); Rig A wires the axis inverted at half strength.
  assert.ok(Math.max(...nodA) > 10 && Math.max(...nodA) < 20, `inverted, half-strength nod: ${Math.max(...nodA)}`);
  assert.ok(Math.min(...nodA) > -5, `no downward nod on an inverted axis: ${Math.min(...nodA)}`);

  // Switch to Lite mid-turn: the in-flight turn is cut; nothing of it reaches Lite.
  host.holdTts = true;
  perf = await host.begin();
  await perf.feed('【流汗特效,摇头】说到一半换了模型。');
  await until(() => host.ttsTexts.includes('说到一半换了模型。'));
  vts.loadModel({ name: 'Lite', inputs: liteInputs.concat(['FaceAngleY']) }); // VTS lists it, model file does not wire it
  assert.ok((await perf.result).error, 'old turn cancelled by model switch');
  host.holdTts = false;
  await until(() => host.statuses.at(-1)?.model === 'Lite');
  const lite = host.statuses.at(-1);
  assert.equal(lite.mode, 'conservative');
  assert.ok(lite.skipped.some((s: any) => s.param === 'FaceAngleY'));
  assert.ok(lite.driven.includes('MouthOpen') && lite.driven.includes('FaceAngleX'));

  const before = vts.frames.length;
  perf = await host.begin();
  await perf.feed('【用力点头,摇头】换好了。'); await perf.end();
  assert.equal((await perf.result).error, undefined, host.errors);
  const liteFrames = vts.frames.slice(before).filter(f => f.model === 'Lite');
  const ids = new Set(liteFrames.flatMap(f => f.parameterValues.map(p => p.id)));
  assert.ok(!ids.has('FaceAngleY') && !ids.has('FaceAngleZ') && !ids.has('BrowLeftY'), [...ids].join(','));
  assert.ok(peak(vts.values('MouthOpen', 'Lite')) > 0, 'lip sync still works on the lite rig');
  const shakeLite = peak(vts.values('FaceAngleX', 'Lite'));
  assert.ok(shakeLite > 0, 'shake still performed on its available axis');

  // Whatever of Rig A's turn was in flight across the switch, Lite ends with none of Rig A's
  // expressions switched on, and its own conservative turn switches none on.
  await sleep(3200);
  assert.deepEqual(vts.activeOn('Lite'), [], JSON.stringify(vts.expressions));
  assert.ok(!vts.expressions.some(e => e.model === 'Lite' && e.active && e.file !== 'Sweat.exp3.json'));
 }, (live2d, vts) => {
  writeModel(live2d, 'RigA', PACK_PARAMS, rigA);
  writeModel(live2d, 'Lite', liteInputs);
  vts.model = { name: 'RigA', inputs: PACK_PARAMS };
 });
});

test('a rig without profile or model file is only lip-synced, and says so', { timeout: 30000 }, async () => {
 await withHost(async (vts, host) => {
  const status = host.statuses.at(-1);
  assert.equal(status.mode, 'conservative');
  assert.deepEqual(status.driven, ['MouthOpen']);
  assert.ok(status.warnings.some((w: string) => w.includes('只驱动口型')));
  const perf = await host.begin();
  await perf.feed('【摇头】你好。'); await perf.end();
  assert.equal((await perf.result).error, undefined, host.errors);
  const ids = new Set(vts.frames.flatMap(f => f.parameterValues.map(p => p.id)));
  assert.deepEqual([...ids], ['MouthOpen']);
 }, (_live2d, vts) => { vts.model = { name: 'Unknown', inputs: PACK_PARAMS }; });
});

test('a stop fenced to an earlier turn never cuts the next one', { timeout: 30000 }, async () => {
 await withHost(async (_vts, host) => {
  const perf = await host.begin();
  await host.command('interrupt', { fence: perf.id - 1 }).result; // a late stop aimed at the previous turn
  await perf.feed('这一轮不受影响。'); await perf.end();
  assert.equal((await perf.result).error, undefined, host.errors);
  assert.deepEqual(host.authorized, ['这一轮不受影响。']);
 }, (live2d) => writeModel(live2d, 'Fake', PACK_PARAMS));
});
