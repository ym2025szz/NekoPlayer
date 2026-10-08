import { randomUUID } from 'node:crypto';
import { GatewayError } from './errors.js';
import { requests } from './network.js';

export function cookieObject(cookie = '') {
  return Object.fromEntries(String(cookie).split(';').map(x => x.trim()).filter(x => x.includes('='))
    .map(x => [x.slice(0, x.indexOf('=')), x.slice(x.indexOf('=') + 1)])
    .filter(([key]) => !/^(path|domain|expires|max-age|samesite|secure|httponly|__proto__|constructor|prototype)$/i.test(key)));
}
export function accountCookie(providerId) {
  const context = requests.getStore();
  return context?.providerId === providerId ? context.accountCookie || '' : '';
}

export class AccountManager {
  constructor(providers) { this.providers = providers; this.sessions = new Map(); this.pending = new Map(); this.revisions = new Map(); }
  revision(id) { return this.revisions.get(id) || 0; }
  invalidate(id) {
    this.revisions.set(id, this.revision(id) + 1);
    for (const [key, value] of this.pending) if (value.providerId === id) this.pending.delete(key);
  }
  info(id) {
    const provider = this.providers.get(id); const session = this.sessions.get(id);
    return { providerId: id, name: provider.name, canLogin: !!provider.api.auth,
      loggedIn: !!session, displayName: session?.displayName || '',
      message: session ? '已登录，播放权限以此账号的平台授权为准' : provider.api.auth ? '游客模式，可扫码登录' : '此来源暂未提供稳定的账号登录接口，可继续游客搜索' };
  }
  async handle(route, body) {
    if (route === 'auth/status') return { accounts: [...this.providers.keys()].map(id => this.info(id)) };
    const provider = this.providers.get(body.providerId);
    if (!provider) throw new GatewayError('unknown_provider', '不支持此平台', 400);
    const id = body.providerId;
    if (route === 'auth/logout') { this.invalidate(id); this.sessions.delete(id); return this.info(id); }
    if (route === 'auth/cancel') {
      const pending = this.pending.get(body.loginId);
      if (pending?.providerId === id) this.pending.delete(body.loginId);
      return { cancelled: true };
    }
    const auth = provider.api.auth;
    if (!auth) throw new GatewayError('login_unavailable', '此平台的账号登录接口暂不可用', 400);
    if (route === 'auth/restore') {
      const revision = this.revision(id);
      const credential = body.credential;
      if (typeof credential !== 'string' || credential.length > 16384 || !/^[\x20-\x7e]+$/.test(credential)) throw new GatewayError('invalid_credential', '账号凭证格式无效', 400);
      const session = await auth.validate(credential);
      if (revision !== this.revision(id)) throw new GatewayError('login_cancelled', '账号操作已取消', 409);
      this.sessions.set(id, { credential, displayName: String(session.displayName || provider.name).slice(0, 100) });
      return this.info(id);
    }
    if (route === 'auth/start') {
      this.invalidate(id); const revision = this.revision(id);
      const login = await auth.start();
      if (revision !== this.revision(id)) throw new GatewayError('login_cancelled', '账号操作已取消', 409);
      if (typeof login.image !== 'string' || login.image.length > 512000 || !/^data:image\/png;base64,/.test(login.image)) throw new GatewayError('invalid_login_response', '平台没有返回有效的登录二维码', 502);
      const loginId = randomUUID(); const expiresAt = Date.now() + 180000;
      for (const [key, value] of this.pending) if (value.expiresAt <= Date.now()) this.pending.delete(key);
      this.pending.set(loginId, { providerId: id, login, revision, expiresAt });
      return { providerId: id, loginId, qrImage: login.image, expiresAt: new Date(expiresAt).toISOString(), message: login.message || '请使用平台手机 App 扫码，并在手机上确认授权' };
    }
    if (route === 'auth/check') {
      const pending = this.pending.get(body.loginId);
      if (!pending || pending.providerId !== id || pending.expiresAt <= Date.now()) {
        if (pending?.providerId === id) this.pending.delete(body.loginId);
        return { state: 'expired', message: '二维码已过期，请刷新' };
      }
      const result = await auth.check(pending.login);
      if (this.pending.get(body.loginId) !== pending || pending.revision !== this.revision(id) || pending.expiresAt <= Date.now()) return { state: 'expired', message: '登录请求已取消或过期' };
      if (result.state === 'authorized') {
        if (requests.getStore()?.signal?.aborted) throw new GatewayError('login_cancelled', '登录请求已取消', 409);
        if (typeof result.credential !== 'string' || result.credential.length > 16384 || !/^[\x20-\x7e]+$/.test(result.credential)) throw new GatewayError('invalid_login_response', '平台未返回有效登录凭证', 502);
        this.pending.delete(body.loginId);
        this.sessions.set(id, { credential: result.credential, displayName: String(result.displayName || provider.name).slice(0, 100) });
        return { state: 'authorized', message: '授权成功', credential: result.credential, account: this.info(id) };
      }
      if (result.state === 'expired') this.pending.delete(body.loginId);
      return { state: result.state, message: result.message };
    }
    throw new GatewayError('not_found', '接口不存在', 404);
  }
}
