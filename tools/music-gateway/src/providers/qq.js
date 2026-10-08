import { checkBusiness, GatewayError, transportError } from '../errors.js';
import { sdkBody, sdkOptions, jsonRequest } from '../network.js';
import { artists, mapQqResolution, track, validateId } from '../mapping.js';
import { accountCookie, cookieObject } from '../accounts.js';

export function musicSessionCookie(cookie) {
  const values = cookieObject(cookie);
  const uin = values.qqmusic_uin || values.uin;
  const key = values.qqmusic_key || values.qm_keyst;
  if (!uin || !key) throw new GatewayError('authentication_required', 'QQ 音乐未返回有效账号凭证，请重新扫码', 403);
  // Retain only music-session keys; QQ login handoff cookies are not needed after authorization.
  const session = { uin, qqmusic_uin: uin, qm_keyst: key, qqmusic_key: key };
  if (values.tmeLoginType) session.tmeLoginType = values.tmeLoginType;
  return Object.entries(session).map(([name, value]) => `${name}=${value}`).join('; ');
}

export async function createQq() {
  const sdk = await import('@sansenjian/qq-music-api/sdk');
  const songs = new Map();
  const remember = s => { const mid = s.songmid || s.mid; if (mid) { songs.set(mid, s); if (songs.size > 600) songs.delete(songs.keys().next().value); } };
  async function songInfo(mid) {
    if (Number(songs.get(mid)?.interval) > 0) return songs.get(mid);
    const payload = { comm: { ct: 24, cv: 0, uin: 0, format: 'json' }, req_0: { module: 'music.pf_song_detail_svr', method: 'get_song_detail_yqq', param: { song_mid: mid, song_type: 0 } } };
    const { body } = await jsonRequest(`https://u.y.qq.com/cgi-bin/musicu.fcg?data=${encodeURIComponent(JSON.stringify(payload))}`);
    checkBusiness(body); checkBusiness(body.req_0);
    const info = body.req_0?.data?.track_info;
    if (!info) throw new GatewayError('invalid_upstream_response', 'QQ 音乐歌曲详情结构无效');
    remember(info); return info;
  }
  return {
    auth: {
      async start() {
        const body = sdkBody(await sdk.getLoginQr());
        return { image: body.img, ptqrtoken: body.ptqrtoken, qrsig: body.qrsig, message: '使用手机 QQ 扫码，在手机上确认 QQ 音乐授权' };
      },
      async check(login) {
        const body = sdkBody(await sdk.checkLoginQr(login));
        if (body.isOk && body.session?.cookie) {
          const credential = musicSessionCookie(body.session.cookie);
          return { state: 'authorized', credential, displayName: 'QQ 音乐账号' };
        }
        return { state: body.refresh ? 'expired' : 'waiting', message: body.refresh ? '二维码已失效，请刷新' : '等待手机 QQ 扫码与确认' };
      },
      async validate(credential) {
        const cookies = cookieObject(credential);
        if (!(cookies.uin || cookies.qqmusic_uin) || !(cookies.qm_keyst || cookies.qqmusic_key)) throw new GatewayError('authentication_required', 'QQ 音乐登录凭证已失效，请重新扫码', 403);
        // QQ has no dependable public session-validation endpoint; playback checks the restored credential.
        return { displayName: 'QQ 音乐账号（已恢复凭证）' };
      },
    },
    async search({ query, page, pageSize }) {
      const option = sdkOptions(); option.headers.Cookie = accountCookie('qq');
      const raw = sdkBody(await sdk.search({ key: query, page, limit: pageSize, option }));
      const body = checkBusiness(raw.response || raw);
      const list = body.data?.song?.list;
      if (!Array.isArray(list)) throw new GatewayError('invalid_upstream_response', 'QQ 音乐搜索结果结构无效');
      list.forEach(remember);
      return { tracks: list.map(s => track({ id: s.songmid || s.mid, title: s.songname || s.name, artist: artists(s.singer),
        album: s.albumname || s.album?.name, duration: s.interval, cover: s.albummid || s.album?.mid ? `https://y.gtimg.cn/music/photo_new/T002R300x300M000${s.albummid || s.album.mid}.jpg` : '',
        version: s.songtitle && s.songtitle !== s.songname ? s.songtitle : '',
        reason: Number(s.pay?.pay_play) === 1 ? '平台标注付费，实际权限以解析结果为准' : '',
        metadata: { songmid: s.songmid || s.mid, songid: s.songid || s.id, interval: s.interval, media_mid: s.strMediaMid || s.file?.media_mid,
          pay: s.pay || null, action: s.action || null, switch: s.switch, status: s.status, belongCD: s.belongCD, nt: s.nt } })),
        hasMore: (page - 1) * pageSize + list.length < Number(body.data.song.totalnum || 0) };
    },
    async resolve({ providerTrackId }) {
      const mid = validateId(providerTrackId, /^[a-zA-Z0-9]{8,30}$/);
      const raw = sdkBody(await sdk.getPlayUrl({ songmid: mid, quality: 128, resType: 'detail', cookie: accountCookie('qq'), option: sdkOptions() }));
      const body = raw.data || raw;
      checkBusiness(body); if (body.req_0) checkBusiness(body.req_0);
      if (!body.req_0?.data || !Array.isArray(body.req_0.data.midurlinfo)) throw new GatewayError('invalid_upstream_response', 'QQ 音乐未返回播放权限数据');
      const actual = body.req_0.data.midurlinfo.find(x => x.songmid === mid);
      const interval = actual?.purl ? (await songInfo(mid)).interval : 0;
      return mapQqResolution(body, mid, { interval });
    },
    async lyrics({ providerTrackId }) {
      const mid = validateId(providerTrackId, /^[a-zA-Z0-9]{8,30}$/);
      const song = songs.get(mid) || await songInfo(mid);
      const raw = sdkBody(await sdk.lyric({ songmid: mid, songid: String(song?.songid || song?.id || ''), isFormat: false, cookie: accountCookie('qq'), option: sdkOptions() }));
      const body = checkBusiness(raw.response || raw);
      checkBusiness(body, { key: 'retcode', success: [0] });
      checkBusiness(body, { key: 'subcode', success: [0] });
      const lyric = body.lyric || body.lrc || '';
      if (typeof lyric !== 'string') throw new GatewayError('invalid_upstream_response', 'QQ 音乐歌词结构无效');
      return { lrc: lyric };
    },
  };
}
