import { createRequire } from 'node:module';
import { checkBusiness, GatewayError } from '../errors.js';
import { sdkBody } from '../network.js';
import { artists, mapNeteaseResolution, track, validateId } from '../mapping.js';
import { accountCookie, cookieObject } from '../accounts.js';

export function createNetease() {
  process.env.ENABLE_GENERAL_UNBLOCK = 'false';
  const require = createRequire(import.meta.url);
  const pkg = require.resolve('@neteasecloudmusicapienhanced/api/package.json');
  const local = createRequire(pkg);
  const request = local('./util/request');
  const modules = Object.fromEntries(['cloudsearch','song_url_v1','song_detail','lyric','login_qr_key','login_qr_create','login_qr_check','login_status'].map(n => [n, local(`./module/${n}`)]));
  const rawCall = (name, query, cookie = accountCookie('netease')) => modules[name]({ ...query, cookie: cookieObject(cookie), unblock: 'false', source: [], crypto: name.startsWith('login_') ? 'weapi' : 'eapi', randomCNIP: false, timeout: 10000 }, request);
  const call = async (name, query) => {
    // Credentials come only from this provider's request context; unlocking remains disabled.
    const result = await rawCall(name, query);
    return checkBusiness(sdkBody(result), { success: [200] });
  };
  return {
    auth: {
      async start() {
        const body = sdkBody(await rawCall('login_qr_key', {}, ''));
        const key = body.data?.unikey;
        if (!key) throw new GatewayError('invalid_login_response', '网易云没有返回二维码标识', 502);
        const qr = sdkBody(await rawCall('login_qr_create', { key, qrimg: true }, ''));
        return { key, image: qr.data?.qrimg, message: '使用网易云音乐 App 扫码，在手机上确认授权' };
      },
      async check({ key }) {
        const raw = await rawCall('login_qr_check', { key, noCookie: true }, '');
        const body = sdkBody(raw);
        if (Number(body.code) === 803) {
          const credential = Object.entries(cookieObject(body.cookie || (raw.cookie || []).join(';'))).map(([k,v]) => `${k}=${v}`).join('; ');
          const info = await this.validate(credential);
          return { state: 'authorized', credential, ...info };
        }
        if (Number(body.code) === 800) return { state: 'expired', message: '二维码已过期，请刷新' };
        if (Number(body.code) === 802) return { state: 'scanned', message: '已扫码，请在手机上确认授权' };
        if (Number(body.code) === 801) return { state: 'waiting', message: '等待网易云音乐 App 扫码' };
        throw new GatewayError('login_check_failed', '网易云登录状态暂不可用，请重试', 502, true);
      },
      async validate(credential) {
        const body = sdkBody(await rawCall('login_status', {}, credential));
        const profile = body.data?.profile;
        if (!profile?.userId) throw new GatewayError('authentication_required', '网易云登录已失效，请重新扫码', 403);
        return { displayName: profile.nickname || '网易云账号' };
      },
    },
    async search({ query, page, pageSize }) {
      const body = await call('cloudsearch', { keywords: query, type: 1, limit: pageSize, offset: (page - 1) * pageSize });
      const list = body.result?.songs;
      if (!Array.isArray(list)) {
        if (Number(body.result?.songCount) === 0) return { tracks: [], hasMore: false };
        throw new GatewayError('invalid_upstream_response', '网易云搜索结果结构无效');
      }
      return { tracks: list.map(s => track({ id: s.id, title: s.name, artist: artists(s.ar || s.artists), album: s.al?.name || s.album?.name,
        duration: s.dt / 1000, cover: s.al?.picUrl || s.album?.picUrl, version: Array.isArray(s.alia) ? s.alia.join(' / ') : '',
        reason: Number(s.fee) > 0 ? '平台标注付费，实际权限以解析结果为准' : '',
        metadata: { id: s.id, dt: s.dt, fee: s.fee, privilege: s.privilege || null, noCopyrightRcmd: s.noCopyrightRcmd || null } })),
        hasMore: (page - 1) * pageSize + list.length < Number(body.result?.songCount || 0) };
    },
    async resolve({ providerTrackId }) {
      const id = validateId(providerTrackId, /^\d{1,20}$/);
      const body = await call('song_url_v1', { id, level: 'standard' });
      const data = body.data?.find(x => String(x.id) === id);
      if (!data) throw new GatewayError('invalid_upstream_response', '网易云未返回此歌曲的播放权限数据');
      let detail = {};
      if (data.url && !data.freeTrialInfo && !data.time) {
        const response = await call('song_detail', { ids: id }); detail = response.songs?.[0] || {};
      }
      return mapNeteaseResolution(data, detail);
    },
    async lyrics({ providerTrackId }) {
      const body = await call('lyric', { id: validateId(providerTrackId, /^\d{1,20}$/) });
      return { lrc: typeof body.lrc?.lyric === 'string' ? body.lrc.lyric : '' };
    },
  };
}
