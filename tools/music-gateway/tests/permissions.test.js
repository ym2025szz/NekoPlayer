import test from 'node:test';
import assert from 'node:assert/strict';
import { mapNeteaseResolution, mapQqResolution } from '../src/mapping.js';
import { mapKugouResolution } from '../src/providers/kugou.js';
import { mapQishuiResolution } from '../src/providers/qishui.js';
import { checkBusiness, transportError } from '../src/errors.js';
import { createKuwoSecret } from '../src/providers/kuwo.js';

test('Kuwo visitor signature matches a deterministic public-site fixture', () => {
  // Value and salt are synthetic. Expected result was captured from the source asset recorded in LICENSES.
  assert.equal(createKuwoSecret('00011122233344455566677788899900', 23450000), '1360bb9320165a19ec1693a27f8e85183dfac7e101cdab3031b843a0bfbf55bc0165d190');
});

test('Netease observed trial payload exposes only the granted 30 seconds', () => {
  const response = { id: 1850000000, code: 200, fee: 1, time: 254000, url: 'https://m801.music.126.net/example.mp3', freeTrialInfo: { start: 90, end: 120 } };
  const resolved = mapNeteaseResolution(response, { dt: 254000 });
  assert.equal(resolved.availability, 'preview'); assert.equal(resolved.durationSeconds, 30);
  assert.equal(resolved.previewStartSeconds, 90); assert.equal(resolved.previewDurationSeconds, 30);
});

test('Netease paid-song null URL stays unavailable despite successful business code', () => {
  const resolved = mapNeteaseResolution({ code: 200, fee: 1, url: null, time: 252000 });
  assert.equal(resolved.availability, 'unavailable'); assert.equal(resolved.durationSeconds, 0); assert.equal(resolved.url, undefined);
});

test('Netease no trial and platform timing permits full guest playback', () => {
  const resolved = mapNeteaseResolution({ code: 200, fee: 0, time: 241050, url: 'https://m801.music.126.net/example.mp3', freeTrialInfo: null });
  assert.equal(resolved.availability, 'full'); assert.equal(resolved.durationSeconds, 241.05);
});

test('QQ observed member payload with code 0 and empty purl is unavailable', () => {
  const body = { code: 0, req_0: { code: 0, data: { sip: ['https://dl.stream.qqmusic.qq.com/'], midurlinfo: [{ songmid: '0039MnYb0qxYhV', purl: '', vkey: 'fixture-value', filename: 'M500fixture.mp3' }] } } };
  const resolved = mapQqResolution(body, '0039MnYb0qxYhV', { interval: 269 });
  assert.equal(resolved.availability, 'unavailable'); assert.equal(resolved.url, undefined);
});

test('QQ full entitlement requires the exact requested mid and public platform URL', () => {
  const body = { code: 0, req_0: { code: 0, data: { sip: ['https://dl.stream.qqmusic.qq.com/'], midurlinfo: [{ songmid: '000C9FCy4HUcTW', purl: 'M500fixture.mp3' }] } } };
  const resolved = mapQqResolution(body, '000C9FCy4HUcTW', { interval: 231 });
  assert.equal(resolved.availability, 'full'); assert.equal(resolved.durationSeconds, 231);
  assert.equal(mapQqResolution(body, '0039MnYb0qxYhV', { interval: 269 }).availability, 'unavailable');
});

test('cross-provider and arbitrary playback URLs are rejected', () => {
  assert.equal(mapNeteaseResolution({ time: 123000, url: 'https://dl.stream.qqmusic.qq.com/fixture.mp3' }).availability, 'unavailable');
  assert.equal(mapNeteaseResolution({ time: 123000, url: 'http://127.0.0.1/fixture.mp3' }).availability, 'unavailable');
});

test('Kugou incomplete trial and encrypted media never claim full entitlement', () => {
  assert.equal(mapKugouResolution({ url: ['https://fs.kugou.com/example.mp3'], is_free_part: 1, timelength: 240000 }).availability, 'unavailable');
  assert.equal(mapKugouResolution({ url: ['https://fs.kugou.com/example.mp3'], encrypted: true, timelength: 240000 }).availability, 'unavailable');
  const preview = mapKugouResolution({ url: ['https://fs.kugou.com/example.mp3'], free_part: { start: 50, end: 80 }, timelength: 240000 });
  assert.equal(preview.availability, 'preview'); assert.equal(preview.durationSeconds, 30);
});

test('Qishui encrypted public metadata cannot enter playback', () => {
  const body = { status_code: 0, track_player: { video_model: JSON.stringify({ duration: 241, video_list: [{ main_url: Buffer.from('https://v.example.douyinvod.com/audio.mp4').toString('base64'), spade_a: 'encrypted-key-fixture' }] }) } };
  const resolved = mapQishuiResolution(body); assert.equal(resolved.availability, 'unavailable'); assert.equal(resolved.url, undefined);
});

test('business, authentication, TLS, throttling and timeout remain distinguishable', () => {
  assert.throws(() => checkBusiness({ code: 301 }), e => e.code === 'authentication_required' && e.status === 403);
  assert.throws(() => checkBusiness({ code: -999 }), e => e.code === 'upstream_business_error');
  assert.equal(transportError({ response: { status: 429 } }).status, 429);
  assert.equal(transportError({ code: 'CERT_HAS_EXPIRED' }).code, 'tls_error');
  assert.equal(transportError({ name: 'AbortError' }).status, 504);
});
