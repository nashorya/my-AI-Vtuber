/**
 * Per-model adaptation for the host (ours, not upstream). Decides which semantic parameters of
 * the performance pack may be driven on the model that VTS currently has loaded.
 *
 * - A dedicated profile (configured for, or matched by name to, the loaded model) keeps its own
 *   mapping, range, direction, neutral and strength. Parameters whose targets the model does not
 *   have are added to `unsupported`, so upstream skips that motion instead of sending an input
 *   the model lacks.
 * - Without a dedicated profile, the mapping is derived from the model file itself: only inputs
 *   its `.vtube.json` ParameterSettings shows as connected are driven, each calibrated from that
 *   entry's input/output ranges (upstream LIVE2D-ADAPTATION.md §2.2), and idle blinks follow its
 *   idle animation (§2.3). Expression files are not guessed. Without a readable model file only
 *   the mouth is driven. Another rig's profile is never applied to this model.
 *
 * VTS's API lists every default tracking input for any model, so the API list alone cannot
 * prove that an input moves anything; the model file is the only local evidence.
 */
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { DEFAULT_PROFILE, toWire, type ModelProfile } from './upstream/models/index.ts';

const MOUTH = 'MouthOpen';

export interface Skipped { param: string; reason: string }

export interface Adaptation {
  profile: ModelProfile;
  mode: 'dedicated' | 'auto';
  modelFile: string | null;
  driven: string[];
  skipped: Skipped[];
  warnings: string[];
}

/** One `ParameterSettings` entry of a `.vtube.json`. */
export interface ParamSetting {
  Input?: string; OutputLive2D?: string;
  InputRangeLower?: number; InputRangeUpper?: number; OutputRangeLower?: number; OutputRangeUpper?: number;
}

export interface ModelFile {
  file: string;
  wiredInputs: Set<string>;
  settings: ParamSetting[];
  /** Whether the idle animation blinks by itself; null when that cannot be read. */
  idleBlinks: boolean | null;
}

interface ParsedModelFile { name?: string; wired: Set<string>; settings: ParamSetting[]; idleBlinks: boolean | null }

function readModelFile(file: string): ParsedModelFile | null {
  try {
    const json = JSON.parse(readFileSync(file, 'utf8')) as {
      Name?: string; ParameterSettings?: ParamSetting[]; FileReferences?: { IdleAnimation?: string };
    };
    const settings = (json.ParameterSettings ?? []).filter(p => p.Input && p.OutputLive2D);
    const wired = new Set(settings.map(p => p.Input!));
    return { name: json.Name, wired, settings, idleBlinks: idleAnimationBlinks(file, json.FileReferences?.IdleAnimation) };
  } catch {
    return null;
  }
}

/** §2.3: does the idle `.motion3.json` (searched by file name under the model folder) drive the eyelids? */
function idleAnimationBlinks(modelFile: string, idle: string | undefined): boolean | null {
  if (!idle) return false;
  const wanted = basename(idle);
  const find = (dir: string, depth: number): string | null => {
    let entries: string[] = [];
    try { entries = readdirSync(dir); } catch { return null; }
    if (entries.includes(wanted)) return join(dir, wanted);
    if (depth === 0) return null;
    for (const entry of entries) {
      const sub = join(dir, entry);
      try { if (!statSync(sub).isDirectory()) continue; } catch { continue; }
      const hit = find(sub, depth - 1);
      if (hit) return hit;
    }
    return null;
  };
  const motion = find(dirname(modelFile), 3);
  if (!motion) return null;
  try {
    const curves = (JSON.parse(readFileSync(motion, 'utf8')) as { Curves?: Array<{ Id?: string }> }).Curves ?? [];
    return curves.some(c => c.Id === 'ParamEyeLOpen' || c.Id === 'ParamEyeROpen');
  } catch {
    return null;
  }
}

/** The Live2D output each pack input normally drives; preferred when an input drives several. */
const PRIMARY_OUTPUT: Record<string, string> = {
  FaceAngleX: 'ParamAngleX', FaceAngleY: 'ParamAngleY', FaceAngleZ: 'ParamAngleZ',
  MouthOpen: 'ParamMouthOpenY', MouthSmile: 'ParamMouthForm', CheekPuff: 'ParamCheek',
  EyeOpenLeft: 'ParamEyeLOpen', EyeOpenRight: 'ParamEyeROpen',
  EyeLeftX: 'ParamEyeBallX', EyeRightX: 'ParamEyeBallX', EyeLeftY: 'ParamEyeBallY', EyeRightY: 'ParamEyeBallY',
  BrowLeftY: 'ParamBrowLY', BrowRightY: 'ParamBrowRY',
};

