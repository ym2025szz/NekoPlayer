using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Metadata;
using NekoPlayer.Infrastructure.Repositories;

namespace NekoPlayer.Tests;

public sealed class SeekPointerMathTests
{
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(8, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(92, 100, 100)]
    [InlineData(100, 100, 100)]
    [InlineData(-20, 100, 0)]
    [InlineData(120, 100, 100)]
    [InlineData(25, 50, 50)]
    [InlineData(25, 0, 0)]
    public void PointerPositionMapsToExpectedValue(double x, double width, double expected) =>
        Assert.Equal(expected, SeekPointerMath.ValueFromX(x, width, 0, 100), 3);

    [Fact]
    public void PointerMappingSupportsNonZeroRange() => Assert.Equal(15, SeekPointerMath.ValueFromX(50, 100, 10, 20), 3);

    [Theory]
    [InlineData(-5, 200, 0)]
    [InlineData(0, 200, 0)]
    [InlineData(100, 200, 100)]
    [InlineData(199.9, 200, 199.75)]
    [InlineData(220, 200, 199.75)]
    [InlineData(10, 0, 0)]
    public void SeekTargetAlwaysStaysInsideSafeDuration(double target, double duration, double expected) =>
        Assert.Equal(expected, SeekTarget.Clamp(TimeSpan.FromSeconds(target), TimeSpan.FromSeconds(duration)).TotalSeconds, 2);
}

public sealed class GreetingFormatterTests
{
    [Theory]
    [InlineData(5, 0, "早上好")]
    [InlineData(10, 59, "早上好")]
    [InlineData(11, 0, "中午好")]
    [InlineData(12, 59, "中午好")]
    [InlineData(13, 0, "下午好")]
    [InlineData(17, 59, "下午好")]
    [InlineData(18, 0, "晚上好")]
    [InlineData(22, 59, "晚上好")]
    [InlineData(23, 0, "夜深了")]
    [InlineData(4, 59, "夜深了")]
    public void LocalHourUsesExpectedGreetingPeriod(int hour, int minute, string prefix)
    {
        var greeting = GreetingFormatter.GetGreeting(new DateTimeOffset(2026, 8, 4, hour, minute, 0, TimeSpan.FromHours(8)));
        Assert.StartsWith(prefix, greeting);
    }

    [Fact]
    public void ClockCanBeInjected() => Assert.Equal(12, new FakeClock(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.FromHours(8))).LocalNow.Hour);

    private sealed class FakeClock(DateTimeOffset value) : IClock { public DateTimeOffset LocalNow => value; }
}

public sealed class QueueProjectionTests
{
    [Fact]
    public void FirstDisplayIndexIsOne() => Assert.Equal(1, QueueIndexing.Create([Track("a")], null)[0].DisplayIndex);

    [Fact]
    public void DisplayIndexesAreContinuous()
    {
        var result = QueueIndexing.Create([Track("a"), Track("b"), Track("c")], null);
        Assert.Equal([1, 2, 3], result.Select(x => x.DisplayIndex));
    }

    [Fact]
    public void CurrentProjectionUsesTrackId()
    {
        var current = Track("b");
        var result = QueueIndexing.Create([Track("a"), current, Track("c")], current.Id);
        Assert.True(result[1].IsCurrent);
        Assert.Single(result.Where(x => x.IsCurrent));
    }

    [Fact]
    public void RemovingItemRebuildsContinuousIndexes()
    {
        var queue = new PlaybackQueueService();
        queue.Replace([Track("a"), Track("b"), Track("c")]);
        queue.Remove(queue.Items[1].Id);
        Assert.Equal([1, 2], QueueIndexing.Create(queue.Items, queue.Current?.Id).Select(x => x.DisplayIndex));
    }

    [Fact]
    public void SetCurrentDoesNotReorderOrDuplicate()
    {
        var queue = new PlaybackQueueService();
        var tracks = new[] { Track("a"), Track("b"), Track("c") };
        queue.Replace(tracks);
        queue.SetCurrent(tracks[2].Id);
        Assert.Equal(tracks.Select(x => x.Id), queue.Items.Select(x => x.Id));
        Assert.Equal(2, queue.CurrentIndex);
    }

