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
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Playlists.AnyAsync(x => x.Id == playlistId, cancellationToken))
            throw new KeyNotFoundException("未找到目标歌单。");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var validTrackIds = await db.Tracks.Where(x => requested.Contains(x.Id)).Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var existingTrackIds = await db.PlaylistTracks
            .Where(x => x.PlaylistId == playlistId && validTrackIds.Contains(x.TrackId))
            .Select(x => x.TrackId)
            .ToListAsync(cancellationToken);
        var additions = validTrackIds.Except(existingTrackIds).ToArray();
        var order = await db.PlaylistTracks.Where(x => x.PlaylistId == playlistId).Select(x => (int?)x.SortOrder).MaxAsync(cancellationToken) ?? -1;
        db.PlaylistTracks.AddRange(additions.Select((trackId, index) => new PlaylistTrack
        {
            PlaylistId = playlistId,
            TrackId = trackId,
            SortOrder = order + index + 1
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlaylistAddResult(additions.Length, requested.Length - additions.Length);
    }

    public async Task RemoveTrackAsync(Guid playlistId, Guid trackId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.PlaylistTracks.FindAsync([playlistId, trackId], cancellationToken);
        if (item is null) return;
        db.PlaylistTracks.Remove(item); await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PlaylistTracks.AsNoTracking().Where(x => x.PlaylistId == playlistId).OrderBy(x => x.SortOrder)
            .Select(x => x.Track!).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> GetTrackIdsAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await db.PlaylistTracks.AsNoTracking().Where(x => x.PlaylistId == playlistId)
            .Select(x => x.TrackId).ToListAsync(cancellationToken)).ToHashSet();
    }

    private static string NormalizeName(string name) => string.IsNullOrWhiteSpace(name) ? "新建歌单" : name.Trim()[..Math.Min(name.Trim().Length, 80)];
}
