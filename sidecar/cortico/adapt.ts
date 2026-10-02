/**
 * Per-model adaptation for the host (ours, not upstream). Decides which semantic parameters of
 * the performance pack may be driven on the model that VTS currently has loaded.
 *
 * - A dedicated profile (configured for, or matched by name to, the loaded model) keeps its own
 *   mapping, range, direction, neutral and strength. Parameters whose targets the model does not
 *   have are added to `unsupported`, so upstream skips that motion instead of sending an input
 *   the model lacks.
 * - Without a dedicated profile, a conservative adaptation is used: only inputs the model file
 *   (`.vtube.json` ParameterSettings) shows as connected are driven, at reduced strength, with
 *   no expression files. Without a readable model file only the mouth is driven. Another rig's
 *   profile is never applied to this model.
 *
 * VTS's API lists every default tracking input for any model, so the API list alone cannot
 * prove that an input moves anything; the model file is the only local evidence.
 */
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { DEFAULT_PROFILE, toWire, type ModelProfile } from './upstream/models/index.ts';

/** Strength of conservatively adapted motion relative to the generic mapping. */
export const CONSERVATIVE_STRENGTH = 0.6;
const MOUTH = 'MouthOpen';

export interface Skipped { param: string; reason: string }

export interface Adaptation {
  profile: ModelProfile;
  mode: 'dedicated' | 'conservative';
  modelFile: string | null;
  driven: string[];
  skipped: Skipped[];
  warnings: string[];
}

export interface ModelFile { file: string; wiredInputs: Set<string> }

function readModelFile(file: string): { name?: string; wired: Set<string> } | null {
  try {
    const json = JSON.parse(readFileSync(file, 'utf8')) as {
      Name?: string; ParameterSettings?: Array<{ Input?: string; OutputLive2D?: string }>;
    };
    const wired = new Set<string>();
    for (const p of json.ParameterSettings ?? []) if (p.Input && p.OutputLive2D) wired.add(p.Input);
    return { name: json.Name, wired };
  } catch {
    return null;
  }
}

function vtubeFilesIn(dir: string): string[] {
  try { return readdirSync(dir).filter(f => f.endsWith('.vtube.json')).map(f => join(dir, f)); }
  catch { return []; }
}

/** Finds the loaded model's `.vtube.json`: first in `preferredDir`, then by `Name` under live2dDir. */
export function findModelFile(live2dDir: string, modelName: string, preferredDir?: string | null): ModelFile | null {
  const candidates: string[] = [];
  if (preferredDir) candidates.push(...vtubeFilesIn(preferredDir));
  const root = live2dDir.trim();
  if (root && existsSync(root)) {
    let entries: string[] = [];
    try { entries = readdirSync(root).sort(); } catch { /* unreadable root */ }
    for (const entry of entries) {
      const dir = join(root, entry);
      try { if (!statSync(dir).isDirectory()) continue; } catch { continue; }
      candidates.push(...vtubeFilesIn(dir));
    }
  }
  for (const file of candidates) {
    const parsed = readModelFile(file);
    if (parsed && parsed.name === modelName) return { file, wiredInputs: parsed.wired };
  }
  return null;
}

export interface AdaptInput {
  /** Result of upstream resolveProfile. */
  resolution: { profile: ModelProfile; how: 'configured' | 'matched' | 'fallback' | 'missing'; source: { dir: string } | null };
  configured: string;
  modelName: string;
  /** Input names VTS reports for the loaded model. */
  known: Set<string>;
  packParams: readonly string[];
  live2dDir: string;
}

export function adapt(input: AdaptInput): Adaptation {
  const { resolution, modelName, known, packParams } = input;
  const warnings: string[] = [];
  const dedicated =
    (resolution.how === 'configured' && resolution.profile.vtsModelName === modelName) ||
    resolution.how === 'matched';
  if (resolution.how === 'configured' && !dedicated)
    warnings.push(`配置的档案「${resolution.profile.id}」属于模型「${resolution.profile.vtsModelName}」，与当前模型「${modelName}」不符，已改用保守适配`);
  if (resolution.how === 'missing')
    warnings.push(`配置的档案「${input.configured}」不存在，已改用保守适配`);

  const modelFile = findModelFile(input.live2dDir, modelName, dedicated ? resolution.source?.dir : null);
  const skipped: Skipped[] = [];
  const driven: string[] = [];

  const reach = (profile: ModelProfile, param: string): string | null => {
    const wired = toWire(profile, param, 0);
    if (!wired) return '档案标记为此模型演不出';
    const present = wired.targets.filter(t =>
      (known.size === 0 || known.has(t)) && (!modelFile || modelFile.wiredInputs.has(t)));
    if (present.length > 0) return null;
    return modelFile ? `模型文件里没有接线（${wired.targets.join('/')}）` : `VTS 没有这个输入（${wired.targets.join('/')}）`;
  };

  if (dedicated) {
    const base = resolution.profile;
    const unsupported = new Set(base.unsupported);
    for (const param of packParams) {
      const why = reach(base, param);
      if (why) { skipped.push({ param, reason: why }); unsupported.add(param); }
      else driven.push(param);
    }
    if (!modelFile) warnings.push('找不到当前模型的 .vtube.json，未能按模型文件核对接线');
    return { profile: { ...base, unsupported: [...unsupported] }, mode: 'dedicated',
      modelFile: modelFile?.file ?? null, driven, skipped, warnings };
  }

  const wiring: Record<string, NonNullable<ModelProfile['wiring'][string]>> = {};
  const unsupported: string[] = [];
  for (const param of packParams) {
    let why: string | null;
    if (!modelFile && param !== MOUTH) why = '没有专用档案，也找不到模型文件确认接线';
    else why = reach(DEFAULT_PROFILE, param);
    if (why) { skipped.push({ param, reason: why }); unsupported.push(param); continue; }
    driven.push(param);
    const generic = DEFAULT_PROFILE.wiring[param] ?? {};
    wiring[param] = param === MOUTH ? { ...generic } : { ...generic, scale: (generic.scale ?? 1) * CONSERVATIVE_STRENGTH };
  }
  if (!modelFile) warnings.push('没有专用档案且找不到模型文件：只驱动口型，其余动作未适配');
  warnings.push('保守适配：未校准的模型，动作幅度降低、不使用表情特效；精细效果需为该模型编写 cortico.profile.json');
  const profile: ModelProfile = {
    ...DEFAULT_PROFILE,
    id: 'AIVTuber-Conservative',
    label: `保守适配（${modelName || '未知模型'}）`,
    vtsModelName: modelName,
    wiring,
    unsupported,
    fx: {},
    keepExpressions: [],
    // Leave eyelids to the model's own idle animation: without calibration we cannot know
    // whether taking them over would double-blink or freeze the eyes.
    idleBlinks: true,
  };
  return { profile, mode: 'conservative', modelFile: modelFile?.file ?? null, driven, skipped, warnings };
}
