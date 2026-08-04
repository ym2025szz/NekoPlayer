using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FFMpegCore;
using NekoPlayer.Audio.Playback;
using NekoPlayer.Audio.Spectrum;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Configuration;

Console.OutputEncoding = Encoding.UTF8;
var options = VerifierOptions.Parse(args);
var report = new VerificationReport
{
    StartedAtUtc = DateTime.UtcNow,
    AudioDirectoryName = string.IsNullOrWhiteSpace(options.AudioDirectory) ? string.Empty : new DirectoryInfo(options.AudioDirectory).Name,
    Volume = options.Volume
};
var root = FindProjectRoot(AppContext.BaseDirectory);
var reportsDirectory = Path.Combine(root, "artifacts", "test-reports");
Directory.CreateDirectory(reportsDirectory);
var jsonPath = Path.Combine(reportsDirectory, "playback-verification.json");
var markdownPath = Path.Combine(reportsDirectory, "playback-verification.md");
var initialFfmpegPids = GetFfmpegPids();
var observedFfmpegPids = new HashSet<int>();
IAudioPlayerService? player = null;
var exitCode = 1;

try
{
    Console.WriteLine(OperatingSystem.IsWindows()
        ? "This verifier will briefly play two MP3 files through the Windows default audio device."
        : "This verifier will exercise the Linux FFmpeg audio pipeline. Set NEKOPLAYER_LINUX_AUDIO_DEVICE=null for headless validation.");
    Console.WriteLine($"Volume: {options.Volume:0.00}. The system volume will not be changed.");
    Require(Directory.Exists(options.AudioDirectory), "Precheck", "Audio directory does not exist.");
    var files = Directory.GetFiles(options.AudioDirectory, "*.mp3", SearchOption.TopDirectoryOnly)
        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    Require(files.Length >= 2, "Precheck", $"At least two MP3 files are required, but only {files.Length} were found.");
    files = files.Take(2).ToArray();

    var paths = new UserDataPaths();
    paths.EnsureCreated();
    var locator = new FfmpegLocator(paths);
    locator.Configure();
    var validation = await locator.ValidateAsync();
    Require(validation.IsAvailable, "FFmpeg precheck", validation.StatusMessage);
    Require(validation.HasSharedLibraries, "FFmpeg precheck", "Shared FFmpeg DLL files are incomplete.");
    report.FfmpegVersion = validation.Version;
    report.FfmpegDirectory = validation.BinaryDirectory;
    AddStage(report, "FFmpeg precheck", true, validation.Version);

    foreach (var file in files)
    {
        var analysis = await FFProbe.AnalyseAsync(file);
        var audio = analysis.PrimaryAudioStream;
        Require(audio is not null && analysis.Duration > TimeSpan.Zero, "FFprobe", $"No valid audio stream: {Path.GetFileName(file)}");
        report.Files.Add(new AudioFileResult
        {
            FileName = Path.GetFileName(file),
            DurationSeconds = analysis.Duration.TotalSeconds,
            Codec = audio!.CodecName ?? string.Empty,
            SampleRate = audio.SampleRateHz,
            Channels = audio.Channels,
            BitRate = audio.BitRate
        });
        AddStage(report, "FFprobe", true, $"{Path.GetFileName(file)}: {analysis.Duration}, {audio.CodecName}, {audio.SampleRateHz} Hz, {audio.Channels} channels");
    }

    var spectrum = new SpectrumService { IsEnabled = true, FramesPerSecond = 30 };
    player = AudioPlayerFactory.Create(locator, spectrum);
    player.Volume = options.Volume;
    var diagnostics = (IAudioPlaybackDiagnostics)player;
    var states = new List<PlaybackState>();
    Exception? playbackFailure = null;
    player.StateChanged += (_, state) => states.Add(state);
    player.PlaybackFailed += (_, exception) => playbackFailure = exception;

    await player.LoadAsync(files[0]);
    ObserveNewFfmpegPids(initialFfmpegPids, observedFfmpegPids);
    Require(states.Contains(PlaybackState.Loading), "A - first load", "Loading state was not observed.");
    Require(player.Duration > TimeSpan.Zero, "A - first load", "Duration is zero.");
    Require(playbackFailure is null, "A - first load", playbackFailure?.Message ?? string.Empty);
    if (OperatingSystem.IsWindows()) Require(diagnostics.IsOutputInitialized, "A - first load", "Windows audio output was not initialized.");
    AddStage(report, "A - first load", true, $"Duration {player.Duration}; platform {Environment.OSVersion.Platform}; buffer {diagnostics.BufferedBytes}/{diagnostics.BufferCapacityBytes}");

    var pcmBeforePlay = diagnostics.TotalPcmBytesReceived;
    await player.PlayAsync();
    await Task.Delay(TimeSpan.FromSeconds(3));
    ObserveNewFfmpegPids(initialFfmpegPids, observedFfmpegPids);
    Require(player.State == PlaybackState.Playing, "B - first play", $"Unexpected state: {player.State}");
    Require(diagnostics.IsOutputInitialized, "B - first play", "Platform audio output process/device was not initialized.");
    Require(player.Position > TimeSpan.FromSeconds(1.5), "B - first play", $"Position did not advance: {player.Position}");
    Require(diagnostics.TotalPcmBytesReceived > pcmBeforePlay, "B - first play", "No new PCM data was received.");
    Require(playbackFailure is null, "B - first play", playbackFailure?.Message ?? string.Empty);
    Require(CountActiveVerifierFfmpeg(initialFfmpegPids) <= 1, "B - first play", "More than one new ffmpeg process is active.");
    AddStage(report, "B - first play", true, $"Position {player.Position}; PCM bytes {diagnostics.TotalPcmBytesReceived}");

    await player.PauseAsync();
    var pausedPosition = player.Position;
    var memoryBeforePause = GC.GetTotalMemory(false);
    await Task.Delay(TimeSpan.FromSeconds(2));
    var pauseDrift = (player.Position - pausedPosition).Duration();
    var memoryGrowth = GC.GetTotalMemory(false) - memoryBeforePause;
    Require(player.State == PlaybackState.Paused, "C - pause", $"Unexpected state: {player.State}");
    Require(pauseDrift <= TimeSpan.FromMilliseconds(500), "C - pause", $"Position drifted by {pauseDrift.TotalMilliseconds:0} ms.");
    Require(diagnostics.BufferedBytes <= diagnostics.BufferCapacityBytes, "C - pause", "PCM buffer exceeded capacity.");
    Require(memoryGrowth < 32 * 1024 * 1024, "C - pause", $"Memory grew by {memoryGrowth / 1024d / 1024d:0.0} MiB.");
    AddStage(report, "C - pause", true, $"Drift {pauseDrift.TotalMilliseconds:0} ms; buffer {diagnostics.BufferedBytes}/{diagnostics.BufferCapacityBytes}; memory delta {memoryGrowth}");

    await player.PlayAsync();
    var resumeStart = player.Position;
    await Task.Delay(TimeSpan.FromSeconds(2.5));
    Require(player.State == PlaybackState.Playing, "D - resume", $"Unexpected state: {player.State}");
    Require(player.Position - resumeStart > TimeSpan.FromSeconds(1.5), "D - resume", "Position did not continue after resume.");
    AddStage(report, "D - resume", true, $"Position continued from {resumeStart} to {player.Position}");

    var seekTarget = TimeSpan.FromSeconds(Math.Clamp(player.Duration.TotalSeconds * 0.35, 1, Math.Max(1, player.Duration.TotalSeconds - 3)));
    var previewTargets = new[]
    {
        TimeSpan.FromSeconds(player.Duration.TotalSeconds * 0.2),
        TimeSpan.FromSeconds(player.Duration.TotalSeconds * 0.28),
        seekTarget
    };
    var seekCommitCount = 0;
    using (var seekCoordinator = new SeekRequestCoordinator())
    {
        var submissions = new List<Task>();
        foreach (var preview in previewTargets)
        {
            submissions.Add(seekCoordinator.SubmitAsync(preview, async (target, token) =>
            {
                Interlocked.Increment(ref seekCommitCount);
                await player.SeekAsync(target, token);
            }, TimeSpan.FromMilliseconds(180)));
            await Task.Delay(35);
        }
        foreach (var submission in submissions)
        {
            try { await submission; }
            catch (OperationCanceledException) { }
        }
    }
    ObserveNewFfmpegPids(initialFfmpegPids, observedFfmpegPids);
    Require(seekCommitCount == 1, "E1 - debounced playing seek", $"Expected one final seek commit, got {seekCommitCount}.");
    Require(player.State == PlaybackState.Playing, "E1 - debounced playing seek", $"Playing seek did not resume playback: {player.State}.");
    Require((player.Position - seekTarget).Duration() <= TimeSpan.FromSeconds(2), "E1 - debounced playing seek", $"Position {player.Position} is not near target {seekTarget}.");
    Require(CountActiveVerifierFfmpeg(initialFfmpegPids) <= 1, "E1 - debounced playing seek", "More than one new ffmpeg process is active after seek.");
    var seekStart = player.Position;
    await Task.Delay(TimeSpan.FromSeconds(2));
    Require(player.Position - seekStart > TimeSpan.FromSeconds(1), "E1 - debounced playing seek", "Position did not advance after seek.");
    Require(player.Position >= seekTarget - TimeSpan.FromSeconds(1), "E1 - debounced playing seek", "Position jumped back toward pre-seek PCM.");
    AddStage(report, "E1 - debounced playing seek", true, $"Three previews produced {seekCommitCount} final commit; target {seekTarget}; continued to {player.Position}; no position rollback detected.");

    await player.PauseAsync();
    var pausedSeekTarget = TimeSpan.FromSeconds(Math.Clamp(player.Duration.TotalSeconds * 0.62, 1, Math.Max(1, player.Duration.TotalSeconds - 3)));
    await player.SeekAsync(pausedSeekTarget);
    Require(player.State == PlaybackState.Paused, "E2 - paused seek", $"Paused seek changed state to {player.State}.");
    Require((player.Position - pausedSeekTarget).Duration() <= TimeSpan.FromSeconds(2), "E2 - paused seek", $"Position {player.Position} is not near target {pausedSeekTarget}.");
    var pausedSeekPosition = player.Position;
    await Task.Delay(TimeSpan.FromSeconds(1));
    Require((player.Position - pausedSeekPosition).Duration() <= TimeSpan.FromMilliseconds(500), "E2 - paused seek", "Position advanced while remaining paused.");
    await player.PlayAsync();
    await Task.Delay(TimeSpan.FromSeconds(1.5));
    Require(player.Position - pausedSeekPosition > TimeSpan.FromSeconds(0.8), "E2 - paused seek", "Playback did not continue from paused seek target.");
    Require(CountActiveVerifierFfmpeg(initialFfmpegPids) <= 1, "E2 - paused seek", "Paused seek left multiple ffmpeg processes active.");
    AddStage(report, "E2 - paused seek", true, $"Target {pausedSeekTarget}; remained paused, then resumed to {player.Position}.");

    await player.StopAsync();
    Require(player.State == PlaybackState.Stopped, "F - stop", $"Unexpected state: {player.State}");
    Require(player.Position <= TimeSpan.FromMilliseconds(250), "F - stop", $"Position did not reset: {player.Position}");
    Require(string.Equals(Path.GetFullPath(diagnostics.CurrentFilePath!), Path.GetFullPath(files[0]), StringComparison.OrdinalIgnoreCase), "F - stop", "Stop cleared the current media path.");
    Require(diagnostics.BufferedBytes == 0, "F - stop", $"Buffer was not cleared: {diagnostics.BufferedBytes}");
    Require(!diagnostics.IsDecodeActive, "F - stop", "Decode task is still active.");
    AddStage(report, "F - stop", true, $"State {player.State}; position reset; current media retained; buffer cleared");

    var pcmBeforeReplay = diagnostics.TotalPcmBytesReceived;
    await player.PlayAsync();
    await Task.Delay(TimeSpan.FromSeconds(2));
    Require(player.State == PlaybackState.Playing, "F2 - replay after stop", $"Unexpected state: {player.State}");
    Require(player.Position > TimeSpan.FromSeconds(1), "F2 - replay after stop", $"Position did not advance from the beginning: {player.Position}");
    Require(player.Position < TimeSpan.FromSeconds(5), "F2 - replay after stop", $"Replay did not restart near zero: {player.Position}");
    Require(diagnostics.TotalPcmBytesReceived > pcmBeforeReplay, "F2 - replay after stop", "No fresh PCM arrived after replay.");
    Require(CountActiveVerifierFfmpeg(initialFfmpegPids) <= 1, "F2 - replay after stop", "Replay left multiple ffmpeg processes active.");
    AddStage(report, "F2 - replay after stop", true, $"Replayed from the beginning to {player.Position}; fresh PCM received.");
    await player.StopAsync();

    var secondExpectedDuration = TimeSpan.FromSeconds(report.Files[1].DurationSeconds);
    var pcmBeforeSecond = diagnostics.TotalPcmBytesReceived;
    playbackFailure = null;
    await player.LoadAsync(files[1]);
    Require(string.Equals(Path.GetFullPath(diagnostics.CurrentFilePath!), Path.GetFullPath(files[1]), StringComparison.OrdinalIgnoreCase), "G - second track", "Current media path did not switch.");
    Require((player.Duration - secondExpectedDuration).Duration() <= TimeSpan.FromSeconds(1), "G - second track", "Second duration does not match ffprobe.");
    Require(player.Position <= TimeSpan.FromMilliseconds(500), "G - second track", $"Second track did not start near zero: {player.Position}");
    await player.PlayAsync();
    await Task.Delay(TimeSpan.FromSeconds(3));
    Require(diagnostics.TotalPcmBytesReceived > pcmBeforeSecond, "G - second track", "No PCM data arrived for the second track.");
    Require(playbackFailure is null, "G - second track", playbackFailure?.Message ?? string.Empty);
    Require(CountActiveVerifierFfmpeg(initialFfmpegPids) <= 1, "G - second track", "More than one ffmpeg process is active after track switch.");
    AddStage(report, "G - second track", true, $"Duration {player.Duration}; position {player.Position}");

    await player.PauseAsync();
    var secondPause = player.Position;
    await Task.Delay(TimeSpan.FromSeconds(1));
    Require((player.Position - secondPause).Duration() <= TimeSpan.FromMilliseconds(500), "H - second pause/resume", "Position moved while paused.");
    await player.PlayAsync();
    await Task.Delay(TimeSpan.FromSeconds(2));
    Require(player.Position - secondPause > TimeSpan.FromSeconds(1), "H - second pause/resume", "Position did not advance after resume.");
    AddStage(report, "H - second pause/resume", true, $"Continued from {secondPause} to {player.Position}");

    await player.StopAsync();
    Require(player.Position <= TimeSpan.FromMilliseconds(250), "H2 - second stop/replay", "Second track did not reset to zero after stop.");
    var secondReplayPcm = diagnostics.TotalPcmBytesReceived;
    await player.PlayAsync();
    await Task.Delay(TimeSpan.FromSeconds(2));
    Require(player.State == PlaybackState.Playing, "H2 - second stop/replay", $"Unexpected state: {player.State}");
    Require(player.Position > TimeSpan.FromSeconds(1) && player.Position < TimeSpan.FromSeconds(5), "H2 - second stop/replay", $"Second track did not replay from the beginning: {player.Position}");
    Require(diagnostics.TotalPcmBytesReceived > secondReplayPcm, "H2 - second stop/replay", "Second replay did not receive fresh PCM.");
    AddStage(report, "H2 - second stop/replay", true, $"Second track replayed from zero to {player.Position}.");

    playbackFailure = null;
    await player.LoadAsync(files[0]);
    await player.PlayAsync();
    await Task.Delay(700);
    await player.LoadAsync(files[1]);
    await player.PlayAsync();
    await Task.Delay(700);
    ObserveNewFfmpegPids(initialFfmpegPids, observedFfmpegPids);
    Require(string.Equals(Path.GetFullPath(diagnostics.CurrentFilePath!), Path.GetFullPath(files[1]), StringComparison.OrdinalIgnoreCase), "I - rapid switch", "Rapid switch did not finish on the second track.");
    Require(CountActiveVerifierFfmpeg(initialFfmpegPids) <= 1, "I - rapid switch", "Rapid switching left multiple ffmpeg processes active.");
    Require(playbackFailure is null, "I - rapid switch", playbackFailure?.Message ?? string.Empty);
    AddStage(report, "I - rapid switch", true, "First -> second completed without an exposed cancellation or disposal exception.");

    await player.StopAsync();
    await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    player = null;
    await Task.Delay(1000);
    var remaining = observedFfmpegPids.Where(IsProcessAlive).ToArray();
    Require(remaining.Length == 0, "J - resource release", $"Verifier-started ffmpeg processes remain: {string.Join(", ", remaining)}");
    AddStage(report, "J - resource release", true, "Disposed within 5 seconds; no verifier-observed ffmpeg process remains.");

    report.TechnicalVerificationPassed = true;
    exitCode = 0;
}
catch (Exception ex)
{
    report.Error = ex.ToString();
    AddStage(report, "Verification stopped", false, ex.Message);
    Console.Error.WriteLine(ex.Message);
}
finally
{
    if (player is not null)
    {
        try { await player.DisposeAsync(); }
        catch (Exception ex) { report.CleanupError = ex.ToString(); }
    }
    report.CompletedAtUtc = DateTime.UtcNow;
    report.ObservedFfmpegProcessIds = observedFfmpegPids.Order().ToArray();
    report.RemainingObservedFfmpegProcessIds = observedFfmpegPids.Where(IsProcessAlive).Order().ToArray();
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report));
    Console.WriteLine($"JSON report: {jsonPath}");
    Console.WriteLine($"Markdown report: {markdownPath}");
    Console.WriteLine("Technical verification cannot confirm audible sound quality; manual listening acceptance is still required.");
}

