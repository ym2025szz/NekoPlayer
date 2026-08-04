# Changelog

本项目按语义化版本记录面向用户的重要变化。

## [1.0.0] - 2026-08-04

### Added

- 首个公开正式版本，提供 Windows x64 与 Linux x64 独立发布包。
- Linux XDG 数据、配置和缓存目录支持。
- Linux 系统 FFmpeg/ffprobe 定位与 FFmpeg ALSA 播放后端。
- Linux 用户级 `install-linux.sh`、`uninstall-linux.sh` 与桌面入口。
- Windows/Ubuntu GitHub Actions 构建、测试、发布包、播放管线与 GUI 启动验证。

### Changed

- 打开目录操作按平台使用 `explorer.exe` 或 `xdg-open`，参数使用安全的参数列表传递。
- Windows 保持原有 `%LocalAppData%\NekoPlayer` 数据目录和 NAudio 输出后端。
- 默认封面统一回退到共享 AppIcon，真实封面继续优先且在后台解码。
- 收藏按钮改用独立矢量图形，并同步所有主要页面的收藏状态。

### Fixed

- 修复真实鼠标 Seek 命中范围、播放态与暂停态 Seek 状态保持。
- 修复 Stop 后不能稳定从头重播的问题。
- 修复歌单批量添加重复关系、播放队列序号和立即播放边界。
- 修复无封面、封面丢失、权限异常或图片损坏时的显示回退。

### Security and privacy

- 公开仓库与发布包排除数据库、音乐、缓存、日志、本机路径和构建临时文件。
- Linux 卸载默认只移除程序与桌面入口，不删除用户数据。