    [Fact]
    public void ClearCurrentKeepsQueueItems()
    {
        var queue = new PlaybackQueueService();
        queue.Replace([Track("a"), Track("b")]);
        queue.ClearCurrent();
        Assert.Null(queue.Current);
        Assert.Equal(2, queue.Items.Count);
    }

    [Fact]
    public void AddStillDeduplicatesTrackIds()
    {
        var queue = new PlaybackQueueService();
        var track = Track("a");
        queue.Add(track); queue.Add(track);
        Assert.Single(queue.Items);
    }

    [Fact]
    public void PlayNextKeepsOtherRelativeOrder()
    {
        var queue = new PlaybackQueueService();
        var tracks = new[] { Track("a"), Track("b"), Track("c") };
        queue.Replace(tracks); queue.PlayNext(tracks[2]);
        Assert.Equal([tracks[0].Id, tracks[2].Id, tracks[1].Id], queue.Items.Select(x => x.Id));
    }

    private static Track Track(string title) => new() { Id = Guid.NewGuid(), Title = title, FilePath = title + ".mp3" };
}

public sealed class PlaylistBatchPersistenceTests
{
    [Fact] public void PlaylistAddResultReportsRequestedCount() => Assert.Equal(5, new PlaylistAddResult(3, 2).RequestedCount);
    [Fact] public async Task SingleTrackAddSucceeds() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); var r = await h.Playlists.AddTracksAsync(p.Id, [t.Id]); Assert.Equal(1, r.AddedCount); }
    [Fact] public async Task MultipleTrackAddSucceeds() { await using var h = await V014Harness.CreateAsync(); var a = await h.AddTrackAsync(); var b = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); var r = await h.Playlists.AddTracksAsync(p.Id, [a.Id, b.Id]); Assert.Equal(2, r.AddedCount); }
    [Fact] public async Task DuplicateTrackIsSkipped() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTrackAsync(p.Id, t.Id); var r = await h.Playlists.AddTracksAsync(p.Id, [t.Id]); Assert.Equal((0, 1), (r.AddedCount, r.SkippedCount)); }
    [Fact] public async Task DuplicateInputIdsCreateOneRelation() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTracksAsync(p.Id, [t.Id, t.Id]); await using var db = h.CreateDb(); Assert.Equal(1, await db.PlaylistTracks.CountAsync()); }
    [Fact] public async Task AddingDoesNotCreateDuplicateTrack() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTrackAsync(p.Id, t.Id); await using var db = h.CreateDb(); Assert.Equal(1, await db.Tracks.CountAsync()); }
    [Fact] public async Task AddingDoesNotChangeFavorite() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(true); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTrackAsync(p.Id, t.Id); await using var db = h.CreateDb(); Assert.True((await db.Tracks.SingleAsync()).IsFavorite); }
    [Fact] public async Task AddingDoesNotChangeHistory() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); await h.AddHistoryAsync(t.Id); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTrackAsync(p.Id, t.Id); await using var db = h.CreateDb(); Assert.Equal(1, await db.PlaybackHistories.CountAsync()); }
    [Fact] public async Task AddedTracksAreImmediatelyQueryable() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTrackAsync(p.Id, t.Id); Assert.Equal(t.Id, Assert.Single(await h.Playlists.GetTracksAsync(p.Id)).Id); }
    [Fact] public async Task TrackIdsExposeExistingMembership() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTrackAsync(p.Id, t.Id); Assert.Contains(t.Id, await h.Playlists.GetTrackIdsAsync(p.Id)); }
    [Fact] public async Task SortOrderFollowsSelectionOrder() { await using var h = await V014Harness.CreateAsync(); var a = await h.AddTrackAsync(); var b = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTracksAsync(p.Id, [a.Id, b.Id]); await using var db = h.CreateDb(); Assert.Equal([0, 1], await db.PlaylistTracks.OrderBy(x => x.SortOrder).Select(x => x.SortOrder).ToListAsync()); }
    [Fact] public async Task UnknownTrackIsReportedAsSkipped() { await using var h = await V014Harness.CreateAsync(); var p = await h.AddPlaylistAsync(); var r = await h.Playlists.AddTracksAsync(p.Id, [Guid.NewGuid()]); Assert.Equal((0, 1), (r.AddedCount, r.SkippedCount)); }
    [Fact] public async Task MissingPlaylistThrows() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Playlists.AddTrackAsync(Guid.NewGuid(), t.Id)); }
}

