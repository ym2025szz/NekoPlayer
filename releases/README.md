# 正式版发布与安装

本项目只向用户部署正式版，版本号为 `X.Y.Z`；测试程序和验证数据不作为用户安装版本。

- 当前安装位置：`artifacts/publish/win-x64/NekoPlayer.exe`。项目根目录的“启动猫娘播放器.cmd”始终打开这个正式安装。
- 每个版本的 ZIP、SHA256、发布说明与版本信息保存在独立的 `releases/vX.Y.Z` 目录；已归档的版本包不得替换成不同内容。
- `index.json` 列出正式 release；`installed.json` 记录当前安装版本与更新前的备份目录。
- 覆盖安装先校验正式包，再解压到暂存目录；将原安装保存在 `artifacts/installation-backups`，部署并校验新版。失败会恢复原安装。
- 用户曲库、歌单、历史、设置和授权凭证保留在原有 Windows 用户数据目录，不打入 release 包。

发布流程先运行 `publish-win-x64.ps1`，输出目录为与项目版本一致的 `artifacts/publish/win-x64-vX.Y.Z`，不会直接替换正在运行的正式安装。将该目录打包为 `artifacts/NekoPlayer-vX.Y.Z-win-x64.zip`，旁边保存同名 `.zip.sha256` 校验文件后，运行 `install-release.ps1 -Version X.Y.Z -Launch`。

v1.1.1 及更早的播放器需要先正常关闭窗口。v1.2.0 及以后的运行实例，由安装脚本调用当前安装程序的 `--exit-for-update`，等待同一 Windows 用户正常数据目录的播放、网关和应用服务释放；失败或超时不会替换安装文件。新包不会提前启动来抢占已有实例。

每个 `releases/vX.Y.Z` 都保存对应 ZIP、SHA256、`release.json`、发布说明、验收记录与 README。已存在的 ZIP、校验值和发布记录不会被不同内容覆盖；历史版本在 `index.json` 中继续保留，`CurrentVersion` 只在安装成功后更新。

Windows x64 发布包自带 .NET 8、FFmpeg Shared 和 Node.js。目标 Windows SDK 为 19041，最低声明支持 Windows 10 1809（17763）；不同 Windows 版本仍需实机覆盖。隔离验证工具、生成音频、测试账号和用户配置不属于发布包。
