namespace NekoPlayer.Core.Models;

public sealed class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<PlaylistTrack> Tracks { get; set; } = [];
}

public sealed class PlaylistTrack
{
    public Guid PlaylistId { get; set; }
    public Guid TrackId { get; set; }
    public int SortOrder { get; set; }
    public Playlist? Playlist { get; set; }
    public Track? Track { get; set; }
}
