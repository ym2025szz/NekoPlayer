# Neko music gateway

Node.js 24 本机音乐网关。启动入口为 `server.mjs`，应用通过 stdin 单行 JSON 下发随机 token 和独立数据目录。网关只绑定 `127.0.0.1`，SDK 原始日志静默，stdout 只输出一次 ready 握手。支持网易云、QQ、酷狗扫码授权，凭证由桌面端以 Windows DPAPI 加密保存；网关按平台隔离并仅在内存中持有登录凭证，不读取浏览器或环境变量中的账号 Cookie。手机确认由用户完成。

```powershell
# 从仓库根目录初始化运行时和依赖；安装脚本禁用 npm lifecycle scripts
.\setup-music-gateway.ps1
# 独立 Node 协议与权限 fixture 测试
.\tools\node\node.exe --test .\tools\music-gateway\tests\*.test.js
# 官方游客接口只读探测：只输出状态、长度与计时，不输出 URL/token/Cookie
.\tools\node\node.exe .\tools\music-gateway\scripts\live-probe.mjs
```

应用启动配置：`{ "token": "至少32个可打印ASCII字符", "dataDirectory": "绝对路径", "port": 0 }`。握手：`{ "ready": true, "port": 12345, "protocolVersion": 1 }`。每个 HTTP 请求必须带 `X-Neko-Token`；浏览器 Origin 请求被拒绝。`GET /health` 和 `GET /v1/providers` 可读健康状态与平台能力。业务路由均为 JSON POST：

| 路由 | 请求 |
| --- | --- |
| `/v1/search` | `{providerId,query,page,pageSize}` |
| `/v1/resolve` | `{providerId,providerTrackId,metadata:{}}` |
| `/v1/lyrics` | `{providerId,providerTrackId,metadata:{}}` |

搜索结果保留来源歌曲 ID、版本标签及真实平台权限字段到 `providerMetadataJson` 字符串。服务器从实时官方响应判断播放权限，客户端持久化的 metadata 不能覆盖权限、地址或时长。resolve 的 availability 为 `full`、`preview` 或 `unavailable`；试听 durationSeconds 是平台实际授予的试听长度。平台 HTTP、业务码、认证、TLS、限流、超时分别返回明确 HTTP 错误及 `{error:{code,message,retryable}}`，不会转为空搜索成功。

并发上限为 4；每分钟最多 90 个业务请求；请求体 64 KiB；上游数据 4 MiB；单上游请求 10 秒；整业务请求 12 秒；取消传递到 HTTP 请求。Node HTTP/HTTPS 和内置 fetch 复用连接。上游接口及音频源限制为所属平台域名，禁止跨域重定向转发 Cookie。

| providerId | 来源 | 2026-10-08 官方游客只读验证 |
| --- | --- | --- |
| `netease` | [@neteasecloudmusicapienhanced/api 4.41.1](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced) | 搜索/歌词成功；1357374736 返回 full 239.56 秒；1357375695 返回 preview 30 秒 |
| `qq` | [@sansenjian/qq-music-api 2.6.0](https://github.com/sansenjian/qq-music-api) | 搜索/歌词成功；000C9FCy4HUcTW 返回 full 201 秒；0039MnYb0qxYhV 返回空 purl，映射 unavailable |
| `kuwo` | [酷我当前官网](https://www.kuwo.cn/) Web 游客 Cookie / Secret 签名 | 搜索/歌词成功；19528080 返回 full 366 秒；5886682 收听付费，映射 unavailable |
| `kugou` | [MakcRe/KuGouMusicApi 固定 Git 提交](https://github.com/MakcRe/KuGouMusicApi/tree/da5ccfd9304c043085a2fd18e94ebc5c315044ab) | 实验适配；实际 SDK 搜索请求被安全验证拒绝，返回 authentication_required；播放和歌词尚未通过游客网络验收 |
| `qishui` | [guowenye/qishui-api 固定 Git 提交](https://github.com/guowenye/qishui-api/tree/e409c10fe7441a10d370da7da61d62bbc1e5126c) | 实验适配；实际 PC 游客搜索接口返回 HTTP 200 空响应，返回 unsupported_guest_operation；H5 歌词和未加密播放适配尚未通过游客网络验收 |

上述 full 表示平台授予地址和时长；音频解码、非静音 PCM 与跳转验收由仓库 OnlineVerifier 单独执行。平台权限与接口可能变化；这些记录不会被用作硬编码成功或缓存播放地址。酷狗 npm 包 1.6.2 在登记名称下不可用，因此依赖锁定到指定 HTTP 源码 tarball；汽水也使用固定提交 tarball。

网易在 SDK 导入前和每次调用都设置 `ENABLE_GENERAL_UNBLOCK=false` / `unblock=false`，只请求当前平台标准音质。QQ 判断精确歌曲 MID 的 `purl` 非空，根 code 0 不代表有播放权。酷我解析先读取当前 `musicInfo` 权限，付费或 APP 专属歌曲不发出播放请求。汽水只接受未加密音频，不调用 SDK 解密能力。

发布必需文件：`server.mjs`、`src/**`、`package.json`、`package-lock.json`、`node_modules/**`、`LICENSES/**`、本 README。`scripts/**` 和 `tests/**` 仅用于开发验证。SDK MIT 许可证、全部传递依赖许可证与固定源码摘要见 `LICENSES/THIRD-PARTY-NOTICES.md`、`LICENSES/THIRD-PARTY-NOTICES.json`。酷我仅记录官网脚本来源与摘要，不分发完整官网脚本。
