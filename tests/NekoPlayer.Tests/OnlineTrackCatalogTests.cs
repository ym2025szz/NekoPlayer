using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Repositories;

namespace NekoPlayer.Tests;

public sealed class OnlineTrackCatalogTests
{
    [Fact]
    public async Task StableProviderIdentityDeduplicatesSearchObjectsAndRetainsDifferentProviders()
    {
        await using var fixture = await CreateAsync();
        var catalog = new OnlineTrackCatalog(fixture.Factory);
        var first = await catalog.EnsureOnlineTrackAsync(Online(" QQ ", "same", "first"));
        var second = await catalog.EnsureOnlineTrackAsync(Online("qq", "same", "updated"));
        var third = await catalog.EnsureOnlineTrackAsync(Online("netease", "same", "other"));
        Assert.Equal(OnlineTrackIdentity.Create("qq", "same"), first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, third.Id);
        Assert.Equal("updated", second.Title);
        Assert.Equal("qq", first.ProviderId);
        Assert.Equal(string.Empty, first.FilePath);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await db.Tracks.CountAsync());
    }

    [Fact]
    public async Task ExistingCanonicalGuidUserStateAndRelationsSurviveUpsert()
    {
        await using var fixture = await CreateAsync();
        var original = Online("QQ", "legacy", "old");
        original.IsFavorite = true;
        original.PlayCount = 19;
        original.AddedAt = new DateTime(2025, 1, 1);
        original.LastPlayedAt = new DateTime(2026, 1, 1);
        var playlist = new Playlist { Name = "混合歌单" };
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.Tracks.Add(original);
            db.Playlists.Add(playlist);
            db.PlaylistTracks.Add(new PlaylistTrack { PlaylistId = playlist.Id, TrackId = original.Id });
            db.PlaybackHistories.Add(new PlaybackHistory { TrackId = original.Id });
            await db.SaveChangesAsync();
        }
        var catalog = new OnlineTrackCatalog(fixture.Factory);
        var refreshed = await catalog.EnsureOnlineTrackAsync(Online("qq", "legacy", "new"));
        Assert.Equal(original.Id, refreshed.Id);
        Assert.True(refreshed.IsFavorite);
        Assert.Equal(19, refreshed.PlayCount);
        Assert.Equal(original.AddedAt, refreshed.AddedAt);
        Assert.Equal(original.LastPlayedAt, refreshed.LastPlayedAt);
        Assert.Equal("new", refreshed.Title);
        await using var verify = fixture.Factory.CreateDbContext();
        Assert.Equal(original.Id, (await verify.PlaylistTracks.SingleAsync()).TrackId);
        Assert.Equal(original.Id, (await verify.PlaybackHistories.SingleAsync()).TrackId);
        Assert.Single(await verify.Tracks.ToListAsync());
    }

    [Fact]
    public async Task BatchIsIdempotentAndPlaybackSecretsAndAvailabilityNeverPersist()
    {
        await using var fixture = await CreateAsync();
        var catalog = new OnlineTrackCatalog(fixture.Factory);
        var incoming = Online("qq", "1", "试听");
        incoming.FilePath = "https://audio.invalid/signed.mp3?token=secret";
        incoming.ProviderMetadataJson = """{"songMid":"m1","url":"https://audio.invalid/secret","headers":{"Cookie":"secret"},"nested":{"access_token":"secret","albumId":"a1"}}""";
        incoming.Availability = MusicAvailability.Preview;
        incoming.RestrictionReason = "会员试听";
        var other = Online("qq", "2", "另一首");
        var saved = await catalog.EnsureOnlineTracksAsync([incoming, incoming, other]);
        Assert.Equal(3, saved.Count);
        Assert.Equal(saved[0].Id, saved[1].Id);
        Assert.Equal(MusicAvailability.Preview, saved[0].Availability);
        await catalog.EnsureOnlineTracksAsync([incoming, other]);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await db.Tracks.CountAsync());
        var reloaded = await db.Tracks.SingleAsync(x => x.ProviderTrackId == "1");
        Assert.Equal(string.Empty, reloaded.FilePath);
        Assert.Equal(MusicAvailability.Unknown, reloaded.Availability);
        Assert.Null(reloaded.RestrictionReason);
        Assert.DoesNotContain("secret", reloaded.ProviderMetadataJson);
        Assert.Contains("songMid", reloaded.ProviderMetadataJson);
        Assert.Contains("albumId", reloaded.ProviderMetadataJson);
        Assert.Equal("https://audio.invalid/signed.mp3?token=secret", incoming.FilePath);
    }

    [Fact]
    public async Task ConcurrentCatalogInstancesReturnOneCanonicalEntity()
    {
        await using var fixture = await CreateAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            new OnlineTrackCatalog(fixture.Factory).EnsureOnlineTrackAsync(Online("qq", "concurrent", "title " + index)))));
        Assert.Single(results.Select(x => x.Id).Distinct());
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Tracks.ToListAsync());
    }

    [Fact]
    public async Task InvalidBatchIsRejectedBeforeWritingAnyRows()
    {
        await using var fixture = await CreateAsync();
        var catalog = new OnlineTrackCatalog(fixture.Factory);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.EnsureOnlineTracksAsync(
            [Online("qq", "valid", "valid"), new Track { SourceKind = TrackSourceKind.Online }]));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.EnsureOnlineTrackAsync(new Track { FilePath = "song.mp3" }));
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.Tracks.ToListAsync());
    }

    [Fact]
    public async Task GetByIdsKeepsRequestedOrderIncludesLocalAndChunksLargeSelections()
    {
        await using var fixture = await CreateAsync();
        var local = new Track { FilePath = "C:\\Music\\local.mp3", Title = "local" };
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.Tracks.Add(local);
            await db.SaveChangesAsync();
        }
        var catalog = new OnlineTrackCatalog(fixture.Factory);
        var online = await catalog.EnsureOnlineTracksAsync(Enumerable.Range(0, 405).Select(x => Online("qq", x.ToString(), x.ToString())));
        var selected = new[] { online[^1].Id, local.Id }.Concat(online.Select(x => x.Id)).Append(Guid.NewGuid()).ToArray();
        var result = await catalog.GetTracksByIdsAsync(selected);
        Assert.Equal(406, result.Count);
        Assert.Equal(online[^1].Id, result[0].Id);
        Assert.Equal(local.Id, result[1].Id);
        Assert.Equal(TrackSourceKind.Local, result[1].SourceKind);
    }

    [Fact]
    public async Task LibraryBoundaryIsLocalButFavoritesRecentAndPlaylistsAreMixed()
    {
        await using var fixture = await CreateAsync();
        var library = new MusicLibraryService(fixture.Factory, null!, new TrackStateStore());
        var catalog = new OnlineTrackCatalog(fixture.Factory);
        var online = await catalog.EnsureOnlineTrackAsync(Online("qq", "1", "online"));
        var local = new Track { Title = "local", FilePath = "C:\\Music\\local.mp3" };
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.Tracks.Add(local);
            await db.SaveChangesAsync();
        }
        await library.SetFavoriteAsync(local.Id, true);
        await library.SetFavoriteAsync(online.Id, true);
        await library.RecordPlaybackAsync(local.Id, TimeSpan.FromSeconds(15));
        await library.RecordPlaybackAsync(online.Id, TimeSpan.FromSeconds(20));
        var playlists = new PlaylistService(fixture.Factory);
        var playlist = await playlists.CreateAsync("混合");
        await playlists.AddTracksAsync(playlist.Id, [local.Id, online.Id]);
        Assert.Equal(local.Id, Assert.Single(await library.GetTracksAsync()).Id);
        Assert.Equal(2, (await library.GetFavoritesAsync()).Count);
        Assert.Equal(2, (await library.GetRecentAsync()).Count);
        Assert.Equal(2, (await playlists.GetTracksAsync(playlist.Id)).Count);

        await library.SetFavoriteAsync(online.Id, false);
        var onlineRecent = (await library.GetRecentAsync()).Single(x => x.Track.Id == online.Id);
        await library.RemoveRecentAsync(onlineRecent.HistoryId);
        await playlists.RemoveTrackAsync(playlist.Id, online.Id);
        Assert.Single(await catalog.GetTracksByIdsAsync([online.Id]));
        // Online identities cannot be deleted through the local-library removal command.
        var removal = await library.RemoveFromLibraryAsync([online.Id, local.Id]);
        Assert.Equal(1, removal.RemovedCount);
        Assert.Empty(await library.GetTracksAsync());
        Assert.Single(await catalog.GetTracksByIdsAsync([online.Id]));
    }

    private static Track Online(string provider, string id, string title) => new()
    {
        SourceKind = TrackSourceKind.Online,
        ProviderId = provider,
        ProviderTrackId = id,
        Title = title,
        Duration = TimeSpan.FromSeconds(180)
    };

    private static async Task<DataUpgradeFixture> CreateAsync()
    {
        var fixture = await DataUpgradeFixture.CreateAsync();
        await fixture.InitializeAsync();
        return fixture;
    }
}
