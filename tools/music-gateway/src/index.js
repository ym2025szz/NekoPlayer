import http from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { GatewayError, transportError } from './errors.js';
import { requests, installSdkTransport } from './network.js';
import { createProviders } from './providers/index.js';
import { AccountManager } from './accounts.js';

const PROTOCOL_VERSION = 1;
const MAX_BODY_BYTES = 65536;
const MAX_CONCURRENT_REQUESTS = 4;
const REQUEST_TIMEOUT_MS = 12000;

function send(res, status, data) {
  if (res.destroyed || res.writableEnded) return;
  const body = JSON.stringify(data);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
  res.end(body);
}
function reject(res, error) { send(res, error.status, { error: { code: error.code, message: error.message, retryable: error.retryable } }); }

async function readJson(req) {
  if (!/^application\/json(?:\s*;|$)/i.test(req.headers['content-type'] || '')) throw new GatewayError('unsupported_content_type', '请求必须为 application/json', 415);
  if (Number(req.headers['content-length']) > MAX_BODY_BYTES) throw new GatewayError('request_too_large', '请求超过大小限制', 413);
  const chunks = []; let bytes = 0;
  for await (const chunk of req.iterator({ destroyOnReturn: false })) {
    bytes += chunk.length;
    if (bytes > MAX_BODY_BYTES) { req.resume(); throw new GatewayError('request_too_large', '请求超过大小限制', 413); }
    chunks.push(chunk);
  }
  try { const result = JSON.parse(Buffer.concat(chunks).toString('utf8')); if (!result || typeof result !== 'object' || Array.isArray(result)) throw new Error(); return result; }
  catch { throw new GatewayError('invalid_json', '请求 JSON 无效', 400); }
}

function validatePayload(route, body) {
  if (route.startsWith('auth/')) {
    const allowed = route === 'auth/status' ? [] : route === 'auth/restore' ? ['providerId', 'credential'] : ['providerId', 'loginId'];
    if (Object.keys(body).some(k => !allowed.includes(k))) throw new GatewayError('invalid_request', '请求包含未知字段', 400);
    if (route !== 'auth/status' && (typeof body.providerId !== 'string' || body.providerId.length > 30)) throw new GatewayError('invalid_provider', '平台标识无效', 400);
    if (['auth/check', 'auth/cancel'].includes(route) && (typeof body.loginId !== 'string' || body.loginId.length > 100)) throw new GatewayError('invalid_login_id', '登录请求无效', 400);
    return body;
  }
  const allowed = route === 'search' ? ['providerId','query','page','pageSize'] : ['providerId','providerTrackId','metadata'];
  if (Object.keys(body).some(k => !allowed.includes(k))) throw new GatewayError('invalid_request', '请求包含未知字段', 400);
  if (typeof body.providerId !== 'string' || body.providerId.length > 30) throw new GatewayError('invalid_provider', '平台标识无效', 400);
  if (route === 'search') {
    if (typeof body.query !== 'string' || !body.query.trim() || body.query.length > 200) throw new GatewayError('invalid_query', '搜索内容必须为 1 至 200 个字符', 400);
    const page = body.page ?? 1; const pageSize = body.pageSize ?? 20;
    if (!Number.isInteger(page) || page < 1 || page > 100 || !Number.isInteger(pageSize) || pageSize < 1 || pageSize > 50) throw new GatewayError('invalid_pagination', '页码必须为 1 至 100，每页数量必须为 1 至 50', 400);
    return { providerId: body.providerId, query: body.query.trim(), page, pageSize };
  }
  if (typeof body.providerTrackId !== 'string' || body.providerTrackId.length > 100) throw new GatewayError('invalid_track_id', '歌曲标识无效', 400);
  if (body.metadata !== undefined && (!body.metadata || typeof body.metadata !== 'object' || Array.isArray(body.metadata))) throw new GatewayError('invalid_metadata', '歌曲元数据必须为 JSON 对象', 400);
  // Public callers cannot inject cookies, source platforms, arbitrary URLs or VIP parameters.
  // Adapters obtain playback permissions and timing from the provider itself.
  return { providerId: body.providerId, providerTrackId: body.providerTrackId, metadata: {} };
}

