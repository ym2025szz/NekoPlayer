# 在线验证

常规 `dotnet test` 使用离线 fixture，不联网，也不会发声。这个工具必须显式提供 `--online` 才启动真实 gateway 并发出五个平台的游客请求。

```powershell
dotnet run --project tools/NekoPlayer.OnlineVerifier -- --online
```

可选参数：`--query 周杰伦`、`--providers netease,qq,kuwo,kugou,qishui`、`--timeout-seconds 25`、`--source-timeout-seconds 110`、`--ffmpeg-directory <目录>`、`--no-media`。每项能力和每个平台都有超时；某个平台的失败不会中断其他平台。Ctrl+C 取消请求并关闭由本次工具创建的进程。

可提供已知稳定标识：`--track-ids netease=1357374736,qq=000C9FCy4HUcTW,kuwo=19528080`。这些是曲目标识，播放地址仍由本次真实解析获取。若标识没有出现在本次搜索中，报告明确标记 `explicit-id`；不会把直接解析冒充搜索验证。

验证按平台记录搜索和首曲歌词，逐项记录首屏最多五首曲目的权限解析，再加显式提供的稳定标识。首曲限制仍保留在能力表中；不会为了得到可播放曲目而删掉限制证据。媒体检查优先选声明 Full 的会话。搜索失败且没有指定标识时，歌词与权限解析明确记为 `skipped/no-search-track`，不会伪造成功。试听、无权限与源错误分别记录。

媒体验证使用仓库 `tools/ffmpeg` 下的 ffprobe 和 FFmpeg：读取音频时长，将最多两秒音频解码为内存管道中的 PCM，再按可 seek 声明在允许片段的末段执行一次输入偏移解码。试听 URL 已代表裁剪片段，解码从片段内 0 开始；`PreviewStart` 仅记录歌词时间偏移，不作为输入 seek 偏移。媒体子进程单独清除继承的代理环境变量并对 HTTPS 明确设置 `-tls_verify 1`，不改系统配置或网关 API 网络策略。它不会构造播放器、打开输出音频设备或改变系统音量。报告不把 URL、平台声明的完整权限或一段 PCM 当作整首播放成功；应用暂停、续播、队列和播放状态由其他测试验证。媒体失败仅输出固定错误分类、退出码和可识别的 HTTP 状态码；原始 stderr 不落盘。

输出为 `artifacts/test-reports/online-verification.json` 和 `.md`。报告不包含原始 URL、签名、token、cookie、请求头、响应正文或异常消息。工具使用独立临时数据目录，不读取真实账号凭据、不登录、不注册、不更改音乐库。成功 HTTP 的具体状态码没有从运行时接口暴露，报告明确标记未暴露；错误附带状态码时记录精确数字。

退出码：`0` 表示请求的全部服务能力及启用的媒体检查通过；`1` 表示失败、空结果、试听限制、缺失能力或不完整证据；`2` 表示未显式启用联网或参数无效。受平台网络与游客权限影响，非零退出并不等于本地播放器失效。
