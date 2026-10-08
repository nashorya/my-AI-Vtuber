import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { adapt, calibrate } from '../adapt.ts';
import { DEFAULT_PROFILE, toWire } from '../upstream/models/index.ts';

const near = (actual: number, expected: number) => assert.ok(Math.abs(actual - expected) < 1e-4, `${actual} ≈ ${expected}`);
const range = (il: number, iu: number, ol: number, ou: number) =>
 ({ InputRangeLower: il, InputRangeUpper: iu, OutputRangeLower: ol, OutputRangeUpper: ou });

test('calibration follows LIVE2D-ADAPTATION §2.2', () => {
 // Head: the model reaches the degrees the pack asks for, whatever its input range.
 assert.deepEqual(calibrate('FaceAngleX', range(-30, 30, -30, 30)), { scale: 1 });
 assert.deepEqual(calibrate('FaceAngleY', range(-15, 15, -30, 30)), { scale: 0.5 });
 // The document's own example: EyeOpenLeft input [0.2,1] → output [0,1.5].
 const eyeOpen = calibrate('EyeOpenLeft', range(0.2, 1, 0, 1.5))!;
 near(eyeOpen.neutral!, 0.7333); near(eyeOpen.scale!, 0.5333); assert.deepEqual(eyeOpen.clamp, [0.2, 1]);
 // MouthSmile on VTS's usual [0,1] → [-1,1] wiring equals the generic mapping.
 assert.deepEqual(calibrate('MouthSmile', range(0, 1, -1, 1)), { neutral: 0.5, scale: 0.5, clamp: [0, 1] });
 assert.deepEqual(calibrate('EyeRightX', range(-1, 1, 1, -1)), { invert: true });
 assert.deepEqual(calibrate('EyeRightX', range(-1, 1, -1, 1)), {});
 assert.equal(calibrate('FaceAngleX', { Input: 'FaceAngleX' }), null); // no ranges: caller falls back
});

test('a model without a profile is driven from its own file at full strength', () => {
 const root = mkdtempSync(join(tmpdir(), 'adapt-'));
 try {
  const dir = join(root, 'Cat');
  mkdirSync(join(dir, 'motions'), { recursive: true });
  writeFileSync(join(dir, 'Cat.vtube.json'), JSON.stringify({
   Name: 'Cat', FileReferences: { IdleAnimation: 'motions/idle.motion3.json' },
   ParameterSettings: [
    { Input: 'FaceAngleX', OutputLive2D: 'ParamBodyAngleX', ...range(-30, 30, -10, 10) },
    { Input: 'FaceAngleX', OutputLive2D: 'ParamAngleX', ...range(-30, 30, -30, 30) },
    { Input: 'FaceAngleY', OutputLive2D: 'ParamAngleY', ...range(-20, 20, -30, 30) },
    { Input: 'MouthOpen', OutputLive2D: 'ParamMouthOpenY', ...range(0, 1, 0, 1) },
    { Input: 'Brows', OutputLive2D: 'ParamBrowLY', ...range(0, 1, -1, 1) },
   ] }));
  writeFileSync(join(dir, 'motions', 'idle.motion3.json'), JSON.stringify({ Curves: [{ Id: 'ParamEyeLOpen' }] }));
  const known = new Set(['FaceAngleX', 'FaceAngleY', 'FaceAngleZ', 'MouthOpen', 'BrowLeftY', 'BrowRightY', 'Brows']);
  const result = adapt({ resolution: { profile: DEFAULT_PROFILE, how: 'fallback', source: null }, configured: 'auto',
   modelName: 'Cat', known, packParams: ['FaceAngleX', 'FaceAngleY', 'FaceAngleZ', 'MouthOpen', 'BrowLeftY'], live2dDir: root });
  assert.equal(result.mode, 'auto');
  assert.deepEqual(result.driven, ['FaceAngleX', 'FaceAngleY', 'MouthOpen', 'BrowLeftY']);
  assert.deepEqual(result.skipped.map(s => s.param), ['FaceAngleZ']);
  // The head entry wins over the body entry on the same input; no blanket damping.
  assert.equal(toWire(result.profile, 'FaceAngleX', 20)!.value, 20);
  // A narrower input range means a smaller input for the same head angle.
  near(toWire(result.profile, 'FaceAngleY', 30)!.value, 20);
  assert.deepEqual(toWire(result.profile, 'BrowLeftY', 0)!, { targets: ['Brows'], value: 0.5 });
  assert.equal(result.profile.idleBlinks, true); // the idle animation blinks by itself
 } finally { rmSync(root, { recursive: true, force: true }); }
});
