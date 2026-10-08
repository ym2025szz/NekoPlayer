using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Data;

namespace NekoPlayer.Infrastructure.Repositories;

/// <summary>Persists stable online identities and display metadata, never an audio source.</summary>
public sealed class OnlineTrackCatalog(IDbContextFactory<NekoPlayerDbContext> contextFactory) : ITrackCatalog
{
    private const int QueryBatchSize = 400;

    public async Task<IReadOnlyList<Track>> GetTracksByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var requested = ids.Distinct().ToArray();
        if (requested.Length == 0) return [];
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<Guid, Track>();
        foreach (var batch in requested.Chunk(QueryBatchSize))
        {
            var tracks = await db.Tracks.AsNoTracking().Where(x => batch.Contains(x.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var track in tracks) found[track.Id] = track;
        }
        return requested.Where(found.ContainsKey).Select(id => found[id]).ToArray();
    }

    public async Task<Track> EnsureOnlineTrackAsync(Track track, CancellationToken cancellationToken = default) =>
        (await EnsureOnlineTracksAsync([track], cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<Track>> EnsureOnlineTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        // Validate the entire batch before touching the database; one malformed key cannot leave a
        // half-saved playlist selection. Never mutate search result objects while materializing.
        var requested = tracks.Select(Validate).ToArray();
        if (requested.Length == 0) return [];
        var unique = requested.GroupBy(x => x.Key).Select(x => x.Last()).ToArray();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = item.Track;
            var stableId = OnlineTrackIdentity.Create(item.Key.ProviderId, item.Key.ProviderTrackId);
            var metadata = PersistentMetadata(source.ProviderMetadataJson);
            var addedAt = DateTime.UtcNow;
            var empty = string.Empty;
            var neverWritten = DateTime.MinValue;
            // Atomic upsert serializes concurrent writers at SQLite. DO UPDATE intentionally omits
            // Id/AddedAt/favorite/play counts/last played, keeping every existing relationship valid.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Tracks" (
                    "Id", "SourceKind", "ProviderId", "ProviderTrackId", "ProviderMetadataJson", "CoverUrl", "VersionLabel",
                    "FilePath", "Title", "Artist", "Album", "Genre", "Duration", "TrackNumber", "Year", "SampleRate",
                    "Channels", "BitRate", "CodecName", "CoverCachePath", "IsFavorite", "PlayCount", "AddedAt",
                    "LastPlayedAt", "FileSize", "FileLastWriteTimeUtc")
                VALUES ({stableId}, 1, {item.Key.ProviderId}, {item.Key.ProviderTrackId}, {metadata}, {source.CoverUrl}, {source.VersionLabel ?? empty},
                    {empty}, {source.Title ?? empty}, {source.Artist ?? empty}, {source.Album ?? empty}, {source.Genre ?? empty}, {source.Duration.Ticks},
                    {source.TrackNumber}, {source.Year}, 0, 0, 0, {empty}, NULL, 0, 0, {addedAt}, NULL, 0, {neverWritten})
                ON CONFLICT ("ProviderId", "ProviderTrackId") WHERE "SourceKind" = 1 DO UPDATE SET
                    "FilePath" = excluded."FilePath",
                    "ProviderMetadataJson" = excluded."ProviderMetadataJson",
                    "CoverUrl" = excluded."CoverUrl", "VersionLabel" = excluded."VersionLabel",
                    "Title" = excluded."Title", "Artist" = excluded."Artist", "Album" = excluded."Album",
                    "Genre" = excluded."Genre", "Duration" = excluded."Duration",
                    "TrackNumber" = excluded."TrackNumber", "Year" = excluded."Year";
                """, cancellationToken).ConfigureAwait(false);
        }

        var canonical = new Dictionary<OnlineKey, Track>();
        foreach (var provider in unique.GroupBy(x => x.Key.ProviderId))
        {
            var providerId = provider.Key;
            foreach (var batch in provider.Select(x => x.Key.ProviderTrackId).Chunk(QueryBatchSize))
            {
                // Explicit collation also covers a partially upgraded database whose ProviderId
                // column predates the NOCASE declaration, while its unique index already uses it.
                var matches = await db.Tracks.AsNoTracking().Where(x => x.SourceKind == TrackSourceKind.Online &&
                        EF.Functions.Collate(x.ProviderId!, "NOCASE") == providerId && batch.Contains(x.ProviderTrackId!))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var match in matches)
                    canonical[new OnlineKey(match.ProviderId!.Trim().ToLowerInvariant(), match.ProviderTrackId!)] = match;
            }
        }

        var result = requested.Select(item =>
        {
            if (!canonical.TryGetValue(item.Key, out var saved))
                throw new InvalidOperationException("在线歌曲保存后未能重新确认平台标识。");
            saved.Availability = item.Track.Availability;
            saved.RestrictionReason = item.Track.RestrictionReason;
            return saved;
        }).ToArray();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static ValidatedTrack Validate(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.SourceKind != TrackSourceKind.Online)
            throw new ArgumentException("在线曲目目录仅接收在线歌曲。", nameof(track));
        if (string.IsNullOrWhiteSpace(track.ProviderId) || string.IsNullOrWhiteSpace(track.ProviderTrackId))
            throw new ArgumentException("在线歌曲必须包含平台及平台歌曲标识。", nameof(track));
        return new ValidatedTrack(new OnlineKey(track.ProviderId.Trim().ToLowerInvariant(), track.ProviderTrackId), track);
    }

    private static string PersistentMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject metadata) return "{}";
            RemovePlaybackSecrets(metadata);
            return metadata.ToJsonString();
        }
        catch (JsonException) { return "{}"; }
    }

    private static void RemovePlaybackSecrets(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                var name = key.Replace("_", "").Replace("-", "").ToLowerInvariant();
                if (name is "url" or "urls" or "playurl" or "playbackurl" or "audiourl" or "streamurl" or "downloadurl" or
                    "headers" or "cookie" or "cookies" or "authorization" or "token" or "accesstoken" or "refreshtoken" or "expiresat")
                    obj.Remove(key);
                else RemovePlaybackSecrets(obj[key]);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) RemovePlaybackSecrets(child);
    }

    private readonly record struct OnlineKey(string ProviderId, string ProviderTrackId);
    private sealed record ValidatedTrack(OnlineKey Key, Track Track);
}
