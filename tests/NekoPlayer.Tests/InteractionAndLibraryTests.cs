using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Metadata;
using NekoPlayer.Infrastructure.Repositories;

namespace NekoPlayer.Tests;

public sealed class InteractionStateTests
{
    [Theory]
    [InlineData(800, ResponsiveLayoutMode.Compact)]
    [InlineData(999.9, ResponsiveLayoutMode.Compact)]
    [InlineData(1000, ResponsiveLayoutMode.Standard)]
    [InlineData(1199.9, ResponsiveLayoutMode.Standard)]
    [InlineData(1200, ResponsiveLayoutMode.Wide)]
    [InlineData(1920, ResponsiveLayoutMode.Wide)]
    public void ResponsiveBreakpointsAreStable(double width, ResponsiveLayoutMode expected) =>
        Assert.Equal(expected, ResponsiveLayout.Resolve(width));

    [Theory]
    [InlineData(PlaybackState.Playing, true, false, PlaybackPrimaryAction.Pause)]
    [InlineData(PlaybackState.Paused, true, false, PlaybackPrimaryAction.Play)]
    [InlineData(PlaybackState.Stopped, true, false, PlaybackPrimaryAction.Play)]
    [InlineData(PlaybackState.Loading, true, false, PlaybackPrimaryAction.Busy)]
    [InlineData(PlaybackState.Seeking, true, false, PlaybackPrimaryAction.Busy)]
    [InlineData(PlaybackState.Buffering, true, false, PlaybackPrimaryAction.Busy)]
    [InlineData(PlaybackState.Error, true, false, PlaybackPrimaryAction.Retry)]
    [InlineData(PlaybackState.Idle, false, false, PlaybackPrimaryAction.Disabled)]
    [InlineData(PlaybackState.Idle, false, true, PlaybackPrimaryAction.Play)]
    public void PlaybackStateMapsToNextAction(PlaybackState state, bool current, bool queue, PlaybackPrimaryAction expected) =>
        Assert.Equal(expected, PlaybackActionMapper.Resolve(state, current, queue));

    [Theory]
    [InlineData(-5, 100, 0)]
    [InlineData(0, 100, 0)]
    [InlineData(30, 100, 30)]
    [InlineData(120, 100, 99.75)]
    [InlineData(5, 0, 0)]
    public void SeekTargetIsClamped(double target, double duration, double expected) =>
        Assert.Equal(expected, SeekTarget.Clamp(TimeSpan.FromSeconds(target), TimeSpan.FromSeconds(duration)).TotalSeconds, 3);

