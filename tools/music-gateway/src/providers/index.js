import { createNetease } from './netease.js';
import { createQq } from './qq.js';
import { createKuwo } from './kuwo.js';
import { createKugou } from './kugou.js';
import { createQishui } from './qishui.js';

export async function createProviders() {
  const entries = [
    ['netease', '网易云音乐', false, createNetease()], ['qq', 'QQ 音乐', false, await createQq()], ['kuwo', '酷我音乐', false, createKuwo()],
    ['kugou', '酷狗音乐', true, createKugou()], ['qishui', '汽水音乐', true, createQishui()],
  ];
  return new Map(entries.map(([id, name, experimental, api]) => [id, { id, name, experimental, canSearch: true, canPlay: true,
    canGetLyrics: true, status: 'unverified', message: experimental ? '实验游客适配，能力随平台公开权限变化' : '游客模式，播放权限以平台实时结果为准', api }]));
}
