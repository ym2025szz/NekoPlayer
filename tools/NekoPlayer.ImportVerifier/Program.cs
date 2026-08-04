using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Configuration;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Metadata;
using NekoPlayer.Infrastructure.Repositories;

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    var audioDirectory = GetArgument(args, "--audio-directory");
    if (string.IsNullOrWhiteSpace(audioDirectory))
    {
        Console.Error.WriteLine("Missing required argument: --audio-directory");
        return 2;
    }

    var projectRoot = FindProjectRoot();
    var reportsDirectory = Path.Combine(projectRoot, "artifacts", "test-reports");
    Directory.CreateDirectory(reportsDirectory);
    var jsonPath = Path.Combine(reportsDirectory, "import-verification.json");
    var markdownPath = Path.Combine(reportsDirectory, "import-verification.md");
    var logPath = Path.Combine(reportsDirectory, "import-verification.log");
    var log = new List<string>();
    var started = DateTime.UtcNow;
    var tempRoot = Path.Combine(Path.GetTempPath(), "NekoPlayerImportVerifier", Guid.NewGuid().ToString("N"));
    ImportVerificationReport? report = null;

    try
    {
        audioDirectory = Path.GetFullPath(audioDirectory);
        if (!Directory.Exists(audioDirectory)) throw new DirectoryNotFoundException($"Audio directory does not exist: {audioDirectory}");
        var selectedMp3 = Directory.EnumerateFiles(audioDirectory, "*.mp3", SearchOption.TopDirectoryOnly).OrderBy(x => x).ToArray();
        if (selectedMp3.Length != 2) throw new InvalidOperationException($"Expected exactly two MP3 files, found {selectedMp3.Length}.");

        var paths = new VerifierPaths(tempRoot);
        paths.EnsureCreated();
        var options = new DbContextOptionsBuilder<NekoPlayerDbContext>()
            .UseSqlite($"Data Source={paths.DatabasePath}")
            .Options;
        var factory = new VerifierDbContextFactory(options);
        var ffmpeg = new FfmpegLocator(paths);
        ffmpeg.Configure();
        var validation = await ffmpeg.ValidateAsync();
        if (!validation.IsAvailable) throw new InvalidOperationException(validation.StatusMessage);

        var service = new MusicLibraryService(factory, new TrackMetadataReader(ffmpeg, paths), new TrackStateStore());
        await service.InitializeAsync();
        var firstProgress = new List<ImportProgress>();
        var secondProgress = new List<ImportProgress>();
        log.Add($"Temporary database: {paths.DatabasePath}");
        log.Add($"Audio directory: {audioDirectory}");
        log.Add($"FFmpeg: {validation.Version}");

        var first = await service.ScanFolderAsync(audioDirectory, false, new InlineProgress<ImportProgress>(value =>
        {
            firstProgress.Add(value);
            Console.WriteLine($"[FIRST] {value.Stage}: {value.StatusText} {value.CurrentFileName}");
        }));
        var firstTracks = await service.GetTracksAsync();
        ValidateFirstImport(first, firstTracks, selectedMp3);
        log.Add($"First import: Imported={first.ImportedCount}, Updated={first.UpdatedCount}, Skipped={first.SkippedCount}, Failed={first.FailedCount}, Tracks={firstTracks.Count}");

        var second = await service.ScanFolderAsync(audioDirectory, false, new InlineProgress<ImportProgress>(value =>
        {
            secondProgress.Add(value);
            Console.WriteLine($"[SECOND] {value.Stage}: {value.StatusText} {value.CurrentFileName}");
        }));
        var finalTracks = await service.GetTracksAsync();
        ValidateSecondImport(second, finalTracks);
        log.Add($"Second import: Imported={second.ImportedCount}, Updated={second.UpdatedCount}, Skipped={second.SkippedCount}, Failed={second.FailedCount}, Tracks={finalTracks.Count}");

        var unrelated = finalTracks.Where(x => !string.Equals(Path.GetExtension(x.FilePath), ".mp3", StringComparison.OrdinalIgnoreCase)).Select(x => x.FilePath).ToArray();
        var fictional = finalTracks.Where(x =>
            x.Title.Contains("Mock", StringComparison.OrdinalIgnoreCase) ||
            x.Title.Contains("Sample", StringComparison.OrdinalIgnoreCase) ||
            x.Title.Contains("Demo", StringComparison.OrdinalIgnoreCase)).Select(x => x.Title).ToArray();
        if (unrelated.Length > 0 || fictional.Length > 0) throw new InvalidOperationException("Unrelated or fictional tracks were found in the temporary database.");

        report = new ImportVerificationReport(
            started,
            DateTime.UtcNow,
            true,
            validation.Version,
            selectedMp3.Select(Path.GetFileName).ToArray()!,
            ToSnapshot(first),
            ToSnapshot(second),
            finalTracks.Count,
            finalTracks.Select(x => new VerifiedTrack(x.Id, x.Title, x.Artist, x.Album, x.FilePath, File.Exists(x.FilePath))).ToArray(),
            firstProgress.Select(x => x.Stage.ToString()).Distinct().ToArray(),
            secondProgress.Select(x => x.Stage.ToString()).Distinct().ToArray(),
            [],
            null);
        log.Add("Import verification passed.");
        Console.WriteLine("Import verification passed.");
        return 0;
    }
    catch (Exception ex)
    {
        log.Add(ex.ToString());
        report = new ImportVerificationReport(
            started,
            DateTime.UtcNow,
            false,
            string.Empty,
            [],
            null,
            null,
            0,
            [],
            [],
            [],
            [],
            ex.Message);
        Console.Error.WriteLine(ex);
        return 1;
    }
    finally
    {
        if (report is not null)
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonPath, json, new UTF8Encoding(false));
            await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), new UTF8Encoding(false));
            await File.WriteAllLinesAsync(logPath, log, new UTF8Encoding(true));
            Console.WriteLine($"JSON report: {jsonPath}");
            Console.WriteLine($"Markdown report: {markdownPath}");
            Console.WriteLine($"Log report: {logPath}");
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
    }
}

