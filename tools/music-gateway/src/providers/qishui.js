import { createRequire } from 'node:module';
import { GatewayError, checkBusiness, transportError } from '../errors.js';
import { requests } from '../network.js';
import { artists, mediaUrl, track, unavailable, validateId, seconds } from '../mapping.js';

function business(body) {
  checkBusiness(body, { key: 'status_code', success: [0] });
  checkBusiness(body, { key: 'code', success: [0,200] });
  if (body?.error_code !== undefined) checkBusiness(body, { key: 'error_code', success: [0] });
  return body;
}

export function mapQishuiResolution(body) {
  business(body);
  const player = body.track_player || body.player_info || body.player_infos?.[0];
  let model = player?.video_model;
  if (typeof model === 'string') { try { model = JSON.parse(model); } catch { return unavailable('汽水返回的音频信息无效'); } }
  const options = model?.video_list;
  const encrypted = value => {
    if (!value || typeof value !== 'object') return false;
    return Object.entries(value).some(([key, entry]) => /^(spade_?a|play_?auth|license_?url|encrypt_?type|encrypted|encryption)$/i.test(key)
      ? entry !== null && entry !== undefined && entry !== '' && entry !== false && entry !== 0 && entry !== '0'
      : entry && typeof entry === 'object' && encrypted(entry));
  };
  const audio = Array.isArray(options) ? options.find(x => !encrypted(x) && x.main_url) : null;
  if (!audio) return unavailable(player?.video_model ? '汽水未提供未加密的游客音频，游客适配不支持解密' : '汽水未授予游客播放信息（可能需要登录或会员）');
  if (encrypted(model) || encrypted(player)) return unavailable('汽水返回加密音频，游客适配不支持解密');
  let value = audio.main_url;
  if (typeof value !== 'string') return unavailable('汽水未返回可播放地址');
  if (!value.startsWith('http')) { try { value = Buffer.from(value, 'base64').toString('utf8'); } catch { return unavailable('汽水音频地址无效'); } }
  const url = mediaUrl(value, ['douyinvod.com','douyin.com','qishui.com','bytecdn.cn','bytecdntp.com','bytedance.net']);
  if (!url) return unavailable('汽水音频地址不在平台允许域名内');
  const duration = seconds(model.duration || audio.duration);
  if (!duration) return unavailable('汽水未提供可验证的音频时长');
  const preview = body.preview_info || player.preview_info;
  if (preview) {
    const start = seconds(preview.start || preview.start_time); const length = seconds(preview.duration) || seconds(preview.end || preview.end_time) - start;
    if (length <= 0) return unavailable('汽水试听范围不可验证');
    return { availability: 'preview', url, durationSeconds: length, previewStartSeconds: start, previewDurationSeconds: length, canSeek: true, reason: '汽水只授予试听权限' };
  }
  return { availability: 'full', url, durationSeconds: duration, canSeek: true };
}

export function createQishui() {
  process.env.QISHUI_ENABLE_DECRYPT = 'false';
  const require = createRequire(import.meta.url);
  const local = createRequire(require.resolve('qishui-api/package.json'));
  const { QishuiClient } = local('./src/qishuiClient');
  const { normalizeSearchGroups, normalizeTrack } = local('./src/normalizers');
  const client = new QishuiClient({ lunaApiHost: 'https://beta-luna.douyin.com', pcApiHost: 'https://api.qishui.com', musicShareHost: 'https://music.douyin.com', timeoutMs: 10000 });
  const call = async (name, query) => {
    try {
      const body = await client[name](query, {});
      if (!body) throw new GatewayError('unsupported_guest_operation', '汽水游客接口未返回数据，可能要求设备认证或登录', 403);
      return body;
    }
    catch (error) { throw requests.getStore()?.transportError || transportError(error); }
  };
  return {
    async search({ query, page, pageSize }) {
      const body = business(await call('search', { keywords: query, cursor: (page - 1) * pageSize, count: pageSize, limit: pageSize }));
      const list = Array.isArray(body.tracks) ? body.tracks.map(normalizeTrack) : normalizeSearchGroups(body).tracks;
      if (!Array.isArray(body.tracks) && !Array.isArray(body.result_groups)) throw new GatewayError('unsupported_guest_operation', '汽水未开放此游客搜索能力', 403);
      return { tracks: list.filter(Boolean).map(s => track({ id: s.id, title: s.name, artist: artists(s.artists), album: s.album?.name,
        duration: seconds(s.duration) / 1000, cover: s.album?.cover_url,
        metadata: { id: s.id, duration: s.duration, media_type: s.media_type, vid: s.vid, privilege: s.raw?.privilege || null,
          pay_type: s.raw?.pay_type, copyright: s.raw?.copyright, play_control: s.raw?.play_control || null } })),
        hasMore: Boolean(body.has_more), warning: '汽水为实验游客适配；登录或加密音频不可播放' };
    },
    async resolve({ providerTrackId }) {
      const id = validateId(providerTrackId, /^\d{1,30}$/);
      const body = business(await call('h5SeoTrack', { track_id: id }));
      return mapQishuiResolution(body);
    },
    async lyrics({ providerTrackId }) {
      const id = validateId(providerTrackId, /^\d{1,30}$/);
      const body = business(await call('h5SeoTrack', { track_id: id }));
      const lrc = body.lyric?.content;
      if (typeof lrc !== 'string') {
        if (!body.seo_track) throw new GatewayError('unsupported_guest_operation', '汽水未开放此游客歌词能力', 403);
        return { lrc: '' };
      }
      return { lrc };
    },
  };
}
