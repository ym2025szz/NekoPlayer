# v1.0.0 发布检查清单

## 源码与隐私

- [ ] 所有工作发生在隔离公开副本，原项目保持只读且没有 `.git`
- [ ] `git status --short` 干净，`git diff --check` 通过
- [ ] Git 历史不含 `bin`、`obj`、`artifacts`、发布压缩包、数据库、音乐、日志或缓存
- [ ] 本机绝对路径、Token、私钥、API Key 和用户数据扫描为零命中
- [ ] LICENSE 与 THIRD-PARTY-NOTICES 和实际分发内容一致

## 自动化

- [ ] `dotnet restore`、Release build、全部测试通过
- [ ] Windows build 0 错误，记录真实警告数
- [ ] Linux build 0 错误，记录真实警告数
- [ ] Windows 与 Ubuntu GitHub Actions Job 均成功

## Windows 包

- [ ] `NekoPlayer.exe` 的 ProductVersion=1.0.0、FileVersion=1.0.0.0
- [ ] AppIcon、ICO、README、LICENSE、THIRD-PARTY-NOTICES 完整
- [ ] FFmpeg EXE、ffprobe 和 Shared DLL 完整，来源及许可证材料完整
- [ ] 无 PDB、Avalonia.Diagnostics、数据库、音频、日志或本机路径
- [ ] ZIP 可完整解压，隔离数据下 GUI 启动和正常关闭通过
- [ ] 播放、暂停、恢复、Seek、Stop、重播、切歌和进程释放验证通过

## Linux 包

- [ ] 主程序是可执行的 ELF 64-bit x86-64，自包含且 `ldd` 无意外 `not found`
- [ ] 包内无 `NekoPlayer.exe`、Windows FFmpeg EXE/DLL、数据库、音频、日志、PDB 或本机路径
- [ ] README、LINUX、LICENSE、THIRD-PARTY-NOTICES、AppIcon 和脚本完整
- [ ] Xvfb GUI 初始化和正常关闭通过，无残留进程
- [ ] 临时中文与空格路径的导入、重复检测和播放管线通过
- [ ] XDG 数据、配置、缓存写入发布目录之外
- [ ] 安装与卸载在临时 HOME 中通过，带空格路径可用且用户数据保留

## GitHub 与 Release

- [ ] Public 仓库默认分支为 `main`，描述和 Topics 正确
- [ ] `v1.0.0` annotated tag 指向最终绿色 CI 提交
- [ ] 正式 Release 非 Draft、非 Prerelease，标题正确
- [ ] Windows ZIP、Linux tar.gz 与 `SHA256SUMS.txt` 三个资产齐全
- [ ] 远程重新下载后的 SHA-256 与本地一致
- [ ] 远程包可解压、权限正确、内容扫描干净

## 原项目保护

- [ ] 原项目仍无 `.git`
- [ ] 正式数据库与两份已知音频哈希未变化
- [ ] 原项目旧 `artifacts` 未被清理或覆盖
- [ ] 没有残留 NekoPlayer、ffmpeg 或 ffprobe 进程
