using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Metadata;
using NekoPlayer.Infrastructure.Repositories;

namespace NekoPlayer.Tests;

public sealed class ImportServiceTests
{
    [Fact]
    public async Task NewSongIsPersistedAndCountedAsImported()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var path = harness.CreateAudio("new-song.mp3");

        var result = await harness.Service.ImportAsync([path]);
        var tracks = await harness.Service.GetTracksAsync();

        Assert.Equal(ImportStage.Completed, result.Stage);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Single(tracks);
        Assert.Equal("new-song", tracks[0].Title);
        Assert.Equal("未知艺术家", tracks[0].Artist);
    }

    [Fact]
    public async Task ExistingUnchangedSongIsSkippedWithoutDuplicate()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var path = harness.CreateAudio("repeat.mp3");

        await harness.Service.ImportAsync([path]);
        var result = await harness.Service.ImportAsync([path]);

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(await harness.Service.GetTracksAsync());
    }

    [Fact]
    public async Task CaseVariantOfSameWindowsPathDoesNotDuplicate()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var path = harness.CreateAudio("CaseSong.mp3");

        var result = await harness.Service.ImportAsync([path, path.ToUpperInvariant()]);

        Assert.Equal(1, result.DiscoveredCount);
        Assert.Equal(1, result.ImportedCount);
        Assert.Single(await harness.Service.GetTracksAsync());
    }

    [Fact]
    public async Task UnsupportedFilesAreNotDiscoveredOrPersisted()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var lrc = harness.CreateFile("song.lrc");
        var png = harness.CreateFile("cover.png");
        var txt = harness.CreateFile("notes.txt");

        var result = await harness.Service.ImportAsync([lrc, png, txt]);

        Assert.Equal(0, result.DiscoveredCount);
        Assert.Empty(await harness.Service.GetTracksAsync());
    }

    [Fact]
    public async Task MissingSupportedFileIsCountedAsFailure()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var missing = Path.Combine(harness.Root, "missing.mp3");

        var result = await harness.Service.ImportAsync([missing]);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(ImportStage.Failed, result.Stage);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task PreCancelledImportReturnsCancelledState()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var path = harness.CreateAudio("cancel.mp3");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await harness.Service.ImportAsync([path], cancellationToken: cancellation.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(ImportStage.Cancelled, result.Stage);
        Assert.Empty(await harness.Service.GetTracksAsync());
    }

    [Fact]
    public async Task UpdatedTrackKeepsIdentityFavoriteAndPlayCount()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var path = harness.CreateAudio("updated.mp3");
        await harness.Service.ImportAsync([path]);
        var original = (await harness.Service.GetTracksAsync()).Single();
        await harness.SetUserStateAsync(original.Id, true, 7);
        await File.AppendAllTextAsync(path, "changed");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        var result = await harness.Service.ImportAsync([path]);
        var updated = (await harness.Service.GetTracksAsync()).Single();

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(original.Id, updated.Id);
        Assert.True(updated.IsFavorite);
        Assert.Equal(7, updated.PlayCount);
    }

    [Fact]
    public async Task ImportedTrackIdsAreConfirmedByDatabaseRequery()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var path = harness.CreateAudio("confirmed.mp3");

        var result = await harness.Service.ImportAsync([path]);
        var tracks = await harness.Service.GetTracksAsync();

        Assert.Single(result.AffectedTrackIds);
        Assert.Contains(tracks, x => x.Id == result.AffectedTrackIds[0]);
    }

    [Fact]
    public void ZeroImportedNeverProducesImportedSuccessMessage()
    {
        var result = Result(imported: 0, skipped: 2);
        var message = ImportSummary.Create(result, true, false);
        Assert.Equal("没有新增歌曲，2 首已存在", message);
        Assert.DoesNotContain("已导入", message);
    }

    [Fact]
    public void UiConfirmationFailureCannotProduceCompleteSuccessMessage()
    {
        var result = Result(imported: 1);
        var message = ImportSummary.Create(result, false, false);
        Assert.Contains("列表刷新未确认", message);
        Assert.DoesNotContain("已导入 1 首歌曲", message);
    }

    [Fact]
    public void SearchHiddenImportProducesExplicitNotice()
    {
        var result = Result(imported: 1);
        var message = ImportSummary.Create(result, true, true);
        Assert.Contains("当前搜索条件将其隐藏", message);
    }

    [Fact]
    public async Task RuntimeLibraryContainsOnlyPersistedRealSelections()
    {
        await using var harness = await ImportHarness.CreateAsync();
        var real = harness.CreateAudio("real-track.mp3");
        harness.CreateFile("MockTrack.lrc");
        harness.CreateFile("SampleTrack.png");

        await harness.Service.ScanFolderAsync(harness.Root, false);
        var tracks = await harness.Service.GetTracksAsync();

        var track = Assert.Single(tracks);
        Assert.Equal(Path.GetFullPath(real), track.FilePath);
        Assert.DoesNotContain(tracks, x => x.Title.Contains("Mock", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tracks, x => x.Title.Contains("Sample", StringComparison.OrdinalIgnoreCase));
    }

    private static ImportResult Result(int imported = 0, int skipped = 0, int failed = 0) => new(
        failed > 0 ? ImportStage.Failed : ImportStage.Completed,
        imported + skipped + failed,
        imported + skipped + failed,
        imported,
        0,
        skipped,
        failed,
        [],
        imported > 0 ? [Guid.NewGuid()] : []);

    private sealed class ImportHarness : IAsyncDisposable
    {
        private readonly TestDbContextFactory _factory;

        private ImportHarness(string root, TestPaths paths, TestDbContextFactory factory, MusicLibraryService service)
        {
            Root = root;
            Paths = paths;
            _factory = factory;
            Service = service;
        }

        public string Root { get; }
        public TestPaths Paths { get; }
        public MusicLibraryService Service { get; }

        public static async Task<ImportHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "NekoPlayerImportTests", Guid.NewGuid().ToString("N"));
            var paths = new TestPaths(root);
            paths.EnsureCreated();
            var options = new DbContextOptionsBuilder<NekoPlayerDbContext>()
                .UseSqlite($"Data Source={paths.DatabasePath}")
                .Options;
            var factory = new TestDbContextFactory(options);
            var service = new MusicLibraryService(factory, new TrackMetadataReader(new UnavailableFfmpeg(), paths), new TrackStateStore());
            await service.InitializeAsync();
            return new ImportHarness(root, paths, factory, service);
        }

        public string CreateAudio(string name) => CreateFile(name);

        public string CreateFile(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
            return path;
        }

        public async Task SetUserStateAsync(Guid id, bool favorite, int playCount)
        {
            await using var db = _factory.CreateDbContext();
            var track = await db.Tracks.FindAsync(id) ?? throw new InvalidOperationException();
            track.IsFavorite = favorite;
            track.PlayCount = playCount;
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<NekoPlayerDbContext> options) : IDbContextFactory<NekoPlayerDbContext>
    {
        public NekoPlayerDbContext CreateDbContext() => new(options);
    }

    private sealed class TestPaths(string root) : IUserDataPaths
    {
        public string Root { get; } = root;
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string DatabasePath { get; } = Path.Combine(root, "Data", "test.db");
        public string LogsDirectory { get; } = Path.Combine(root, "Logs");
        public string CoversDirectory { get; } = Path.Combine(root, "Covers");
        public string LyricsDirectory { get; } = Path.Combine(root, "Lyrics");
        public string ConfigDirectory { get; } = Path.Combine(root, "Config");
        public string TempDirectory { get; } = Path.Combine(root, "Temp");
        public string SettingsPath { get; } = Path.Combine(root, "Config", "settings.json");

        public void EnsureCreated()
        {
            foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory })
                Directory.CreateDirectory(path);
        }
    }

    private sealed class UnavailableFfmpeg : IFfmpegLocator
    {
        public string BinaryDirectory => string.Empty;
        public string FfmpegPath => string.Empty;
        public string FfprobePath => string.Empty;
        public bool IsAvailable => false;
        public bool HasSharedLibraries => false;
        public string Version => "unavailable";
        public string StatusMessage => "unavailable";
        public void Configure() { }
        public Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfmpegValidationResult(false, false, Version, BinaryDirectory, StatusMessage));
    }
}
