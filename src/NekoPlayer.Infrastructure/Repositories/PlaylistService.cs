using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Data;

namespace NekoPlayer.Infrastructure.Repositories;

public sealed class PlaylistService(IDbContextFactory<NekoPlayerDbContext> contextFactory) : IPlaylistService
{
    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Playlists.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<Playlist> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var playlist = new Playlist { Name = NormalizeName(name) };
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Playlists.Add(playlist); await db.SaveChangesAsync(cancellationToken); return playlist;
    }

    public async Task RenameAsync(Guid playlistId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var playlist = await db.Playlists.FindAsync([playlistId], cancellationToken);
        if (playlist is null) return;
        playlist.Name = NormalizeName(name); playlist.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var playlist = await db.Playlists.FindAsync([playlistId], cancellationToken);
        if (playlist is null) return;
        db.Playlists.Remove(playlist); await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddTrackAsync(Guid playlistId, Guid trackId, CancellationToken cancellationToken = default)
    {
        await AddTracksAsync(playlistId, [trackId], cancellationToken);
    }

    public async Task<PlaylistAddResult> AddTracksAsync(Guid playlistId, IEnumerable<Guid> trackIds, CancellationToken cancellationToken = default)
    {
        var requested = trackIds.Distinct().ToArray();
        if (requested.Length == 0) return new PlaylistAddResult(0, 0);
        return await WriteAsync(async db =>
        {
            if (!await db.Playlists.AnyAsync(x => x.Id == playlistId, cancellationToken))
                throw new KeyNotFoundException("未找到目标歌单。");

            var validTrackIds = (await db.Tracks.Where(x => requested.Contains(x.Id)).Select(x => x.Id)
                .ToListAsync(cancellationToken)).ToHashSet();
            var items = await OrderedItems(db, playlistId).ToListAsync(cancellationToken);
            var existingTrackIds = items.Select(x => x.TrackId).ToHashSet();
            // The database validates membership; the caller's sequence defines the appended order.
            var additions = requested.Where(id => validTrackIds.Contains(id) && !existingTrackIds.Contains(id)).ToArray();
            var newItems = additions.Select(trackId => new PlaylistTrack
            {
                PlaylistId = playlistId,
                TrackId = trackId
            }).ToArray();
            db.PlaylistTracks.AddRange(newItems);
            items.AddRange(newItems);
            Renumber(items);
            return new PlaylistAddResult(additions.Length, requested.Length - additions.Length);
        }, cancellationToken);
    }

    public async Task RemoveTrackAsync(Guid playlistId, Guid trackId, CancellationToken cancellationToken = default)
    {
        await WriteAsync(async db =>
        {
            var items = await OrderedItems(db, playlistId).ToListAsync(cancellationToken);
            var item = items.FirstOrDefault(x => x.TrackId == trackId);
            if (item is null) return false;
            db.PlaylistTracks.Remove(item);
            items.Remove(item);
            Renumber(items);
            return true;
        }, cancellationToken);
    }

    public async Task MoveTrackAsync(Guid playlistId, Guid trackId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await WriteAsync(async db =>
        {
            var items = await OrderedItems(db, playlistId).ToListAsync(cancellationToken);
            var currentIndex = items.FindIndex(x => x.TrackId == trackId);
            if (currentIndex < 0) return false;

            var item = items[currentIndex];
            var destination = Math.Clamp(targetIndex, 0, items.Count - 1);
            items.RemoveAt(currentIndex);
            items.Insert(destination, item);
            Renumber(items);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await OrderedItems(db, playlistId).AsNoTracking()
            .Select(x => x.Track!).ToListAsync(cancellationToken);
    }

    private static IOrderedQueryable<PlaylistTrack> OrderedItems(NekoPlayerDbContext db, Guid playlistId) =>
        db.PlaylistTracks.Where(x => x.PlaylistId == playlistId).OrderBy(x => x.SortOrder).ThenBy(x => x.TrackId);

    private static void Renumber(IReadOnlyList<PlaylistTrack> items)
    {
        for (var index = 0; index < items.Count; index++) items[index].SortOrder = index;
    }

    private async Task<TResult> WriteAsync<TResult>(Func<NekoPlayerDbContext, Task<TResult>> action, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        // Reserve the SQLite writer before reading order/membership, including across service instances.
        // A deferred read followed by a write could otherwise use another writer's stale snapshot.
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var enlistedTransaction = await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var result = await action(db);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlySet<Guid>> GetTrackIdsAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await db.PlaylistTracks.AsNoTracking().Where(x => x.PlaylistId == playlistId)
            .Select(x => x.TrackId).ToListAsync(cancellationToken)).ToHashSet();
    }

    private static string NormalizeName(string name) => string.IsNullOrWhiteSpace(name) ? "新建歌单" : name.Trim()[..Math.Min(name.Trim().Length, 80)];
}
