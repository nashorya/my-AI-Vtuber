/** Action-only preview: no LLM, TTS request, or model download. */
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const argv = process.argv.slice(2);
const flag = (name: string, fallback = '') => { const i = argv.indexOf(name); return i < 0 ? fallback : argv[i+1] || fallback; };
const configFile = resolve(flag('--config', '../../cortico.json'));
const config = JSON.parse(readFileSync(configFile,'utf8'));
const script = flag('--script', '【微笑,看向镜头】【点头】【歪头】【眨单眼】【Reset】');
const child = spawn(process.execPath, ['--import','tsx','host.ts'], {
 cwd: dirname(fileURLToPath(import.meta.url)), stdio: ['pipe','pipe','inherit'],
});
let seq = 0;
const pending = new Map<number, { resolve: (x: any) => void; reject: (e: Error) => void }>();
const send = (message: unknown) => child.stdin.write(JSON.stringify(message)+'\n');
const command = (command: string, extra: object) => new Promise<any>((resolveReply,reject) => {
 const id = ++seq; pending.set(id,{resolve:resolveReply,reject}); send({id,command,...extra});
});
createInterface({input:child.stdout}).on('line', line => {
 const m = JSON.parse(line);
 if (m.kind === 'result') {
  const item = pending.get(m.id); pending.delete(m.id);
  if (m.error) item?.reject(new Error(m.error)); else item?.resolve(m);
 } else if (m.kind === 'tts') send({ kind: 'reply', id: m.id, error: 'Action-only preview: use tags without speech' });
 else if (m.kind === 'status' && m.model) console.log(m);
});
child.on('exit', () => { for (const p of pending.values()) p.reject(new Error('Preview host exited')); });
const watchdog = setTimeout(() => { child.kill(); }, 90000);
try {
 await command('init', {config: {...config, audioDevice:'none',
  vtsUrl: flag('--vts', 'ws://127.0.0.1:8001'), tokenPath: resolve(dirname(configFile),'.cortico-vts-token')}});
 if ((await command('prepare',{script})).spoken.trim()) throw new Error('Preview accepts action tags only; use the app to test speech.');
 await command('perform',{script});
 console.log('Action sequence completed; confirm the actual appearance in VTS.');
} finally { clearTimeout(watchdog); child.stdin.end(); }
