import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const directory = await mkdtemp(path.join(root, 'artifacts', 'auth-probe-'));
const token = randomBytes(32).toString('hex');
const child = spawn(process.execPath, [fileURLToPath(new URL('../server.mjs', import.meta.url))], { cwd: directory, windowsHide: true, stdio: ['pipe','pipe','pipe'] });
child.stderr.on('data', () => {});
try {
  const ready = await new Promise((resolve, reject) => {
    let buffer = ''; const timeout = setTimeout(() => reject(new Error('startup timeout')), 15000);
    child.once('exit', () => { clearTimeout(timeout); reject(new Error('gateway exited')); });
    child.stdout.on('data', chunk => { buffer += chunk; if (buffer.includes('\n')) { clearTimeout(timeout); try { resolve(JSON.parse(buffer.split('\n')[0])); } catch { reject(new Error('handshake invalid')); } } });
    child.stdin.write(JSON.stringify({ token, port: 0, dataDirectory: directory }) + '\n');
  });
  const send = async (route, body) => {
    const response = await fetch(`http://127.0.0.1:${ready.port}/v1/auth/${route}`, { method: 'POST', headers: { 'X-Neko-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal: AbortSignal.timeout(20000) });
    const result = await response.json(); if (!response.ok) throw new Error(result.error?.message || 'request failed'); return result;
  };
  for (const providerId of ['netease','qq','kugou']) {
    let stage = 'start';
    try {
      const login = await send('start', { providerId });
      stage = 'check';
      const result = await send('check', { providerId, loginId: login.loginId });
      await send('cancel', { providerId, loginId: login.loginId });
      const image = Buffer.from(login.qrImage.split(',')[1], 'base64');
      const png = image.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10]));
      console.log(JSON.stringify({ providerId, qrBytes: image.length, png, width: png ? image.readUInt32BE(16) : null, height: png ? image.readUInt32BE(20) : null, state: result.state, cancelled: true }));
    } catch (error) { console.log(JSON.stringify({ providerId, stage, error: error.message })); }
  }
} finally {
  child.kill();
  if (child.exitCode === null && child.signalCode === null) await new Promise(resolve => child.once('exit', resolve));
  if (directory.startsWith(path.join(root, 'artifacts') + path.sep)) await rm(directory, { recursive: true, force: true });
}
