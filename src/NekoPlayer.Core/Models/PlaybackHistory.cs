namespace NekoPlayer.Core.Models;

public sealed class PlaybackHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TrackId { get; set; }
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
    public TimeSpan LastPosition { get; set; }
    public Track? Track { get; set; }
}

public sealed class LibraryFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FolderPath { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; }
    public DateTime? LastScanAt { get; set; }
}
