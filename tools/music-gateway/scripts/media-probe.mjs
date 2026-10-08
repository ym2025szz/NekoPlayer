// Diagnostic only: never logs media URLs, token values, headers, cookies or raw stderr.
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const token = randomBytes(32).toString('base64url');
const dataDirectory = await mkdtemp(path.join(tmpdir(), 'neko-media-probe-'));
const child = spawn(process.execPath, [path.join(root, 'server.mjs')], { cwd: root, stdio: ['pipe','pipe','pipe'] });
child.stdin.write(JSON.stringify({ token, dataDirectory, port: 0 }) + '\n');
const handshake = await new Promise((resolve, reject) => {
  let buffer = ''; const timer = setTimeout(() => reject(new Error('startup_timeout')), 15000);
  child.stdout.on('data', chunk => { buffer += chunk; const end = buffer.indexOf('\n'); if (end >= 0) { clearTimeout(timer); resolve(JSON.parse(buffer.slice(0,end))); } });
  child.once('exit', () => { clearTimeout(timer); reject(new Error('startup_failed')); });
});

function sanitized(value) {
  return value.replace(/https?:\/\/[^\s"'<>]+/gi, '[media-url]')
    .replace(/(?:token|cookie|authorization|vkey|guid|uin|signature|secret|key)\s*[:=]\s*[^\s,;]+/gi, '[credential]')
    .replace(/\[[^\]]+ @ [0-9a-f]+\]/gi, '[media]').split(/\r?\n/).filter(Boolean).slice(0,3).join(' | ').slice(0,700);
}
async function probeFfmpeg(resolved, { clearProxy = false, userAgent = '' } = {}) {
  const ffprobe = path.join(root, '..', 'ffmpeg', 'ffprobe.exe');
  const args = ['-hide_banner','-loglevel','error','-rw_timeout','15000000'];
  if (userAgent) args.push('-user_agent',userAgent);
  if (new URL(resolved.url).protocol === 'https:') args.push('-tls_verify','1');
  if (resolved.headers && Object.keys(resolved.headers).length) args.push('-headers', Object.entries(resolved.headers).map(([k,v]) => `${k}: ${v}\r\n`).join(''));
  args.push('-select_streams','a:0','-show_entries','stream=codec_name,sample_rate,channels:format=duration','-of','json',resolved.url);
  const env = { ...process.env }; if (clearProxy) for (const key of Object.keys(env)) if (/proxy/i.test(key)) delete env[key];
  const proc = spawn(ffprobe, args, { windowsHide: true, env, stdio: ['ignore','pipe','pipe'] });
  let out = ''; let error = ''; proc.stdout.on('data', x => { if (out.length < 8192) out += x; }); proc.stderr.on('data', x => { if (error.length < 16384) error += x; });
  const timer = setTimeout(() => proc.kill(), 18000);
  const code = await new Promise(resolve => { proc.once('error', () => resolve(-1)); proc.once('exit', resolve); }); clearTimeout(timer);
  let info; try { info = JSON.parse(out); } catch {}
  return { code, firstDiagnostic: sanitized(error), streams: info?.streams?.map(s => ({ codec_name:s.codec_name,sample_rate:s.sample_rate,channels:s.channels })), duration: info?.format?.duration };
}
async function rangeRead(resolved) {
  const controller = new AbortController(); const timer = setTimeout(() => controller.abort(), 15000);
  try {
    const response = await fetch(resolved.url, { headers: { ...resolved.headers, Range: 'bytes=0-65535' }, signal: controller.signal });
    const reader = response.body?.getReader(); let bytes = 0; let prefix = null;
    while (reader && bytes < 65536) {
      const {value,done} = await reader.read(); if (done) break; bytes += value.length; if (!prefix) prefix = Buffer.from(value.subarray(0,12)).toString('hex');
    }
    await reader?.cancel();
    return { http:response.status, protocol:new URL(resolved.url).protocol, host:new URL(resolved.url).hostname, contentType:response.headers.get('content-type'), contentLength:response.headers.get('content-length'), acceptsRange:response.headers.get('accept-ranges'), contentRange:response.headers.get('content-range'), bytes, prefix };
  } catch (error) { return { error:error.name, code:error.cause?.code || error.code, diagnostic:sanitized(error.message) }; }
  finally { clearTimeout(timer); }
}
async function pcmRead(resolved, offsetSeconds = 0) {
  const args = ['-hide_banner','-loglevel','error','-nostdin','-rw_timeout','15000000'];
  if (new URL(resolved.url).protocol === 'https:') args.push('-tls_verify','1');
  if (resolved.headers && Object.keys(resolved.headers).length) args.push('-headers',Object.entries(resolved.headers).map(([k,v]) => `${k}: ${v}\r\n`).join(''));
  if (offsetSeconds) args.push('-ss',String(offsetSeconds));
  args.push('-i',resolved.url,'-map','0:a:0','-vn','-t','2','-ac','1','-ar','8000','-c:a','pcm_s16le','-f','s16le','pipe:1');
  const env = { ...process.env }; for (const key of Object.keys(env)) if (/proxy/i.test(key)) delete env[key];
  const proc = spawn(path.join(root,'..','ffmpeg','ffmpeg.exe'),args,{windowsHide:true,env,stdio:['ignore','pipe','pipe']});
  let bytes = 0; let squared = 0; let nonzero = 0; let error = '';
  proc.stdout.on('data',chunk => { bytes += chunk.length; for(let i=0;i+1<chunk.length;i+=2){const sample=chunk.readInt16LE(i);squared+=sample*sample;if(sample)nonzero++;} });
  proc.stderr.on('data',chunk => { if (error.length < 16384) error += chunk; });
  const timer = setTimeout(()=>proc.kill(),18000);
  const code = await new Promise(resolve=>{proc.once('error',()=>resolve(-1));proc.once('exit',resolve)});clearTimeout(timer);
  return {code,offsetSeconds,bytes,nonzeroSamples:nonzero,rms:bytes?Math.round(Math.sqrt(squared/(bytes/2))):0,firstDiagnostic:sanitized(error)};
}
try {
  for (const [providerId,providerTrackId] of [['qq','000C9FCy4HUcTW'],['kuwo','19528080'],['netease','1357374736']]) {
    const response = await fetch(`http://127.0.0.1:${handshake.port}/v1/resolve`, { method:'POST',headers:{'X-Neko-Token':token,'Content-Type':'application/json'},body:JSON.stringify({providerId,providerTrackId,metadata:{}}) });
    const resolved = await response.json();
    const record = { providerId,providerTrackId,resolveHttp:response.status,availability:resolved.availability,code:resolved.error?.code };
    if (resolved.url) {
      record.nodeRead = await rangeRead(resolved); record.ffprobe = await probeFfmpeg(resolved);
      if (record.ffprobe.code !== 0) record.proxyCleared = await probeFfmpeg(resolved,{ clearProxy:true });
      if (record.proxyCleared?.code !== 0) record.proxyClearedBrowserUa = await probeFfmpeg(resolved,{clearProxy:true,userAgent:'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36'});
      if (record.proxyCleared?.code === 0 || record.proxyClearedBrowserUa?.code === 0) {
        record.directPcm = await pcmRead(resolved); record.directSeekPcm = await pcmRead(resolved,32);
      }
    }
    process.stdout.write(JSON.stringify(record) + '\n');
  }
} finally { child.kill(); }
