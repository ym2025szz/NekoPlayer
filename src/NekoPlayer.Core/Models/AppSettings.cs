using NekoPlayer.Core.Enums;

namespace NekoPlayer.Core.Models;

public sealed class AppSettings
{
    public float Volume { get; set; } = 0.75f;
    public bool IsMuted { get; set; }
    public PlayMode PlayMode { get; set; } = PlayMode.RepeatAll;
    public Guid? LastTrackId { get; set; }
    public TimeSpan LastPosition { get; set; }
    public string Theme { get; set; } = "DarkTeal";
    public bool ReduceMotion { get; set; }
    public bool IncludeSubdirectories { get; set; } = true;
    public bool SpectrumEnabled { get; set; } = true;
    public int SpectrumFps { get; set; } = 30;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;
    public List<Guid> QueueTrackIds { get; set; } = [];
    public int QueueIndex { get; set; } = -1;
}

public enum ImportStage
{
    Idle,
    Enumerating,
    ReadingMetadata,
    AnalyzingMedia,
    Saving,
    RefreshingLibrary,
    Completed,
    Cancelled,
    Failed
}

public sealed record ImportProgress(
    ImportStage Stage,
    int DiscoveredCount,
    int ProcessedCount,
    int ImportedCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    string? CurrentFileName,
    string StatusText,
    double? Percentage,
    bool IsIndeterminate);

public sealed record ImportFailure(string FilePath, string FileName, string Message);

public sealed record ImportResult(
    ImportStage Stage,
    int DiscoveredCount,
    int ProcessedCount,
    int ImportedCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<ImportFailure> Failures,
    IReadOnlyList<Guid> AffectedTrackIds)
{
    public bool IsCancelled => Stage == ImportStage.Cancelled;
}

public sealed record RecentTrack(Guid HistoryId, Track Track, DateTime PlayedAt, TimeSpan LastPosition);

public sealed record PlaybackHistorySnapshot(Guid Id, Guid TrackId, DateTime PlayedAt, TimeSpan LastPosition);

public sealed record RecentRemoval(Guid TrackId, IReadOnlyList<PlaybackHistorySnapshot> Histories);