static void ValidateFirstImport(ImportResult result, IReadOnlyList<Track> tracks, IReadOnlyList<string> selectedMp3)
{
    if (result.ImportedCount != 2 || result.UpdatedCount != 0 || result.SkippedCount != 0 || result.FailedCount != 0)
        throw new InvalidOperationException($"Unexpected first import result: {JsonSerializer.Serialize(result)}");
    if (tracks.Count != 2) throw new InvalidOperationException($"Expected two database tracks after first import, found {tracks.Count}.");
    var selected = selectedMp3.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (tracks.Any(x => !selected.Contains(Path.GetFullPath(x.FilePath)))) throw new InvalidOperationException("The database contains a path outside the selected MP3 files.");
    if (tracks.Any(x => !File.Exists(x.FilePath))) throw new InvalidOperationException("A persisted track does not point to an existing file.");
}

static void ValidateSecondImport(ImportResult result, IReadOnlyList<Track> tracks)
{
    if (result.ImportedCount != 0 || result.UpdatedCount != 0 || result.SkippedCount != 2 || result.FailedCount != 0)
        throw new InvalidOperationException($"Unexpected second import result: {JsonSerializer.Serialize(result)}");
    if (tracks.Count != 2) throw new InvalidOperationException($"Expected two database tracks after duplicate import, found {tracks.Count}.");
}

static ImportResultSnapshot ToSnapshot(ImportResult result) => new(
    result.Stage.ToString(),
    result.DiscoveredCount,
    result.ProcessedCount,
    result.ImportedCount,
    result.UpdatedCount,
    result.SkippedCount,
    result.FailedCount,
    result.Failures);

static string BuildMarkdown(ImportVerificationReport report)
{
    var builder = new StringBuilder();
    builder.AppendLine("# NekoPlayer v0.1.3 真实导入验证报告");
    builder.AppendLine();
    builder.AppendLine($"- 技术验证：{(report.Passed ? "通过" : "失败")}");
    builder.AppendLine($"- 开始时间 UTC：{report.StartedAtUtc:O}");
    builder.AppendLine($"- 完成时间 UTC：{report.CompletedAtUtc:O}");
    builder.AppendLine($"- FFmpeg：{report.FfmpegVersion}");
    builder.AppendLine($"- 临时数据库最终 Track 数量：{report.FinalTrackCount}");
    if (report.FirstImport is not null)
        builder.AppendLine($"- 第一次导入：新增 {report.FirstImport.ImportedCount}，更新 {report.FirstImport.UpdatedCount}，跳过 {report.FirstImport.SkippedCount}，失败 {report.FirstImport.FailedCount}");
    if (report.SecondImport is not null)
        builder.AppendLine($"- 第二次导入：新增 {report.SecondImport.ImportedCount}，更新 {report.SecondImport.UpdatedCount}，跳过 {report.SecondImport.SkippedCount}，失败 {report.SecondImport.FailedCount}");
    builder.AppendLine();
    builder.AppendLine("## 数据库歌曲");
    foreach (var track in report.Tracks) builder.AppendLine($"- {track.Title} / {track.Artist} / 文件存在：{track.FileExists}");
    if (!string.IsNullOrWhiteSpace(report.Error)) builder.AppendLine($"- 错误：{report.Error}");
    return builder.ToString();
}

static string? GetArgument(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
    return null;
}

static string FindProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "NekoPlayer.sln"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate NekoPlayer.sln from the verifier output directory.");
}

sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

sealed class VerifierDbContextFactory(DbContextOptions<NekoPlayerDbContext> options) : IDbContextFactory<NekoPlayerDbContext>
{
    public NekoPlayerDbContext CreateDbContext() => new(options);
}

sealed class VerifierPaths(string root) : IUserDataPaths
{
    public string Root { get; } = root;
    public string DataDirectory { get; } = Path.Combine(root, "Data");
    public string DatabasePath { get; } = Path.Combine(root, "Data", "import-verifier.db");
    public string LogsDirectory { get; } = Path.Combine(root, "Logs");
    public string CoversDirectory { get; } = Path.Combine(root, "Covers");
    public string LyricsDirectory { get; } = Path.Combine(root, "Lyrics");
    public string ConfigDirectory { get; } = Path.Combine(root, "Config");
    public string TempDirectory { get; } = Path.Combine(root, "FfmpegTemp");
    public string SettingsPath { get; } = Path.Combine(root, "Config", "settings.json");
    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory })
            Directory.CreateDirectory(path);
    }
}

sealed record ImportVerificationReport(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    bool Passed,
    string FfmpegVersion,
    IReadOnlyList<string> AudioFiles,
    ImportResultSnapshot? FirstImport,
    ImportResultSnapshot? SecondImport,
    int FinalTrackCount,
    IReadOnlyList<VerifiedTrack> Tracks,
    IReadOnlyList<string> FirstObservedStages,
    IReadOnlyList<string> SecondObservedStages,
    IReadOnlyList<string> UnrelatedEntries,
    string? Error);

sealed record ImportResultSnapshot(
    string Stage,
    int DiscoveredCount,
    int ProcessedCount,
    int ImportedCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<ImportFailure> Failures);

sealed record VerifiedTrack(Guid Id, string Title, string Artist, string Album, string FilePath, bool FileExists);
