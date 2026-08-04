using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Interfaces;

public interface IUserDataPaths
{
    string Root { get; }
    string DataDirectory { get; }
    string DatabasePath { get; }
    string LogsDirectory { get; }
    string CoversDirectory { get; }
    string LyricsDirectory { get; }
    string ConfigDirectory { get; }
    string TempDirectory { get; }
    string SettingsPath { get; }
    void EnsureCreated();
}

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ILyricsService
{
    IReadOnlyList<LyricsLine> Parse(string content);
    Task<IReadOnlyList<LyricsLine>> LoadForTrackAsync(Track track, CancellationToken cancellationToken = default);
}

public interface IMusicLibraryService
{
    Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetFavoritesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecentTrack>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default);
    Task<ImportResult> ImportAsync(IEnumerable<string> files, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<ImportResult> ScanFolderAsync(string folder, bool recursive, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(Guid trackId, bool favorite, CancellationToken cancellationToken = default);
    Task<RecentRemoval?> RemoveRecentAsync(Guid historyId, CancellationToken cancellationToken = default);
    Task RestoreRecentAsync(RecentRemoval removal, CancellationToken cancellationToken = default);
    Task<int> ClearRecentAsync(CancellationToken cancellationToken = default);
    Task RemoveFromLibraryAsync(Guid trackId, CancellationToken cancellationToken = default);
    Task<LibraryRemovalResult> RemoveFromLibraryAsync(IEnumerable<Guid> trackIds, CancellationToken cancellationToken = default);
    Task RecordPlaybackAsync(Guid trackId, TimeSpan position, CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ITrackStateStore
{
    event EventHandler<FavoriteStateChanged>? FavoriteChanged;
    void PublishFavoriteChanged(Guid trackId, bool favorite);
}

public interface IPlaylistService
{
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);
    Task<Playlist> CreateAsync(string name, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid playlistId, string name, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid playlistId, CancellationToken cancellationToken = default);
    Task AddTrackAsync(Guid playlistId, Guid trackId, CancellationToken cancellationToken = default);
    Task<PlaylistAddResult> AddTracksAsync(Guid playlistId, IEnumerable<Guid> trackIds, CancellationToken cancellationToken = default);
    Task RemoveTrackAsync(Guid playlistId, Guid trackId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(Guid playlistId, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<Guid>> GetTrackIdsAsync(Guid playlistId, CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset LocalNow { get; }
}

public interface IFfmpegLocator
{
    string BinaryDirectory { get; }
    string FfmpegPath { get; }
    string FfprobePath { get; }
    bool IsAvailable { get; }
    bool HasSharedLibraries { get; }
    string Version { get; }
    string StatusMessage { get; }
    void Configure();
    Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default);
}

public sealed record FfmpegValidationResult(
    bool IsAvailable,
    bool HasSharedLibraries,
    string Version,
    string BinaryDirectory,
    string StatusMessage);

public interface ISpectrumService
{
    IReadOnlyList<float> Bands { get; }
    bool IsEnabled { get; set; }
    int FramesPerSecond { get; set; }
    void PushPcm(ReadOnlySpan<byte> float32StereoPcm);
    event EventHandler<IReadOnlyList<float>>? SpectrumUpdated;
}
