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
