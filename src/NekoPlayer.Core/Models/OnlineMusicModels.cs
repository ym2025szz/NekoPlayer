using System.Security.Cryptography;
using System.Text;
using NekoPlayer.Core.Enums;

namespace NekoPlayer.Core.Models;

public enum TrackSourceKind { Local, Online }
public enum MusicAvailability { Unknown, Full, Preview, Unavailable }
public enum GatewayState { Stopped, Starting, Ready, Unavailable, Stopping }

public sealed record MusicProviderInfo(string Id, string Name, bool Experimental = false,
    bool CanSearch = true, bool CanPlay = true, bool CanGetLyrics = true,
    string Status = "unverified", string? Message = null);

public sealed record MusicSearchRequest(string Query, string ProviderId, int Page = 1, int PageSize = 30);
public sealed record MusicSearchPage(string ProviderId, int Page, IReadOnlyList<Track> Tracks,
    bool HasMore, string? Warning = null);

/// <summary>Only the current playback session owns URLs and headers; never persist this record.</summary>
public sealed record AudioSource(string Input, bool IsRemote = false, TimeSpan? Duration = null,
    IReadOnlyDictionary<string, string>? Headers = null, bool CanSeek = true,
    bool IsPreview = false, TimeSpan? PreviewStart = null, long SessionId = 0,
    string? ProviderId = null, string? ProviderTrackId = null, DateTimeOffset? ExpiresAt = null)
{
    public static AudioSource Local(string path, long sessionId = 0) => new(path, SessionId: sessionId);
}

public sealed record PlaybackResolution(MusicAvailability Availability, AudioSource? Source, string? Reason = null);
public sealed record AudioSessionState(long SessionId, PlaybackState State);
public sealed record AudioSessionPosition(long SessionId, TimeSpan Position);
public sealed record AudioSessionFailure(long SessionId, Exception Error);
public sealed record AudioSessionCompletion(long SessionId, bool IsPreview);

public sealed record PlaybackSnapshot(long SessionId, Track? Track, Track? PendingTrack,
    PlaybackState State, TimeSpan Position, TimeSpan Duration, bool CanSeek,
    bool IsPreview = false, string? Message = null);
public enum LyricsLoadingState { None, Loading, Ready, Empty, Error }
public sealed record PlaybackLyrics(long SessionId, Guid TrackId, IReadOnlyList<LyricsLine> Lines,
    LyricsLoadingState State = LyricsLoadingState.None, string? Message = null);
public sealed record PlaybackRequestResult(bool Started, Track? Track, long SessionId, string? Message = null);
public sealed record SearchHistoryEntry(string Query, string ProviderId, DateTime SavedAt);

public static class OnlineTrackIdentity
{
    public static Guid Create(string providerId, string providerTrackId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("NekoPlayer/online/" +
            providerId.Trim().ToLowerInvariant() + "/" + providerTrackId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
