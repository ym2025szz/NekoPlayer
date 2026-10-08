import { createRequire } from 'node:module';
import { GatewayError, checkBusiness } from '../errors.js';
import { sdkBody } from '../network.js';
import { mediaUrl, track, unavailable, validateId, seconds } from '../mapping.js';
import { accountCookie, cookieObject } from '../accounts.js';

export function mapKugouResolution(body) {
  const data = body.data || body;
  const value = Array.isArray(data.url) ? data.url[0] : data.url || data.play_url;
  const url = mediaUrl(value, ['kugou.com', 'kugou.net']);
  if (!url) return unavailable('酷狗未授予此歌曲的播放链接');
  // Encrypted media, privileged codec variants and unknown timing are never offered as full audio.
  if (data.encrypted || data.encrypt || data.audio_key || data.spade_a) return unavailable('酷狗返回加密音频，当前播放器不支持此格式');
  const freePart = data.free_part || data.freePartInfo || data.free_part_info;
  if (freePart && typeof freePart === 'object') {
    const start = seconds(freePart.start ?? freePart.start_time);
    const end = seconds(freePart.end ?? freePart.end_time);
    const length = seconds(freePart.duration) || end - start;
    if (length <= 0 || length > 600) return unavailable('酷狗试听范围不可验证');
    return { availability: 'preview', url, durationSeconds: length, previewStartSeconds: start, previewDurationSeconds: length, canSeek: true, reason: '酷狗只授予试听权限' };
  }
  if (data.is_free_part === 1 || data.isFreePart === 1) return unavailable('酷狗授予了试听，但未提供可验证的试听范围');
  const duration = seconds(data.timelength ?? data.timeLength) / 1000 || seconds(data.duration);
  if (!duration) return unavailable('酷狗未提供可验证的音频时长');
  return { availability: 'full', url, durationSeconds: duration, canSeek: true };
}

export function createKugou() {
  const require = createRequire(import.meta.url);
  const local = createRequire(require.resolve('kugou-music-api/package.json'));
  const { createRequest } = local('./util/request');
  const { getGuid, calculateMid } = local('./util/util');
  const guid = getGuid();
  const device = { KUGOU_API_GUID: guid, KUGOU_API_MID: calculateMid(guid), KUGOU_API_DEV: guid.toUpperCase(), KUGOU_API_MAC: '02:00:00:00:00:00' };
  const modules = Object.fromEntries(['search','song_url','search_lyric','lyric','login_qr_key','login_qr_create','login_qr_check','user_detail'].map(n => [n, local(`./module/${n}`)]));
  const call = async (name, params, cookie = accountCookie('kugou')) => {
    let result;
    try { result = await modules[name]({ ...params, cookie: { ...device, ...cookieObject(cookie) } }, createRequest); }
    catch (error) {
      if (error?.body?.ssaCode || error?.headers?.['ssa-code']) throw new GatewayError('authentication_required', '酷狗要求游客安全验证', 403);
      if (error?.body && Number(error.body.error_code) > 0) throw new GatewayError('authentication_required', `酷狗接口被拒绝（${Number(error.body.error_code)}）`, 403);
      throw error;
    }
    const body = sdkBody(result);
    if (body?.ssaCode || result.headers?.['ssa-code']) throw new GatewayError('authentication_required', '酷狗要求游客安全验证', 403);
    checkBusiness(body, { key: 'error_code', success: [0] });
    if (Number(body.status) === 0) throw new GatewayError('upstream_business_error', '酷狗业务请求失败', 502);
    return body;
  };
  return {
    auth: {
      async start() {
        const body = await call('login_qr_key', {}, '');
        const key = body.data?.qrcode;
        if (!key) throw new GatewayError('invalid_login_response', '酷狗未返回二维码标识', 502);
        const qr = await call('login_qr_create', { key, qrimg: true }, '');
        return { key, image: qr.data?.base64, message: '使用酷狗音乐 App 扫码，在手机上确认授权' };
      },
      async check({ key }) {
        const body = await call('login_qr_check', { key }, ''); const data = body.data || {};
        if (Number(data.status) === 4 && data.token && data.userid) return { state: 'authorized', credential: Object.entries({ ...device, token: data.token, userid: data.userid }).map(([k,v]) => `${k}=${v}`).join('; '), displayName: data.nickname || '酷狗账号' };
        return { state: Number(data.status) === 0 ? 'expired' : Number(data.status) === 2 ? 'scanned' : 'waiting', message: Number(data.status) === 2 ? '已扫码，请在手机上确认授权' : Number(data.status) === 0 ? '二维码已过期，请刷新' : '等待酷狗音乐 App 扫码' };
      },
      async validate(credential) {
        const cookies = cookieObject(credential);
        if (!cookies.token || !cookies.userid) throw new GatewayError('authentication_required', '酷狗登录凭证无效，请重新扫码', 403);
        const body = await call('user_detail', {}, credential);
        if (!body.data?.userid && !body.data?.nickname) throw new GatewayError('authentication_required', '酷狗登录已失效，请重新扫码', 403);
        return { displayName: body.data.nickname || '酷狗账号' };
      },
    },
    async search({ query, page, pageSize }) {
      const body = await call('search', { keywords: query, page, pagesize: pageSize, type: 'song', privilegefilter: 0 });
      const data = body.data || body; const list = data.lists || data.info;
      if (!Array.isArray(list)) throw new GatewayError('invalid_upstream_response', '酷狗搜索结果结构无效');
      return { tracks: list.map(s => track({ id: s.FileHash || s.hash || s.Hash, title: s.SongName || s.songname || s.FileName,
        artist: s.SingerName || s.singername, album: s.AlbumName || s.album_name, duration: s.Duration || s.duration,
        cover: String(s.Image || s.image || '').replace('{size}', '300'), version: s.Suffix || '',
        metadata: { hash: s.FileHash || s.hash || s.Hash, album_id: s.AlbumID || s.album_id, album_audio_id: s.AlbumAudioID || s.album_audio_id,
          duration: s.Duration || s.duration, privilege: s.Privilege || s.privilege, pay_type: s.PayType || s.pay_type,
          audio_info: s.AudioInfo || s.audio_info, free_part: s.FreePart || s.free_part } })),
        hasMore: (page - 1) * pageSize + list.length < Number(data.total || data.total_count || 0),
        warning: '酷狗为实验游客适配；安全验证、会员权限和试听范围以平台结果为准' };
    },
    async resolve({ providerTrackId }) {
      const hash = validateId(providerTrackId, /^[a-fA-F0-9]{32}$/);
      const body = await call('song_url', { hash, quality: 128, free_part: false });
      return mapKugouResolution(body);
    },
    async lyrics({ providerTrackId }) {
      const hash = validateId(providerTrackId, /^[a-fA-F0-9]{32}$/);
      const search = await call('search_lyric', { hash, duration: 0 });
      const item = search.candidates?.[0] || search.data?.candidates?.[0];
      if (!item) return { lrc: '' };
      const body = await call('lyric', { id: item.id, accesskey: item.accesskey, fmt: 'lrc', decode: false });
      if (typeof body.content !== 'string') throw new GatewayError('invalid_upstream_response', '酷狗歌词内容无效');
      return { lrc: Buffer.from(body.content, 'base64').toString('utf8') };
    },
  };
}
