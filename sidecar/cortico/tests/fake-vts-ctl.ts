/**
 * Controllable fake VTS for the app's acceptance tests (C# drives it over stdin, one JSON per line).
 * First stdout line: {"port":N}. Commands:
 *   {"cmd":"load","name":"RigLite","inputs":["MouthOpen",...]}  → switches model, pushes ModelLoadedEvent
 *   {"cmd":"stats","since":K}  → {"frames":N,"total":T,"models":[...],"ids":[...],"peak":{id:maxAbs},"range":{id:[min,max]},"active":{model:[files]}}
 * It records what a model would receive; it does not render anything.
 */
import { createInterface } from 'node:readline';
import { FakeVts, PACK_PARAMS } from './harness.ts';

const vts = await new FakeVts().start();
vts.model = { name: process.argv[2] || 'Fake', inputs: PACK_PARAMS };
console.log(JSON.stringify({ port: (vts.server.address() as any).port }));
createInterface({ input: process.stdin }).on('line', line => {
 const m = JSON.parse(line);
 if (m.cmd === 'load') { vts.loadModel({ name: m.name, inputs: m.inputs ?? PACK_PARAMS }); console.log(JSON.stringify({ ok: true })); }
 if (m.cmd === 'stats') {
  const frames = vts.frames.slice(m.since ?? 0);
  const peak: Record<string, number> = {};
  const range: Record<string, [number, number]> = {};
  for (const f of frames) for (const p of f.parameterValues) {
   peak[p.id] = Math.max(peak[p.id] ?? 0, Math.abs(p.value));
   const r = range[p.id] ?? [p.value, p.value];
   range[p.id] = [Math.min(r[0], p.value), Math.max(r[1], p.value)];
  }
  const active: Record<string, string[]> = {};
  for (const [model] of vts.active) active[model] = vts.activeOn(model);
  console.log(JSON.stringify({ frames: frames.length, total: vts.frames.length,
   models: [...new Set(frames.map(f => f.model))], ids: Object.keys(peak), peak, range, active }));
 }
}).on('close', () => { void vts.close().then(() => process.exit(0)); });
