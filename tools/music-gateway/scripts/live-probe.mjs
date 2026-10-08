import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const token = randomBytes(32).toString('base64url');
const dataDirectory = await mkdtemp(path.join(tmpdir(), 'neko-gateway-probe-'));
const child = spawn(process.execPath, [path.join(root, 'server.mjs')], { cwd: root, stdio: ['pipe','pipe','pipe'] });
child.stdin.write(JSON.stringify({ token, dataDirectory, port: 0 }) + '\n');
const handshake = await new Promise((resolve, reject) => {
  let buffer = ''; const timer = setTimeout(() => reject(new Error('startup_timeout')), 15000);
  child.stdout.on('data', chunk => { buffer += chunk; const end = buffer.indexOf('\n'); if (end >= 0) { clearTimeout(timer); resolve(JSON.parse(buffer.slice(0,end))); } });
  child.once('exit', () => { clearTimeout(timer); reject(new Error('startup_failed')); });
});
const call = async (route, payload) => {
  const started = performance.now();
  const r = await fetch(`http://127.0.0.1:${handshake.port}/v1/${route}`, { method: 'POST', headers: { 'X-Neko-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify(payload), signal: AbortSignal.timeout(15000) });
  return { http: r.status, body: await r.json(), elapsedMs: Math.round(performance.now() - started) };
};
try {
  for (const providerId of ['netease','qq','kuwo','kugou','qishui']) {
    const search = await call('search', { providerId, query: '夜雨', page: 1, pageSize: 5 });
    const result = { providerId, search: { http: search.http, count: search.body.tracks?.length, code: search.body.error?.code, elapsedMs: search.elapsedMs } };
    const first = search.body.tracks?.[0];
    if (first) {
      const payload = { providerId, providerTrackId: first.providerTrackId, metadata: JSON.parse(first.providerMetadataJson) };
      const lyric = await call('lyrics', payload); result.lyrics = { http: lyric.http, length: lyric.body.lrc?.length, code: lyric.body.error?.code, elapsedMs: lyric.elapsedMs };
      const resolved = await call('resolve', payload); result.resolve = { http: resolved.http, availability: resolved.body.availability, durationSeconds: resolved.body.durationSeconds, canSeek: resolved.body.canSeek, code: resolved.body.error?.code, elapsedMs: resolved.elapsedMs };
    }
    process.stdout.write(JSON.stringify(result) + '\n');
  }
} finally { child.kill(); }