    [Theory]
    [InlineData(10, "刚刚")]
    [InlineData(120, "2 分钟前")]
    [InlineData(7200, "2 小时前")]
    public void RelativeTimeFormatsRecentValues(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, RelativeTimeFormatter.Format(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void RelativeTimeFormatsYesterday()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("昨天", RelativeTimeFormatter.Format(new DateTime(2026, 8, 3, 18, 0, 0, DateTimeKind.Utc), now));
    }

    [Fact]
    public async Task UndoCoordinatorExecutesOnlyCurrentContext()
    {
        var coordinator = new UndoActionCoordinator();
        var values = new List<int>();
        coordinator.Set(Guid.NewGuid(), _ => { values.Add(1); return Task.CompletedTask; });
        coordinator.Set(Guid.NewGuid(), _ => { values.Add(2); return Task.CompletedTask; });

        Assert.True(await coordinator.ExecuteCurrentAsync());
        Assert.Equal([2], values);
        Assert.False(await coordinator.ExecuteCurrentAsync());
    }

    [Fact]
    public async Task UndoCoordinatorClearExpiresExactToken()
    {
        var coordinator = new UndoActionCoordinator();
        var token = coordinator.Set(Guid.NewGuid(), _ => Task.CompletedTask);
        Assert.True(coordinator.Clear(token));
        Assert.False(await coordinator.ExecuteCurrentAsync());
    }

    [Fact]
    public async Task DebouncedSeekCommitsOnlyLastPreview()
    {
        using var coordinator = new SeekRequestCoordinator();
        var committed = new List<TimeSpan>();
        var first = coordinator.SubmitAsync(TimeSpan.FromSeconds(10), (value, _) => { committed.Add(value); return Task.CompletedTask; }, TimeSpan.FromSeconds(5));
        var second = coordinator.SubmitAsync(TimeSpan.FromSeconds(25), (value, _) => { committed.Add(value); return Task.CompletedTask; }, TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.Equal([TimeSpan.FromSeconds(25)], committed);
    }

    [Fact]
    public void TrackNotifiesFavoriteAndFileAvailability()
    {
        var track = new Track();
        var names = new List<string?>();
        track.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        track.IsFavorite = true;
        track.FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp3");
        Assert.Contains(nameof(Track.IsFavorite), names);
        Assert.Contains(nameof(Track.FileExists), names);
        Assert.False(track.FileExists);
    }
}

public sealed class FavoriteAndRecentPersistenceTests
{
    [Fact]
    public async Task FavoritePersistsAndPublishesCorrectTrack()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var track = await harness.AddTrackAsync(favorite: false, playCount: 3);
        FavoriteStateChanged? observed = null;
        harness.Store.FavoriteChanged += (_, value) => observed = value;

        await harness.Service.SetFavoriteAsync(track.Id, true);
        var reloaded = Assert.Single(await harness.Service.GetTracksAsync());

        Assert.True(reloaded.IsFavorite);
        Assert.Equal(track.Id, observed?.TrackId);
        Assert.True(observed?.IsFavorite);
        Assert.Equal(3, reloaded.PlayCount);
    }

    [Fact]
    public async Task CancelFavoriteKeepsTrackPlaylistHistoryAndPlayCount()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var track = await harness.AddTrackAsync(favorite: true, playCount: 7);
        await harness.AddPlaylistAndHistoryAsync(track.Id);

        await harness.Service.SetFavoriteAsync(track.Id, false);
        await using var db = harness.CreateDb();

        var reloaded = await db.Tracks.SingleAsync();
        Assert.False(reloaded.IsFavorite);
        Assert.Equal(7, reloaded.PlayCount);
        Assert.Equal(1, await db.PlaylistTracks.CountAsync());
        Assert.Equal(1, await db.PlaybackHistories.CountAsync());
    }

