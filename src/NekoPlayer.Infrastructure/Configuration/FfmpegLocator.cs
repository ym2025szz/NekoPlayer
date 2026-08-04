using System.Diagnostics;
using FFMpegCore;
using NekoPlayer.Core.Interfaces;
using Serilog;

namespace NekoPlayer.Infrastructure.Configuration;

public sealed class FfmpegLocator : IFfmpegLocator
{
    public const string BinaryDirectoryOverrideEnvironmentVariable = "NEKOPLAYER_FFMPEG_DIR";

    private static readonly string[] RequiredWindowsSharedLibraries =
        ["avcodec-*.dll", "avformat-*.dll", "avutil-*.dll", "swresample-*.dll"];

    private readonly IUserDataPaths _paths;
    private readonly bool _isWindows;
    private volatile bool _processValidated;

    public FfmpegLocator(IUserDataPaths paths)
        : this(paths, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable(BinaryDirectoryOverrideEnvironmentVariable), Environment.GetEnvironmentVariable("PATH"))
    {
    }

    public FfmpegLocator(IUserDataPaths paths, bool isWindows, string? overrideDirectory, string? pathEnvironment)
    {
        _paths = paths;
        _isWindows = isWindows;
        BinaryDirectory = FindBinaryDirectory(isWindows, overrideDirectory, pathEnvironment);
        HasSharedLibraries = CheckPlatformLibraries(BinaryDirectory, isWindows);
        StatusMessage = BuildFileStatus();
    }

    public string BinaryDirectory { get; }
    public string FfmpegPath => Path.Combine(BinaryDirectory, GetExecutableNames(_isWindows).Ffmpeg);
    public string FfprobePath => Path.Combine(BinaryDirectory, GetExecutableNames(_isWindows).Ffprobe);
    public bool HasSharedLibraries { get; private set; }
    public string Version { get; private set; } = "未检测";
    public string StatusMessage { get; private set; }
    public bool IsAvailable => FilesAreComplete() && _processValidated;

    public void Configure()
    {
        Directory.CreateDirectory(_paths.TempDirectory);
        HasSharedLibraries = CheckPlatformLibraries(BinaryDirectory, _isWindows);
        if (!FilesAreComplete())
        {
            StatusMessage = _isWindows
                ? "FFmpeg 文件不完整，请运行 setup-ffmpeg.ps1。"
                : "未找到系统 FFmpeg/ffprobe，请按 Linux 文档安装 ffmpeg。";
            Log.Warning("FFmpeg runtime is incomplete: {BinaryDirectory}", BinaryDirectory);
            return;
        }

        GlobalFFOptions.Configure(new FFOptions
        {
            BinaryFolder = BinaryDirectory,
            TemporaryFilesFolder = _paths.TempDirectory
        });
        Log.Information("FFmpeg binary folder configured: {BinaryDirectory}", BinaryDirectory);
    }

    public async Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        HasSharedLibraries = CheckPlatformLibraries(BinaryDirectory, _isWindows);
        if (!FilesAreComplete())
        {
            _processValidated = false;
            Version = "不可用";
            StatusMessage = _isWindows
                ? "FFmpeg 文件不完整，请运行 setup-ffmpeg.ps1。"
                : "未找到系统 FFmpeg/ffprobe，请按 Linux 文档安装 ffmpeg。";
            return Snapshot();
        }

        try
        {
            var output = await RunVersionCheckAsync(cancellationToken);
            var firstLine = output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (!firstLine.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FFmpeg version output was not recognized.");
            Version = firstLine;
            _processValidated = true;
            StatusMessage = _isWindows ? "FFmpeg 可用（随包 Shared 运行时）" : "FFmpeg 可用（Linux 系统运行时）";
            Log.Information("FFmpeg validation succeeded: {Version}", Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _processValidated = false;
            Version = "启动失败";
            StatusMessage = "FFmpeg 无法启动，请检查安装和执行权限。";
            Log.Error(ex, "FFmpeg validation failed: {BinaryDirectory}", BinaryDirectory);
        }
        return Snapshot();
    }

    public static (string Ffmpeg, string Ffprobe) GetExecutableNames(bool isWindows) =>
        isWindows ? ("ffmpeg.exe", "ffprobe.exe") : ("ffmpeg", "ffprobe");

    public static string FindBinaryDirectory(bool isWindows, string? overrideDirectory, string? pathEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            var full = Path.GetFullPath(overrideDirectory);
            if (HasRequiredFiles(full, isWindows)) return full;
        }

        var published = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (HasRequiredFiles(published, isWindows)) return published;

        if (isWindows)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tools", "ffmpeg");
                if (HasRequiredFiles(candidate, true)) return candidate;
            }
        }

        foreach (var segment in (pathEnvironment ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (HasRequiredFiles(segment, isWindows)) return Path.GetFullPath(segment);

        return published;
    }

    private async Task<string> RunVersionCheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = BinaryDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("-version");
        if (!process.Start()) throw new InvalidOperationException("无法启动 FFmpeg 检测进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("FFmpeg 启动检测超过 8 秒。");
        }
        var output = (await stdout) + (await stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg 检测退出码为 {process.ExitCode}：{output}");
        return output;
    }

    private FfmpegValidationResult Snapshot() => new(IsAvailable, HasSharedLibraries, Version, BinaryDirectory, StatusMessage);
    private bool FilesAreComplete() => File.Exists(FfmpegPath) && File.Exists(FfprobePath) && HasSharedLibraries;
    private string BuildFileStatus() => FilesAreComplete() ? "等待启动检测" : _isWindows ? "Windows FFmpeg Shared 文件不完整" : "未找到 Linux 系统 FFmpeg";

    private static bool CheckPlatformLibraries(string directory, bool isWindows) =>
        !isWindows || Directory.Exists(directory) && RequiredWindowsSharedLibraries.All(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any());

    private static bool HasRequiredFiles(string directory, bool isWindows)
    {
        var names = GetExecutableNames(isWindows);
        if (isWindows) return File.Exists(Path.Combine(directory, names.Ffmpeg)) && File.Exists(Path.Combine(directory, names.Ffprobe));
        if (!Directory.Exists(directory)) return false;
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        return files.Contains(names.Ffmpeg) && files.Contains(names.Ffprobe);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) { Log.Warning(ex, "Failed to terminate timed-out FFmpeg validation process"); }
    }
}
