import http from 'node:http';
import https from 'node:https';
import { AsyncLocalStorage } from 'node:async_hooks';
import { createRequire } from 'node:module';
import { GatewayError, transportError } from './errors.js';

export const requests = new AsyncLocalStorage();
export const httpAgent = new http.Agent({ keepAlive: true, maxSockets: 6, maxFreeSockets: 3, timeout: 10000 });
export const httpsAgent = new https.Agent({ keepAlive: true, maxSockets: 6, maxFreeSockets: 3, timeout: 10000 });
export const MAX_RESPONSE_BYTES = 4 * 1024 * 1024;
const allowedApiDomains = ['music.163.com', 'qq.com', 'kugou.com', 'kuwo.cn', 'douyin.com', 'qishui.com'];
const nativeFetch = globalThis.fetch.bind(globalThis);
let sdkHttpAdapter;

function allowed(url) {
  const u = new URL(url);
  if (!['https:', 'http:'].includes(u.protocol) || u.username || u.password || (u.port && !['80','443'].includes(u.port)) || !allowedApiDomains.some(d => u.hostname === d || u.hostname.endsWith(`.${d}`)))
    throw new GatewayError('blocked_upstream_url', '平台接口地址不在允许范围内', 502);
  return u;
}

export async function boundedFetch(input, init = {}) {
  let url = allowed(typeof input === 'string' || input instanceof URL ? String(input) : input.url);
  const context = requests.getStore();
  const signal = AbortSignal.any([AbortSignal.timeout(10000), ...(context?.signal ? [context.signal] : []), ...(init.signal ? [init.signal] : [])]);
  try {
    for (let redirects = 0; redirects <= 3; redirects++) {
      const response = await nativeFetch(url, { ...init, signal, redirect: 'manual' });
      if ([301,302,303,307,308].includes(response.status) && init.redirect !== 'manual') {
        await response.body?.cancel();
        if (redirects === 3) throw new GatewayError('upstream_redirect_error', '平台重定向次数过多');
        const next = allowed(new URL(response.headers.get('location'), url).href);
        // SDK credentials are never forwarded to a different upstream origin.
        if (next.origin !== url.origin) throw new GatewayError('blocked_upstream_redirect', '平台跨域重定向已被拒绝');
        url = next; continue;
      }
      if (Number(response.headers.get('content-length')) > MAX_RESPONSE_BYTES) {
        await response.body?.cancel(); throw new GatewayError('upstream_response_too_large', '平台返回的数据超过大小限制');
      }
      const chunks = []; let size = 0;
      for await (const chunk of response.body || []) {
        size += chunk.length;
        if (size > MAX_RESPONSE_BYTES) throw new GatewayError('upstream_response_too_large', '平台返回的数据超过大小限制');
        chunks.push(chunk);
      }
      return new Response(Buffer.concat(chunks), { status: response.status, statusText: response.statusText, headers: response.headers });
    }
  } catch (error) {
    const converted = transportError(error); if (context) context.transportError = converted; throw converted;
  }
}

export function installSdkTransport() {
  globalThis.fetch = boundedFetch;
  const require = createRequire(import.meta.url);
  const axiosModules = new Set();
  for (const pkg of ['@neteasecloudmusicapienhanced/api', 'kugou-music-api']) {
    const localRequire = createRequire(require.resolve(`${pkg}/package.json`));
    const axios = localRequire('axios').default || localRequire('axios');
    if (axiosModules.has(axios)) continue; axiosModules.add(axios);
    axios.interceptors.request.use(config => {
      allowed(new URL(config.url, config.baseURL).href);
      config.timeout = 10000; config.signal = requests.getStore()?.signal;
      config.httpAgent = httpAgent; config.httpsAgent = httpsAgent;
      config.maxContentLength = MAX_RESPONSE_BYTES; config.maxBodyLength = MAX_RESPONSE_BYTES;
      config.maxRedirects = 0; config.proxy = false;
      return config;
    });
    axios.interceptors.response.use(x => x, error => {
      const context = requests.getStore(); if (context) context.transportError = transportError(error);
      return Promise.reject(error);
    });
  }
}

export function sdkOptions() {
  if (!sdkHttpAdapter) {
    const require = createRequire(import.meta.url);
    const local = createRequire(require.resolve('@neteasecloudmusicapienhanced/api/package.json'));
    sdkHttpAdapter = local('axios').getAdapter('http');
  }
  return { signal: requests.getStore()?.signal, timeout: 10000, proxy: false,
    httpAgent, httpsAgent, maxContentLength: MAX_RESPONSE_BYTES, maxBodyLength: MAX_RESPONSE_BYTES, maxRedirects: 0,
    adapter: async config => {
      try { allowed(new URL(config.url, config.baseURL).href); return await sdkHttpAdapter(config); }
      catch (error) { const context = requests.getStore(); if (context) context.transportError = transportError(error); throw error; }
    },
    headers: { Cookie: '', 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36' } };
}

export async function jsonRequest(url, init = {}) {
  const response = await boundedFetch(url, init);
  if (!response.ok) throw transportError({ response: { status: response.status } });
  try { return { body: await response.json(), headers: response.headers }; }
  catch (error) { if (error instanceof GatewayError) throw error; throw new GatewayError('invalid_upstream_response', '平台返回了无效 JSON'); }
}

export function sdkBody(result) {
  if (!result || Number(result.status) >= 400) {
    throw requests.getStore()?.transportError || transportError(result?.body?.msg || { response: { status: result?.status } });
  }
  return result.body;
}
