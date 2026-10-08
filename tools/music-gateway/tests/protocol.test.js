import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createGatewayServer } from '../src/index.js';

const token = 'fixture-token-0123456789-abcdefghijklmnop';
async function fixture(t, overrides = {}) {
  const calls = [];
  const providers = new Map([['fixture', { id: 'fixture', name: 'Fixture', experimental: false, canSearch: true, canPlay: true, canGetLyrics: true, status: 'unverified', message: '', api: {
    search: async data => { calls.push(data); return { tracks: [{ providerTrackId: '1', title: 'Test', artist: 'Guest', album: '', durationSeconds: 123, coverUrl: '', versionLabel: '', availability: 'unknown', restrictionReason: '', providerMetadataJson: '{"fee":1}' }], hasMore: false }; },
    resolve: async data => { calls.push(data); return { availability: 'unavailable', durationSeconds: 0, canSeek: false, reason: 'No guest entitlement' }; },
    lyrics: async () => ({ lrc: '[00:00.00]Fixture' }), ...overrides } }]]);
  const server = createGatewayServer({ token, providers }); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const request = async (route, body, extra = {}) => {
    const response = await fetch(`http://127.0.0.1:${server.address().port}${route}`, { method: body === undefined ? 'GET' : 'POST', headers: { 'X-Neko-Token': token, 'Content-Type': 'application/json', ...extra.headers }, ...(body === undefined ? {} : { body: JSON.stringify(body) }), ...extra, headers: { 'X-Neko-Token': token, 'Content-Type': 'application/json', ...extra.headers } });
    return { status: response.status, body: await response.json(), headers: response.headers };
  };
  return { request, calls };
}

test('authenticated health and provider routes return protocol v1 without internal APIs', async t => {
  const { request } = await fixture(t);
  assert.deepEqual((await request('/health')).body, { protocolVersion: 1, ready: true });
  const info = (await request('/v1/providers')).body.providers[0]; assert.equal(info.id, 'fixture'); assert.equal(info.api, undefined);
});

test('missing credentials and browser-origin requests are denied', async t => {
  const { request } = await fixture(t);
  assert.equal((await request('/health', undefined, { headers: { 'X-Neko-Token': '' } })).status, 401);
  assert.equal((await request('/health', undefined, { headers: { Origin: 'https://example.test' } })).status, 403);
  assert.equal((await request('/health', undefined, { headers: { Origin: 'null' } })).status, 403);
});

test('search default pagination and native metadata JSON match desktop protocol', async t => {
  const { request, calls } = await fixture(t);
  const { status, body } = await request('/v1/search', { providerId: 'fixture', query: ' Test ' });
  assert.equal(status, 200); assert.equal(body.providerId, 'fixture'); assert.equal(body.page, 1); assert.equal(body.hasMore, false);
  assert.equal(JSON.parse(body.tracks[0].providerMetadataJson).fee, 1); assert.equal(calls[0].query, 'Test'); assert.equal(calls[0].pageSize, 20);
});

test('page bounds, unknown providers, proxy routes and injected source params fail explicitly', async t => {
  const { request } = await fixture(t);
  assert.equal((await request('/v1/search', { providerId: 'fixture', query: 'x', page: 0 })).status, 400);
  assert.equal((await request('/v1/search', { providerId: 'fixture', query: 'x', pageSize: 51 })).status, 400);
  assert.equal((await request('/v1/search', { providerId: 'missing', query: 'x' })).body.error.code, 'unknown_provider');
  assert.equal((await request('/proxy?url=http://127.0.0.1')).status, 404);
  assert.equal((await request('/v1/resolve', { providerId: 'fixture', providerTrackId: '1', cookie: 'injected' })).status, 400);
});

test('untrusted persisted metadata cannot override platform permission mapping', async t => {
  const { request, calls } = await fixture(t);
  const result = await request('/v1/resolve', { providerId: 'fixture', providerTrackId: '1', metadata: { url: 'http://127.0.0.1/', fee: 0, durationSeconds: 123 } });
  assert.equal(result.status, 200); assert.equal(result.body.availability, 'unavailable'); assert.deepEqual(calls[0].metadata, {});
});

test('wrong content type and oversized bodies produce explicit HTTP errors', async t => {
  const { request } = await fixture(t);
  assert.equal((await request('/v1/search', { providerId: 'fixture', query: 'x' }, { headers: { 'Content-Type': 'text/plain' } })).status, 415);
  assert.equal((await request('/v1/search', { providerId: 'fixture', query: 'x'.repeat(70000) })).status, 413);
});

test('upstream business rejection cannot masquerade as empty successful search', async t => {
  const { GatewayError } = await import('../src/errors.js');
  const { request } = await fixture(t, { search: async () => { throw new GatewayError('authentication_required', 'Guest rejected', 403); } });
  const result = await request('/v1/search', { providerId: 'fixture', query: 'x' });
  assert.equal(result.status, 403); assert.equal(result.body.error.code, 'authentication_required'); assert.equal(result.body.tracks, undefined);
});

test('concurrency is bounded and overload is retryable', async t => {
  const releases = [];
  const { request } = await fixture(t, { search: async () => { await new Promise(resolve => releases.push(resolve)); return { tracks: [], hasMore: false }; } });
  const pending = Array.from({ length: 4 }, () => request('/v1/search', { providerId: 'fixture', query: 'x' }));
  while (releases.length < 4) await new Promise(resolve => setTimeout(resolve, 5));
  const result = await request('/v1/search', { providerId: 'fixture', query: 'x' });
  assert.equal(result.status, 429); assert.equal(result.body.error.retryable, true);
  releases.forEach(resolve => resolve()); await Promise.all(pending);
});

test('disconnect cancels the in-flight provider request', { timeout: 3000 }, async t => {
  const { requests } = await import('../src/network.js');
  let entered; const started = new Promise(resolve => { entered = resolve; });
  let observedAbort; const cancelled = new Promise(resolve => { observedAbort = resolve; });
  const { request } = await fixture(t, { search: async () => {
    const signal = requests.getStore().signal; entered();
    await new Promise(resolve => signal.addEventListener('abort', () => { observedAbort(); resolve(); }, { once: true }));
    return { tracks: [], hasMore: false };
  } });
  const controller = new AbortController();
  const pending = request('/v1/search', { providerId: 'fixture', query: 'x' }, { signal: controller.signal }).catch(error => error);
  await started; controller.abort(); await cancelled;
  assert.equal((await pending).name, 'AbortError');
});
