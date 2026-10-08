using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Repositories;

namespace NekoPlayer.Tests;

public sealed class PlaylistOrderPersistenceTests
{
    [Fact]
    public async Task AdditionFollowsRequestedOrderInsteadOfDatabaseIdOrder()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        var requested = new[] { fixture.Tracks[2].Id, fixture.Tracks[0].Id, fixture.Tracks[3].Id, fixture.Tracks[1].Id };

        var result = await fixture.Service.AddTracksAsync(fixture.Playlist.Id, requested);

        Assert.Equal((4, 0), (result.AddedCount, result.SkippedCount));
        await fixture.AssertOrderAsync(requested);
    }

    [Fact]
    public async Task AdditionKeepsExistingOrderAndSkipsExistingDuplicateAndUnknownIds()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        var tracks = fixture.Tracks;
        await fixture.Service.AddTrackAsync(fixture.Playlist.Id, tracks[1].Id);

        var result = await fixture.Service.AddTracksAsync(fixture.Playlist.Id,
            [tracks[3].Id, tracks[1].Id, tracks[2].Id, tracks[3].Id, Guid.NewGuid(), tracks[0].Id]);

        Assert.Equal((3, 2, 5), (result.AddedCount, result.SkippedCount, result.RequestedCount));
        await fixture.AssertOrderAsync([tracks[1].Id, tracks[3].Id, tracks[2].Id, tracks[0].Id]);
    }

    [Theory]
    [InlineData(0, int.MaxValue, new[] { 1, 2, 3, 0 })]
    [InlineData(3, int.MinValue, new[] { 3, 0, 1, 2 })]
    [InlineData(1, 2, new[] { 0, 2, 1, 3 })]
    [InlineData(3, 1, new[] { 0, 3, 1, 2 })]
    [InlineData(1, 1, new[] { 0, 1, 2, 3 })]
    public async Task MoveUsesFinalIndexAndPreservesOtherTracks(int sourceIndex, int targetIndex, int[] expected)
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        await fixture.AddAllAsync();

        await fixture.Service.MoveTrackAsync(fixture.Playlist.Id, fixture.Tracks[sourceIndex].Id, targetIndex);

        await fixture.AssertOrderAsync(expected.Select(index => fixture.Tracks[index].Id).ToArray());
    }

    [Fact]
    public async Task MissingTrackOrPlaylistMovesAreSafeNoOps()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        await fixture.AddAllAsync();

        await fixture.Service.MoveTrackAsync(fixture.Playlist.Id, Guid.NewGuid(), 0);
        await fixture.Service.MoveTrackAsync(Guid.NewGuid(), fixture.Tracks[0].Id, 0);
        await fixture.Service.RemoveTrackAsync(fixture.Playlist.Id, Guid.NewGuid());

        await fixture.AssertOrderAsync(fixture.Tracks.Select(track => track.Id).ToArray());
    }

    [Fact]
    public async Task LegacyTiedOrdersUseTrackIdsAsStableTieBreakAndMoveNormalizesThem()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.PlaylistTracks.AddRange(new[] { 2, 0, 1 }.Select(index => new PlaylistTrack
            {
                PlaylistId = fixture.Playlist.Id, TrackId = fixture.Tracks[index].Id, SortOrder = 7
            }));
            await db.SaveChangesAsync();
        }
        Assert.Equal(fixture.Tracks.Take(3).Select(track => track.Id),
            (await fixture.Service.GetTracksAsync(fixture.Playlist.Id)).Select(track => track.Id));

        await fixture.Service.MoveTrackAsync(fixture.Playlist.Id, fixture.Tracks[2].Id, 0);

        await fixture.AssertOrderAsync([fixture.Tracks[2].Id, fixture.Tracks[0].Id, fixture.Tracks[1].Id]);
    }

    [Fact]
    public async Task AdditionNormalizesLegacySparseAndTiedOrders()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.PlaylistTracks.AddRange(
                new PlaylistTrack { PlaylistId = fixture.Playlist.Id, TrackId = fixture.Tracks[3].Id, SortOrder = 8 },
                new PlaylistTrack { PlaylistId = fixture.Playlist.Id, TrackId = fixture.Tracks[0].Id, SortOrder = -1 },
                new PlaylistTrack { PlaylistId = fixture.Playlist.Id, TrackId = fixture.Tracks[1].Id, SortOrder = 8 });
            await db.SaveChangesAsync();
        }

        await fixture.Service.AddTrackAsync(fixture.Playlist.Id, fixture.Tracks[2].Id);

        await fixture.AssertOrderAsync([fixture.Tracks[0].Id, fixture.Tracks[1].Id, fixture.Tracks[3].Id, fixture.Tracks[2].Id]);
    }

    [Fact]
    public async Task RemovalCompactsOrderAndReadditionAppendsAfterRemainingTracks()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        var tracks = fixture.Tracks;
        await fixture.Service.AddTracksAsync(fixture.Playlist.Id, [tracks[2].Id, tracks[0].Id, tracks[1].Id, tracks[3].Id]);

        await fixture.Service.RemoveTrackAsync(fixture.Playlist.Id, tracks[0].Id);
        await fixture.AssertOrderAsync([tracks[2].Id, tracks[1].Id, tracks[3].Id]);
        await fixture.Service.AddTrackAsync(fixture.Playlist.Id, tracks[0].Id);

        await fixture.AssertOrderAsync([tracks[2].Id, tracks[1].Id, tracks[3].Id, tracks[0].Id]);
    }

    [Fact]
    public async Task MoveRemovalAndPlaylistDeletionKeepMusicFilesAndLibraryState()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        var track = fixture.Tracks[1];
        var otherPlaylist = await fixture.Service.CreateAsync("Other playlist");
        await fixture.AddAllAsync();
        await fixture.Service.AddTrackAsync(otherPlaylist.Id, track.Id);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.PlaybackHistories.Add(new PlaybackHistory { TrackId = track.Id });
            await db.SaveChangesAsync();
        }

        await fixture.Service.MoveTrackAsync(fixture.Playlist.Id, track.Id, 0);
        await fixture.Service.RemoveTrackAsync(fixture.Playlist.Id, track.Id);
        await fixture.Service.DeleteAsync(fixture.Playlist.Id);

        foreach (var item in fixture.Tracks) Assert.Equal("music fixture", await File.ReadAllTextAsync(item.FilePath));
        await using var verify = fixture.Factory.CreateDbContext();
        Assert.Equal(4, await verify.Tracks.CountAsync());
        var storedTrack = await verify.Tracks.SingleAsync(item => item.Id == track.Id);
        Assert.True(storedTrack.IsFavorite);
        Assert.Equal(7, storedTrack.PlayCount);
        Assert.Equal(track.Id, (await verify.PlaybackHistories.SingleAsync()).TrackId);
        Assert.Equal(track.Id, (await verify.PlaylistTracks.SingleAsync()).TrackId);
        Assert.Equal(otherPlaylist.Id, (await verify.PlaylistTracks.SingleAsync()).PlaylistId);
    }

    [Fact]
    public async Task FailedMoveRollsBackAllReorderedRows()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        await fixture.AddAllAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectPlaylistReorder BEFORE UPDATE OF SortOrder ON PlaylistTracks
                WHEN NEW.SortOrder = 1
                BEGIN SELECT RAISE(ABORT, 'reorder rejected'); END;
                """);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Service.MoveTrackAsync(fixture.Playlist.Id, fixture.Tracks[0].Id, 3));

        await fixture.AssertOrderAsync(fixture.Tracks.Select(track => track.Id).ToArray());
    }

    [Fact]
    public async Task ConcurrentOverlappingAdditionsKeepEachBatchOrderAndCountDuplicatesOnce()
    {
        await using var fixture = await PlaylistFixture.CreateAsync(5);
        var tracks = fixture.Tracks;
        var secondService = new PlaylistService(fixture.Factory);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await start.Task;
            return await fixture.Service.AddTracksAsync(fixture.Playlist.Id, [tracks[4].Id, tracks[3].Id, tracks[2].Id]);
        });
        var second = Task.Run(async () =>
        {
            await start.Task;
            return await secondService.AddTracksAsync(fixture.Playlist.Id, [tracks[2].Id, tracks[1].Id, tracks[0].Id]);
        });
        start.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(5, results.Sum(result => result.AddedCount));
        Assert.Equal(1, results.Sum(result => result.SkippedCount));
        var actual = (await fixture.Service.GetTracksAsync(fixture.Playlist.Id)).Select(track => track.Id).ToArray();
        var firstWins = new[] { tracks[4].Id, tracks[3].Id, tracks[2].Id, tracks[1].Id, tracks[0].Id };
        var secondWins = new[] { tracks[2].Id, tracks[1].Id, tracks[0].Id, tracks[4].Id, tracks[3].Id };
        Assert.True(actual.SequenceEqual(firstWins) || actual.SequenceEqual(secondWins));
        await fixture.AssertOrderAsync(actual);
    }

    [Fact]
    public async Task ConcurrentMoveRemoveAndAddAcrossContextsKeepMembershipAndContiguousOrder()
    {
        await using var fixture = await PlaylistFixture.CreateAsync(10);
        var tracks = fixture.Tracks;
        await fixture.Service.AddTracksAsync(fixture.Playlist.Id, tracks.Take(8).Select(track => track.Id));
        var secondService = new PlaylistService(fixture.Factory);
        var thirdService = new PlaylistService(fixture.Factory);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var move = Task.Run(async () =>
        {
            await start.Task;
            await fixture.Service.MoveTrackAsync(fixture.Playlist.Id, tracks[0].Id, int.MaxValue);
        });
        var remove = Task.Run(async () =>
        {
            await start.Task;
            await secondService.RemoveTrackAsync(fixture.Playlist.Id, tracks[1].Id);
        });
        var add = Task.Run(async () =>
        {
            await start.Task;
            return await thirdService.AddTracksAsync(fixture.Playlist.Id, [tracks[9].Id, tracks[8].Id]);
        });
        start.SetResult();

        await Task.WhenAll(move, remove, add);

        Assert.Equal(2, (await add).AddedCount);
        var actual = (await fixture.Service.GetTracksAsync(fixture.Playlist.Id)).Select(track => track.Id).ToArray();
        Assert.Equal(9, actual.Length);
        Assert.Equal(tracks.Where((_, index) => index != 1).Select(track => track.Id).Order(), actual.Order());
        Assert.Equal(tracks.Skip(2).Take(6).Select(track => track.Id), actual.Where(id => tracks.Skip(2).Take(6).Any(track => track.Id == id)));
        Assert.True(Array.IndexOf(actual, tracks[9].Id) < Array.IndexOf(actual, tracks[8].Id));
        await fixture.AssertOrderAsync(actual);
    }

    [Fact]
    public async Task CancelledMoveDoesNotChangePersistedOrder()
    {
        await using var fixture = await PlaylistFixture.CreateAsync();
        await fixture.AddAllAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.MoveTrackAsync(fixture.Playlist.Id, fixture.Tracks[0].Id, 3, cancellation.Token));

        await fixture.AssertOrderAsync(fixture.Tracks.Select(track => track.Id).ToArray());
    }

    private sealed class PlaylistFixture : IAsyncDisposable
    {
        private PlaylistFixture(string root, TestContextFactory factory, Playlist playlist, Track[] tracks)
        {
            Root = root;
            Factory = factory;
            Playlist = playlist;
            Tracks = tracks;
            Service = new PlaylistService(factory);
        }

        public string Root { get; }
        public TestContextFactory Factory { get; }
        public Playlist Playlist { get; }
        public Track[] Tracks { get; }
        public PlaylistService Service { get; }

        public static async Task<PlaylistFixture> CreateAsync(int trackCount = 4)
        {
            var root = Path.Combine(Path.GetTempPath(), "NekoPlayerPlaylistOrderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DbContextOptionsBuilder<NekoPlayerDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "playlist.db"), Pooling = false }.ToString())
                .Options;
            var factory = new TestContextFactory(options);
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            var playlist = new Playlist { Name = "Ordered playlist" };
            var tracks = Enumerable.Range(1, trackCount).Select(index => new Track
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"),
                Title = $"Track {index}", FilePath = Path.Combine(root, $"track-{index}.mp3"),
                IsFavorite = true, PlayCount = 7
            }).ToArray();
            foreach (var track in tracks) await File.WriteAllTextAsync(track.FilePath, "music fixture");
            db.Playlists.Add(playlist);
            db.Tracks.AddRange(tracks);
            await db.SaveChangesAsync();
            return new PlaylistFixture(root, factory, playlist, tracks);
        }

        public Task<PlaylistAddResult> AddAllAsync() => Service.AddTracksAsync(Playlist.Id, Tracks.Select(track => track.Id));

        public async Task AssertOrderAsync(Guid[] expected)
        {
            // A fresh service/context confirms that order is persisted rather than held in memory.
            var reopened = new PlaylistService(Factory);
            Assert.Equal(expected, (await reopened.GetTracksAsync(Playlist.Id)).Select(track => track.Id));
            await using var db = Factory.CreateDbContext();
            var rows = await db.PlaylistTracks.Where(item => item.PlaylistId == Playlist.Id)
                .OrderBy(item => item.SortOrder).ThenBy(item => item.TrackId).ToListAsync();
            Assert.Equal(expected, rows.Select(item => item.TrackId));
            Assert.Equal(Enumerable.Range(0, expected.Length), rows.Select(item => item.SortOrder));
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestContextFactory(DbContextOptions<NekoPlayerDbContext> options) : IDbContextFactory<NekoPlayerDbContext>
    {
        public NekoPlayerDbContext CreateDbContext() => new(options);
    }
}
