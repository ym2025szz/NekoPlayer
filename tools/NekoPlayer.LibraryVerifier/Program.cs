using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Metadata;
using NekoPlayer.Infrastructure.Repositories;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 2 && string.Equals(args[0], "--snapshot-database", StringComparison.OrdinalIgnoreCase))
    return await SnapshotDatabaseAsync(Path.GetFullPath(args[1]));

var root = FindProjectRoot(AppContext.BaseDirectory);
var reportDirectory = Path.Combine(root, "artifacts", "test-reports");
Directory.CreateDirectory(reportDirectory);
var report = new LibraryPerformanceReport
{
    StartedAtUtc = DateTime.UtcNow,
    Machine = $"{Environment.MachineName} / {Environment.OSVersion} / {Environment.ProcessorCount} logical CPUs / {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d:0.0} GiB available"
};
var log = new StringBuilder();

foreach (var count in new[] { 500, 1000, 2000 })
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "NekoPlayerLibraryVerifier", $"{count}-{Guid.NewGuid():N}");
    var paths = new TempPaths(tempRoot);
    paths.EnsureCreated();
    try
    {
        var options = new DbContextOptionsBuilder<NekoPlayerDbContext>().UseSqlite($"Data Source={paths.DatabasePath}").Options;
        var factory = new DbFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Tracks.AddRange(Enumerable.Range(1, count).Select(i => new Track
            {
                Title = $"Track {i:0000} responsive mix",
                Artist = $"Artist {i % 83:00}",
                Album = $"Album {i % 37:00}",
                Genre = i % 2 == 0 ? "Electronic" : "Acoustic",
                FilePath = Path.Combine(tempRoot, $"virtual-{i:0000}.mp3"),
                Duration = TimeSpan.FromSeconds(120 + i % 360),
                AddedAt = DateTime.UtcNow.AddSeconds(-i),
                PlayCount = i % 19
            }));
            var seedWatch = Stopwatch.StartNew();
            await db.SaveChangesAsync();
            seedWatch.Stop();

            var queryWatch = Stopwatch.StartNew();
            var tracks = await db.Tracks.AsNoTracking().OrderBy(x => x.Title).ToListAsync();
            queryWatch.Stop();

            var searchWatch = Stopwatch.StartNew();
            var searchCount = tracks.Count(x => TrackSearch.Matches(x, "Artist 17"));
            searchWatch.Stop();

            var sortWatch = Stopwatch.StartNew();
            var sorted = tracks.OrderBy(x => x.Album).ThenBy(x => x.Artist).ThenBy(x => x.Title).ToArray();
            sortWatch.Stop();

            var favoriteTarget = await db.Tracks.OrderBy(x => x.Title).Skip(count / 2).FirstAsync();
            var favoriteWatch = Stopwatch.StartNew();
            favoriteTarget.IsFavorite = true;
            await db.SaveChangesAsync();
            favoriteWatch.Stop();
            var favoriteCount = await db.Tracks.CountAsync(x => x.IsFavorite);

            db.PlaybackHistories.Add(new PlaybackHistory { TrackId = favoriteTarget.Id, PlayedAt = DateTime.UtcNow, LastPosition = TimeSpan.FromSeconds(30) });
            await db.SaveChangesAsync();

            var store = new TrackStateStore();
            var service = new MusicLibraryService(factory, new TrackMetadataReader(new UnavailableFfmpeg(), paths), store);
            var recent = AssertSingle(await service.GetRecentAsync(), "recent row");
            var removal = await service.RemoveRecentAsync(recent.HistoryId);
            var trackStillExists = await db.Tracks.AnyAsync(x => x.Id == favoriteTarget.Id);
            var historyRemoved = !await db.PlaybackHistories.AnyAsync(x => x.TrackId == favoriteTarget.Id);

            var result = new LibraryScenario
            {
                RecordCount = count,
                SeedMilliseconds = seedWatch.Elapsed.TotalMilliseconds,
                QueryMilliseconds = queryWatch.Elapsed.TotalMilliseconds,
                SearchMilliseconds = searchWatch.Elapsed.TotalMilliseconds,
                SortMilliseconds = sortWatch.Elapsed.TotalMilliseconds,
                FavoriteUpdateMilliseconds = favoriteWatch.Elapsed.TotalMilliseconds,
                SearchMatches = searchCount,
                SortedCount = sorted.Length,
                FavoriteInvariantPassed = favoriteCount == 1,
                RecentRemovalInvariantPassed = removal is not null && trackStillExists && historyRemoved
            };
            report.Scenarios.Add(result);
            log.AppendLine($"{count}: seed={result.SeedMilliseconds:0.###}ms query={result.QueryMilliseconds:0.###}ms search={result.SearchMilliseconds:0.###}ms sort={result.SortMilliseconds:0.###}ms favorite={result.FavoriteUpdateMilliseconds:0.###}ms matches={searchCount} favoriteInvariant={result.FavoriteInvariantPassed} recentInvariant={result.RecentRemovalInvariantPassed}");
        }
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
    }
}

report.CompletedAtUtc = DateTime.UtcNow;
report.Passed = report.Scenarios.Count == 3 && report.Scenarios.All(x => x.SortedCount == x.RecordCount && x.SearchMatches > 0 && x.FavoriteInvariantPassed && x.RecentRemovalInvariantPassed);
var jsonPath = Path.Combine(reportDirectory, "library-performance.json");
var markdownPath = Path.Combine(reportDirectory, "library-performance.md");
var logPath = Path.Combine(reportDirectory, "library-performance.log");
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report));
await File.WriteAllTextAsync(logPath, log.ToString());
Console.Write(log);
Console.WriteLine($"Report: {markdownPath}");
return report.Passed ? 0 : 1;

