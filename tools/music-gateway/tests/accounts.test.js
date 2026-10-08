import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { AccountManager, accountCookie, cookieObject } from '../src/accounts.js';
import { createGatewayServer } from '../src/index.js';
import { requests } from '../src/network.js';
import { musicSessionCookie } from '../src/providers/qq.js';

function fixture(auth = {}) {
  const providers = new Map([['fixture', { name: 'Fixture', api: { auth: {
    start: async () => ({ image: 'data:image/png;base64,fixture', privateKey: 'private-login-secret' }),
    check: async () => ({ state: 'authorized', credential: 'session=private-credential', displayName: 'Test account' }),
    validate: async () => ({ displayName: 'Restored account' }), ...auth } } }], ['guest', { name: 'Guest', api: {} }]]);
  return { providers, accounts: new AccountManager(providers) };
}
test('account status exposes capabilities and names without credentials or SDK APIs', async () => {
  const { accounts } = fixture();
  await accounts.handle('auth/restore', { providerId: 'fixture', credential: 'session=private-credential' });
  const result = await accounts.handle('auth/status', {});
  assert.equal(result.accounts[0].loggedIn, true); assert.equal(result.accounts[1].canLogin, false);
  assert.doesNotMatch(JSON.stringify(result), /private-credential|privateKey|api/);
});
test('login start exposes only an opaque id and image, with private QR keys retained in the gateway', async () => {
  const { accounts } = fixture();
  const login = await accounts.handle('auth/start', { providerId: 'fixture' });
  assert.ok(login.loginId); assert.doesNotMatch(JSON.stringify(login), /private-login-secret/);
  const done = await accounts.handle('auth/check', { providerId: 'fixture', loginId: login.loginId });
  assert.equal(done.state, 'authorized'); assert.equal(done.account.loggedIn, true);
  await accounts.handle('auth/logout', { providerId: 'fixture' });
  assert.equal(accounts.sessions.size, 0);
  assert.equal((await accounts.handle('auth/check', { providerId: 'fixture', loginId: login.loginId })).state, 'expired');
});
test('cancel while authorization is in flight prevents stale login from committing', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  const { accounts } = fixture({ check: () => pending });
  const login = await accounts.handle('auth/start', { providerId: 'fixture' });
  const checking = accounts.handle('auth/check', { providerId: 'fixture', loginId: login.loginId });
  await accounts.handle('auth/cancel', { providerId: 'fixture', loginId: login.loginId });
  release({ state: 'authorized', credential: 'session=stale', displayName: 'Stale' });
  assert.equal((await checking).state, 'expired'); assert.equal(accounts.sessions.size, 0);
});
test('logout during credential restoration cannot resurrect the account', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  const { accounts } = fixture({ validate: () => pending });
  const restoring = accounts.handle('auth/restore', { providerId: 'fixture', credential: 'session=stale' });
  await accounts.handle('auth/logout', { providerId: 'fixture' });
  release({ displayName: 'Stale' });
  await assert.rejects(restoring, e => e.code === 'login_cancelled'); assert.equal(accounts.sessions.size, 0);
});
test('cross-provider login ids cannot be used to authorize or cancel another provider', async () => {
  const { accounts, providers } = fixture(); providers.set('other', providers.get('fixture'));
  const login = await accounts.handle('auth/start', { providerId: 'fixture' });
  await accounts.handle('auth/cancel', { providerId: 'other', loginId: login.loginId });
  assert.equal((await accounts.handle('auth/check', { providerId: 'other', loginId: login.loginId })).state, 'expired');
  assert.equal(accounts.sessions.size, 0);
  assert.equal((await accounts.handle('auth/check', { providerId: 'fixture', loginId: login.loginId })).state, 'authorized');
});
test('an account cookie is available only to the matching provider request context', () => {
  assert.equal(accountCookie('fixture'), '');
  requests.run({ providerId: 'fixture', accountCookie: 'session=private' }, () => {
    assert.equal(accountCookie('fixture'), 'session=private'); assert.equal(accountCookie('other'), '');
  });
  assert.deepEqual(cookieObject('MUSIC_U=value; Path=/; HttpOnly; Domain=.music.163.com; __proto__=evil'), { MUSIC_U: 'value' });
});
test('QQ authorization retains music keys, fills SDK authst alias and drops handoff cookies', () => {
  const values = cookieObject(musicSessionCookie('uin=o123; qm_keyst=music-key; p_skey=qq-secret; qrsig=qr-secret; Path=/'));
  assert.equal(values.qqmusic_key, 'music-key'); assert.equal(values.qqmusic_uin, 'o123');
  assert.equal(values.p_skey, undefined); assert.equal(values.qrsig, undefined);
  assert.throws(() => musicSessionCookie('uin=o123; p_skey=invalid'), e => e.code === 'authentication_required');
});
test('expired QR codes and unsupported login sources fail honestly', async () => {
  const { accounts } = fixture();
  const login = await accounts.handle('auth/start', { providerId: 'fixture' });
  accounts.pending.get(login.loginId).expiresAt = 0;
  assert.equal((await accounts.handle('auth/check', { providerId: 'fixture', loginId: login.loginId })).state, 'expired');
  await assert.rejects(accounts.handle('auth/start', { providerId: 'guest' }), e => e.code === 'login_unavailable');
});
test('auth routes retain loopback token, browser origin and input validation', async t => {
  const { accounts, providers } = fixture(); const token = 'test-token-long-enough-for-fixture';
  const server = createGatewayServer({ token, providers, accounts }); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => { server.closeAllConnections(); server.close(); });
  const send = (body, headers = {}) => fetch(`http://127.0.0.1:${server.address().port}/v1/auth/start`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Neko-Token': token, ...headers }, body: JSON.stringify(body) });
  assert.equal((await send({ providerId: 'fixture' }, { 'X-Neko-Token': '' })).status, 401);
  assert.equal((await send({ providerId: 'fixture' }, { Origin: 'https://example.test' })).status, 403);
  assert.equal((await send({ providerId: 'fixture', password: 'forbidden' })).status, 400);
});
