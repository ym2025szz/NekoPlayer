请把 Windows x64 版本的 ffmpeg.exe 与 ffprobe.exe 放在本目录。

开发运行时程序会向上查找 tools\ffmpeg；发布脚本会将两个文件复制到：
artifacts\publish\win-x64\ffmpeg\

本项目不会自动下载 FFmpeg，也不会替你选择 LGPL/GPL 构建。请从可信来源取得二进制文件，并自行核对所用构建的许可证义务。
