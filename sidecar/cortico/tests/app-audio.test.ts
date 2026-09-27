import test from 'node:test';
import assert from 'node:assert/strict';
import { AppAudioBridge } from '../app-audio.ts';

const pcm = (samples: number[]) => { const b = Buffer.alloc(samples.length * 2); samples.forEach((s, i) => b.writeInt16LE(s, i * 2)); return b; };
const setup = () => {
 const sent: any[] = []; let now = 1000;
 const bridge = new AppAudioBridge(m => sent.push(m), () => now);
 bridge.beginTurn(7);
 return { sent, bridge, tick: (ms: number) => { now += ms; } };
};

test('synthStream asks the app, builds the envelope from app PCM and resolves on synthEnd', async () => {
 const { sent, bridge } = setup();
 const chunks: Uint8Array[] = []; let began = 0;
 const piece = bridge.tts.synthStream!('你好', { begin: () => { began++; }, pcm: c => chunks.push(c) }, new AbortController().signal);
 assert.deepEqual(sent[0], { kind: 'synth', requestId: 7, pieceId: 1, text: '你好' });
 // 1600 samples at 16 kHz = 100 ms = 5 envelope hops of 20 ms (a single hop cannot show a
 // non-zero value at 5 ms: StreamingEnvelope.at(ms) is 0 whenever ceil(ms/20) >= hopCount).
 bridge.onMessage({ kind: 'pcm', requestId: 7, pieceId: 1, sampleRate: 16000, data: pcm(Array(1600).fill(3000)).toString('base64') });
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 });
 const result = await piece;
 assert.equal(began, 1);
 assert.equal(chunks.length, 1);
 assert.equal(Math.round(result.durationMs), 100);
 assert.ok(result.envelope.at(20) > 0, 'envelope follows app audio');
});

test('odd-length chunks are re-aligned to PCM16 before reaching the envelope', async () => {
 const { bridge } = setup();
 const got: Uint8Array[] = [];
 const piece = bridge.tts.synthStream!('嗯', { pcm: c => got.push(c) }, new AbortController().signal);
 const whole = pcm([1000, 2000, 3000]);
 bridge.onMessage({ kind: 'pcm', requestId: 7, pieceId: 1, sampleRate: 16000, data: whole.subarray(0, 3).toString('base64') });
 bridge.onMessage({ kind: 'pcm', requestId: 7, pieceId: 1, sampleRate: 16000, data: whole.subarray(3).toString('base64') });
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 });
 await piece;
 assert.ok(got.every(c => c.length % 2 === 0));
 assert.equal(got.reduce((n, c) => n + c.length, 0), 6);
});

test('beginStream sends play and its session follows started / ended', async () => {
 const { sent, bridge, tick } = setup();
 const piece = bridge.tts.synthStream!('你好', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 }); await piece;
 const session = bridge.audio.beginStream(16000, '你好');
 assert.deepEqual(sent.at(-1), { kind: 'play', requestId: 7, pieceId: 1 });
 tick(30); bridge.onMessage({ kind: 'started', requestId: 7, pieceId: 1 });
 assert.equal(await session.started, 1030);
 tick(500); bridge.onMessage({ kind: 'ended', requestId: 7, pieceId: 1 });
 assert.equal(await session.ended, 1530);
});

test('stopped before started resolves both, so upstream never waits forever', async () => {
 const { bridge } = setup();
 const piece = bridge.tts.synthStream!('你好', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 1 }); await piece;
 const session = bridge.audio.beginStream(16000, '你好');
 bridge.onMessage({ kind: 'stopped', requestId: 7, pieceId: 1 });
 await session.started; await session.ended;
});

test('abort sends cancelSynth; session abort sends stop; stale turn reports are ignored', async () => {
 const { sent, bridge } = setup();
 const ac = new AbortController();
 const piece = bridge.tts.synthStream!('取消我', { pcm() {} }, ac.signal);
 ac.abort();
 await assert.rejects(piece);
 assert.deepEqual(sent.at(-1), { kind: 'cancelSynth', requestId: 7, pieceId: 1 });
 const second = bridge.tts.synthStream!('第二片', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthEnd', requestId: 6, pieceId: 2 }); // older turn: ignored
 bridge.onMessage({ kind: 'synthEnd', requestId: 7, pieceId: 2 }); await second;
 bridge.audio.beginStream(16000, '第二片').abort();
 assert.deepEqual(sent.at(-1), { kind: 'stop', requestId: 7, pieceId: 2 });
});

test('synthError rejects so upstream skips the piece', async () => {
 const { bridge } = setup();
 const piece = bridge.tts.synthStream!('坏片', { pcm() {} }, new AbortController().signal);
 bridge.onMessage({ kind: 'synthError', requestId: 7, pieceId: 1, message: 'boom' });
 await assert.rejects(piece, /boom/);
});