public sealed class LibraryRemovalPersistenceTests
{
    [Fact] public async Task SingleRemovalDeletesTrackRow() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var r = await h.Library.RemoveFromLibraryAsync([t.Id]); Assert.Equal(1, r.RemovedCount); Assert.Empty(await h.Library.GetTracksAsync()); }
    [Fact] public async Task SingleRemovalKeepsPhysicalFile() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(createFile: true); await h.Library.RemoveFromLibraryAsync([t.Id]); Assert.True(File.Exists(t.FilePath)); }
    [Fact] public async Task MultipleRemovalOnlyAffectsSelected() { await using var h = await V014Harness.CreateAsync(); var a = await h.AddTrackAsync(); var b = await h.AddTrackAsync(); var c = await h.AddTrackAsync(); await h.Library.RemoveFromLibraryAsync([a.Id, b.Id]); Assert.Equal(c.Id, Assert.Single(await h.Library.GetTracksAsync()).Id); }
    [Fact] public async Task RemovalCascadesPlaylistTrack() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTrackAsync(p.Id, t.Id); await h.Library.RemoveFromLibraryAsync([t.Id]); await using var db = h.CreateDb(); Assert.Empty(await db.PlaylistTracks.ToListAsync()); }
    [Fact] public async Task RemovalCascadesPlaybackHistory() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); await h.AddHistoryAsync(t.Id); await h.Library.RemoveFromLibraryAsync([t.Id]); await using var db = h.CreateDb(); Assert.Empty(await db.PlaybackHistories.ToListAsync()); }
    [Fact] public async Task RemovingFavoriteDoesNotChangeOtherFavorite() { await using var h = await V014Harness.CreateAsync(); var a = await h.AddTrackAsync(true); var b = await h.AddTrackAsync(true); await h.Library.RemoveFromLibraryAsync([a.Id]); Assert.True(Assert.Single(await h.Library.GetTracksAsync()).IsFavorite); }
    [Fact] public async Task UnknownTrackDoesNotCountAsRemoved() { await using var h = await V014Harness.CreateAsync(); var r = await h.Library.RemoveFromLibraryAsync([Guid.NewGuid()]); Assert.Equal((1, 0), (r.RequestedCount, r.RemovedCount)); }
    [Fact] public async Task DuplicateRequestedIdCountsOnce() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var r = await h.Library.RemoveFromLibraryAsync([t.Id, t.Id]); Assert.Equal(1, r.RequestedCount); }
    [Fact] public async Task CancelledRemovalKeepsTrack() { await using var h = await V014Harness.CreateAsync(); await h.AddTrackAsync(); using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Library.RemoveFromLibraryAsync(h.TrackIds, cts.Token)); Assert.Single(await h.Library.GetTracksAsync()); }
    [Fact] public async Task RemovalKeepsFileHashAndTimestamp() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(createFile: true); var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(t.FilePath)); var beforeTime = File.GetLastWriteTimeUtc(t.FilePath); await h.Library.RemoveFromLibraryAsync([t.Id]); Assert.Equal(beforeHash, SHA256.HashData(await File.ReadAllBytesAsync(t.FilePath))); Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(t.FilePath)); }
    [Fact] public async Task RemovalKeepsUnselectedPlaylistRelation() { await using var h = await V014Harness.CreateAsync(); var a = await h.AddTrackAsync(); var b = await h.AddTrackAsync(); var p = await h.AddPlaylistAsync(); await h.Playlists.AddTracksAsync(p.Id, [a.Id, b.Id]); await h.Library.RemoveFromLibraryAsync([a.Id]); Assert.Equal(b.Id, Assert.Single(await h.Playlists.GetTracksAsync(p.Id)).Id); }
    [Fact] public async Task ResultContainsExactRemovedIds() { await using var h = await V014Harness.CreateAsync(); var t = await h.AddTrackAsync(); var r = await h.Library.RemoveFromLibraryAsync([t.Id, Guid.NewGuid()]); Assert.Equal([t.Id], r.RemovedTrackIds); }
}