export function createGatewayServer({ token, providers, accounts = new AccountManager(providers) }) {
  const expected = Buffer.from(token, 'utf8');
  let active = 0; let rateWindow = Date.now(); let count = 0;
  const server = http.createServer(async (req, res) => {
    try {
      if (!['127.0.0.1','::ffff:127.0.0.1'].includes(req.socket.remoteAddress)) throw new GatewayError('forbidden', '只接受本机请求', 403);
      if (req.headers.origin !== undefined || req.headers['sec-fetch-site'] !== undefined) throw new GatewayError('browser_origin_forbidden', '浏览器来源请求已被拒绝', 403);
      const actual = Buffer.from(typeof req.headers['x-neko-token'] === 'string' ? req.headers['x-neko-token'] : '', 'utf8');
      if (actual.length !== expected.length || !timingSafeEqual(actual, expected)) throw new GatewayError('unauthorized', '网关凭证无效', 401);
      if (req.headers.host !== `127.0.0.1:${server.address()?.port}` && req.headers.host !== `localhost:${server.address()?.port}`) throw new GatewayError('invalid_host', '网关 Host 无效', 403);
      if (req.url === '/health' && req.method === 'GET') { send(res, 200, { protocolVersion: PROTOCOL_VERSION, ready: true }); return; }
      if (req.url === '/v1/providers' && req.method === 'GET') {
        send(res, 200, { providers: [...providers.values()].map(({ api, ...info }) => info) }); return;
      }
      const routes = { '/v1/search': 'search', '/v1/resolve': 'resolve', '/v1/lyrics': 'lyrics',
        ...Object.fromEntries(['status','start','check','restore','cancel','logout'].map(x => [`/v1/auth/${x}`, `auth/${x}`])) };
      const route = routes[req.url];
      if (!route) throw new GatewayError('not_found', '接口不存在', 404);
      if (req.method !== 'POST') throw new GatewayError('method_not_allowed', '此接口仅接受 POST', 405);
      if (Date.now() - rateWindow > 60000) { rateWindow = Date.now(); count = 0; }
      if (active >= MAX_CONCURRENT_REQUESTS || count >= 90) throw new GatewayError('gateway_busy', '网关请求过于频繁，请稍后重试', 429, true);
      active++; count++;
      const controller = new AbortController();
      const context = { signal: controller.signal, transportError: null };
      let timer;
      const disconnected = () => { if (!res.writableEnded) controller.abort(); };
      req.once('aborted', disconnected); res.once('close', disconnected);
      let provider;
      try {
        await requests.run(context, async () => {
          const timeout = new Promise((_, fail) => {
            timer = setTimeout(() => { controller.abort(); fail(new GatewayError('timeout', '平台请求超时', 504, true)); }, REQUEST_TIMEOUT_MS);
          });
          const operation = (async () => {
            const body = validatePayload(route, await readJson(req));
            if (route.startsWith('auth/')) { send(res, 200, await accounts.handle(route, body)); return; }
            provider = providers.get(body.providerId);
            if (!provider) throw new GatewayError('unknown_provider', '不支持此平台', 400);
            context.providerId = body.providerId;
            context.accountCookie = accounts.sessions.get(body.providerId)?.credential || '';
            const result = await provider.api[route](body);
            provider.status = route === 'resolve' && result.availability === 'unavailable' ? 'limited' : 'available';
            provider.message = route === 'resolve' && result.reason ? result.reason : '平台请求成功，播放仍按每首歌曲权限判断';
            send(res, 200, route === 'search' ? { providerId: body.providerId, page: body.page, ...result } : result);
          })();
          await Promise.race([operation, timeout]);
        });
      } catch (error) {
        const converted = error instanceof GatewayError ? error : context.transportError || transportError(error);
        if (provider) { provider.status = ['authentication_required','unsupported_guest_operation'].includes(converted.code) ? 'limited' : 'error'; provider.message = converted.message; }
        reject(res, converted);
      } finally {
        clearTimeout(timer); controller.abort(); active--;
        req.removeListener('aborted', disconnected); res.removeListener('close', disconnected);
      }
    } catch (error) { reject(res, error instanceof GatewayError ? error : new GatewayError('internal_error', '网关内部请求失败', 500)); }
  });
  server.requestTimeout = 15000; server.headersTimeout = 5000; server.keepAliveTimeout = 5000;
  server.maxRequestsPerSocket = 100; server.maxHeadersCount = 40;
  return server;
}

async function startupLine() {
  return new Promise((resolve, reject) => {
    let buffer = ''; const timer = setTimeout(() => finish(new Error()), 10000);
    function finish(error, value) { clearTimeout(timer); process.stdin.off('data', onData); process.stdin.off('end', onEnd); error ? reject(error) : resolve(value); }
    const onEnd = () => finish(new Error());
    function onData(chunk) {
      buffer += chunk.toString('utf8'); if (Buffer.byteLength(buffer) > 16384) { finish(new Error()); return; }
      const line = buffer.indexOf('\n');
      if (line >= 0) { try { finish(null, JSON.parse(buffer.slice(0, line))); } catch { finish(new Error()); } }
    }
    process.stdin.on('data', onData); process.stdin.once('end', onEnd);
  });
}

export async function main() {
  if (Number(process.versions.node.split('.')[0]) !== 24) throw new Error('Node 24 required');
  const config = await startupLine();
  if (!config || typeof config.token !== 'string' || config.token.length < 32 || config.token.length > 512 || !/^[\x21-\x7e]+$/.test(config.token) || config.port !== 0 || typeof config.dataDirectory !== 'string' || !path.isAbsolute(config.dataDirectory)) throw new Error('Invalid startup config');
  await mkdir(config.dataDirectory, { recursive: true });
  const guestDirectory = await mkdtemp(path.join(config.dataDirectory, 'gateway-guest-'));
  // Fresh SDK state ensures pre-existing account cookies can never be consumed.
  process.env.TMP = guestDirectory; process.env.TEMP = guestDirectory;
  process.env.QQ_MUSIC_API_CONFIG_DIR = path.join(guestDirectory, 'qq');
  process.env.ENABLE_GENERAL_UNBLOCK = 'false'; process.env.QISHUI_ENABLE_DECRYPT = 'false';
  process.env.DEBUG = 'false'; process.env.ENABLE_RANDOM_CN_IP = 'false';
  delete process.env.NETEASE_COOKIE;
  for (const key of Object.keys(process.env)) if (/^QISHUI_/.test(key) && key !== 'QISHUI_ENABLE_DECRYPT') delete process.env[key];
  await writeFile(path.join(guestDirectory, 'anonymous_token'), '', 'utf8');
  installSdkTransport();
  const providers = await createProviders();
  const server = createGatewayServer({ token: config.token, providers });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  process.stdout.write(JSON.stringify({ ready: true, port: server.address().port, protocolVersion: PROTOCOL_VERSION }) + '\n');
  let stopping = false;
  const shutdown = async () => {
    if (stopping) return; stopping = true;
    server.closeAllConnections(); server.close();
    await rm(guestDirectory, { recursive: true, force: true }); process.exit(0);
  };
  process.once('SIGTERM', shutdown); process.once('SIGINT', shutdown);
}
