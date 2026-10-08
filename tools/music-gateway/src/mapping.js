import { GatewayError } from './errors.js';

export const text = (x, max = 1000) => typeof x === 'string' ? x.replace(/<[^>]*>/g, '').slice(0, max) : x == null ? '' : String(x).slice(0, max);
export const seconds = x => Number.isFinite(Number(x)) && Number(x) > 0 ? Number(x) : 0;
export const artists = x => Array.isArray(x) ? x.map(a => text(a?.name || a?.singername)).filter(Boolean).join(' / ') : text(x);
export function track(value) {
  return { providerTrackId: text(value.id, 100), title: text(value.title), artist: text(value.artist), album: text(value.album),
    durationSeconds: seconds(value.duration), coverUrl: coverUrl(value.cover), versionLabel: text(value.version, 100),
    availability: value.availability || 'unknown', restrictionReason: text(value.reason, 300),
    providerMetadataJson: JSON.stringify(value.metadata || {}) };
}
export function coverUrl(value) { try { const u = new URL(value); return ['http:', 'https:'].includes(u.protocol) && !u.username && !u.password ? u.href : ''; } catch { return ''; } }
export function mediaUrl(value, domains) {
  try { const u = new URL(value); if (['http:','https:'].includes(u.protocol) && !u.username && !u.password && !u.port && domains.some(d => u.hostname === d || u.hostname.endsWith(`.${d}`))) return u.href; }
  catch {} return '';
}
export function unavailable(reason) { return { availability: 'unavailable', durationSeconds: 0, canSeek: false, reason }; }

export function mapNeteaseResolution(data, metadata = {}) {
  const url = mediaUrl(data?.url, ['music.126.net', 'music.163.com', '126.net']);
  if (!url) return unavailable(Number(data?.code) === -110 ? '版权或地区限制' : '平台未提供游客可播放链接（可能需要会员、登录或存在版权限制）');
  const trial = data.freeTrialInfo;
  if (trial && typeof trial === 'object') {
    // freeTrialInfo uses seconds; the separate `time` / song.dt fields use milliseconds.
    const start = seconds(trial.start); const end = seconds(trial.end);
    if (end <= start) return unavailable('平台返回的试听长度无效');
    return { availability: 'preview', url, durationSeconds: end - start, previewStartSeconds: start, previewDurationSeconds: end - start, canSeek: true, reason: '平台只授予试听权限' };
  }
  const duration = seconds(data.time) / 1000 || seconds(metadata.durationSeconds) || seconds(metadata.dt) / 1000;
  if (!duration) return unavailable('平台未提供可验证的音频时长');
  return { availability: 'full', url, durationSeconds: duration, canSeek: true };
}

export function mapQqResolution(body, mid, metadata = {}) {
  const root = body?.data || body;
  const info = root?.req_0?.data?.midurlinfo?.find(x => x.songmid === mid);
  const sip = root?.req_0?.data?.sip;
  // Root code 0 is not an entitlement. A non-empty purl is required.
  if (!info?.purl || !Array.isArray(sip) || !sip.length) return unavailable('QQ 音乐未授予此歌曲的播放链接（可能需要会员、登录或存在版权限制）');
  const base = sip.find(x => typeof x === 'string' && x.startsWith('https://')) || sip.find(x => typeof x === 'string' && x.startsWith('http://'));
  let value; try { value = new URL(info.purl, base).href; } catch { return unavailable('QQ 音乐返回的播放地址无效'); }
  const url = mediaUrl(value, ['qq.com', 'qqmusic.qq.com']);
  if (!url) return unavailable('QQ 音乐播放地址不在平台允许域名内');
  const duration = seconds(metadata.durationSeconds) || seconds(metadata.interval);
  if (!duration) return unavailable('QQ 音乐未提供可验证的音频时长');
  return { availability: 'full', url, durationSeconds: duration, canSeek: true, headers: { Referer: 'https://y.qq.com/' } };
}

export function validateId(id, pattern) { if (typeof id !== 'string' || !pattern.test(id)) throw new GatewayError('invalid_track_id', '歌曲标识无效', 400); return id; }