static T AssertSingle<T>(IReadOnlyList<T> values, string name) => values.Count == 1 ? values[0] : throw new InvalidOperationException($"Expected one {name}, got {values.Count}.");

static async Task<int> SnapshotDatabaseAsync(string databasePath)
{
    if (!File.Exists(databasePath)) throw new FileNotFoundException("Database not found.", databasePath);
    var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly };
    await using var connection = new SqliteConnection(builder.ConnectionString);
    await connection.OpenAsync();
    var counts = new Dictionary<string, long>();
    foreach (var table in new[] { "Tracks", "Playlists", "PlaylistTracks", "PlaybackHistories", "LibraryFolders" })
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        counts[table] = (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT COUNT(*) FROM Tracks WHERE IsFavorite = 1";
        counts["Favorites"] = (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
    var snapshot = new
    {
        DatabasePath = databasePath,
        FileLength = new FileInfo(databasePath).Length,
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(databasePath))),
        Counts = counts
    };
    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static string BuildMarkdown(LibraryPerformanceReport report)
{
    var builder = new StringBuilder();
    builder.AppendLine("# NekoPlayer v1.0.0 大曲库性能验证");
    builder.AppendLine();
    builder.AppendLine($"- 机器：{report.Machine}");
    builder.AppendLine($"- 开始（UTC）：{report.StartedAtUtc:o}");
    builder.AppendLine($"- 完成（UTC）：{report.CompletedAtUtc:o}");
    builder.AppendLine($"- 结果：{(report.Passed ? "通过" : "未通过")}");
    builder.AppendLine("- 数据库：每个规模使用独立临时 SQLite，完成后删除；未使用正式数据库，也未创建真实音频文件。");
    builder.AppendLine();
    builder.AppendLine("| 记录数 | 写入 ms | 查询 ms | 搜索 ms | 排序 ms | 单条收藏 ms | 搜索命中 | 数据边界 | ");
    builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|:---|");
    foreach (var item in report.Scenarios)
        builder.AppendLine($"| {item.RecordCount} | {item.SeedMilliseconds:0.###} | {item.QueryMilliseconds:0.###} | {item.SearchMilliseconds:0.###} | {item.SortMilliseconds:0.###} | {item.FavoriteUpdateMilliseconds:0.###} | {item.SearchMatches} | 收藏仅一条：{item.FavoriteInvariantPassed}；移除历史保留 Track：{item.RecentRemovalInvariantPassed} |");
    builder.AppendLine();
    builder.AppendLine("> 本报告记录真实机器耗时，不设置脆弱的固定毫秒门槛，也不声称 GUI 滚动达到特定 FPS。Avalonia ListBox 保持默认虚拟化面板，封面仍按缩略图宽度解码。");
    return builder.ToString();
}

static string FindProjectRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "NekoPlayer.sln"))) return directory.FullName;
    throw new DirectoryNotFoundException("NekoPlayer.sln not found.");
}

sealed class LibraryPerformanceReport
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public string Machine { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public List<LibraryScenario> Scenarios { get; } = [];
}

sealed class LibraryScenario
{
    public int RecordCount { get; set; }
    public double SeedMilliseconds { get; set; }
    public double QueryMilliseconds { get; set; }
    public double SearchMilliseconds { get; set; }
    public double SortMilliseconds { get; set; }
    public double FavoriteUpdateMilliseconds { get; set; }
    public int SearchMatches { get; set; }
    public int SortedCount { get; set; }
    public bool FavoriteInvariantPassed { get; set; }
    public bool RecentRemovalInvariantPassed { get; set; }
}

sealed class DbFactory(DbContextOptions<NekoPlayerDbContext> options) : IDbContextFactory<NekoPlayerDbContext>
{
    public NekoPlayerDbContext CreateDbContext() => new(options);
}

sealed class TempPaths(string root) : IUserDataPaths
{
    public string Root { get; } = root;
    public string DataDirectory { get; } = Path.Combine(root, "Data");
    public string DatabasePath { get; } = Path.Combine(root, "Data", "library-performance.db");
    public string LogsDirectory { get; } = Path.Combine(root, "Logs");
    public string CoversDirectory { get; } = Path.Combine(root, "Covers");
    public string LyricsDirectory { get; } = Path.Combine(root, "Lyrics");
    public string ConfigDirectory { get; } = Path.Combine(root, "Config");
    public string TempDirectory { get; } = Path.Combine(root, "Temp");
    public string SettingsPath { get; } = Path.Combine(root, "Config", "settings.json");
    public void EnsureCreated() { foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory }) Directory.CreateDirectory(path); }
}

sealed class UnavailableFfmpeg : IFfmpegLocator
{
    public string BinaryDirectory => string.Empty;
    public string FfmpegPath => string.Empty;
    public string FfprobePath => string.Empty;
    public bool IsAvailable => false;
    public bool HasSharedLibraries => false;
    public string Version => "unavailable";
    public string StatusMessage => "unavailable";
    public void Configure() { }
    public Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FfmpegValidationResult(false, false, Version, BinaryDirectory, StatusMessage));
}