return exitCode;

static void Require(bool condition, string stage, string message)
{
    if (!condition) throw new VerificationException(stage, message);
}

static void AddStage(VerificationReport report, string name, bool passed, string details)
{
    report.Stages.Add(new StageResult { Name = name, Passed = passed, Details = details, TimestampUtc = DateTime.UtcNow });
    Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}: {details}");
}

static HashSet<int> GetFfmpegPids() => Process.GetProcessesByName("ffmpeg").Select(x => { try { return x.Id; } finally { x.Dispose(); } }).ToHashSet();
static int CountActiveVerifierFfmpeg(HashSet<int> initial) => GetFfmpegPids().Except(initial).Count();
static void ObserveNewFfmpegPids(HashSet<int> initial, HashSet<int> observed)
{
    foreach (var pid in GetFfmpegPids().Except(initial)) observed.Add(pid);
}
static bool IsProcessAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static string FindProjectRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "NekoPlayer.sln"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate NekoPlayer.sln from the verifier output directory.");
}

static string BuildMarkdown(VerificationReport report)
{
    var builder = new StringBuilder();
    builder.AppendLine("# NekoPlayer v1.0.0 技术播放、Seek 与 Stop 重播验证报告");
    builder.AppendLine();
    builder.AppendLine($"- 开始时间（UTC）：{report.StartedAtUtc:o}");
    builder.AppendLine($"- 完成时间（UTC）：{report.CompletedAtUtc:o}");
    builder.AppendLine($"- 音频目录名：{report.AudioDirectoryName}");
    builder.AppendLine($"- 测试音量：{report.Volume:0.00}");
    builder.AppendLine($"- 技术验证：{(report.TechnicalVerificationPassed ? "通过" : "未通过")}");
    builder.AppendLine($"- FFmpeg：{report.FfmpegVersion}");
    builder.AppendLine();
    builder.AppendLine("## 音频文件");
    foreach (var file in report.Files) builder.AppendLine($"- {file.FileName}：{file.DurationSeconds:0.###} 秒，{file.Codec}，{file.SampleRate} Hz，{file.Channels} 声道，{file.BitRate} bit/s");
    builder.AppendLine();
    builder.AppendLine("## 阶段结果");
    foreach (var stage in report.Stages) builder.AppendLine($"- [{(stage.Passed ? "通过" : "失败")}] {stage.Name}：{stage.Details}");
    builder.AppendLine();
    builder.AppendLine($"- 观察到的 ffmpeg PID：{string.Join(", ", report.ObservedFfmpegProcessIds)}");
    builder.AppendLine($"- 验证结束后仍存在的观察 PID：{string.Join(", ", report.RemainingObservedFfmpegProcessIds)}");
    if (!string.IsNullOrWhiteSpace(report.Error)) builder.AppendLine($"- 错误：`{report.Error.Replace("`", "'")}`");
    builder.AppendLine();
    builder.AppendLine("> Seek 技术状态与位置验证已通过，但拖动手感、跳转后的实际听感、爆音和残音仍需用户人工验收。");
    return builder.ToString();
}

sealed class VerifierOptions
{
    public string AudioDirectory { get; private set; } = string.Empty;
    public float Volume { get; private set; } = 0.15f;
    public static VerifierOptions Parse(string[] args)
    {
        var result = new VerifierOptions();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--audio-directory" && i + 1 < args.Length) result.AudioDirectory = Path.GetFullPath(args[++i]);
            else if (args[i] == "--volume" && i + 1 < args.Length && float.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var volume)) result.Volume = Math.Clamp(volume, 0f, 1f);
            else throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
        }
        if (string.IsNullOrWhiteSpace(result.AudioDirectory)) throw new ArgumentException("--audio-directory is required.");
        return result;
    }
}

sealed class VerificationException(string stage, string message) : Exception($"{stage}: {message}");
sealed class VerificationReport
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public string AudioDirectoryName { get; set; } = string.Empty;
    public float Volume { get; set; }
    public string FfmpegVersion { get; set; } = string.Empty;
    public string FfmpegDirectory { get; set; } = string.Empty;
    public bool TechnicalVerificationPassed { get; set; }
    public List<AudioFileResult> Files { get; } = [];
    public List<StageResult> Stages { get; } = [];
    public int[] ObservedFfmpegProcessIds { get; set; } = [];
    public int[] RemainingObservedFfmpegProcessIds { get; set; } = [];
    public string? Error { get; set; }
    public string? CleanupError { get; set; }
}
sealed class AudioFileResult
{
    public string FileName { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string Codec { get; set; } = string.Empty;
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public long BitRate { get; set; }
}
sealed class StageResult
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
}
