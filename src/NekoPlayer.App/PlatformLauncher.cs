using System.Diagnostics;

namespace NekoPlayer.App;

public static class PlatformLauncher
{
    public static ProcessStartInfo CreateOpenDirectoryStartInfo(string path, bool isWindows)
    {
        var fullPath = Path.GetFullPath(path);
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "explorer.exe" : "xdg-open",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(fullPath);
        return startInfo;
    }

    public static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        using var process = Process.Start(CreateOpenDirectoryStartInfo(path, OperatingSystem.IsWindows()));
        if (process is null) throw new InvalidOperationException("无法启动系统文件管理器。");
    }
}
