using System.Diagnostics;
using FFMpegCore;
using NekoPlayer.Core.Interfaces;
using Serilog;

namespace NekoPlayer.Infrastructure.Configuration;

public sealed class FfmpegLocator : IFfmpegLocator
{
    private static readonly string[] RequiredSharedLibraries =
        ["avcodec-*.dll", "avformat-*.dll", "avutil-*.dll", "swresample-*.dll"];
    private readonly IUserDataPaths _paths;
    private volatile bool _processValidated;

    public FfmpegLocator(IUserDataPaths paths)
    {
        _paths = paths;
        BinaryDirectory = FindBinaryDirectory();
        HasSharedLibraries = CheckSharedLibraries(BinaryDirectory);
        StatusMessage = BuildFileStatus();
    }

    public string BinaryDirectory { get; }
    public string FfmpegPath => Path.Combine(BinaryDirectory, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(BinaryDirectory, "ffprobe.exe");
    public bool HasSharedLibraries { get; private set; }
    public string Version { get; private set; } = "未检测";
    public string StatusMessage { get; private set; }
    public bool IsAvailable => FilesAreComplete() && _processValidated;

    public void Configure()
    {
        Directory.CreateDirectory(_paths.TempDirectory);
        HasSharedLibraries = CheckSharedLibraries(BinaryDirectory);
        if (!FilesAreComplete())
        {
            StatusMessage = "FFmpeg 文件不完整，请重新运行 setup-ffmpeg.ps1。";
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
        HasSharedLibraries = CheckSharedLibraries(BinaryDirectory);
        if (!FilesAreComplete())
        {
            _processValidated = false;
            Version = "不可用";
            StatusMessage = "FFmpeg 文件不完整，请重新运行 setup-ffmpeg.ps1。";
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
            StatusMessage = "FFmpeg 可用";
            Log.Information("FFmpeg validation succeeded: {Version}", Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _processValidated = false;
            Version = "启动失败";
            StatusMessage = "FFmpeg 文件不完整或无法启动，请重新运行 setup-ffmpeg.ps1。";
            Log.Error(ex, "FFmpeg validation failed: {BinaryDirectory}", BinaryDirectory);
        }
        return Snapshot();
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
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = BinaryDirectory
            }
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 FFmpeg 检测进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // Cancellation of the calling startup probe must also reap its child process.
            // Keep Process alive until exit and observe both redirected read tasks.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { Log.Warning(ex, "FFmpeg validation process cleanup failed"); }
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("FFmpeg 启动检测超过 8 秒。");
        }
        var output = (await stdout) + (await stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg 检测退出码为 {process.ExitCode}：{output}");
        return output;
    }

    private FfmpegValidationResult Snapshot() => new(IsAvailable, HasSharedLibraries, Version, BinaryDirectory, StatusMessage);
    private bool FilesAreComplete() => File.Exists(FfmpegPath) && File.Exists(FfprobePath) && HasSharedLibraries;
    private string BuildFileStatus() => FilesAreComplete() ? "等待启动检测" : "FFmpeg 文件不完整，请重新运行 setup-ffmpeg.ps1。";

    private static bool CheckSharedLibraries(string directory) =>
        Directory.Exists(directory) && RequiredSharedLibraries.All(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any());

    private static string FindBinaryDirectory()
    {
        var published = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (HasRequiredFiles(published)) return published;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "ffmpeg");
            if (HasRequiredFiles(candidate)) return candidate;
        }
        return published;
    }

    private static bool HasRequiredFiles(string directory) =>
        File.Exists(Path.Combine(directory, "ffmpeg.exe")) && File.Exists(Path.Combine(directory, "ffprobe.exe"));

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) { Log.Warning(ex, "Failed to terminate timed-out FFmpeg validation process"); }
    }
}
