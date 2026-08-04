namespace NekoPlayer.Core.Models;

public enum ResponsiveLayoutMode
{
    Compact,
    Standard,
    Wide
}

public enum PageLoadState
{
    Loading,
    Empty,
    Content,
    Error
}

public enum PlaybackPrimaryAction
{
    Disabled,
    Play,
    Pause,
    Busy,
    Retry
}

public enum SnackbarTone
{
    Success,
    Warning,
    Error
}

public sealed record FavoriteStateChanged(Guid TrackId, bool IsFavorite, DateTime ChangedAtUtc);

public sealed record PlaylistAddResult(int AddedCount, int SkippedCount)
{
    public int RequestedCount => AddedCount + SkippedCount;
}

public sealed record LibraryRemovalResult(int RequestedCount, int RemovedCount, IReadOnlyList<Guid> RemovedTrackIds);

public sealed record QueueDisplaySnapshot(int DisplayIndex, Track Track, bool IsCurrent);
