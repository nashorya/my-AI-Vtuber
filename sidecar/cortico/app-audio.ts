/**
 * App-owned audio for the unmodified upstream Performer. The app synthesizes and plays every
 * piece; this bridge turns its reports into the PerformerTts.synthStream / AudioSink.beginStream
 * contracts, so upstream pacing, lip-sync, accents and anchors run on the app's real timeline.
 */
import type { AudioSink, AudioStreamSession, PerformerTts } from './upstream/orchestrator.ts';
import { StreamingEnvelope, type TtsPiece, type TtsStreamSink } from './upstream/tts.ts';

type Send = (message: object) => void;

interface Unit {
 pieceId: number; text: string; sink: TtsStreamSink;
 sampleRate: number; envelope: StreamingEnvelope | null; bytes: number; carry: number | null;
 synthDone: boolean; failed: boolean; played: boolean;
 resolve: (piece: TtsPiece) => void; reject: (error: Error) => void;
 started?: (t: number) => void; ended?: (t: number) => void;
}

const toFloat = (pcm: Uint8Array) => {
 const view = new DataView(pcm.buffer, pcm.byteOffset, pcm.byteLength);
 const out = new Float32Array(pcm.byteLength / 2);
 for (let i = 0; i < out.length; i++) out[i] = view.getInt16(i * 2, true) / 32768;
 return out;
};

export class AppAudioBridge {
 private seq = 0;
 private requestId = 0;
 private readonly units = new Map<number, Unit>();
 private readonly order: number[] = [];
 /** Set while the host is being cut for a model switch: the app keeps its current piece. */
 suppressStop = false;

 constructor(private readonly send: Send, private readonly now: () => number = Date.now) {}

 /** A new app turn: reports for earlier turns are ignored from now on. */
 beginTurn(requestId: number): void {
  this.reset();
  this.requestId = requestId;
  this.suppressStop = false;
 }

 /** Releases every waiter (upstream must never hang on a piece the app will not report). */
 reset(): void {
  const t = this.now();
  for (const u of this.units.values()) {
   if (!u.synthDone) { u.synthDone = true; u.reject(new Error('Cancelled')); }
   u.started?.(t); u.ended?.(t);
  }
  this.units.clear();
  this.order.length = 0;
 }

 readonly tts: PerformerTts = {
  synth: async () => { throw new Error('Whole-piece synthesis is not used: audio is app-owned'); },
  synthStream: (text, sink, signal) => new Promise<TtsPiece>((resolve, reject) => {
   if (signal.aborted) { reject(new Error('Cancelled')); return; }
   const pieceId = ++this.seq;
   const unit: Unit = { pieceId, text, sink, sampleRate: 0, envelope: null, bytes: 0, carry: null,
    synthDone: false, failed: false, played: false, resolve, reject };
   this.units.set(pieceId, unit);
   this.order.push(pieceId);
   signal.addEventListener('abort', () => {
    if (unit.synthDone) return;
    unit.synthDone = true; unit.failed = true;
    this.drop(pieceId);
    this.send({ kind: 'cancelSynth', requestId: this.requestId, pieceId });
    reject(new Error('Cancelled'));
   }, { once: true });
   this.send({ kind: 'synth', requestId: this.requestId, pieceId, text });
  }),
 };

 readonly audio: AudioSink = {
  play: async () => { throw new Error('Whole-piece playback is not used: audio is app-owned'); },
  beginStream: (_sampleRate, text) => this.beginStream(text),
  // The app owns its player; per-piece stops go through the session's abort.
  stop: () => {},
 };

 private beginStream(text: string): AudioStreamSession {
  let started!: (t: number) => void; let ended!: (t: number) => void;
  const session: AudioStreamSession = {
   started: new Promise<number>(r => { started = r; }),
   ended: new Promise<number>(r => { ended = r; }),
   push() {}, end() {}, abort() {},
  };
  // Upstream plays segments in synthesis order and skips failed ones.
  const pieceId = this.order.find(id => { const u = this.units.get(id); return !!u && !u.played && !u.failed && u.text === text; });
  if (pieceId === undefined) { const t = this.now(); started(t); ended(t); return session; }
  const unit = this.units.get(pieceId)!;
  unit.played = true; unit.started = started; unit.ended = ended;
  session.abort = () => {
   if (!this.suppressStop) this.send({ kind: 'stop', requestId: this.requestId, pieceId });
   const t = this.now(); started(t); ended(t);
   this.drop(pieceId);
  };
  this.send({ kind: 'play', requestId: this.requestId, pieceId });
  return session;
 }

 /** App → host report. Reports for another turn or an unknown piece are ignored. */
 onMessage(m: any): void {
  if (m.requestId !== this.requestId) return;
  const u = this.units.get(m.pieceId);
  if (!u) return;
  switch (m.kind) {
   case 'pcm': {
    let bytes: Uint8Array = Buffer.from(String(m.data), 'base64');
    if (u.carry !== null) { const merged = new Uint8Array(bytes.length + 1); merged[0] = u.carry; merged.set(bytes, 1); bytes = merged; u.carry = null; }
    if (bytes.length % 2) { u.carry = bytes[bytes.length - 1]; bytes = bytes.subarray(0, bytes.length - 1); }
    if (!u.envelope) {
     u.sampleRate = Number(m.sampleRate) || 24000;
     u.envelope = new StreamingEnvelope(u.sampleRate);
     u.sink.begin?.({ sampleRate: u.sampleRate, envelope: u.envelope });
    }
    if (bytes.length === 0) return;
    u.envelope.append(toFloat(bytes));
    u.bytes += bytes.length;
    u.sink.pcm(bytes);
    return;
   }
   case 'synthEnd': {
    if (u.synthDone) return;
    if (!u.envelope) { u.sampleRate = 24000; u.envelope = new StreamingEnvelope(u.sampleRate); }
    u.envelope.finish();
    u.synthDone = true;
    u.resolve({ text: u.text, wav: new Uint8Array(0), envelope: u.envelope,
     durationMs: u.bytes / 2 / u.sampleRate * 1000 });
    return;
   }
   case 'synthError':
    if (u.synthDone) return;
    u.synthDone = true; u.failed = true;
    this.drop(u.pieceId);
    u.reject(new Error(String(m.message || 'TTS failed')));
    return;
   case 'started': u.started?.(this.now()); return;
   case 'ended': case 'stopped': {
    const t = this.now(); u.started?.(t); u.ended?.(t);
    this.drop(u.pieceId);
    return;
   }
  }
 }

 private drop(pieceId: number): void {
  this.units.delete(pieceId);
  const at = this.order.indexOf(pieceId);
  if (at >= 0) this.order.splice(at, 1);
 }
}
