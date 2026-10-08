import { randomUUID } from 'node:crypto';
import { checkBusiness, GatewayError } from '../errors.js';
import { boundedFetch, jsonRequest } from '../network.js';
import { mediaUrl, track, unavailable, validateId, seconds, text } from '../mapping.js';

const cookieName = 'Hm_Iuvt_cdb524f42f23cer9b268564v7y735ewrq2324';
const userAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36';

// The public website combines its server-issued guest cookie with a random salt.
// Source reference and asset digest are recorded in LICENSES/kuwo-web-source.json.
export function createKuwoSecret(cookie, salt = Math.round(1e9 * Math.random()) % 1e8) {
  const digits = [...cookieName].map(c => c.charCodeAt(0)).join('');
  const stride = Math.floor(digits.length / 5);
  const multiplier = Number([1,2,3,4,5].map(i => digits.charAt(i * stride)).join(''));
  const increment = Math.ceil(cookieName.length / 2);
  const modulus = 2 ** 31 - 1;
  let seed = digits + salt;
  // parseInt's prefix semantics also apply after a long seed becomes exponential notation.
  while (seed.length > 10) seed = String(Number.parseInt(seed.slice(0, 10), 10) + Number.parseInt(seed.slice(10), 10));
  let state = (multiplier * Number(seed) + increment) % modulus;
  let output = '';
  for (let i = 0; i < cookie.length; i++) {
    output += (cookie.charCodeAt(i) ^ Math.floor(state / modulus * 255)).toString(16).padStart(2, '0');
    state = (multiplier * state + increment) % modulus;
  }
  return output + salt.toString(16).padStart(8, '0');
}

export function createKuwo() {
  let guest = null; let initialization = null;
  async function session() {
    if (guest && Date.now() - guest.at < 20 * 60 * 1000) return guest;
    if (initialization) return initialization;
    initialization = (async () => {
      const response = await boundedFetch('https://www.kuwo.cn/', { headers: { 'User-Agent': userAgent } });
      if (!response.ok) throw new GatewayError('guest_session_failed', '酷我游客会话初始化失败', 502, true);
      const pairs = response.headers.getSetCookie().map(x => x.split(';')[0]);
      const value = pairs.find(x => x.startsWith(`${cookieName}=`))?.slice(cookieName.length + 1);
      if (!value) throw new GatewayError('authentication_required', '酷我未下发游客 Web 会话', 403);
      guest = { value, cookie: pairs.join('; '), at: Date.now() }; return guest;
    })().finally(() => { initialization = null; });
    return initialization;
  }
  async function api(path, params) {
    const s = await session();
    const query = new URLSearchParams({ ...params, httpsStatus: '1', reqId: randomUUID(), plat: 'web_www' });
    const { body } = await jsonRequest(`https://www.kuwo.cn${path}?${query}`, { headers: { 'User-Agent': userAgent, Referer: 'https://www.kuwo.cn/', Cookie: s.cookie, Secret: createKuwoSecret(s.value) } });
    if (body?.success === false || [-1,-111].includes(Number(body?.code))) { guest = null; throw new GatewayError('authentication_required', '酷我游客 Web 会话或签名被平台拒绝', 403, true); }
    return checkBusiness(body, { success: [200,2001] });
  }
  return {
    async search({ query, page, pageSize }) {
      // Current www search page uses the public /search route, with zero-based page numbers.
      const s = await session();
      const params = new URLSearchParams({ vipver: '1', client: 'kt', ft: 'music', cluster: '0', strategy: '2012', encoding: 'utf8', rformat: 'json', mobi: '1', issubtitle: '1', show_copyright_off: '1', pn: String(page - 1), rn: String(pageSize), all: query });
      const { body } = await jsonRequest(`https://www.kuwo.cn/search/searchMusicBykeyWord?${params}`, { headers: { 'User-Agent': userAgent, Referer: 'https://www.kuwo.cn/search/list', Cookie: s.cookie } });
      if (body?.success === false) throw new GatewayError('authentication_required', '酷我拒绝游客搜索请求', 403);
      const list = body.abslist;
      if (!Array.isArray(list)) throw new GatewayError('invalid_upstream_response', '酷我搜索结果结构无效');
      return { tracks: list.map(s => track({ id: String(s.MUSICRID || '').replace(/^MUSIC_/, ''), title: s.SONGNAME || s.NAME, artist: s.ARTIST, album: s.ALBUM, duration: s.DURATION,
        cover: s.web_albumpic_short ? `https://img2.kuwo.cn/star/albumcover/${s.web_albumpic_short}` : s.web_artistpic_short ? `https://img1.kuwo.cn/star/starheads/${s.web_artistpic_short}` : '',
        version: s.version || '', availability: Number(s.ONLINE) === 0 || s.isListenFee === true ? 'unavailable' : 'unknown',
        reason: Number(s.ONLINE) === 0 ? '歌曲已下架' : s.isListenFee === true ? '酷我 Web 标注此歌曲需要付费收听' : '',
        metadata: { rid: String(s.MUSICRID || '').replace(/^MUSIC_/, ''), duration: s.DURATION, online: s.ONLINE, isListenFee: s.isListenFee, pay: s.pay || null,
          payInfo: s.payInfo || null, tpay: s.tpay, fpay: s.fpay, opay: s.opay, formats: s.FORMATS || s.formats } })),
        hasMore: (page - 1) * pageSize + list.length < Number(body.TOTAL || 0) };
    },
    async resolve({ providerTrackId }) {
      const id = validateId(providerTrackId, /^\d{1,20}$/);
      const infoResponse = await api('/api/www/music/musicInfo', { mid: id });
      if (Number(infoResponse.code) === 2001) return unavailable('版权方要求此歌曲仅限新版酷我 APP 收听');
      const info = infoResponse.data;
      if (!info || typeof info !== 'object') throw new GatewayError('invalid_upstream_response', '酷我歌曲权限数据无效');
      if (info.isListenFee === true || String(info.isListenFee) === '1') return unavailable('酷我 Web 标注此歌曲需要付费收听');
      if (Number(info.online) === 0) return unavailable('酷我歌曲已下架或无版权');
      const body = await api('/api/v1/www/music/playUrl', { mid: id, type: 'music' });
      const data = body.data;
      const url = mediaUrl(data?.url, ['kuwo.cn', 'sycdn.kuwo.cn', 'kwcdn.kuwo.cn']);
      if (!url) return unavailable('酷我未提供游客可播放链接');
      const duration = seconds(info.duration);
      if (!duration) return unavailable('酷我未提供可验证的音频时长');
      return { availability: 'full', url, durationSeconds: duration, canSeek: true, headers: { Referer: 'https://www.kuwo.cn/' } };
    },
    async lyrics({ providerTrackId }) {
      const id = validateId(providerTrackId, /^\d{1,20}$/);
      const body = await api('/openapi/v1/www/lyric/getlyric', { musicId: id });
      const lines = body.data?.lrclist;
      if (!Array.isArray(lines)) { if (body.data && body.data.lrclist === null) return { lrc: '' }; throw new GatewayError('invalid_upstream_response', '酷我歌词结构无效'); }
      return { lrc: lines.map(x => {
        const time = Math.max(0, seconds(x.time)); const min = Math.floor(time / 60).toString().padStart(2, '0');
        return `[${min}:${(time % 60).toFixed(2).padStart(5, '0')}]${text(x.lineLyric, 3000)}`;
      }).join('\n') };
    },
  };
}
