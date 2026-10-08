using System.Text.Json;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Interfaces;

public interface IGatewayRuntime : IAsyncDisposable
{
    GatewayState State { get; }
    string? LastError { get; }
    Task<Uri> EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> SendAsync(string route, object? payload = null, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IMusicProvider
{
    MusicProviderInfo Info { get; }
    Task<MusicSearchPage> SearchAsync(string query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);
}

public interface IOnlineMusicService
{
    IReadOnlyList<MusicProviderInfo> Providers { get; }
    Task<MusicSearchPage> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default);
    Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);
}

public interface ITrackCatalog
{
    Task<IReadOnlyList<Track>> GetTracksByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);
    Task<Track> EnsureOnlineTrackAsync(Track track, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> EnsureOnlineTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default);
}

public interface ISessionAudioPlayerService : IAudioPlayerService
{
    long SessionId { get; }
    bool CanSeek { get; }
    Task LoadAsync(AudioSource source, TimeSpan? startPosition = null, CancellationToken cancellationToken = default);
    event EventHandler<AudioSessionState>? SessionStateChanged;
    event EventHandler<AudioSessionPosition>? SessionPositionChanged;
    event EventHandler<AudioSessionFailure>? SessionPlaybackFailed;
    event EventHandler<AudioSessionCompletion>? SessionPlaybackCompleted;
}