    [Fact]
    public async Task FavoriteQueryReturnsOnlyFavoriteTracks()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var liked = await harness.AddTrackAsync(favorite: true);
        await harness.AddTrackAsync(favorite: false);
        var result = await harness.Service.GetFavoritesAsync();
        Assert.Equal(liked.Id, Assert.Single(result).Id);
    }

    [Fact]
    public async Task SequentialFavoriteChangesKeepFinalState()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var track = await harness.AddTrackAsync();
        await harness.Service.SetFavoriteAsync(track.Id, true);
        await harness.Service.SetFavoriteAsync(track.Id, false);
        await harness.Service.SetFavoriteAsync(track.Id, true);
        Assert.True((await harness.Service.GetTracksAsync()).Single().IsFavorite);
    }

    [Fact]
    public async Task RemoveRecentDeletesOnlySelectedTracksHistoryGroup()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var first = await harness.AddTrackAsync(favorite: true);
        var second = await harness.AddTrackAsync(favorite: false);
        var firstHistory = await harness.AddHistoryAsync(first.Id, DateTime.UtcNow.AddMinutes(-1));
        await harness.AddHistoryAsync(first.Id, DateTime.UtcNow.AddMinutes(-2));
        await harness.AddHistoryAsync(second.Id, DateTime.UtcNow);

        var removal = await harness.Service.RemoveRecentAsync(firstHistory.Id);
        await using var db = harness.CreateDb();

        Assert.NotNull(removal);
        Assert.Equal(2, removal!.Histories.Count);
        Assert.Equal(2, await db.Tracks.CountAsync());
        Assert.True((await db.Tracks.SingleAsync(x => x.Id == first.Id)).IsFavorite);
        Assert.Single(await db.PlaybackHistories.ToListAsync());
    }

    [Fact]
    public async Task RestoreRecentRestoresOriginalIdsAndTimes()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var track = await harness.AddTrackAsync();
        var playedAt = DateTime.UtcNow.AddHours(-2);
        var history = await harness.AddHistoryAsync(track.Id, playedAt);
        var removal = await harness.Service.RemoveRecentAsync(history.Id);

        await harness.Service.RestoreRecentAsync(removal!);
        await using var db = harness.CreateDb();
        var restored = await db.PlaybackHistories.SingleAsync();
        Assert.Equal(history.Id, restored.Id);
        Assert.Equal(playedAt, restored.PlayedAt);
    }

    [Fact]
    public async Task ClearRecentKeepsTracksFavoritesAndPlaylists()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var track = await harness.AddTrackAsync(favorite: true);
        await harness.AddPlaylistAndHistoryAsync(track.Id);

        Assert.Equal(1, await harness.Service.ClearRecentAsync());
        await using var db = harness.CreateDb();
        Assert.Single(await db.Tracks.ToListAsync());
        Assert.True((await db.Tracks.SingleAsync()).IsFavorite);
        Assert.Single(await db.Playlists.ToListAsync());
        Assert.Single(await db.PlaylistTracks.ToListAsync());
        Assert.Empty(await db.PlaybackHistories.ToListAsync());
    }

    [Fact]
    public async Task RecentIsNewestFirstAndDeduplicatedByTrack()
    {
        await using var harness = await LibraryHarness.CreateAsync();
        var first = await harness.AddTrackAsync();
        var second = await harness.AddTrackAsync();
        var now = DateTime.UtcNow;
        await harness.AddHistoryAsync(first.Id, now.AddHours(-2));
        var newestFirst = await harness.AddHistoryAsync(first.Id, now);
        await harness.AddHistoryAsync(second.Id, now.AddHours(-1));

        var recent = await harness.Service.GetRecentAsync();
        Assert.Equal(2, recent.Count);
        Assert.Equal(newestFirst.Id, recent[0].HistoryId);
        Assert.Equal(first.Id, recent[0].Track.Id);
        Assert.Equal(second.Id, recent[1].Track.Id);
    }

    private sealed class LibraryHarness : IAsyncDisposable
    {
        private readonly TestDbContextFactory _factory;
        private int _trackNumber;

        private LibraryHarness(string root, TestDbContextFactory factory, MusicLibraryService service, TrackStateStore store)
        {
            Root = root;
            _factory = factory;
            Service = service;
            Store = store;
        }

        public string Root { get; }
        public MusicLibraryService Service { get; }
        public TrackStateStore Store { get; }
        public NekoPlayerDbContext CreateDb() => _factory.CreateDbContext();

        public static async Task<LibraryHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "NekoPlayerInteractionTests", Guid.NewGuid().ToString("N"));
            var paths = new TestPaths(root);
            paths.EnsureCreated();
            var options = new DbContextOptionsBuilder<NekoPlayerDbContext>().UseSqlite($"Data Source={paths.DatabasePath}").Options;
            var factory = new TestDbContextFactory(options);
            var store = new TrackStateStore();
            var service = new MusicLibraryService(factory, new TrackMetadataReader(new UnavailableFfmpeg(), paths), store);
            await service.InitializeAsync();
            return new LibraryHarness(root, factory, service, store);
        }

        public async Task<Track> AddTrackAsync(bool favorite = false, int playCount = 0)
        {
            var number = Interlocked.Increment(ref _trackNumber);
            var track = new Track
            {
                Title = $"Track {number:000}", Artist = "Tester", Album = "Temporary", FilePath = Path.Combine(Root, $"track-{number:000}.mp3"),
                IsFavorite = favorite, PlayCount = playCount, Duration = TimeSpan.FromMinutes(3), AddedAt = DateTime.UtcNow.AddSeconds(number)
            };
            await using var db = CreateDb();
            db.Tracks.Add(track);
            await db.SaveChangesAsync();
            return track;
        }

        public async Task<PlaybackHistory> AddHistoryAsync(Guid trackId, DateTime playedAt)
        {
            var history = new PlaybackHistory { TrackId = trackId, PlayedAt = playedAt, LastPosition = TimeSpan.FromSeconds(42) };
            await using var db = CreateDb();
            db.PlaybackHistories.Add(history);
            await db.SaveChangesAsync();
            return history;
        }

        public async Task AddPlaylistAndHistoryAsync(Guid trackId)
        {
            await using var db = CreateDb();
            var playlist = new Playlist { Name = "Keep me" };
            db.Playlists.Add(playlist);
            db.PlaylistTracks.Add(new PlaylistTrack { PlaylistId = playlist.Id, TrackId = trackId, SortOrder = 0 });
            db.PlaybackHistories.Add(new PlaybackHistory { TrackId = trackId, PlayedAt = DateTime.UtcNow, LastPosition = TimeSpan.FromSeconds(10) });
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
        public void EnsureCreated() { foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory }) Directory.CreateDirectory(path); }
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
        public Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FfmpegValidationResult(false, false, Version, BinaryDirectory, StatusMessage));
    }
}