public sealed class V014SourceContractTests
{
    private static readonly string Root = FindRoot();
    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    [Fact] public void SeekControlUsesHandledEventsToo() => Assert.Contains("handledEventsToo: true", Read(@"src\NekoPlayer.App\Controls\PlaybackSeekSlider.cs"));
    [Fact] public void SeekControlOwnsFullHitSurface() => Assert.Contains("DrawRectangle(Brushes.Transparent", Read(@"src\NekoPlayer.App\Controls\PlaybackSeekSlider.cs"));
    [Fact] public void SeekControlDoesNotDependOnDefaultSliderTemplate() => Assert.Contains("PlaybackSeekSlider : TemplatedControl", Read(@"src\NekoPlayer.App\Controls\PlaybackSeekSlider.cs"));
    [Fact] public void SeekControlCapturesPointer() => Assert.Contains("e.Pointer.Capture(this)", Read(@"src\NekoPlayer.App\Controls\PlaybackSeekSlider.cs"));
    [Fact] public void SeekControlHandlesCaptureLost() => Assert.Contains("PointerCaptureLostEvent", Read(@"src\NekoPlayer.App\Controls\PlaybackSeekSlider.cs"));
    [Fact] public void SeekControlCommitsOnRelease() => Assert.Contains("SeekCompleted?.Invoke", Read(@"src\NekoPlayer.App\Controls\PlaybackSeekSlider.cs"));
    [Fact] public void SeekControlSupportsKeyboard() => Assert.Contains("Key.End", Read(@"src\NekoPlayer.App\Controls\PlaybackSeekSlider.cs"));
    [Fact] public void BothPlaybackLayoutsUseSameSeekControl() => Assert.Equal(2, Count(Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"), "controls:PlaybackSeekSlider"));
    [Fact] public void OldOuterPointerHandlersAreGone() => Assert.DoesNotContain("OnSeekPointerReleased", Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void NavigationButtonStretches() => Assert.Contains("HorizontalAlignment\" Value=\"Stretch", Read(@"src\NekoPlayer.App\App.axaml"));
    [Fact] public void NavigationContentUsesUnifiedClass() => Assert.Equal(7, Count(Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"), "Classes=\"navContent\""));
    [Fact] public void TrackPickerExists() => Assert.Contains("IsTrackPickerVisible", Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void PlaylistPickerExists() => Assert.Contains("IsPlaylistPickerVisible", Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void RemovalWarningMentionsDiskFiles() => Assert.Contains("不会删除磁盘上的音频文件", Read(@"src\NekoPlayer.App\ViewModels\MainWindowViewModel.cs"));
    [Fact] public void ProductionSourceNeverCallsFileDelete() => Assert.DoesNotContain("File.Delete", string.Join('\n', Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText)));
    [Fact] public void CreatorTextIsExact() => Assert.Contains("创作者：梦怀殇", Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void GreetingIsVisibleOnHome() => Assert.Contains("GreetingText", Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void QueueDisplaysOneBasedIndex() => Assert.Contains("DisplayIndex", Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void QueueHasImmediatePlayCommand() => Assert.Contains("PlayQueueItemCommand", Read(@"src\NekoPlayer.App\Views\MainWindow.axaml"));
    [Fact] public void StopPreservesLoadedFile() => Assert.Contains("StopCoreAsync(false)", Read(@"src\NekoPlayer.Audio\Playback\FfmpegAudioPlayerService.cs"));
    [Fact] public void UnloadIsSeparateFromStop() => Assert.Contains("public async Task UnloadAsync", Read(@"src\NekoPlayer.Audio\Playback\FfmpegAudioPlayerService.cs"));
    [Fact] public void ReleaseDiagnosticsRemainConditional() => Assert.Contains("Condition=\"'$(Configuration)' == 'Debug'\"", Read(@"src\NekoPlayer.App\NekoPlayer.App.csproj"));

    private static int Count(string text, string value) => (text.Length - text.Replace(value, string.Empty).Length) / value.Length;
    private static string FindRoot() { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "NekoPlayer.sln"))) return d.FullName; throw new DirectoryNotFoundException(); }
}

internal sealed class V014Harness : IAsyncDisposable
{
    private readonly V014Factory _factory;
    private int _number;
    private V014Harness(string root, V014Factory factory, MusicLibraryService library, PlaylistService playlists) { Root = root; _factory = factory; Library = library; Playlists = playlists; }
    public string Root { get; }
    public MusicLibraryService Library { get; }
    public PlaylistService Playlists { get; }
    public IReadOnlyList<Guid> TrackIds { get; private set; } = [];
    public NekoPlayerDbContext CreateDb() => _factory.CreateDbContext();
    public static async Task<V014Harness> CreateAsync() { var root = Path.Combine(Path.GetTempPath(), "NekoPlayerV014Tests", Guid.NewGuid().ToString("N")); var paths = new V014Paths(root); paths.EnsureCreated(); var options = new DbContextOptionsBuilder<NekoPlayerDbContext>().UseSqlite($"Data Source={paths.DatabasePath}").Options; var factory = new V014Factory(options); var library = new MusicLibraryService(factory, new TrackMetadataReader(new V014NoFfmpeg(), paths), new TrackStateStore()); await library.InitializeAsync(); return new V014Harness(root, factory, library, new PlaylistService(factory)); }
    public async Task<Track> AddTrackAsync(bool favorite = false, bool createFile = false) { var n = Interlocked.Increment(ref _number); var path = Path.Combine(Root, $"track-{n}.mp3"); if (createFile) await File.WriteAllBytesAsync(path, [1, 2, 3, (byte)n]); var track = new Track { Title = $"Track {n}", Artist = "Tester", Album = "Temp", FilePath = path, IsFavorite = favorite, Duration = TimeSpan.FromMinutes(3) }; await using var db = CreateDb(); db.Tracks.Add(track); await db.SaveChangesAsync(); TrackIds = TrackIds.Append(track.Id).ToArray(); return track; }
    public async Task<Playlist> AddPlaylistAsync() { var playlist = new Playlist { Name = "Test playlist" }; await using var db = CreateDb(); db.Playlists.Add(playlist); await db.SaveChangesAsync(); return playlist; }
    public async Task AddHistoryAsync(Guid trackId) { await using var db = CreateDb(); db.PlaybackHistories.Add(new PlaybackHistory { TrackId = trackId, PlayedAt = DateTime.UtcNow, LastPosition = TimeSpan.FromSeconds(12) }); await db.SaveChangesAsync(); }
    public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); return ValueTask.CompletedTask; }
}

internal sealed class V014Factory(DbContextOptions<NekoPlayerDbContext> options) : IDbContextFactory<NekoPlayerDbContext> { public NekoPlayerDbContext CreateDbContext() => new(options); }
internal sealed class V014Paths(string root) : IUserDataPaths { public string Root { get; } = root; public string DataDirectory { get; } = Path.Combine(root, "Data"); public string DatabasePath { get; } = Path.Combine(root, "Data", "test.db"); public string LogsDirectory { get; } = Path.Combine(root, "Logs"); public string CoversDirectory { get; } = Path.Combine(root, "Covers"); public string LyricsDirectory { get; } = Path.Combine(root, "Lyrics"); public string ConfigDirectory { get; } = Path.Combine(root, "Config"); public string TempDirectory { get; } = Path.Combine(root, "Temp"); public string SettingsPath { get; } = Path.Combine(root, "Config", "settings.json"); public void EnsureCreated() { foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory }) Directory.CreateDirectory(path); } }
internal sealed class V014NoFfmpeg : IFfmpegLocator { public string BinaryDirectory => string.Empty; public string FfmpegPath => string.Empty; public string FfprobePath => string.Empty; public bool IsAvailable => false; public bool HasSharedLibraries => false; public string Version => "none"; public string StatusMessage => "none"; public void Configure() { } public Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FfmpegValidationResult(false, false, Version, BinaryDirectory, StatusMessage)); }