/**
 * §2.2: the wiring that turns the pack's semantic value into this model's input, so the model
 * reaches the output its author set up (VTS maps input range → output range itself). Null when
 * the entry has no usable ranges; the caller then uses the generic mapping.
 */
export function calibrate(param: string, entry: ParamSetting): NonNullable<ModelProfile['wiring'][string]> | null {
  const { InputRangeLower: il, InputRangeUpper: iu, OutputRangeLower: ol, OutputRangeUpper: ou } = entry;
  if (eye(param)) {
    // Gaze is already ±1 on both sides; only a flipped output range needs a sign change.
    if (typeof ol !== 'number' || typeof ou !== 'number') return null;
    return ol > ou ? { invert: true } : {};
  }
  if (![il, iu, ol, ou].every(v => typeof v === 'number' && Number.isFinite(v)) || iu === il) return null;
  const gain = (ou! - ol!) / (iu! - il!);
  if (gain === 0) return null;
  const atZero = ol! - il! * gain;
  const scale = (1 / gain);
  const clamp: [number, number] = [Math.min(il!, iu!), Math.max(il!, iu!)];
  if (param.startsWith('FaceAngle')) return { scale };
  if (param === MOUTH || param === 'CheekPuff') return { neutral: -atZero / gain, scale, clamp };
  if (param.startsWith('EyeOpen')) return { neutral: (1 - atZero) / gain, scale, clamp };
  // MouthSmile and brows: 0 is the Live2D neutral output 0.
  return { neutral: -atZero / gain, scale, clamp };
}

const eye = (param: string) => /^Eye(Left|Right)[XY]$/.test(param);

function settingFor(settings: ParamSetting[], input: string, param: string): ParamSetting | undefined {
  const all = settings.filter(p => p.Input === input);
  return all.find(p => p.OutputLive2D === PRIMARY_OUTPUT[param]) ?? all[0];
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
    if (parsed && parsed.name === modelName)
      return { file, wiredInputs: parsed.wired, settings: parsed.settings, idleBlinks: parsed.idleBlinks };
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
    warnings.push(`配置的档案「${resolution.profile.id}」属于模型「${resolution.profile.vtsModelName}」，与当前模型「${modelName}」不符，已改用自动适配`);
  if (resolution.how === 'missing')
    warnings.push(`配置的档案「${input.configured}」不存在，已改用自动适配`);

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
  const guessed: string[] = [];
  for (const param of packParams) {
    let input = param;
    let why: string | null;
    if (!modelFile && param !== MOUTH) why = '没有专用档案，也找不到模型文件确认接线';
    else why = reach(DEFAULT_PROFILE, param);
    // §2.2 merged input: one "Brows" entry drives both brows.
    if (why && modelFile && param.startsWith('Brow') && modelFile.wiredInputs.has('Brows') &&
        (known.size === 0 || known.has('Brows'))) { input = 'Brows'; why = null; }
    if (why) { skipped.push({ param, reason: why }); unsupported.push(param); continue; }
    driven.push(param);
    const entry = modelFile ? settingFor(modelFile.settings, input, param) : undefined;
    const fitted = entry ? calibrate(param, entry) : null;
    if (!fitted && modelFile) guessed.push(param);
    wiring[param] = { ...(fitted ?? DEFAULT_PROFILE.wiring[param] ?? {}), ...(input !== param ? { aliasTo: [input] } : {}) };
  }
  if (!modelFile) warnings.push('没有专用档案且找不到模型文件：只驱动口型，其余动作未适配');
  else warnings.push('自动适配：按模型文件的接线和输入输出区间换算幅度，不使用表情特效；特效需为该模型编写 cortico.profile.json');
  if (guessed.length > 0) warnings.push(`模型文件没有写区间，按通用接线驱动：${guessed.join('、')}`);
  const profile: ModelProfile = {
    ...DEFAULT_PROFILE,
    id: 'AIVTuber-Auto',
    label: `自动适配（${modelName || '未知模型'}）`,
    vtsModelName: modelName,
    wiring,
    unsupported,
    fx: {},
    keepExpressions: [],
    // §2.3. When the idle animation cannot be read, leave eyelids to the model: taking them
    // over unseen could double-blink or freeze the eyes.
    idleBlinks: modelFile?.idleBlinks ?? true,
  };
  return { profile, mode: 'auto', modelFile: modelFile?.file ?? null, driven, skipped, warnings };
}
